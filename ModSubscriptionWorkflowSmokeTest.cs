using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// Shipped with --self-test-ui. Only the self-test's isolated fixture is modified;
// the source rejects network requests and the host never launches a game.
internal static class ModSubscriptionWorkflowSmokeTest
{
    internal static void Run(string fixture)
    {
        var previous = LocalizedStrings.Instance.Culture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
                CheckLanguage(Path.Combine(fixture, "subscription-workflow", language), language);
            }
            AppDiagnostics.Log("Subscription workflow self-test passed: both languages, automatic checks, legacy downloads, filenames, placement confirmation, conflicts, cancellation and reviewed cleanup.");
        }
        finally { LocalizedStrings.Instance.SetCulture(previous); }
    }

    private static void CheckLanguage(string root, string language)
    {
        Directory.CreateDirectory(root);
        string game = Path.Combine(root, "game");
        var main = new MainWindow(Path.Combine(root, "settings.conf"))
        { SuppressSubscriptionActivation = true, SuppressLauncherUpdates = true };
        LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
        void SelectGame(string path)
        {
            main.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(path, "binaries", "x64"));
            main.universalVars.localDir = Directory.CreateDirectory(Path.Combine(path, "mods"));
        }
        SelectGame(game);
        var store = new SubscriptionStore(Path.Combine(root, "library.json"));
        var mod = new RemoteMod { Id = new("moddb", "workflow-diagnostic"), PageUrl = new("https://www.moddb.com/mods/workflow-diagnostic"), Title = "Workflow diagnostic" };
        store.SaveMod(mod); store.SetFavorite(mod.Id, true);
        RemoteModFile File(string id, string name) => new()
        {
            Id = id, Revision = "fixture-1", PageUrl = new("https://www.moddb.com/mods/workflow-diagnostic/downloads/" + id),
            Name = name, Description = "Author-only diagnostic instructions", DownloadOnly = true, IsSourceSnapshot = true
        };
        var sub = store.Subscribe(mod.Id, game, [File("raw", "Author display title"), File("legacy", "legacy.zip"), File("pending", "pending.zip")]);
        var raw = sub.Downloads[0]; string artifact = ModDownloadService.ArtifactPath(game, raw.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!); System.IO.File.WriteAllText(artifact, "inert diagnostic DLL payload");
        store.SaveDownload(sub.Id, raw with { State = DownloadState.NeedsManualInstall, LocalPath = artifact, FileName = "example.dll",
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(artifact))), UpdatedAt = DateTimeOffset.UtcNow });
        var legacy = sub.Downloads[1]; string archive = ModDownloadService.ArtifactPath(game, legacy.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using (var zip = new ZipArchive(System.IO.File.Create(archive), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("resource/payload.txt").Open()); writer.Write("nonstandard legacy package");
        }
        store.SaveDownload(sub.Id, legacy with { State = DownloadState.Downloaded, LocalPath = archive,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(archive))), UpdatedAt = DateTimeOffset.UtcNow });
        var host = new Host(); var runtime = new ModSubscriptionRuntime(host, store, new OfflineSource());
        ModSubscriptionsPage? page = null;
        try
        {
            Pump(runtime.Ready); page = new(main, runtime); Pump(page.ShowTasksAsync());
            var tabs = Find<TabControl>(page, "subscriptionDetails");
            Require(tabs.Items.Count == 3 && tabs.SelectedIndex == 2 && tabs.Items[2] is TabItem { Name: "subscriptionTasksTab" }, "Downloads must be the third peer tab.");
            Wait(() => runtime.PreparedDownloads.TryGetValue(raw.Id, out var prepared) && !prepared.Checking);
            Require(runtime.PreparedDownloads[raw.Id].Inspection is { ManualInstallReason: null }, "Legacy flags blocked placement.");
            Require(store.GetSubscriptions(game).Single().Installations.Length == 0, "Automatic check installed without confirmation.");
            var workflow = Find<ContentControl>(page, "subscriptionOperation");
            var cards = Controls(workflow).OfType<Border>().Where(item => item.Name == "subscriptionTaskCard").ToArray();
            Require(cards.Length == 3 && cards.Any(item => item.Tag is Guid id && id == raw.Id), "Task identity was lost when removing duplicate headers.");
            Require(cards.All(card => Controls(card).OfType<TextBlock>().Any(text => text.Name == "subscriptionTaskFileName" && !string.IsNullOrEmpty(text.Text))), "A task card is missing its file name.");
            Require(!Controls(workflow).Any(control => control.Name is "subscriptionWorkflowTitle" or "subscriptionBadgeSubscribed" or "subscriptionBadgeFavorited"), "Removed task headers remain visible.");
            Directory.CreateDirectory(Path.Combine(game, "mods", "example"));
            sub = store.GetSubscriptions(game).Single(); raw = sub.Downloads[0]; Pump(page.InstallAsync(sub, raw));
            Require(!Find<Button>(page, "subscriptionConfirmInstall").IsEnabled, "Occupied target was accepted.");
            Require(!Find<Expander>(page, "subscriptionAuthorNotes").IsExpanded, "Author notes were mixed into the steps.");
            Require(Find<Button>(page, "subscriptionWorkflowBack").Content as string == LocalizedStrings.Instance.Get("MS_Back"), "The workflow return action has the wrong caption.");
            var review = workflow.Content;
            tabs.SelectedIndex = 0; tabs.SelectedIndex = 2;
            Require(ReferenceEquals(review, workflow.Content), "Switching reading tabs discarded the installation review.");
            Find<TextBox>(page, "subscriptionInstallDirectory").Text = "diagnostic-content";
            Wait(() => Find<Button>(page, "subscriptionConfirmInstall").IsEnabled && Controls(workflow).OfType<TextBlock>()
                .Any(text => text.Text == LocalizedStrings.Instance.Get("MS_Destination") + " " + Path.Combine(game, "mods", "diagnostic-content")));
            Click(Find<Button>(page, "subscriptionConfirmInstall"));
            tabs.SelectedIndex = 0;
            Wait(() => store.GetSubscriptions(game).Single().Installations.Length == 1);
            Wait(() => HasText(workflow, "PlacementComplete"));
            Require(tabs.SelectedIndex == 0, "A background installation switched away from reading.");
            tabs.SelectedIndex = 2;
            Wait(() => HasText(page, "PlacementComplete"));
            string target = store.GetSubscriptions(game).Single().Installations.Single().DirectoryPath;
            Require(System.IO.File.ReadAllText(Path.Combine(target, "example.dll")) == "inert diagnostic DLL payload" && host.Refreshes == 1, "Filename or file content changed.");
            sub = store.GetSubscriptions(game).Single();
            Pump(page.InstallAsync(sub, sub.Downloads.Single(item => item.Id == legacy.Id)));
            Find<TextBox>(page, "subscriptionInstallDirectory").Text = "diagnostic-secondary";
            Wait(() => Find<Button>(page, "subscriptionConfirmInstall").IsEnabled && Controls(workflow).OfType<TextBlock>()
                .Any(text => text.Text == LocalizedStrings.Instance.Get("MS_Destination") + " " + Path.Combine(game, "mods", "diagnostic-secondary")));
            Click(Find<Button>(page, "subscriptionConfirmInstall"));
            Wait(() => store.GetSubscriptions(game).Single().Installations.Length == 2);
            Wait(() => HasText(workflow, "PlacementComplete"));
            Pump(page.ShowTasksAsync());
            var legacyCard = Controls(workflow).OfType<Border>().Single(card => card.Name == "subscriptionTaskCard" && card.Tag is Guid id && id == legacy.Id);
            Click(Find<Button>(legacyCard, "subscriptionUnsubscribeFile"));
            Wait(() => Controls(workflow).OfType<Button>().Any(button => button.Name == "subscriptionConfirmRemoval"));
            Require(LocalizedStrings.Instance.Get("MS_CleanupBoundary").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3, "Cleanup instructions must use separate lines.");
            Click(Find<Button>(workflow, "subscriptionConfirmRemoval"));
            Wait(() => !store.GetSubscriptions(game).Single().Downloads.Any(item => item.Id == legacy.Id));
            Wait(() => HasText(workflow, "CleanupComplete"));
            var remaining = store.GetSubscriptions(game).Single();
            Require(remaining.State == SubscriptionState.Active && remaining.Downloads.Length == 2 && remaining.Downloads.Last().State == DownloadState.Pending &&
                remaining.Installations.Single().DirectoryPath == target && System.IO.File.Exists(artifact) && !System.IO.File.Exists(archive) &&
                !Directory.Exists(Path.Combine(game, "mods", "diagnostic-secondary")), "File cleanup affected another task or the Mod subscription.");
            string manual = Path.Combine(target, "manual.txt"); System.IO.File.WriteAllText(manual, "preserve");
            Pump(page.ReviewUnsubscribeAsync(sub.Id)); Click(Find<Button>(page, "subscriptionStopTasks"));
            Wait(() => Controls(page).OfType<Button>().Any(button => button.Name == "subscriptionConfirmRemoval"));
            Require(store.GetSubscriptions(game).Single().Downloads.Last().State == DownloadState.Cancelled && System.IO.File.Exists(artifact), "Stopping tasks deleted content before confirmation.");
            var obsolete = Find<Button>(page, "subscriptionConfirmRemoval");
            SelectGame(Path.Combine(root, "other-game")); page.OnGameChanged(); Click(obsolete);
            Require(store.GetSubscriptions(game).Length == 1, "A cross-game confirmation remained active.");
            SelectGame(game); page.OnGameChanged(); Pump(page.ReviewUnsubscribeAsync(sub.Id));
            Click(Find<Button>(page, "subscriptionConfirmRemoval"));
            Wait(() => store.GetSubscriptions(game).Length == 0); Wait(() => HasText(page, "CleanupComplete"));
            Require(System.IO.File.ReadAllText(manual) == "preserve" && store.Snapshot().Favorites.Length == 1 && !System.IO.File.Exists(artifact), "Cleanup lost a manual file or favorite.");
            Require(RestartedLibrary(store.LibraryPath).Favorites.Length == 1, "Favorite did not survive restart.");
            // Stopped tasks remain independently removable, including ones without a payload.
            var stopped = store.Subscribe(mod.Id, game, [File("failed", "failed.zip"), File("cancelled", "cancelled.zip")]);
            store.SaveDownload(stopped.Id, stopped.Downloads[0] with { State = DownloadState.Failed });
            store.SaveDownload(stopped.Id, stopped.Downloads[1] with { State = DownloadState.Cancelled });
            Pump(runtime.RefreshLibraryAsync()); Pump(page.ShowTasksAsync());
            foreach (var download in stopped.Downloads)
            {
                var card = Controls(workflow).OfType<Border>().Single(item => item.Name == "subscriptionTaskCard" && item.Tag is Guid id && id == download.Id);
                Click(Find<Button>(card, "subscriptionUnsubscribeFile"));
                Wait(() => Controls(workflow).OfType<Button>().Any(button => button.Name == "subscriptionConfirmRemoval"));
                Click(Find<Button>(workflow, "subscriptionConfirmRemoval"));
                Wait(() => !store.GetSubscriptions(game).Any(item => item.Downloads.Any(task => task.Id == download.Id)));
                Wait(() => HasText(workflow, "CleanupComplete"));
                Pump(page.ShowTasksAsync());
            }
            Require(store.GetSubscriptions(game).Length == 0 && runtime.Library.Subscriptions.Length == 0 &&
                store.Snapshot().Favorites.Length == 1, "Last-file cleanup retained an empty subscription or removed its favorite.");
            // Finish background recovery previews before opening an independent store.
            page.Dispose(); Pump(runtime.ShutdownAsync());
            var restarted = RestartedLibrary(store.LibraryPath);
            Require(restarted.Subscriptions.Length == 0 && restarted.Favorites.Length == 1, "Last-file cleanup or favorite did not survive restart.");
        }
        finally { page?.Dispose(); Pump(runtime.ShutdownAsync()); main.Close(); }
    }

    // An unattached TabControl need not materialize its selected-content presenter yet.
    private static IEnumerable<Control> Controls(Control root)
    {
        var controls = root.GetLogicalDescendants().OfType<Control>().Prepend(root).ToArray();
        return controls.Concat(controls.OfType<TabControl>().SelectMany(tabs => tabs.Items.OfType<TabItem>())
            .Select(item => item.Content).OfType<Control>()
            .SelectMany(content => content.GetLogicalDescendants().OfType<Control>().Prepend(content))).Distinct();
    }
    private static bool HasText(Control root, string key) => Controls(root).OfType<TextBlock>().Any(text => text.Text == LocalizedStrings.Instance.Get("MS_" + key));
    private static T Find<T>(Control root, string name) where T : Control => Controls(root).OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static SubscriptionLibrary RestartedLibrary(string path)
    {
        SubscriptionLibrary? snapshot = null;
        Wait(() =>
        {
            try { snapshot = new SubscriptionStore(path).Snapshot(); return true; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return false; }
        });
        return snapshot!;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Subscription workflow diagnostic timed out.");
            Dispatcher.UIThread.RunJobs(); Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Host : IModInstallationHost
    {
        internal int Refreshes;
        public bool IsGameRunning(string gameDirectory) => false;
        public void RefreshMods(string gameDirectory) => Refreshes++;
        public void RemoveEnabledMods(string gameDirectory, IReadOnlyList<string> directories) { }
    }

    private sealed class OfflineSource : IModSource
    {
        public string Provider => "moddb";
        public ModSourceCapabilities Capabilities => new();
        private static Task<ModSourceResult<T>> NoNetwork<T>() => Task.FromException<ModSourceResult<T>>(new InvalidOperationException("Network access is disabled in this diagnostic."));
        public Task<ModSourceResult<ModSourcePage<RemoteMod>>> BrowseAsync(string keyword = "", int page = 1, ModBrowseSort sort = ModBrowseSort.Updated, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModSourcePage<RemoteMod>>();
        public Task<ModSourceResult<RemoteMod>> GetModAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<RemoteMod>();
        public Task<ModSourceResult<ModSourcePage<ModArticle>>> GetArticlesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModSourcePage<ModArticle>>();
        public Task<ModSourceResult<ModArticle>> GetArticleAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModArticle>();
        public Task<ModSourceResult<ModSourcePage<ModImageReference>>> GetImagesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModSourcePage<ModImageReference>>();
        public Task<ModSourceResult<ModImage>> GetImageAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModImage>();
        public Task<ModSourceResult<ModSourcePage<ModFileReference>>> GetFilesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModSourcePage<ModFileReference>>();
        public Task<ModSourceResult<RemoteModFile>> GetFileAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<RemoteModFile>();
        public Task<ModSourceResult<ModFileSnapshot>> GetFileSnapshotAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModFileSnapshot>();
        public Task<ModSourceResult<ModFileHints>> ReadFileHintsAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) => NoNetwork<ModFileHints>();
        public Task<ModSourceResult<ModDownloadLink>> ResolveDownloadAsync(Uri page, CancellationToken cancellationToken = default) => NoNetwork<ModDownloadLink>();
    }
}
