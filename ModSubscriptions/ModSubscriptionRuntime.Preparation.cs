using System.Collections.Concurrent;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModDownloadPreparation(DateTimeOffset DownloadUpdatedAt, string? ContentSha256,
    ModArchiveInspection? Inspection = null, string? Error = null)
{
    internal bool Checking => Inspection is null && Error is null;
}

internal sealed partial class ModSubscriptionRuntime
{
    internal ConcurrentDictionary<Guid, ModDownloadPreparation> PreparedDownloads { get; } = new();
    private readonly ConcurrentDictionary<Guid, byte> stoppedSubscriptions = new();
    private readonly object preparationSync = new();
    private readonly SemaphoreSlim preparationGate = new(1, 1);
    private readonly Dictionary<Guid, (Guid Subscription, CancellationTokenSource Cancellation, Task Task)> preparationJobs = [];

    private bool CanResumeSubscription(Guid id) => !stoppedSubscriptions.ContainsKey(id);
    internal void AllowSubscriptionDownloads(Guid id) => stoppedSubscriptions.TryRemove(id, out _);

    internal void PrepareDownloadsForGame(string game)
    {
        if (disposed || !Ready.IsCompletedSuccessfully) return;
        lock (preparationSync)
        {
            foreach (var sub in Library.Subscriptions.Where(sub => sub.State == SubscriptionState.Active &&
                sub.GameDirectory.Equals(game, SubscriptionPaths.Comparison) && CanResumeSubscription(sub.Id)))
                foreach (var download in sub.Downloads.Where(download => download.State is DownloadState.Downloaded or DownloadState.NeedsManualInstall &&
                    !sub.Installations.Any(installation => installation.DownloadId == download.Id && installation.State == InstallationState.Installed)))
                {
                    if (preparationJobs.ContainsKey(download.Id)) continue;
                    if (PreparedDownloads.TryGetValue(download.Id, out var existing) && existing.DownloadUpdatedAt == download.UpdatedAt &&
                        existing.ContentSha256 == download.ContentSha256) continue;
                    var prepared = new ModDownloadPreparation(download.UpdatedAt, download.ContentSha256);
                    PreparedDownloads[download.Id] = prepared;
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    // Hashing and archive inspection stay away from the dispatcher.
                    preparationJobs[download.Id] = (sub.Id, cancellation, Task.Run(() => PrepareDownloadAsync(sub, download, prepared, cancellation.Token)));
                }
        }
    }

    private async Task PrepareDownloadAsync(GameModSubscription sub, ModDownload download, ModDownloadPreparation prepared, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await preparationGate.WaitAsync(token).ConfigureAwait(false); entered = true;
            ModArchiveInspection inspection;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { inspection = Installer.Inspect(sub.Id, download.Id, token); break; }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                { await Task.Delay(100, token).ConfigureAwait(false); }
            }
            PreparedDownloads.TryUpdate(download.Id, prepared with { Inspection = inspection }, prepared);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { PreparedDownloads.TryRemove(download.Id, out _); }
        catch (Exception ex) { PreparedDownloads.TryUpdate(download.Id, prepared with { Error = ex.Message }, prepared); }
        finally
        {
            if (entered) preparationGate.Release();
            lock (preparationSync)
            {
                if (preparationJobs.Remove(download.Id, out var job)) job.Cancellation.Dispose();
            }
            if (!disposed) Changed?.Invoke();
        }
    }

    internal void RecheckDownload(Guid downloadId)
    {
        lock (preparationSync) if (!preparationJobs.ContainsKey(downloadId)) PreparedDownloads.TryRemove(downloadId, out _);
    }

    internal Task WaitForDownloadPreparationAsync(Guid downloadId, CancellationToken token)
    {
        lock (preparationSync)
            return preparationJobs.TryGetValue(downloadId, out var job) ? job.Task.WaitAsync(token) : Task.CompletedTask;
    }

    internal async Task StopSubscriptionTasksAsync(GameModSubscription subscription, CancellationToken token)
    {
        stoppedSubscriptions[subscription.Id] = 0;
        Task[] checking;
        lock (preparationSync)
        {
            var jobs = preparationJobs.Values.Where(job => job.Subscription == subscription.Id).ToArray();
            foreach (var job in jobs) job.Cancellation.Cancel();
            checking = jobs.Select(job => job.Task).ToArray();
        }
        await Task.WhenAll(checking).WaitAsync(token).ConfigureAwait(false);
        await GetDownloads(subscription.Source.Provider).CancelSubscriptionAsync(subscription.Id, token).ConfigureAwait(false);
        await RefreshLibraryAsync(token).ConfigureAwait(false);
        NotifyChanged();
    }
}
