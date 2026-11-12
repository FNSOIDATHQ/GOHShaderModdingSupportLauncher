using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// One runtime per window. Disk recovery and all network work stay off the UI thread.
internal sealed class ModSubscriptionRuntime : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private readonly SemaphoreSlim libraryGate = new(1, 1);
    private readonly SemaphoreSlim verificationGate = new(1, 1);
    private readonly ModBrowserClearance clearance = new();
    private readonly ModVerificationBrowser verificationBrowser;
    private readonly object statusGate = new();
    private readonly HashSet<string> recovered = new(StringComparer.OrdinalIgnoreCase);
    private SubscriptionLibrary library = SubscriptionLibrary.Empty();
    private readonly Task background;
    private Task? shutdown;
    private string? gameDirectory;
    private string? error;
    private bool disposed;
    internal Task Ready { get; }
    internal SubscriptionStore Store { get; private set; } = null!;
    internal IModSource Source { get; private set; } = null!;
    internal GitHubModSource GitHub { get; private set; } = null!;
    internal IModSource MowPortal { get; private set; } = null!;
    internal GitHubOptionsStore GitHubSettings { get; private set; } = null!;
    internal ModDownloadService Downloads { get; private set; } = null!;
    private ModDownloadService githubDownloads = null!;
    private ModDownloadService mowPortalDownloads = null!;
    internal IModSource? ResolveSource(string provider) => provider == Source.Provider ? Source :
        provider == GitHub.Provider ? GitHub : provider == MowPortal.Provider ? MowPortal : null;
    internal ModDownloadService GetDownloads(string provider) => provider == Source.Provider ? Downloads :
        provider == GitHub.Provider ? githubDownloads : provider == MowPortal.Provider ? mowPortalDownloads :
        throw new ArgumentException("Unsupported Mod source.", nameof(provider));
    internal ModInstallService Installer { get; private set; } = null!;
    internal ModUpdateCheckService Updates { get; private set; } = null!;
    internal ModVerificationState Verification { get; } = new();
    // Detached store snapshots are replaced atomically; UI readers never clone JSON on the dispatcher.
    internal SubscriptionLibrary Library => Volatile.Read(ref library);
    internal ConcurrentDictionary<Guid, ModDownloadProgress> Progress { get; } = new();
    internal string? Error { get => Volatile.Read(ref error); private set => Volatile.Write(ref error, value); }
    internal event Action? Changed;

    internal ModSubscriptionRuntime(IModInstallationHost host, SubscriptionStore? store = null, IModSource? source = null,
        Action<ModHttpDiagnostic>? diagnostic = null, Action<string>? verificationDiagnostic = null,
        GitHubModSource? githubSource = null, IModSource? mowPortalSource = null)
    {
        verificationBrowser = new(clearance, verificationDiagnostic);
        Verification.Changed += NotifyChanged;
        Ready = Task.Run(() =>
        {
            Store = store ?? new SubscriptionStore();
            Source = source ?? new ModDbSource(diagnostic: diagnostic, verification: Verification, clearance: clearance);
            GitHubSettings = new GitHubOptionsStore(Path.Combine(Path.GetDirectoryName(Store.LibraryPath)!, "github-settings.json"));
            GitHub = githubSource ?? new GitHubModSource(settings: GitHubSettings, diagnostic: diagnostic);
            MowPortal = mowPortalSource ?? new MowPortalModSource(diagnostic: diagnostic, verification: Verification);
            Installer = new(Store, host);
            Updates = new(Store, ResolveSource, Verification);
            Downloads = new(Store, Source, progress: new RuntimeProgress(this), verification: Verification, clearance: clearance);
            githubDownloads = new(Store, GitHub, progress: new RuntimeProgress(this));
            mowPortalDownloads = new(Store, MowPortal, progress: new RuntimeProgress(this), verification: Verification);
            Volatile.Write(ref library, Store.Snapshot());
        });
        background = RunAsync();
    }

    internal async Task OpenVerificationAsync(Uri origin, CancellationToken token = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await Ready.WaitAsync(cancellation.Token).ConfigureAwait(false);
        await verificationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            if (!Verification.IsPausedFor(origin)) return;
            Uri page = Verification.TryGetProbe(origin, out var target, out _) ? target : origin;
            await verificationBrowser.OpenAsync(page, cancellation.Token).ConfigureAwait(false);
        }
        finally { verificationGate.Release(); }
    }

    internal async Task<bool> CompleteVerificationAsync(Uri origin, CancellationToken token = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await Ready.WaitAsync(cancellation.Token).ConfigureAwait(false);
        await verificationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            if (!Verification.IsPausedFor(origin)) return true;
            if (ModBrowserClearance.IsSupportedOrigin(origin) && verificationBrowser.IsOpen)
            {
                try { await verificationBrowser.CaptureAsync(origin, cancellation.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or
                    InvalidOperationException or System.Text.Json.JsonException)
                { /* A closed browser does not prevent an explicit native access probe. */ }
            }
            bool passed;
            using (Verification.AllowRetry(origin))
            {
                passed = true;
                if (Verification.TryGetProbe(origin, out var target, out bool fileProbe))
                    passed = await ProbeProtectedRequestAsync(origin, target, fileProbe, cancellation.Token).ConfigureAwait(false);
                if (passed && ModBrowserClearance.IsSupportedOrigin(origin))
                {
                    var response = await Source.BrowseAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
                    passed = response.IsSuccess && !response.IsStale;
                }
                else if (!ModBrowserClearance.IsSupportedOrigin(origin) && !Verification.TryGetProbe(origin, out _, out _)) passed = false;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!passed) return false;
            Verification.Clear(origin);
            lock (statusGate) Error = null;
            await verificationBrowser.CloseAsync(clearSession: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            wake.Writer.TryWrite(0);
            string? game = Volatile.Read(ref gameDirectory);
            if (game is not null) _ = ResumeAsync(game);
            NotifyChanged();
            return true;
        }
        finally { verificationGate.Release(); }
    }

    internal async Task CloseVerificationAsync(CancellationToken token = default)
    {
        await verificationGate.WaitAsync(token).ConfigureAwait(false);
        try { await verificationBrowser.CloseAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); }
        finally { verificationGate.Release(); }
    }

    internal void PrepareVerificationRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri) throw new ArgumentException("A request address is required.", nameof(request));
        request.Headers.Remove("Cookie");
        if (ModBrowserClearance.IsSupportedOrigin(uri)) request.Headers.UserAgent.Clear();
        if (Verification.IsPausedFor(uri)) Verification.RecordProbe(uri, fileProbe: true);
        Verification.ThrowIfPaused(uri);
        clearance.Apply(request);
    }

    internal void RequireVerification(Uri uri, System.Net.HttpStatusCode? status, bool fileProbe = true)
    {
        clearance.Clear(uri);
        Verification.RecordProbe(uri, fileProbe);
        Verification.Pause(uri);
        throw ModChallengeDetector.Required(uri, status);
    }

    private async Task<bool> ProbeProtectedRequestAsync(Uri origin, Uri target, bool fileProbe, CancellationToken token)
    {
        using var http = new AnonymousModHttpClient(new ModHttpOptions
        { Verification = Verification, Clearance = clearance, MaxRetries = 0 });
        bool Allow(Uri uri) => ModVerificationState.Origin(uri) == ModVerificationState.Origin(origin) ||
            fileProbe && YandexPublicFileResolver.IsDownloadHost(target) && YandexPublicFileResolver.IsDownloadHost(uri);
        try
        {
            if (fileProbe) await http.ResolveFileAsync(target, Allow, token).ConfigureAwait(false);
            else if (target.Host == "cloud-api.yandex.net") await http.GetJsonAsync(target, Allow, token).ConfigureAwait(false);
            else await http.GetTextAsync(target, Allow, token).ConfigureAwait(false);
            return true;
        }
        catch (ModSourceException) { return false; }
    }

    private sealed class RuntimeProgress(ModSubscriptionRuntime owner) : IProgress<ModDownloadProgress>
    {
        public void Report(ModDownloadProgress value) => owner.Progress[value.DownloadId] = value;
    }

    internal void SelectGame(string? game)
    {
        if (disposed) return;
        string? next = game is null ? null : SubscriptionPaths.Normalize(game);
        lock (statusGate)
        {
            if (!string.Equals(gameDirectory, next, SubscriptionPaths.Comparison)) Error = null;
            Volatile.Write(ref gameDirectory, next);
        }
        wake.Writer.TryWrite(0);
        Changed?.Invoke();
    }

    private void SetGameError(string game, string? message)
    {
        lock (statusGate)
        {
            // An old background request may finish after a game switch.
            if (gameDirectory is null || string.Equals(gameDirectory, game, SubscriptionPaths.Comparison)) Error = message;
        }
    }

    internal Task EnsureRecoveredAsync(string game, CancellationToken token = default) => RecoverAsync(game, false, token);

    // A failed install/removal can leave a durable transaction after the first recovery.
    // Explicit retry must inspect that journal again, even in the same launcher session.
    internal Task RecoverAgainAsync(string game, CancellationToken token = default) => RecoverAsync(game, true, token);

    private async Task RecoverAsync(string game, bool force, CancellationToken token)
    {
        game = SubscriptionPaths.Normalize(game);
        await Ready.ConfigureAwait(false);
        await recoveryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!force && recovered.Contains(game)) return;
            await Task.Run(() => Installer.Recover(game), token).ConfigureAwait(false);
            await RefreshLibraryAsync().ConfigureAwait(false);
            recovered.Add(game);
            // Only persisted, explicitly selected tasks are resumed; never infer a latest file.
            _ = ResumeAsync(game);
        }
        finally { recoveryGate.Release(); }
    }

    private async Task ResumeAsync(string game)
    {
        try
        {
            await Task.WhenAll(Downloads.ResumePendingAsync(game, lifetime.Token),
                githubDownloads.ResumePendingAsync(game, lifetime.Token),
                mowPortalDownloads.ResumePendingAsync(game, lifetime.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetGameError(game, ex.Message); }
        finally { NotifyChanged(); }
    }

    internal async Task RefreshLibraryAsync(CancellationToken token = default)
    {
        await Ready.WaitAsync(token).ConfigureAwait(false);
        await libraryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = await Task.Run(Store.Snapshot, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref library, snapshot);
        }
        finally { libraryGate.Release(); }
    }

    internal void ReportCheckResults(IReadOnlyCollection<ModUpdateCheckResult> results, string? game = null)
    {
        // An empty due-only poll did not retry a previous failure.
        if (results.Count == 0) return;
        string? message = results.FirstOrDefault(result => !result.Succeeded)?.Failure?.Message;
        if (game is not null) SetGameError(game, message);
        else lock (statusGate) Error = message;
    }

    internal void NotifyChanged() => _ = NotifyChangedAsync();

    private async Task NotifyChangedAsync()
    {
        try { await RefreshLibraryAsync().ConfigureAwait(false); }
        catch (Exception ex) { Error = ex.Message; }
        Changed?.Invoke();
    }

    private async Task RunAsync()
    {
        try
        {
            await Ready.ConfigureAwait(false);
            while (!lifetime.IsCancellationRequested)
            {
                while (wake.Reader.TryRead(out _)) { }
                string? game = Volatile.Read(ref gameDirectory);
                if (game is not null)
                {
                    try
                    {
                        await EnsureRecoveredAsync(game, lifetime.Token).ConfigureAwait(false);
                        var results = await Updates.CheckGameAsync(game, onlyDue: true, cancellationToken: lifetime.Token).ConfigureAwait(false);
                        // Skipped checks must retain the last failed check's status until a retry.
                        ReportCheckResults(results, game);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                    catch (Exception ex) { SetGameError(game, ex.Message); }
                    NotifyChanged();
                }
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                tick.CancelAfter(TimeSpan.FromMinutes(1));
                try { await wake.Reader.ReadAsync(tick.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Error = ex.Message; Changed?.Invoke(); }
    }

    public void Dispose()
        => _ = ShutdownAsync();

    internal Task ShutdownAsync()
    {
        lock (statusGate)
        {
            if (shutdown is not null) return shutdown;
            disposed = true;
            lifetime.Cancel();
            return shutdown = CloseAsync();
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            await background.ConfigureAwait(false);
            await Ready.ConfigureAwait(false);
            await Downloads.DisposeAsync().ConfigureAwait(false);
            await githubDownloads.DisposeAsync().ConfigureAwait(false);
            await mowPortalDownloads.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { /* Initialization failure is already exposed to the page. */ }
        finally
        {
            await verificationBrowser.DisposeAsync().ConfigureAwait(false);
            clearance.Clear();
            (Source as IDisposable)?.Dispose();
            GitHub?.Dispose();
            (MowPortal as IDisposable)?.Dispose();
            lifetime.Dispose();
        }
    }
}
