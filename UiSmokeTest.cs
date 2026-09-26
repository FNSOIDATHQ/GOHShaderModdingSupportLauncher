using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.Properties;
using System.ComponentModel;
using System.Globalization;

namespace GOHShaderModdingSupportLauncher;

internal static class UiSmokeTest
{
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
            var mods = Directory.CreateDirectory(Path.Combine(fixture, "mods"));
            foreach (var name in new[] { "alpha", "beta", "zeta", "gamma" })
            {
                var directory = Directory.CreateDirectory(Path.Combine(mods.FullName, name));
                File.WriteAllText(Path.Combine(directory.FullName, "mod.info"), "{name \"" + name + "\"}\n");
                if (name == "gamma")
                {
                    var shader = Directory.CreateDirectory(Path.Combine(directory.FullName, "resource", "shader", "dx10"));
                    File.WriteAllText(Path.Combine(shader.FullName, "fixture.txt"), "shader");
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
            var window = new MainWindow(settingsPath);
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
            if (window.ExtendClientAreaToDecorationsHint || !window.CanResize ||
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
            if (primary.Items.Count != 4 || footer.Items.Count != 2)
                throw new InvalidOperationException("Six navigation entries were expected.");
            Type[] expected = [typeof(Launcher), typeof(ModManager), typeof(Converter),
                typeof(Tools), typeof(Settings), typeof(About)];
            for (var i = 0; i < expected.Length; i++)
            {
                if (i < 4) primary.SelectedIndex = i;
                else footer.SelectedIndex = i - 4;
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
                }
                if (i == 2)
                {
                    var tabs = ((Converter)host.Content).FindControl<TabControl>("Tabs");
                    if (tabs?.TabStripPlacement != Dock.Left)
                        throw new InvalidOperationException("Converter tabs are not stacked on the left.");
                    ((Converter)host.Content).FindControl<TextBox>("convertPath")!.Text = "keep this path";
                }
            }
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
                ((ListBoxItem)primary.Items[0]!).Content as string != i18n.Tab_Launcher ||
                ((ListBoxItem)footer.Items[0]!).Content as string != i18n.Tab_Settings ||
                (FileManager.ReadSettings(settingsPath).Length != 14 || FileManager.ReadSettings(settingsPath)[13] != "zh-CN"))
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
            primary.SelectedIndex = 2;
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
            if ((FileManager.ReadSettings(settingsPath).Length != 14 || FileManager.ReadSettings(settingsPath)[13] != "en-US") ||
                gameButton.Content as string != i18n.L_Main ||
                window.universalVars.modDic["alpha"].type != i18n.Main_ModLocal ||
                window.universalVars.modDic["mod_123"].type != i18n.Main_ModWorkshop)
                throw new InvalidOperationException("Switching the visible mod page back to English failed.");
            Directory.CreateDirectory(window.universalVars.cacheLoc);
            File.WriteAllText(Path.Combine(window.universalVars.cacheLoc, "fixture.txt"), "cache");
            window.ClearCacheWork();
            if (Directory.Exists(window.universalVars.cacheLoc))
                throw new InvalidOperationException("Shader cache tool failed.");
            AppDiagnostics.Log("Desktop UI self-test passed: six pages, two languages, mod load/unload/reorder and column sorting, left converter tabs, matching translucent title bar and sidebar, fitted background, resource restore, and cache cleanup.");
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
}
