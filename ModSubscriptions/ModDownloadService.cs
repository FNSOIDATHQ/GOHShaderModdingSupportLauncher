using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModDownloadProgress(Guid SubscriptionId, Guid DownloadId, DownloadState State,
    long ReceivedBytes, long? TotalBytes);

// Selection is recorded by SubscriptionStore.Subscribe; only explicitly enqueued task IDs run here.
internal sealed class ModDownloadService : IAsyncDisposable
{
    private sealed class Job(Guid subscriptionId, Guid downloadId, CancellationToken token, bool onlyPending)
    {
        internal Guid SubscriptionId { get; } = subscriptionId;
        internal Guid DownloadId { get; } = downloadId;
        internal bool OnlyPending { get; } = onlyPending;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal TaskCompletionSource<ModDownload> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly SubscriptionStore store;
    private readonly IModSource source;
    private readonly ModFileTransferClient transfer;
    private readonly bool ownsTransfer;
    private readonly IProgress<ModDownloadProgress>? progress;
    private readonly Channel<Job> queue = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, Job> jobs = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool disposed;

    internal ModDownloadService(SubscriptionStore store, IModSource source, ModFileTransferClient? transfer = null,
        IProgress<ModDownloadProgress>? progress = null, ModVerificationState? verification = null,
        ModBrowserClearance? clearance = null)
    {
        this.store = store;
        this.source = source;
        this.transfer = transfer ?? new(new ModTransferOptions
        {
            Verification = verification, Clearance = clearance,
            AllowHost = source.Provider switch
            {
                "github" => GitHubModSource.IsDownloadHost,
                "mowportal" => YandexPublicFileResolver.IsDownloadHost,
                _ => ModDbParser.IsMirrorHost
            }
        });
        ownsTransfer = transfer is null;
        this.progress = progress;
        worker = Task.Run(WorkAsync);
    }

    internal static string DownloadDirectory(string gameDirectory, Guid downloadId) =>
        Path.Combine(ManagedModFiles.PrivateRoot(gameDirectory), "downloads", downloadId.ToString("N"));
    internal static string ArtifactPath(string gameDirectory, Guid downloadId) =>
        Path.Combine(DownloadDirectory(gameDirectory, downloadId), "download.bin");

    // A repeated enqueue shares its existing task; Retry is an explicit enqueue after completion.
    internal Task<ModDownload> EnqueueAsync(Guid subscriptionId, Guid downloadId, CancellationToken cancellationToken = default)
        => QueueAsync(subscriptionId, downloadId, cancellationToken, onlyPending: false);

