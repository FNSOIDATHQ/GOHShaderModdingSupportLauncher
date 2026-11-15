using Avalonia.Controls;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    internal void OpenSubscriptionModManager(string? directory = null) => _ = LocateSubscriptionModAsync(directory);
    private bool openingInstalledSubscription;
    private ModSubscriptionOrigins? modOrigins;
    private SubscriptionLibrary? originLibrary;
    private string? originCulture;
    internal event Action? ModOriginsChanged;

    private ModSubscriptionOrigins CurrentModOrigins()
    {
        var library = subscriptionRuntime?.Ready.IsCompletedSuccessfully == true ? subscriptionRuntime.Library : null;
        string? game = SubscriptionGameDirectory;
        if (modOrigins is null || originLibrary?.Revision != library?.Revision ||
            !string.Equals(modOrigins.GameDirectory, game, SubscriptionPaths.Comparison))
        {
            originLibrary = library;
            modOrigins = new(library ?? SubscriptionLibrary.Empty(), game, message => AppDiagnostics.Log(message));
        }
        return modOrigins;
    }

    internal void RefreshModOrigins()
    {
        var origins = CurrentModOrigins();
        bool languageChanged = originCulture != LocalizedStrings.Instance.Culture.Name;
        originCulture = LocalizedStrings.Instance.Culture.Name;
        bool changed = false;
        foreach (var mod in universalVars.modDic.Values.Concat(universalVars.modLoaded).Distinct())
        {
            changed |= origins.Apply(mod);
            if (languageChanged)
            {
                if (mod.Inspection is { } inspection) mod.type = inspection.Source == ModSourceKind.Workshop
                    ? Properties.i18n.Main_ModWorkshop : Properties.i18n.Main_ModLocal;
                mod.RefreshSourceDisplay();
            }
        }
        if (changed || languageChanged) ModOriginsChanged?.Invoke();
    }

    internal async Task OpenModSubscriptionAsync(Mod mod)
    {
        CurrentModOrigins().Apply(mod);
        if (mod.SubscriptionLink is not { } link) return;
        openingInstalledSubscription = true;
        try { SelectNavigationPage(PageId.ModSubscriptions); }
        finally { openingInstalledSubscription = false; }
        if (pageHost.Content is ModSubscriptionsPage page)
            await page.OpenInstalledSubscriptionAsync(link.SubscriptionId, link.InstallationId);
    }

    private async Task LocateSubscriptionModAsync(string? directory)
    {
        var context = ModDirectoryContext;
        try
        {
            SelectNavigationPage(PageId.ModManager);
            if (directory is null) return;
            Task refresh;
            do
            {
                refresh = PendingModRefresh;
                await refresh;
                if (context != ModDirectoryContext || pageHost.Content is not ModManager) return;
            } while (!ReferenceEquals(refresh, PendingModRefresh));
            if (pageHost.Content is not ModManager manager) return;
            if (!manager.SelectInstalledDirectory(directory))
                MessageBox.Show(LocalizedStrings.Instance.Get("MS_InstallationUnavailable"),
                    Properties.i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { AppDiagnostics.Log("Unable to locate subscription installation.", ex); }
    }
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
        AttachSubscriptionRuntime(runtime);
        return runtime;
    }

    private void AttachSubscriptionRuntime(ModSubscriptionRuntime runtime)
    {
        runtime.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(subscriptionRuntime, runtime) || subscriptionShutdownStarted) return;
            if (runtime.Ready.IsCompletedSuccessfully)
            {
                string? game = SubscriptionGameDirectory;
                int count = game is null ? 0 : runtime.Library.Subscriptions.Where(subscription =>
                    subscription.GameDirectory.Equals(game, SubscriptionPaths.Comparison))
                    .Sum(ModUpdateCheckService.GetUpdateCount);
                UpdateSubscriptionBadge(count);
                RefreshModOrigins();
            }
        });
        runtime.SelectGame(SubscriptionGameDirectory);
        _ = RefreshOriginsWhenReadyAsync(runtime);
    }

    private async Task RefreshOriginsWhenReadyAsync(ModSubscriptionRuntime runtime)
    {
        try
        {
            // The factory returns before the lazy runtime field is assigned.
            await Task.Yield();
            await runtime.Ready;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(subscriptionRuntime, runtime) && !subscriptionShutdownStarted) RefreshModOrigins();
            });
        }
        catch (Exception ex) { AppDiagnostics.Log("Unable to read subscription origins.", ex); }
    }

    private void StartSubscriptionRuntime() => _ = SubscriptionRuntime;
    private void DisposeSubscriptionRuntime() => subscriptionRuntime?.Dispose();
    private void ChangeSubscriptionGame()
    {
        subscriptionRuntime?.SelectGame(SubscriptionGameDirectory);
        RefreshModOrigins();
    }

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
            var available = main.universalVars.modDic;
            // A Workshop Mod can share a local folder identifier. Preserve that separate owner.
            identifiers.RemoveWhere(id => available.TryGetValue(id, out var mod) &&
                !removed.Contains(SubscriptionPaths.Normalize(mod.path)));
            identifiers.RemoveWhere(id => id.StartsWith("mod_", StringComparison.OrdinalIgnoreCase) && id.Length > 4 &&
                main.universalVars.workshopDir is { } workshop && Directory.Exists(Path.Combine(workshop.FullName, id[4..])));

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
            main.ModListWritten();
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
