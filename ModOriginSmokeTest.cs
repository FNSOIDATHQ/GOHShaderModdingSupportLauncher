using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// Shipped offline diagnostic: every file and subscription lives in the self-test fixture.
internal static class ModOriginSmokeTest
{
    internal static void Run(string fixture)
    {
        var previous = LocalizedStrings.Instance.Culture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN" })
                CheckLanguage(Path.Combine(fixture, "mod-origins", language), language);
            AppDiagnostics.Log("Mod origin self-test passed: both languages, three providers, exact paths, Workshop isolation, removal states, restart, offline subscription navigation and manager source columns.");
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
        main.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(game, "binaries", "x64"));
        main.universalVars.localDir = Directory.CreateDirectory(Path.Combine(game, "mods"));
        main.universalVars.workshopDir = Directory.CreateDirectory(Path.Combine(root, "workshop"));
        main.universalVars.optionLoc = Path.Combine(root, "options.set");
        File.WriteAllText(main.universalVars.optionLoc, "{options\n{mods\n\"github:0\"\n\"moddb:0\"\n}\n}\n");
        var store = new SubscriptionStore(Path.Combine(root, "library.json"));
        string Make(string directory)
        {
            Directory.CreateDirectory(Path.Combine(directory, "resource"));
            File.WriteAllText(Path.Combine(directory, "mod.info"), "{mod {name \"Same diagnostic name\"}}\n");
            File.WriteAllText(Path.Combine(directory, "resource", "content.set"), "diagnostic data");
            return directory;
        }
        foreach (string provider in new[] { "moddb", "github", "mowportal" })
        {
            string id = provider switch { "github" => "202", "mowportal" => "203", _ => "201" };
            var mod = new RemoteMod { Id = new(provider, id), Title = "Same diagnostic name",
                PageUrl = new(provider switch { "github" => "https://github.com/diagnostic/origin",
                    "mowportal" => "https://mow-portal.com/call_to_arms_gates_of_hell/203-origin.html", _ => "https://www.moddb.com/mods/origin-diagnostic" }) };
            var file = new RemoteModFile { Id = provider + "-file", Revision = "1", Name = provider + ".zip",
                PageUrl = mod.PageUrl, Description = provider + " saved file notes" };
            store.SaveMod(mod);
            var subscription = store.Subscribe(mod.Id, game, [file]);
            string directory = Make(Path.Combine(game, "mods", provider));
            store.SaveInstallation(subscription.Id, new() { Id = Guid.NewGuid(), DownloadId = subscription.Downloads[0].Id,
                DirectoryPath = directory, OwnedFiles = ["mod.info", "resource/content.set"], State = InstallationState.Installed,
                UpdatedAt = DateTimeOffset.UtcNow });
            store.SaveFileTracking(subscription.Id, new() { HasBaseline = true, KnownFiles = [file] });
        }
        Make(Path.Combine(game, "mods", "manual"));
        Make(Path.Combine(main.universalVars.workshopDir.FullName, "123"));
        var offline = new OfflineSource();
        var runtime = new ModSubscriptionRuntime(new Host(), store, offline);
        ModSubscriptionsPage? page = null;
        try
        {
            Pump(runtime.Ready);
            main.RefreshMods();
            var library = new SubscriptionStore(store.LibraryPath).Snapshot();
            var origins = new ModSubscriptionOrigins(library, game);
            foreach (var mod in main.universalVars.modDic.Values) origins.Apply(mod);
            Mod Local(string directory) => main.universalVars.modDic.Values.Single(mod => mod.path.Equals(Path.Combine(game, "mods", directory), StringComparison.OrdinalIgnoreCase));
            foreach (var (provider, label) in new[] { ("moddb", "ModDB"), ("github", "GitHub"), ("mowportal", "Mow Portal") })
            {
                var local = Local(provider);
                Require(local.SourceLabel == label && local.SubscriptionLink?.Source.Provider == provider && local.Inspection?.Source == ModSourceKind.Local,
                    "An installed provider was not separated from its physical local directory.");
                local.SetAlias("Shared alias");
                Require(origins.Resolve(local)?.Source.Provider == provider, "An alias changed the installation association.");
                var events = new List<string?>();
                local.PropertyChanged += (_, args) => events.Add(args.PropertyName);
                local.SetSubscriptionLink(null); origins.Apply(local);
                Require(events.Contains(nameof(Mod.SourceLabel)) && events.Contains(nameof(Mod.SourceSortKey)), "Origin changes did not notify bound cells.");
            }
            Require(Local("manual").SubscriptionLink is null, "A same-name hand-installed Mod was adopted.");
            main.ModAliases.Save("Same diagnostic name", "Name fallback");
            main.ModAliases.SetItemMapping(true);
            foreach (var project in library.Mods)
            {
                Require(ModPageRouter.TryResolveSupportedPage(project.PageUrl, out var route), "The saved project's URL did not route.");
                var identified = ModAliasItems.DirectItem(project.PageUrl);
                if (identified is null)
                {
                    var lookup = ModAliasItems.ResolveProjectAsync(route!, runtime, CancellationToken.None);
                    Pump(lookup);
                    identified = lookup.GetAwaiter().GetResult();
                }
                var local = Local(project.Id.Provider);
                Require(identified.Key == ModAliasItems.ForMod(local)?.Key, "The saved project URL and installed directory yielded different identities.");
                main.ModAliases.Save(new(identified.Key, project.Id.Provider + " alias") { Kind = ModAliasKind.Item });
                main.ModAliases.Apply(local);
                Require(local.DisplayName == project.Id.Provider + " alias", "A committed installation did not use its own alias.");
            }
            main.ModAliases.Apply(Local("manual"));
            Require(Local("manual").DisplayName == "Name fallback", "A manual install acquired an item alias.");
            var workshop = main.universalVars.modDic.Values.Single(mod => mod.Inspection?.Source == ModSourceKind.Workshop);
            Require(workshop.SubscriptionLink is null && workshop.SourceLabel == Properties.i18n.Main_ModWorkshop, "A Workshop Mod acquired local subscription ownership.");
            Require(new ModSubscriptionOrigins(library, Path.Combine(root, "other-game")).Resolve(Local("moddb")) is null,
                "An installation from another game was associated.");
            var prefix = Local("moddb");
            var nested = new Mod(prefix.name, "", Path.Combine(prefix.path, "nested"), prefix.folderName, false)
            { Inspection = prefix.Inspection! with { DirectoryPath = Path.Combine(prefix.path, "nested") } };
            Require(origins.Resolve(nested) is null, "A nested path matched by prefix.");
            var failed = library with { Subscriptions = library.Subscriptions.Select(sub => sub with
                { Installations = sub.Installations.Select(installation => installation with { State = InstallationState.Failed }).ToArray() }).ToArray() };
            Require(new ModSubscriptionOrigins(failed, game).Resolve(prefix) is null, "A failed installation was treated as committed.");
            var removing = library with { Subscriptions = library.Subscriptions.Select(sub => sub with { State = SubscriptionState.RemovalFailed }).ToArray() };
            Require(new ModSubscriptionOrigins(removing, game).Resolve(prefix) is not null, "A cleanup failure hid an installed origin.");
            var ambiguous = library with { Subscriptions = [.. library.Subscriptions, library.Subscriptions.First() with { Id = Guid.NewGuid() }] };
            string firstPath = library.Subscriptions.First().Installations.Single().DirectoryPath;
            Require(new ModSubscriptionOrigins(ambiguous, game).Resolve(main.universalVars.modDic.Values.Single(mod => mod.path == firstPath)) is null,
                "An ambiguous installation silently chose an owner.");
            new ModSubscriptionOrigins(ambiguous, game).Apply(prefix);
            main.ModAliases.Apply(prefix);
            Require(prefix.DisplayName == "Name fallback", "An ambiguous installation retained a project alias.");
            origins.Apply(prefix);
            var manager = new ModManager(main);
            Require(manager.FindControl<DataGrid>("loadedMods")!.ItemsSource!.Cast<Mod>().Select(mod => mod.folderName).SequenceEqual(new[] { "github", "moddb" }),
                "Origin presentation changed game load order.");
            Require(manager.FindControl<DataGrid>("loadedMods")!.Columns[1] is DataGridTextColumn &&
                manager.FindControl<DataGrid>("unloadedMods")!.Columns[1] is DataGridTextColumn, "A manager source column is missing.");

            page = new(main, runtime);
            foreach (var subscription in library.Subscriptions)
            {
                Pump(page.OpenInstalledSubscriptionAsync(subscription.Id, subscription.Installations.Single().Id));
                Require(Find<TabStrip>(page, "subscriptionSource").SelectedIndex == (subscription.Source.Provider switch { "github" => 1, "mowportal" => 2, _ => 0 }), "Subscription navigation selected the wrong platform.");
                Require(Find<TabControl>(page, "subscriptionDetails").SelectedIndex == 1, "Subscription navigation did not open files.");
                string key = "file:" + subscription.Downloads[0].File.Id;
                Require(Controls(page).OfType<Button>().Any(button => button.Tag as string == key && button.FontWeight == FontWeight.Bold),
                    "Subscription navigation lost the installed source file.");
            }
            Require(offline.Requests == 0 && store.Snapshot().Subscriptions.All(sub => sub.Downloads.All(download => download.State == DownloadState.Pending)),
                "Viewing an installed source requested HTTP or started a download.");
            var before = Find<TabStrip>(page, "subscriptionSource").SelectedIndex;
            main.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(root, "other-game", "binaries", "x64"));
            main.universalVars.localDir = Directory.CreateDirectory(Path.Combine(root, "other-game", "mods"));
            Pump(page.OpenInstalledSubscriptionAsync(library.Subscriptions[0].Id, library.Subscriptions[0].Installations[0].Id));
            Require(Find<TabStrip>(page, "subscriptionSource").SelectedIndex == before, "A stale cross-game navigation changed the page.");
        }
        finally { page?.Dispose(); Pump(runtime.ShutdownAsync()); main.Close(); }
    }

    private static IEnumerable<Control> Controls(Control root)
    {
        var controls = root.GetLogicalDescendants().OfType<Control>().Prepend(root).ToArray();
        return controls.Concat(controls.OfType<TabControl>().SelectMany(tabs => tabs.Items.OfType<TabItem>())
            .Select(item => item.Content).OfType<Control>().SelectMany(content => content.GetLogicalDescendants().OfType<Control>().Prepend(content))).Distinct();
    }
    private static T Find<T>(Control root, string name) where T : Control => Controls(root).OfType<T>().Single(control => control.Name == name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Mod origin self-test: " + message); }
    private static void Pump(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Mod origin diagnostic timed out.");
            Dispatcher.UIThread.RunJobs(); Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
    }
    private sealed class Host : IModInstallationHost
    {
        public bool IsGameRunning(string gameDirectory) => false;
        public void RefreshMods(string gameDirectory) { }
        public void RemoveEnabledMods(string gameDirectory, IReadOnlyList<string> directories) { }
    }
    private sealed class OfflineSource : IModSource
    {
        internal int Requests;
        public string Provider => "moddb";
        public ModSourceCapabilities Capabilities => new();
        private Task<ModSourceResult<T>> NoNetwork<T>() { Requests++; return Task.FromException<ModSourceResult<T>>(new InvalidOperationException("Network disabled in Mod origin diagnostic.")); }
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