    private Task<ModDownload> QueueAsync(Guid subscriptionId, Guid downloadId, CancellationToken cancellationToken, bool onlyPending)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (jobs.TryGetValue(downloadId, out var existing))
            {
                if (existing.SubscriptionId != subscriptionId) throw new ArgumentException("The task belongs to another subscription.");
                return existing.Completion.Task;
            }
            var subscription = GetSubscription(subscriptionId);
            if (subscription.State != SubscriptionState.Active || subscription.Source.Provider != source.Provider)
                throw new InvalidOperationException("The subscription is being removed or has another source provider.");
            var selected = subscription.Downloads.Single(item => item.Id == downloadId);
            if (!selected.File.CanDownload)
                throw new InvalidOperationException("The selected file has no supported anonymous download resolver.");
            var job = new Job(subscriptionId, downloadId, cancellationToken, onlyPending);
            if (!jobs.TryAdd(downloadId, job)) throw new InvalidOperationException("The task was already queued.");
            if (!queue.Writer.TryWrite(job))
            {
                jobs.TryRemove(downloadId, out _);
                job.Cancellation.Dispose();
                throw new ObjectDisposedException(nameof(ModDownloadService));
            }
            return job.Completion.Task;
        }
    }

    internal bool Cancel(Guid downloadId)
    {
        if (!jobs.TryGetValue(downloadId, out var job)) return false;
        try { job.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    internal async Task<bool> CancelAsync(Guid downloadId, CancellationToken token = default)
    {
        if (Cancel(downloadId)) return true;
        return await Task.Run(() =>
        {
            var subscription = store.Snapshot().Subscriptions.SingleOrDefault(item =>
                item.Source.Provider == source.Provider && item.Downloads.Any(download => download.Id == downloadId));
            if (subscription is null) return false;
            using var lease = GameOperationLock.Acquire(subscription.GameDirectory);
            token.ThrowIfCancellationRequested();
            store.Reload();
            subscription = GetSubscription(subscription.Id);
            var download = subscription.Downloads.Single(item => item.Id == downloadId);
            if (download.State != DownloadState.Pending || download.Error != nameof(ModSourceError.VerificationRequired)) return false;
            download = download with { State = DownloadState.Cancelled, Error = null, UpdatedAt = DateTimeOffset.UtcNow };
            store.SaveDownload(subscription.Id, download);
            progress?.Report(new(subscription.Id, downloadId, download.State, download.ReceivedBytes, download.File.SizeBytes));
            return true;
        }, token).ConfigureAwait(false);
    }

    // Recovery is caller initiated, scoped to the selected game; it never invents selected files.
    internal Task<ModDownload[]> ResumePendingAsync(string gameDirectory, CancellationToken cancellationToken = default) =>
        Task.WhenAll(store.GetSubscriptions(gameDirectory).Where(item => item.State == SubscriptionState.Active && item.Source.Provider == source.Provider)
            .SelectMany(subscription => subscription.Downloads.Where(item => item.State is DownloadState.Pending or DownloadState.Downloading)
                .Select(item => QueueAsync(subscription.Id, item.Id, cancellationToken, onlyPending: true))));

    private async Task WorkAsync()
    {
        await foreach (var job in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            ModDownload? result = null;
            Exception? failure = null;
            try { result = await RunAsync(job).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lock (sync) { jobs.TryRemove(job.DownloadId, out _); job.Cancellation.Dispose(); }
            }
            if (failure is null) job.Completion.TrySetResult(result!);
            else job.Completion.TrySetException(failure);
        }
    }

    private async Task<ModDownload> RunAsync(Job job)
    {
        CancellationToken token = job.Cancellation.Token;
        var subscription = GetSubscription(job.SubscriptionId);
        var download = subscription.Downloads.Single(item => item.Id == job.DownloadId);
        IDisposable? gameLease = null;
        try
        {
            // The same cross-process lease protects downloads, installation and unsubscribe cleanup.
            while (gameLease is null)
            {
                try { gameLease = GameOperationLock.Acquire(subscription.GameDirectory); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                { await Task.Delay(100, token).ConfigureAwait(false); }
            }
            token.ThrowIfCancellationRequested();
            store.Reload();
            subscription = GetSubscription(job.SubscriptionId);
            if (subscription.State != SubscriptionState.Active)
                throw new InvalidOperationException("The subscription is being removed.");
            download = subscription.Downloads.Single(item => item.Id == job.DownloadId);
            if (job.OnlyPending && download.State is not (DownloadState.Pending or DownloadState.Downloading)) return download;
            string directory = DownloadDirectory(subscription.GameDirectory, download.Id);
            string artifact = ArtifactPath(subscription.GameDirectory, download.Id);
            string partial = Path.Combine(directory, "download.part");
            string resume = Path.Combine(directory, "resume.json");
            ManagedModFiles.EnsureSafe(directory);
            ManagedModFiles.EnsureSafe(artifact);
            Directory.CreateDirectory(directory);
            bool previouslyManaged = download.LocalPath is { } previous &&
                (previous.Equals(partial, SubscriptionPaths.Comparison) || previous.Equals(artifact, SubscriptionPaths.Comparison));
            if (!previouslyManaged && (File.Exists(artifact) || File.Exists(partial) || File.Exists(resume)))
                throw new IOException("An unowned file already occupies the download task directory.");
            if (File.Exists(artifact))
            {
                // A move completed before a metadata write can be recovered without another download.
                if (download.ContentSha256 is null && download.File.Md5 is null)
                    throw new InvalidDataException("The existing payload has no durable checksum proof and needs manual review.");
                string sha256 = await VerifyAsync(artifact, download.File, token, download.ContentSha256).ConfigureAwait(false);
                download = download with { State = download.State == DownloadState.NeedsManualInstall ? download.State : DownloadState.Downloaded,
                    LocalPath = artifact, ReceivedBytes = new FileInfo(artifact).Length, UpdatedAt = DateTimeOffset.UtcNow, Error = null, ContentSha256 = sha256 };
                store.SaveDownload(job.SubscriptionId, download);
                Report(job, download.State, download.ReceivedBytes, download.File.SizeBytes);
                return download;
            }
            download = download with { State = DownloadState.Downloading, LocalPath = partial,
                ReceivedBytes = File.Exists(partial) ? new FileInfo(partial).Length : 0, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
            store.SaveDownload(job.SubscriptionId, download);
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var resolved = await source.ResolveDownloadAsync(download.File.PageUrl, token).ConfigureAwait(false);
                    if (!resolved.IsSuccess) throw new ModSourceException(resolved.Failure!);
                    var link = resolved.Content!;
                    if (!link.File.CanDownload)
                        throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "This link no longer supports anonymous downloads.");
                    if (link.File.Id != download.File.Id || link.File.Revision != download.File.Revision ||
                        (download.File.SizeBytes is { } selectedSize && link.File.SizeBytes != selectedSize) ||
                        (download.File.Md5 is { } selectedMd5 && !selectedMd5.Equals(link.File.Md5, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("The selected file changed. Select its current revision before downloading.");
                    // Schema-one records selected before checksum support can gain missing metadata
                    // only after the immutable source identity and revision have matched.
                    download = download with { File = download.File with
                    {
                        SizeBytes = download.File.SizeBytes ?? link.File.SizeBytes,
                        Md5 = download.File.Md5 ?? link.File.Md5,
                        Description = link.File.Description,
                        Name = link.File.Name,
                        DownloadOnly = download.File.DownloadOnly || link.File.DownloadOnly,
                        IsSourceSnapshot = download.File.IsSourceSnapshot || link.File.IsSourceSnapshot
                    } };
                    store.SaveDownload(job.SubscriptionId, download);
                    DateTimeOffset lastSaved = DateTimeOffset.UtcNow;
                    await transfer.TransferAsync(link, partial, resume, (received, total) =>
                    {
                        download = download with { ReceivedBytes = received, UpdatedAt = DateTimeOffset.UtcNow };
                        Report(job, DownloadState.Downloading, received, total);
                        if (DateTimeOffset.UtcNow - lastSaved >= TimeSpan.FromSeconds(1))
                        {
                            store.SaveDownload(job.SubscriptionId, download);
                            lastSaved = DateTimeOffset.UtcNow;
                        }
                    }, token).ConfigureAwait(false);
                    string sha256 = await VerifyAsync(partial, download.File, token).ConfigureAwait(false);
                    // Persist the proof before moving, so recovery never adopts a changed same-size payload.
                    download = download with { ContentSha256 = sha256 };
                    store.SaveDownload(job.SubscriptionId, download);
                    token.ThrowIfCancellationRequested();
                    ManagedModFiles.EnsureSafe(artifact);
                    File.Move(partial, artifact); // Never overwrite an unknown existing payload.
                    download = download with { State = DownloadState.Downloaded, LocalPath = artifact,
                        ReceivedBytes = new FileInfo(artifact).Length, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
                    store.SaveDownload(job.SubscriptionId, download);
                    ManagedModFiles.EnsureSafe(resume);
                    File.Delete(resume);
                    Report(job, download.State, download.ReceivedBytes, download.File.SizeBytes);
                    return download;
                }
                catch (Exception ex) when (IsRetryable(ex, token) && attempt < transfer.MaxRetries)
                {
                    if (ex is ModSourceException sourceError && sourceError.Failure.RetryAfter > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)) throw;
                    await Task.Delay(TimeSpan.FromTicks(checked(transfer.RetryDelay.Ticks * (1L << attempt))), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            download = download with { State = DownloadState.Cancelled, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
            if (gameLease is not null) store.SaveDownload(job.SubscriptionId, download);
            Report(job, download.State, download.ReceivedBytes, download.File.SizeBytes);
            return download;
        }
        catch (ModSourceException ex) when (ex.Failure.Kind == ModSourceError.VerificationRequired)
        {
            // These files were explicitly selected. Keep the durable intent, partial bytes and proof;
            // a successful human verification resumes them without selecting additional files.
            download = download with { State = DownloadState.Pending, UpdatedAt = DateTimeOffset.UtcNow,
                Error = nameof(ModSourceError.VerificationRequired) };
            if (gameLease is not null) store.SaveDownload(job.SubscriptionId, download);
            Report(job, download.State, download.ReceivedBytes, download.File.SizeBytes);
            return download;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep private payloads and ownership for retry. Do not persist signed URI-bearing exceptions.
            download = download with { State = DownloadState.Failed, UpdatedAt = DateTimeOffset.UtcNow,
                Error = ex is InvalidDataException ? "The selected file or downloaded content failed validation. Select the file again or retry."
                    : ex is ModSourceException sourceError ? sourceError.Failure.Kind.ToString()
                    : "The download could not finish. Check the connection, disk space and access, then retry." };
            if (gameLease is not null) store.SaveDownload(job.SubscriptionId, download);
            Report(job, download.State, download.ReceivedBytes, download.File.SizeBytes);
            return download;
        }
        finally { gameLease?.Dispose(); }
    }

    private static bool IsRetryable(Exception ex, CancellationToken token) => !token.IsCancellationRequested && ex switch
    {
        ModSourceException sourceError => sourceError.Failure.Kind != ModSourceError.VerificationRequired &&
            (sourceError.Failure.Kind is ModSourceError.Timeout or ModSourceError.RateLimited ||
            sourceError.Failure.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.InternalServerError or
                System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout),
        HttpRequestException or EndOfStreamException or OperationCanceledException => true,
        _ => false
    };

    internal static async Task<string> VerifyAsync(string path, RemoteModFile file, CancellationToken token, string? contentSha256 = null)
    {
        ManagedModFiles.EnsureSafe(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length == 0 || file.SizeBytes is { } size && input.Length != size)
            throw new InvalidDataException("The downloaded file size is invalid.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = file.Md5 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            md5?.AppendData(buffer, 0, read);
        }
        if (md5 is not null && !Convert.ToHexString(md5.GetHashAndReset()).Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded file checksum is invalid.");
        string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (contentSha256 is not null && !hash.Equals(contentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The previously downloaded content was modified.");
        return hash;
    }

    private GameModSubscription GetSubscription(Guid id) => store.Snapshot().Subscriptions.SingleOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException("The subscription does not exist.");

    private void Report(Job job, DownloadState state, long received, long? total) =>
        progress?.Report(new(job.SubscriptionId, job.DownloadId, state, received, total));

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var job in jobs.Values) job.Cancellation.Cancel();
            queue.Writer.TryComplete();
        }
        await worker.ConfigureAwait(false);
        if (ownsTransfer) transfer.Dispose();
    }
}
