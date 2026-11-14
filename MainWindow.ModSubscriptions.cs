using Avalonia.Controls;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private ModSubscriptionRuntime? subscriptionRuntime;
    private bool subscriptionShutdownStarted, subscriptionShutdownFinished;
    internal ModSubscriptionRuntime SubscriptionRuntime => subscriptionRuntime ??= CreateSubscriptionRuntime();

    internal string? SubscriptionGameDirectory => universalVars.localDir is { } mods &&
        universalVars.gameDir?.Parent?.Parent is { } game &&
        SubscriptionPaths.Normalize(mods.FullName).Equals(SubscriptionPaths.ModsDirectory(game.FullName), SubscriptionPaths.Comparison)
            ? game.FullName : null;

    private ModSubscriptionRuntime CreateSubscriptionRuntime()
    {
        var runtime = new ModSubscriptionRuntime(CreateModInstallationHost(), diagnostic: entry => AppDiagnostics.Log("Mod source HTTP: " + entry),
            verificationDiagnostic: message => AppDiagnostics.Log("ModDB verification: " + message));
        runtime.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (runtime.Ready.IsCompletedSuccessfully)
            {
                string? game = SubscriptionGameDirectory;
                int count = game is null ? 0 : runtime.Library.Subscriptions.Where(subscription =>
                    subscription.GameDirectory.Equals(game, SubscriptionPaths.Comparison))
                    .Sum(subscription => ModUpdateCheckService.GetNotices(subscription).Length);
                UpdateSubscriptionBadge(count);
            }
        });
        runtime.SelectGame(SubscriptionGameDirectory);
        return runtime;
    }

    private void StartSubscriptionRuntime() => _ = SubscriptionRuntime;
    private void DisposeSubscriptionRuntime() => subscriptionRuntime?.Dispose();
    private void ChangeSubscriptionGame() => subscriptionRuntime?.SelectGame(SubscriptionGameDirectory);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || subscriptionShutdownFinished || subscriptionRuntime is null) return;
        // Await the owned browser/profile cleanup while the UI dispatcher is still alive.
        e.Cancel = true;
        if (subscriptionShutdownStarted) return;
        subscriptionShutdownStarted = true;
        IsEnabled = false;
        if (pages.TryGetValue(PageId.ModSubscriptions, out var page)) ((ModSubscriptionsPage)page).Dispose();
        _ = FinishSubscriptionCloseAsync();
    }

    private async Task FinishSubscriptionCloseAsync()
    {
        try { await subscriptionRuntime!.ShutdownAsync(); }
        catch (Exception ex) { AppDiagnostics.Log("Subscription shutdown cleanup: " + ex.GetType().Name); }
        finally { subscriptionShutdownFinished = true; Close(); }
    }

    internal IModInstallationHost CreateModInstallationHost() => new MainWindowModInstallationHost(this);

    private sealed class MainWindowModInstallationHost(MainWindow main) : IModInstallationHost
    {
        public bool IsGameRunning(string gameDirectory) => ModGameProcessGuard.IsGameRunning(gameDirectory);

        private bool IsCurrentGame(string gameDirectory)
        {
            string game = SubscriptionPaths.Normalize(gameDirectory);
            var selected = main.universalVars.gameDir?.Parent?.Parent;
            return selected is not null && main.universalVars.localDir is not null &&
                SubscriptionPaths.Normalize(selected.FullName).Equals(game, SubscriptionPaths.Comparison) &&
                SubscriptionPaths.Normalize(main.universalVars.localDir.FullName)
                    .Equals(SubscriptionPaths.ModsDirectory(game), SubscriptionPaths.Comparison);
        }

        public void RemoveEnabledMods(string gameDirectory, IReadOnlyList<string> directories) => OnUiThread(() =>
        {
            if (!IsCurrentGame(gameDirectory))
                throw new InvalidOperationException("Select this subscription's game directory before completing removal.");
            if (IsGameRunning(gameDirectory))
                throw new InvalidOperationException("Close the game and editor before changing the enabled Mod list.");

            var removed = directories.Select(directory => SubscriptionPaths.InstallationDirectory(gameDirectory, directory))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var identifiers = removed.Select(directory => Path.GetFileName(directory).ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = main.ScanMods();
            // A Workshop Mod can share a local folder identifier. Preserve that separate owner.
            identifiers.RemoveWhere(id => available.TryGetValue(id, out var mod) &&
                !removed.Contains(SubscriptionPaths.Normalize(mod.path)));

            string options = SubscriptionPaths.Normalize(main.universalVars.optionLoc);
            ManagedModFiles.EnsureSafe(options);
            using var reader = File.OpenText(options);
            var enabled = FileManager.ReadLoadedModNames(reader).ToArray();
            var retained = enabled.Where(id => !identifiers.Contains(id)).ToArray();
            if (retained.Length == enabled.Length) return;

            // Keep order and missing references; presets remain untouched.
            var mods = retained.Select(id => new Mod(id, "", "", id, false)).ToArray();
            reader.Close();
            FileManager.WriteLoadedMods(options, mods);
        });

        public void RefreshMods(string gameDirectory) => OnUiThread(() =>
        {
            // A completed background task must not refresh a different game's state.
            if (!IsCurrentGame(gameDirectory)) return;
            main.universalVars.localDir!.Refresh();
            main.BeginModRefresh();
        });

        private static void OnUiThread(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Invoke(action);
        }
    }
}
