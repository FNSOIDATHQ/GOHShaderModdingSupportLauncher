using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.Properties;
using GOHShaderModdingSupportLauncher.ModSubscriptions;
using System.ComponentModel;
using System.Globalization;

namespace GOHShaderModdingSupportLauncher;

internal static class UiSmokeTest
{
    // RAR5 (store), containing shader/dx10/example.fx with the text "fixture".
    internal static byte[] CreateRarShaderFixture() => Convert.FromBase64String(
        "UmFyIRoHAQAzkrXlCgEFBgAFAQGAgAAGX6fjMgIDC4cABIcAIO5A5QWAAAAWc2hhZGVyL2R4MTAvZXhhbXBsZS5meAoDAm5YEHghWt0BZml4dHVyZWcIMVwhAgMLAAEAEIAAAAtzaGFkZXIvZHgxMAoDAm5YEHghWt0BSKlEohwCAwsAAQAQgAAABnNoYWRlcgoDAjCVD3ghWt0BHXdWUQMFBAA=");

    /// <summary>
    /// Exercises compiled AXAML and DataGrid under the actual desktop platform without touching game files.
    /// </summary>
    public static int Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "GOHSMSLauncher-UiCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            if (Application.Current?.Styles.Count != 2)
                throw new InvalidOperationException("Avalonia theme or DataGrid styles did not load.");
            CheckSubscriptionMedia();
            var mods = Directory.CreateDirectory(Path.Combine(fixture, "mods"));
            foreach (var name in new[] { "alpha", "beta", "zeta", "gamma" })
            {
                var directory = Directory.CreateDirectory(Path.Combine(mods.FullName, name));
                File.WriteAllText(Path.Combine(directory.FullName, "mod.info"), "{name \"" + name + "\"}\n");
                if (name == "gamma")
                {
                    var resource = Directory.CreateDirectory(Path.Combine(directory.FullName, "resource"));
                    var pak = Path.Combine(resource.FullName, "shader.pak");
                    File.WriteAllBytes(pak, CreateRarShaderFixture());
                    if (FileManager.TryReadPakEntries(pak) is not [{ FullName: "shader/dx10/example.fx", Length: 7 }])
                        throw new InvalidOperationException("RAR pak metadata did not load.");
                }
            }
            var workshop = Directory.CreateDirectory(Path.Combine(fixture, "workshop"));
            var workshopMod = Directory.CreateDirectory(Path.Combine(workshop.FullName, "123"));
            File.WriteAllText(Path.Combine(workshopMod.FullName, "mod.info"), "{name \"omega\"}\n");
            var options = Path.Combine(fixture, "options.set");
            File.WriteAllText(options, "{mods\r\n\"alpha:0\"\r\n\"beta:0\"\r\n}\r\n");

