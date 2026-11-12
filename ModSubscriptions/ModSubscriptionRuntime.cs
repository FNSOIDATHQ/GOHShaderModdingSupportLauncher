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
    private readonly object statusGate = new();
    private readonly HashSet<string> recovered = new(StringComparer.OrdinalIgnoreCase);
    private SubscriptionLibrary library = SubscriptionLibrary.Empty();
    private readonly Task background;
    private string? gameDirectory;
    private string? error;
    private bool disposed;
    internal Task Ready { get; }
    internal SubscriptionStore Store { get; private set; } = null!;
    internal IModSource Source { get; private set; } = null!;
    internal ModDownloadService Downloads { get; private set; } = null!;
    internal ModInstallService Installer { get; private set; } = null!;
    internal ModUpdateCheckService Updates { get; private set; } = null!;
    // Detached store snapshots are replaced atomically; UI readers never clone JSON on the dispatcher.
    internal SubscriptionLibrary Library => Volatile.Read(ref library);
    internal ConcurrentDictionary<Guid, ModDownloadProgress> Progress { get; } = new();
    internal string? Error { get => Volatile.Read(ref error); private set => Volatile.Write(ref error, value); }
    internal event Action? Changed;

    internal ModSubscriptionRuntime(IModInstallationHost host, SubscriptionStore? store = null, IModSource? source = null)
    {
        Ready = Task.Run(() =>
        {
            Store = store ?? new SubscriptionStore();
            Source = source ?? new ModDbSource();
            Installer = new(Store, host);
            Updates = new(Store, provider => provider == Source.Provider ? Source : null);
            Downloads = new(Store, Source, progress: new RuntimeProgress(this));
            Volatile.Write(ref library, Store.Snapshot());
        });
        background = RunAsync();
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
        try { await Downloads.ResumePendingAsync(game, lifetime.Token).ConfigureAwait(false); }
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
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        _ = CloseAsync();
    }

    private async Task CloseAsync()
    {
        try
        {
            await background.ConfigureAwait(false);
            await Ready.ConfigureAwait(false);
            await Downloads.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { /* Initialization failure is already exposed to the page. */ }
        finally
        {
            (Source as IDisposable)?.Dispose();
            lifetime.Dispose();
        }
    }
}