            var settingsPath = Path.Combine(fixture, "settings.conf");
            string[] settings =
            [
                "FileReplace", "True", "True", "False", "False", "True", "True",
                "False", "False", "False", "True", "-1", "0", "en-US"
            ];
            FileManager.WriteSettings(settingsPath, [.. settings, fixture, fixture]);
            if (FileManager.ReadSettings(settingsPath).Length != 16)
                throw new InvalidOperationException("Settings with language and cached paths were rejected.");
            settings[13] = "fr-FR";
            FileManager.WriteSettings(settingsPath, settings);
            var invalidLanguageRejected = false;
            try { FileManager.ReadSettings(settingsPath); }
            catch (FormatException) { invalidLanguageRejected = true; }
            if (!invalidLanguageRejected)
                throw new InvalidOperationException("Unsupported settings language was accepted.");
            settings[13] = "en-US";
            FileManager.WriteSettings(settingsPath, settings);
            var window = new MainWindow(settingsPath) { SuppressSubscriptionActivation = true };
            window.universalVars.configLoc = settingsPath;
            var layout = window.FindControl<Grid>("windowLayout")
                ?? throw new InvalidOperationException("Main window layout is missing.");
            var titleBar = window.FindControl<Border>("titleBar")
                ?? throw new InvalidOperationException("Custom title bar is missing.");
            var minimizeButton = window.FindControl<Button>("minimizeButton")
                ?? throw new InvalidOperationException("Minimize button is missing.");
            var maximizeButton = window.FindControl<Button>("maximizeButton")
                ?? throw new InvalidOperationException("Maximize button is missing.");
            var closeButton = window.FindControl<Button>("closeButton")
                ?? throw new InvalidOperationException("Close button is missing.");
            var resizeGrips = window.FindControl<Grid>("resizeGrips")
                ?? throw new InvalidOperationException("Resize grips are missing.");
            var splitView = window.FindControl<SplitView>("mainView")
                ?? throw new InvalidOperationException("Navigation sidebar is missing.");
            if (splitView.DisplayMode != SplitViewDisplayMode.CompactInline || splitView.OpenPaneLength != 240 || splitView.CompactPaneLength != 52 ||
                window.ExtendClientAreaToDecorationsHint || !window.CanResize ||
                window.WindowDecorations != WindowDecorations.None ||
                resizeGrips.Children.Count != 8 || !resizeGrips.IsVisible ||
                layout.RowDefinitions[0].Height.Value != 56 ||
                layout.Background is not ImageBrush
                {
                    Stretch: Stretch.UniformToFill,
                    AlignmentX: AlignmentX.Left,
                    AlignmentY: AlignmentY.Top,
                    Source: not null
                } ||
                titleBar.Background is not SolidColorBrush titleBrush ||
                splitView.PaneBackground is not SolidColorBrush paneBrush ||
                titleBrush.Color != paneBrush.Color || titleBrush.Color.A == 255)
                throw new InvalidOperationException("Window background, title bar, or sidebar layout is incorrect.");
            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.WindowState != WindowState.Maximized || resizeGrips.IsVisible ||
                maximizeButton.Content as string != "\uE923" ||
                minimizeButton.Parent is null || maximizeButton.Parent != minimizeButton.Parent ||
                closeButton.Parent != minimizeButton.Parent)
                throw new InvalidOperationException("Maximizing removed the custom title bar controls.");
            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.WindowState != WindowState.Normal || !resizeGrips.IsVisible ||
                maximizeButton.Content as string != "\uE922")
                throw new InvalidOperationException("Restoring the window did not restore its title bar.");
            var material = "{material simple\r\n}\r\n";
            if (!Converter.EnableEnvInMTLFile(ref material) ||
                !material.Contains("{material bump", StringComparison.Ordinal) ||
                !material.Contains("{parallax_scale 1000}", StringComparison.Ordinal) ||
                Converter.EnableEnvInMTLFile(ref material))
                throw new InvalidOperationException("Material converter failed.");
            var lzma = Path.Combine(fixture, "shader.lzma");
            var restoredPak = Path.Combine(fixture, "shader.pak");
            MainWindow.ExtractFile("GOHShaderModdingSupportLauncher.pak.Ori.shader.lzma", lzma, 358400);
            window.DecompressFileLZMA(lzma, restoredPak);
            if (new FileInfo(restoredPak).Length <= 7 * 1048576)
                throw new InvalidOperationException("Original game shader resource did not restore.");
            window.universalVars.gameDir = new DirectoryInfo(fixture);
            window.universalVars.resourceDir = new DirectoryInfo(fixture);
            window.universalVars.localDir = mods;
            window.universalVars.workshopDir = workshop;
            window.universalVars.profileLoc = fixture;
            window.universalVars.cacheLoc = Path.Combine(fixture, "shader_cache");
            window.universalVars.optionLoc = options;
            var primary = window.FindControl<ListBox>("primaryNavigation")
                ?? throw new InvalidOperationException("Primary navigation is missing.");
            var footer = window.FindControl<ListBox>("footerNavigation")
                ?? throw new InvalidOperationException("Footer navigation is missing.");
            var host = window.FindControl<ContentControl>("pageHost")
                ?? throw new InvalidOperationException("Page host is missing.");
            if (primary.Items.Count != 5 || footer.Items.Count != 2)
                throw new InvalidOperationException("Seven navigation entries were expected.");
            Type[] expected = [typeof(Launcher), typeof(ModManager), typeof(ModSubscriptionsPage),
                typeof(Converter), typeof(Tools), typeof(Settings), typeof(About)];
            for (var i = 0; i < expected.Length; i++)
            {
                if (i < primary.Items.Count) primary.SelectedIndex = i;
                else footer.SelectedIndex = i - primary.Items.Count;
                if (host.Content?.GetType() != expected[i])
                    throw new InvalidOperationException($"Navigation page {i} failed to load.");
                if (i == 1)
                {
                    var manager = (ModManager)host.Content;
                    var grid = manager.FindControl<DataGrid>("loadedMods");
                    if (grid?.Columns.Count != 3 || grid.ItemsSource?.Cast<Mod>().Count() != 2)
                        throw new InvalidOperationException("Compiled mod table failed to load.");
                    var unload = manager.FindControl<Button>("unloadMod")!;
                    unload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (window.universalVars.modLoaded.Count != 1)
                        throw new InvalidOperationException("Mod unload failed.");
                    var unloaded = manager.FindControl<DataGrid>("unloadedMods")!;
                    unloaded.ApplyTemplate();
                    unloaded.Measure(new Size(760, 500));
                    unloaded.Arrange(new Rect(0, 0, 760, 500));
                    Dispatcher.UIThread.RunJobs();
                    var sortEventCount = 0;
                    unloaded.Sorting += (_, _) => sortEventCount++;
                    if (!unloaded.CanUserSortColumns || grid.CanUserSortColumns ||
                        unloaded.Columns.Any(column => !column.CanUserSort) ||
                        unloaded.ItemsSource is not DataGridCollectionView || unloaded.ItemsSource.Cast<Mod>().Count() != 4)
                        throw new InvalidOperationException($"Mod table sort configuration failed: enabled={unloaded.CanUserSortColumns}, count={unloaded.ItemsSource?.Cast<Mod>().Count()}.");
                    foreach (var columnIndex in new[] { 0, 1, 2 })
                    {
                        Comparison<Mod> compare = columnIndex switch
                        {
                            0 => (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.name, right.name),
                            1 => (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.type, right.type),
                            _ => (left, right) => left.hasShader.CompareTo(right.hasShader)
                        };
                        unloaded.Columns[columnIndex].Sort(ListSortDirection.Ascending);
                        Dispatcher.UIThread.RunJobs();
                        var ascending = unloaded.ItemsSource!.Cast<Mod>().ToArray();
                        unloaded.Columns[columnIndex].Sort(ListSortDirection.Descending);
                        Dispatcher.UIThread.RunJobs();
                        var descending = unloaded.ItemsSource!.Cast<Mod>().ToArray();
                        if (ascending.Length != 4 || descending.Length != 4 ||
                            ascending.Zip(ascending.Skip(1)).Any(pair => compare(pair.First, pair.Second) > 0) ||
                            descending.Zip(descending.Skip(1)).Any(pair => compare(pair.First, pair.Second) < 0) ||
                            compare(ascending[0], descending[0]) >= 0)
                            throw new InvalidOperationException($"Unloaded mod column {columnIndex} did not sort both ways: ascending={string.Join(',', ascending.Select(mod => mod.name))}, descending={string.Join(',', descending.Select(mod => mod.name))}.");
                    }
                    if (sortEventCount != 6)
                        throw new InvalidOperationException($"Column header sorting raised {sortEventCount} events instead of six.");
                    unloaded.SelectedIndex = 0;
                    manager.FindControl<Button>("loadMod")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (window.universalVars.modLoaded.Count != 2)
                        throw new InvalidOperationException($"Mod load failed: loaded={window.universalVars.modLoaded.Count}, unloaded selection={unloaded.SelectedIndex}, loaded selection={grid.SelectedIndex}.");
                    if (!manager.MoveLoadedMod(window.universalVars.modLoaded[1], window.universalVars.modLoaded[0]))
                        throw new InvalidOperationException("Mod reorder failed.");
                    using var optionReader = File.OpenText(options);
                    var names = FileManager.ReadLoadedModNames(optionReader).ToArray();
                    if (!names.SequenceEqual(window.universalVars.modLoaded.Select(mod => mod.folderName)))
                        throw new InvalidOperationException("Reordered mods were not saved to options.set.");
                    optionReader.Dispose();
                    PresetUiSmokeTest.Run(manager, window, fixture);
                }
                if (i == 3)
                {
                    var tabs = ((Converter)host.Content).FindControl<TabControl>("Tabs");
                    if (tabs?.TabStripPlacement != Dock.Left)
                        throw new InvalidOperationException("Converter tabs are not stacked on the left.");
                    ((Converter)host.Content).FindControl<TextBox>("convertPath")!.Text = "keep this path";
                }
            }
            // Routing follows stable tags even if an entry moves in the sidebar.
            var subscriptionsItem = primary.Items.OfType<ListBoxItem>()
                .Single(item => item.Tag as string == "ModSubscriptions");
            var subscriptionsPage = default(ModSubscriptionsPage);
            primary.SelectedItem = subscriptionsItem;
            subscriptionsPage = host.Content as ModSubscriptionsPage
                ?? throw new InvalidOperationException("Subscriptions page was not cached.");
            var navigationToggle = window.FindControl<Button>("navigationToggle")!;
            navigationToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (splitView.IsPaneOpen || !window.InterfacePreferences.NavigationCollapsed ||
                !ReferenceEquals(host.Content, subscriptionsPage) ||
                !window.FindControl<Button>("languageButton")!.IsVisible ||
                string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(navigationToggle)))
                throw new InvalidOperationException("Collapsing the sidebar lost its route or accessible controls.");
            var restoredPreferences = new UiPreferencesStore(Path.Combine(fixture, "ui-preferences.json")).Load();
            if (!restoredPreferences.NavigationCollapsed)
                throw new InvalidOperationException("Sidebar preference did not persist.");
            navigationToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!splitView.IsPaneOpen || window.InterfacePreferences.NavigationCollapsed)
                throw new InvalidOperationException("Sidebar did not expand again.");
            // Inspect both tab contents explicitly: the desktop presenter attaches only the active tab.
            var readingSurfaces = subscriptionsPage.GetLogicalDescendants().OfType<TabControl>().Single()
                .Items.OfType<TabItem>().Select(item => item.Content).OfType<Control>()
                .SelectMany(content => content.GetLogicalDescendants()).OfType<Border>()
                .Where(control => control.Name is "subscriptionReaderSurface" or "subscriptionFileReadingSurface").ToArray();
            if (readingSurfaces.Length != 2 || readingSurfaces.Any(surface => surface.Padding.Left != 16 || surface.Padding.Right != 16 ||
                surface.Padding.Top is not (8 or 16) || surface.Padding.Bottom != surface.Padding.Top ||
                surface.Background is not SolidColorBrush { Color: { R: 36, G: 42, B: 51, A: 255 } }))
                throw new InvalidOperationException($"Subscription readers did not load their shared opaque surface: found {readingSurfaces.Length}.");
            CheckSubscriptionDocument(subscriptionsPage);
            var subscriptionViews = subscriptionsPage.GetLogicalDescendants().OfType<ComboBox>()
                .Single(control => control.Name == "subscriptionView");
            var subscriptionTabs = subscriptionsPage.GetLogicalDescendants().OfType<TabControl>()
                .Single(control => control.Name == "subscriptionDetails");
            var subscriptionSources = subscriptionsPage.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.TabStrip>()
                .Single(control => control.Name == "subscriptionSource");
            if (subscriptionViews.Items.Count != 4 || subscriptionTabs.Items.Count != 2 || subscriptionSources.Items.Count != 3 ||
                primary.Items.IndexOf(subscriptionsItem) != primary.Items.OfType<ListBoxItem>().ToList().FindIndex(item => item.Tag as string == "ModManager") + 1 ||
                subscriptionsPage.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "subscriptionStatus").Text != LocalizedStrings.Instance.Get("MS_Ready") ||
                subscriptionsPage.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "subscriptionUnsubscribe").IsVisible)
                throw new InvalidOperationException("Subscriptions views or detail tabs did not load.");
            primary.Items.Remove(subscriptionsItem);
            primary.Items.Insert(0, subscriptionsItem);
            primary.SelectedItem = subscriptionsItem;
            if (!ReferenceEquals(host.Content, subscriptionsPage))
                throw new InvalidOperationException("Moving a navigation item changed its page route.");
            primary.Items.Remove(subscriptionsItem);
            primary.Items.Insert(2, subscriptionsItem);
            window.UpdateSubscriptionBadge(2);
            var previous = i18n.Culture;
            try
            {
                i18n.Culture = CultureInfo.GetCultureInfo("en-US");
                var english = i18n.M_Load;
                i18n.Culture = CultureInfo.GetCultureInfo("zh-CN");
                if (english == i18n.M_Load || string.IsNullOrWhiteSpace(i18n.M_Load))
                    throw new InvalidOperationException("Embedded Chinese strings did not load.");
            }
            finally { i18n.Culture = previous; }
            var languageSelector = window.FindControl<ComboBox>("languageSelector")
                ?? throw new InvalidOperationException("Sidebar language selector is missing.");
            if (languageSelector.SelectedIndex != 0)
                throw new InvalidOperationException("Saved English language was not selected.");
            languageSelector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            if (window.Title != i18n.Title || window.Title is null ||
                NavigationLabel((ListBoxItem)primary.Items[0]!) != i18n.Tab_Launcher ||
                NavigationLabel((ListBoxItem)footer.Items[0]!) != i18n.Tab_Settings ||
                NavigationLabel(subscriptionsItem) != LocalizedStrings.Instance.Tab_ModSubscriptions + " (2)" ||
                (FileManager.ReadSettings(settingsPath).Length != 17 || FileManager.ReadSettings(settingsPath)[13] != "zh-CN"))
                throw new InvalidOperationException($"Chinese language did not update or persist: title={window.Title}, expectedTitle={i18n.Title}, nav={((ListBoxItem)primary.Items[0]!).Content}, expectedNav={i18n.Tab_Launcher}, footer={((ListBoxItem)footer.Items[0]!).Content}, expectedFooter={i18n.Tab_Settings}, saved={FileManager.ReadSettings(settingsPath)[13]}.");
            primary.SelectedIndex = 0;
            var gameButton = ((Launcher)host.Content!).FindControl<Button>("game")!;
            if (gameButton.Content as string != i18n.L_Main)
                throw new InvalidOperationException("Cached launcher page did not change language.");
            primary.SelectedIndex = 1;
            var modGrid = ((ModManager)host.Content!).FindControl<DataGrid>("unloadedMods")!;
            if (modGrid.Columns[0].Header as string != i18n.M_GridName ||
                window.universalVars.modDic["alpha"].type != i18n.Main_ModLocal ||
                window.universalVars.modDic["mod_123"].type != i18n.Main_ModWorkshop)
                throw new InvalidOperationException("Cached mod table headers or type labels did not change language.");
            primary.SelectedIndex = 3;
            var conversionPath = ((Converter)host.Content!).FindControl<TextBox>("convertPath")!;
            if (conversionPath.Text != "keep this path")
                throw new InvalidOperationException("Language switching discarded converter input.");
            var restoredWindow = new MainWindow(settingsPath);
            if (restoredWindow.FindControl<ComboBox>("languageSelector")?.SelectedIndex != 1 ||
                restoredWindow.Title != i18n.Title)
                throw new InvalidOperationException("A new window did not restore the selected language.");
            primary.SelectedIndex = 1;
            languageSelector.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            if ((FileManager.ReadSettings(settingsPath).Length != 17 || FileManager.ReadSettings(settingsPath)[13] != "en-US") ||
                gameButton.Content as string != i18n.L_Main ||
                window.universalVars.modDic["alpha"].type != i18n.Main_ModLocal ||
                window.universalVars.modDic["mod_123"].type != i18n.Main_ModWorkshop)
                throw new InvalidOperationException("Switching the visible mod page back to English failed.");
            Directory.CreateDirectory(window.universalVars.cacheLoc);
            File.WriteAllText(Path.Combine(window.universalVars.cacheLoc, "fixture.txt"), "cache");
            window.ClearCacheWork();
            if (Directory.Exists(window.universalVars.cacheLoc))
                throw new InvalidOperationException("Shader cache tool failed.");
            var manualGameRoot = Directory.CreateDirectory(Path.Combine(fixture, "manual-game"));
            var manualGame = Directory.CreateDirectory(Path.Combine(manualGameRoot.FullName, "binaries", "x64"));
            Directory.CreateDirectory(Path.Combine(manualGameRoot.FullName, "resource"));
            File.WriteAllText(Path.Combine(manualGame.FullName, "call_to_arms.exe"), "fixture");
            var manualProfile = Directory.CreateDirectory(Path.Combine(fixture, "manual-profile"));
            var manualAccount = Directory.CreateDirectory(Path.Combine(manualProfile.FullName, "profiles", "account"));
            File.WriteAllText(Path.Combine(manualAccount.FullName, "options.set"), "{mods\n}\n");
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                if (!window.TrySetManualGameDirectory(manualGame.FullName) ||
                    !window.TrySetManualProfileDirectory(manualProfile.FullName) ||
                    window.TrySetManualGameDirectory(fixture) ||
                    window.TrySetManualProfileDirectory(fixture))
                    throw new InvalidOperationException("Manual path validation rejected a valid folder or accepted an invalid one.");
                var savedPaths = FileManager.ReadSettings(settingsPath);
                if (savedPaths.Length != 17 || savedPaths[^2] != manualGame.FullName ||
                    savedPaths[^1] != manualProfile.FullName ||
                    window.universalVars.gameDir?.FullName != manualGame.FullName ||
                    window.universalVars.profileLoc != manualProfile.FullName)
                    throw new InvalidOperationException("A rejected manual path replaced the saved game or profile folder.");
                var secondAccount = Directory.CreateDirectory(Path.Combine(manualProfile.FullName, "profiles", "second-account"));
                var secondOptions = Path.Combine(secondAccount.FullName, "options.set");
                File.WriteAllText(secondOptions, "{mods\n}\n");
                if (!window.TrySetManualProfileDirectory(manualProfile.FullName))
                    throw new InvalidOperationException("Profile rescan failed.");
                footer.SelectedIndex = 0;
                var accountSelector = ((Settings)host.Content!).FindControl<ComboBox>("profileAccount")
                    ?? throw new InvalidOperationException("Profile account selector is missing.");
                if (accountSelector.Items.Count != 2)
                    throw new InvalidOperationException("Profile scan did not retain both valid accounts.");
                accountSelector.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                savedPaths = FileManager.ReadSettings(settingsPath);
                if (window.universalVars.optionLoc != secondOptions || savedPaths[14] != secondOptions)
                    throw new InvalidOperationException("Profile account selection did not update or persist.");
                savedPaths[8] = bool.TrueString;
                FileManager.WriteSettings(settingsPath, savedPaths);
                var reopened = new MainWindow(settingsPath);
                reopened.LoadConfigFromFile();
                if (!reopened.universalVars.AlwaysConfirm ||
                    reopened.universalVars.gameDir?.FullName != manualGame.FullName ||
                    reopened.universalVars.profileLoc != manualProfile.FullName ||
                    reopened.universalVars.optionLoc != secondOptions)
                    throw new InvalidOperationException("Restart ignored the selected profile account or a valid manual path when confirmation was enabled.");
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
            }
            subscriptionsPage.Dispose();
            AppDiagnostics.Log("Desktop UI self-test passed: seven pages with stable collapsible navigation and persisted preferences, shared reading surfaces, subscription notices, two languages, mod presets, load/unload/reorder and column sorting, left converter tabs, matching translucent title bar and sidebar, fitted background, resource restore, cache cleanup, and profile account selection/persistence.");
            return 0;
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(fixture).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI fixture path escaped the temporary directory.");
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static string? NavigationLabel(ListBoxItem item) => item.GetLogicalDescendants().OfType<TextBlock>()
        .Single(control => control.Classes.Contains("navigationLabel")).Text;

    private static void CheckSubscriptionMedia()
    {
        using var asset = Avalonia.Platform.AssetLoader.Open(new("avares://GOHShaderModdingSupportLauncher/img/logo.png"));
        using var bytes = new MemoryStream(); asset.CopyTo(bytes);
        var payload = new ModImagePayload(bytes.ToArray(), new("https://media.moddb.com/self-check.png"), "image/png");
        using var thumbnail = ModImageDecoder.Decode(payload, 360, ModMediaOperation.Thumbnail, CancellationToken.None);
        using var full = ModImageDecoder.Decode(payload, 1600, ModMediaOperation.Original, CancellationToken.None);
        if (thumbnail.PixelSize.Width <= 0 || thumbnail.PixelSize.Width > 360 || full.PixelSize.Width <= 0)
            throw new InvalidOperationException("Subscription image decoding failed.");
        try
        {
            using var invalid = ModImageDecoder.Decode(payload with { Bytes = [1, 2, 3] }, 360,
                ModMediaOperation.Thumbnail, CancellationToken.None);
            throw new InvalidOperationException("Invalid image content was accepted.");
        }
        catch (ModSourceException ex) when (ex.Failure.DiagnosticKind == ModHttpFailureKind.ImageDecode) { }
        AppDiagnostics.Log("Subscription media self-test passed: thumbnail/full decoding and invalid raster rejection.");
    }

    private static void CheckSubscriptionDocument(ModSubscriptionsPage page)
    {
        var document = GitHubParser.ReadmeDocument("Before\n\n![Caption](https://raw.githubusercontent.com/example/mod/main/picture.png)\n\nAfter [guide](https://example.org/guide)",
            new("https://github.com/example/mod"), "main", "README.md");
        string json = System.Text.Json.JsonSerializer.Serialize(document, ModContentJsonContext.Default.ModReadingDocument);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, ModContentJsonContext.Default.ModReadingDocument)!;
        var panel = new StackPanel(); page.AddDocument(panel, restored);
        if (!ModDocumentParser.HasDocument(restored) || restored.Blocks is not [{ Kind: ModBlockKind.Paragraph }, { Kind: ModBlockKind.Image }, { Kind: ModBlockKind.Paragraph }] ||
            panel.Children.Count != 3 || panel.Children[0] is not SelectableTextBlock ||
            !panel.Children[1].GetLogicalDescendants().OfType<Image>().Any() ||
            !panel.Children[2].GetLogicalDescendants().OfType<HyperlinkButton>().Any())
            throw new InvalidOperationException("Ordered subscription document serialization or rendering failed.");
        AppDiagnostics.Log("Subscription document self-test passed: source-generated JSON, paragraph/image order and inline links.");
        var anchored = GitHubParser.ReadmeDocument("## Install guide\n\n[Jump](#install-guide)", new("https://github.com/example/mod"), "main", "README.md");
        string anchorJson = System.Text.Json.JsonSerializer.Serialize(anchored, ModContentJsonContext.Default.ModReadingDocument);
        var anchorCopy = System.Text.Json.JsonSerializer.Deserialize(anchorJson, ModContentJsonContext.Default.ModReadingDocument)!;
        if (!anchorCopy.Blocks[0].Anchors.Contains("install-guide") || anchorCopy.Blocks.Last().Runs.Single(run => run.Link is not null).Link?.AbsoluteUri != "https://github.com/example/mod#install-guide" ||
            !ModPageRouter.TryResolveSupportedPage(new("https://www.moddb.com/mods/example/news/guide"), out var articleRoute) || articleRoute?.Kind != ModPageKind.Article ||
            !ModPageRouter.TryResolveSupportedPage(new("https://github.com/example/mod/releases/tag/v1"), out var releaseRoute) || releaseRoute?.Kind != ModPageKind.Release ||
            !ModPageRouter.TryResolveSupportedPage(new("https://mow-portal.com/call_to_arms_gates_of_hell/page/2/"), out var categoryRoute) || categoryRoute?.PageNumber != 2 ||
            ModPageRouter.TryResolveSupportedPage(new("https://www.moddb.com.example.org/mods/example"), out _) ||
            ModPageRouter.TryResolveSupportedPage(new("https://github.com/example/mod/issues/1"), out _) ||
            page.GetLogicalDescendants().OfType<Button>().Count(button => button.Content as string == LocalizedStrings.Instance.Get("MS_Original")) != 1 ||
            page.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "subscriptionOriginal").IsEnabled ||
            !page.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "subscriptionLinkBack" && !button.IsEnabled))
            throw new InvalidOperationException("Subscription original action, bounded link routes or native anchor serialization failed.");
        AppDiagnostics.Log("Subscription link self-test passed: one external original action, disabled empty context, three-source URL routes and preserved document anchors.");
        var group = new ModFileGroup { Id = "release-1", Title = "Version 1", PageUrl = new("https://github.com/example/mod/releases/tag/v1"),
            Description = "Shared notes", Document = restored };
        var snapshot = new ModFileSnapshot([new() { Id = "asset:1", Revision = "unchanged", Name = "mod.zip",
            PageUrl = new("https://api.github.com/repositories/1/releases/assets/1"), GroupId = group.Id }]) { Groups = [group] };
        var cached = new CachedModContent<ModFileSnapshot> { Version = 4, Key = "self-test", FetchedAt = DateTimeOffset.UtcNow, Content = snapshot };
        string fileJson = System.Text.Json.JsonSerializer.Serialize(cached, ModContentJsonContext.Default.CachedModContentModFileSnapshot);
        var files = System.Text.Json.JsonSerializer.Deserialize(fileJson, ModContentJsonContext.Default.CachedModContentModFileSnapshot)!.Content;
        if (files.Groups.Single().Id != files.Files.Single().GroupId || files.Files[0].Revision != "unchanged" ||
            !ModDocumentParser.HasDocument(files.Groups[0].Document) ||
            ((TabItem)page.GetLogicalDescendants().OfType<TabControl>().Single().Items[1]!).Content is not Control fileRoot ||
            !fileRoot.GetLogicalDescendants().OfType<ScrollViewer>().Any(control => control.Name == "subscriptionFileNavigation") ||
            !fileRoot.GetLogicalDescendants().OfType<Button>().Any(control => control.Name == "subscriptionDownloadSelected") ||
            !fileRoot.GetLogicalDescendants().OfType<GridSplitter>().Any(control => control.Name == "subscriptionFileSplitter") ||
            !fileRoot.GetLogicalDescendants().OfType<Button>().Any(control => control.Name == "subscriptionFileNavigationToggle") ||
            page.GetLogicalDescendants().OfType<Button>().Any(control => control.Name == "subscriptionChooseFiles"))
            throw new InvalidOperationException("Subscription file navigation or shared version serialization failed.");
        var hosted = files.Files[0] with { Name = "Author title — modsfire.com", BrowserUrl = new("https://modsfire.com/public-file") };
        var presentation = ModFilePresentation.From(hosted, "mowportal");
        if (presentation is not { Title: "Author title", Host: "ModsFire" } ||
            ModFilePresentation.From(hosted, "github").Title != hosted.Name ||
            ModFilePresentation.From(hosted with { BrowserUrl = new("https://disk.yandex.ru/d/example") }, "mowportal").Title != hosted.Name ||
            hosted.Name != "Author title — modsfire.com" || hosted.Revision != "unchanged")
            throw new InvalidOperationException("Download host presentation changed file metadata or stripped an unrelated suffix.");
        AppDiagnostics.Log("Subscription file self-test passed: shared version metadata, stable file identity and independent file navigation.");
        var statuses = new ModPageStatus();
        var scope = new ModPageScope(null, "moddb", null, "overview");
        var failed = statuses.Begin("overview", scope, "Fetch");
        statuses.Update(failed, "Completed", "offline", ModPageOperationState.Failed);
        var success = statuses.Begin("images", scope, "Fetch");
        statuses.Update(success, "Completed", "", ModPageOperationState.Succeeded);
        if (!statuses.Operations.Any(item => item.Id == failed.Id && item.State == ModPageOperationState.Failed))
            throw new InvalidOperationException("An independent success dismissed a content failure.");
        var replacement = statuses.Begin("overview", scope, "Fetch");
        if (statuses.Update(failed, "Completed", "late", ModPageOperationState.Succeeded) ||
            !statuses.Operations.Any(item => item.Id == replacement.Id && item.State == ModPageOperationState.Running))
            throw new InvalidOperationException("A superseded status completion replaced a current request.");
        statuses.Reset();
        if (statuses.Update(replacement, "Completed", "late", ModPageOperationState.Failed) || statuses.Operations.Count != 0 ||
            page.GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "subscriptionRefresh") != 1 ||
            !page.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "subscriptionStatusToggle") ||
            !page.GetLogicalDescendants().OfType<Border>().Any(border => border.Name == "subscriptionStatusDetails"))
            throw new InvalidOperationException("Subscription refresh scope or inline status details failed.");
        AppDiagnostics.Log("Subscription refresh self-test passed: one entry, inline status details and obsolete completion rejection.");
        var preferences = new ModTranslationSettings();
        string translationJson = System.Text.Json.JsonSerializer.Serialize(preferences, ModTranslationJsonContext.Default.ModTranslationSettings);
        var translationPreferences = System.Text.Json.JsonSerializer.Deserialize(translationJson, ModTranslationJsonContext.Default.ModTranslationSettings);
        var chunks = ModDocumentTranslator.Chunks(new string('a', 899) + "🙂" + new string('b', 905)).ToArray();
        if (translationPreferences is not { Enabled: false, Language: "", ShowOriginal: false } ||
            string.Concat(chunks) != new string('a', 899) + "🙂" + new string('b', 905) || chunks.Any(chunk => char.IsHighSurrogate(chunk[^1])) ||
            !page.GetLogicalDescendants().OfType<CheckBox>().Any(control => control.Name == "subscriptionTranslate" && control.IsChecked != true) ||
            !page.GetLogicalDescendants().OfType<ComboBox>().Any(control => control.Name == "subscriptionTranslationLanguage" && control.SelectedIndex == 0) ||
            !page.GetLogicalDescendants().OfType<ComboBox>().Any(control => control.Name == "subscriptionTranslationDisplay" && !control.IsEnabled))
            throw new InvalidOperationException("Subscription translation controls, defaults, Unicode chunks or source-generated preferences failed.");
        AppDiagnostics.Log("Subscription translation self-test passed: default-off controls, UI language target, Unicode chunks and source-generated preferences without network access.");
    }
}
