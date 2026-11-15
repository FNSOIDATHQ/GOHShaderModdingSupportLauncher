using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// Shipped offline diagnostic, exercised by --self-test-ui in the published Native AOT executable.
internal static class ModInspectionUiSmokeTest
{
    private sealed class InspectionWindow(string settings) : MainWindow(settings)
    { protected override void OnOpened(EventArgs e) { } }

    internal static void Run(string root)
    {
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Mod inspection UI: " + message); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static T Find<T>(Control control, string name) where T : Control =>
            control.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
        static void Pump(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            do { Dispatcher.UIThread.RunJobs(); if (ready()) return; Thread.Sleep(1); } while (timer.Elapsed < TimeSpan.FromSeconds(15));
            throw new InvalidOperationException("Mod inspection UI timed out.");
        }
        var culture = LocalizedStrings.Instance.Culture;
        string fixture = Path.Combine(root, "inspection-ui");
        string game = Path.Combine(fixture, "game");
        var mods = Directory.CreateDirectory(Path.Combine(game, "mods"));
        string options = Path.Combine(fixture, "options.set");
        foreach (string id in new[] { "regular", "mixed", "shader", "all", "extra", "program", "missing", "unknown" })
        {
            string dir = Directory.CreateDirectory(Path.Combine(mods.FullName, id)).FullName;
            if (id != "missing") File.WriteAllText(Path.Combine(dir, "mod.info"), "{mod {name \"" + id + "\"}}");
            if (id is "regular" or "mixed" or "shader" or "all" or "extra")
            {
                string resource = Directory.CreateDirectory(Path.Combine(dir, "resource")).FullName;
                File.WriteAllText(Path.Combine(resource, "game.set"), "fixture");
            }
            if (id is "mixed" or "all" or "program") File.WriteAllText(Path.Combine(dir, "loader.dll"), "static fixture");
            if (id is "shader" or "all")
            {
                string shader = Directory.CreateDirectory(Path.Combine(dir, "resource", "shader", "dx10")).FullName;
                File.WriteAllText(Path.Combine(shader, "example.fx"), "fixture");
            }
            if (id is "all" or "extra")
            {
                string source = Directory.CreateDirectory(Path.Combine(dir, "file")).FullName;
                File.WriteAllText(Path.Combine(source, "source.max"), "development fixture");
            }
        }
        File.WriteAllText(options, "{options\n{mods\n\"all:0\"\n\"program:0\"\n}\n}\n");
        var window = new InspectionWindow(Path.Combine(fixture, "settings.conf")) { SuppressSubscriptionActivation = true };
        window.universalVars.localDir = mods;
        window.universalVars.workshopDir = null;
        window.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(game, "binaries", "x64"));
        window.universalVars.optionLoc = options;
        window.RefreshMods();
        var manager = new ModManager(window);
        window.Content = manager;
        window.Show();
        var available = Find<DataGrid>(manager, "unloadedMods")!;
        var enabled = Find<DataGrid>(manager, "loadedMods")!;
        var order = window.universalVars.modLoaded.Select(mod => mod.folderName).ToArray();
        Require(order.SequenceEqual(new[] { "all", "program" }), "enabled fixture");
        string before = File.ReadAllText(options);
        try
        {
            Require(available.Columns[2] is DataGridTemplateColumn a && enabled.Columns[2] is DataGridTemplateColumn b &&
                ReferenceEquals(a.CellTemplate, b.CellTemplate) && ReferenceEquals(a.HeaderTemplate, b.HeaderTemplate),
                "both tables must share the feature and header templates");
            Require(enabled.CanUserSortColumns == false && available.CanUserResizeColumns, "order and resize");
            using var subscriptions = new ModSubscriptionsPage(window);
            var installation = new ModInstallation
            {
                Id = Guid.NewGuid(), DownloadId = Guid.NewGuid(), DirectoryPath = window.universalVars.modDic["mixed"].path,
                OwnedFiles = [], State = InstallationState.Installed
            };
            Require(subscriptions.InstalledModInspection(game, installation)?.Inspection?.ExternalPrograms.Detected == true, "explicit local association");
            Require(subscriptions.InstalledModInspection(game, installation with { State = InstallationState.Pending }) is null &&
                subscriptions.InstalledModInspection(fixture, installation) is null &&
                subscriptions.InstalledModInspection(game, installation with { DirectoryPath = Path.Combine(game, "outside") }) is null,
                "uninstalled, other-game and outside paths must not inherit labels");

            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
                Dispatcher.UIThread.RunJobs();
                Require(available.Columns[1].Header as string == LocalizedStrings.Instance.MI_Source &&
                    enabled.Columns[2].Header as string == LocalizedStrings.Instance.MI_Features, "live column language");
                window.Width = 900; window.Height = 600;
                Dispatcher.UIThread.RunJobs();
                Require(!manager.GetLogicalDescendants().OfType<Button>().Any(button => button.Name is "inspectMod" or "modTypeHelp"), "inspection and help UI removed");
                enabled.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
                Require(!window.OwnedWindows.Any(), "F1 has no inspection route");
                foreach (var grid in new[] { available, enabled })
                {
                    var hint = grid.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "modFeaturesHeaderHint");
                    var tip = ToolTip.GetTip(hint) as ModFeatureHintView
                        ?? throw new InvalidOperationException("Feature hint must show a styled legend.");
                    ToolTip.SetIsOpen(hint, true); Dispatcher.UIThread.RunJobs();
                    Require(ToolTip.GetIsOpen(hint), "column hint opens as a tooltip");
                    var legend = (Grid)tip.Content!;
                    var legendBadges = legend.Children.OfType<Border>().ToArray();
                    var descriptions = legend.Children.OfType<TextBlock>().Select(text => text.Text).ToArray();
                    Require(legendBadges.Length == 4 && descriptions.Length == 4, "exactly four legend rows");
                    var actualBadges = enabled.GetVisualDescendants().OfType<ModFeaturesView>()
                        .SelectMany(view => view.GetLogicalDescendants().OfType<Border>())
                        .Where(border => border.Child is TextBlock).GroupBy(border => ((TextBlock)border.Child!).Text!)
                        .ToDictionary(group => group.Key, group => group.First());
                    var lines = new List<string>();
                    for (int row = 0; row < legendBadges.Length; row++)
                    {
                        var badge = legendBadges[row];
                        var label = (TextBlock)badge.Child!;
                        var actual = actualBadges[label.Text!];
                        Require(badge.Background is ISolidColorBrush background && actual.Background is ISolidColorBrush actualBackground &&
                            background.Color == actualBackground.Color && badge.Padding == actual.Padding && badge.CornerRadius == actual.CornerRadius &&
                            label.FontSize == ((TextBlock)actual.Child!).FontSize &&
                            ((ISolidColorBrush)label.Foreground!).Color == ((ISolidColorBrush)((TextBlock)actual.Child!).Foreground!).Color,
                            "legend badge matches the displayed row's background, shape and text style");
                        lines.Add(label.Text + descriptions[row]);
                    }
                    string legendText = string.Join("\n", lines);
                    Require(legendText == LocalizedStrings.Instance.MI_FeatureHint && AutomationProperties.GetName(tip) == legendText,
                        "visual and accessible legend text agree");
                    Require(descriptions.All(description => !string.IsNullOrWhiteSpace(description)), "each independent feature has a description");
                    ToolTip.SetIsOpen(hint, false);
                }
                foreach (string id in new[] { "regular", "mixed", "shader", "all", "extra", "program", "missing", "unknown" })
                {
                    var mod = window.universalVars.modDic[id];
                    var view = new ModFeaturesView { Mod = mod };
                    view.Measure(new Size(105, double.PositiveInfinity));
                    view.Arrange(new Rect(0, 0, 105, view.DesiredSize.Height));
                    var labels = view.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.FontSize == 11 && text.Parent is Border).Select(text => text.Text).ToArray();
                    var expected = new List<string>();
                    if (mod.Inspection!.HasRegularFeature) expected.Add(ModInspectionPresentation.Text("Regular"));
                    if (mod.Inspection.ExternalPrograms.Detected) expected.Add(ModInspectionPresentation.Text("Dll"));
                    if (mod.Inspection.Shaders.Detected) expected.Add(ModInspectionPresentation.Text("Shader"));
                    if (mod.Inspection.HasOtherFeature) expected.Add(ModInspectionPresentation.Text("Other"));
                    Require(labels.SequenceEqual(expected) && AutomationProperties.GetName(view) == mod.DisplayName + ": " + string.Join(" · ", expected),
                        "ordered independent badges and accessible summary " + id);
                    if (id == "all") Require(labels.Length == 4 && mod.CanActivate, "all four features coexist and remain eligible");
                    if (id == "extra") Require(labels.SequenceEqual(new[] { ModInspectionPresentation.Text("Regular"), ModInspectionPresentation.Text("Other") }) && mod.CanActivate,
                        "ordinary and Other coexist without an external program or activation denial");
                    Require(!view.Focusable && !view.GetLogicalDescendants().OfType<Button>().Any() && ToolTip.GetTip(view) is null &&
                        AutomationProperties.GetName(view)!.Contains(labels[0]!), "passive badges with accessible names and no inspection button");
                }
                var mixed = window.universalVars.modDic["mixed"];
                var partial = new Mod("partial", "", mixed.path, mixed.folderName, false)
                {
                    Inspection = mixed.Inspection! with
                    { ExternalPrograms = mixed.Inspection.ExternalPrograms with { Complete = false, MatchCount = 200 } }
                };
                var partialView = new ModFeaturesView { Mod = partial };
                Require(!AutomationProperties.GetName(partialView)!.Contains(ModInspectionPresentation.Text("Other")) &&
                    AutomationProperties.GetName(partialView)!.Contains(ModInspectionPresentation.Text("Dll")) &&
                    AutomationProperties.GetName(partialView)!.Contains(ModInspectionPresentation.Text("Regular")) && partial.CanActivate &&
                    ModInspectionPresentation.Details(partial).Contains(ModInspectionPresentation.Format("Truncated", 1, 200)), "positive incomplete result and evidence truncation");
                foreach (var inspection in new[]
                {
                    mixed.Inspection! with { Structure = ModStructureState.Invalid },
                    mixed.Inspection! with { Structure = ModStructureState.Unconfirmed }
                })
                {
                    var other = new ModFeaturesView { Mod = new Mod("other", "", mixed.path, "", false) { Inspection = inspection } };
                    Require(AutomationProperties.GetName(other) == "other: " + ModInspectionPresentation.Text("Dll") + " · " + ModInspectionPresentation.Text("Other"),
                        "unconfirmed structures use the fallback independently of the DLL feature");
                }
                foreach (var inspection in new[]
                {
                    mixed.Inspection! with { StructureComplete = false },
                    mixed.Inspection! with { Shaders = mixed.Inspection.Shaders with { Complete = false } },
                    mixed.Inspection! with { LoadId = null }
                })
                {
                    var ordinary = new Mod("ordinary", "", mixed.path, "", false) { Inspection = inspection };
                    var ordinaryView = new ModFeaturesView { Mod = ordinary };
                    Require(AutomationProperties.GetName(ordinaryView) == "ordinary: " + ModInspectionPresentation.Text("Regular") + " · " + ModInspectionPresentation.Text("Dll") &&
                        ordinary.HasRegularFeature && ordinary.CanActivate == (inspection.LoadId is not null),
                        "coverage and invalid IDs do not erase ordinary content; only the invalid ID blocks activation");
                }
                var uninspected = new ModFeaturesView { Mod = new Mod("remote", "", "", "", true) };
                Require(AutomationProperties.GetName(uninspected)!.Contains(ModInspectionPresentation.Text("NotInspected")) &&
                    !uninspected.GetLogicalDescendants().OfType<Border>().Any(), "uninspected files cannot inherit a shader badge");

                var allBadges = enabled.GetVisualDescendants().OfType<ModFeaturesView>().First(view => view.Mod?.folderName == "all");
                Require(AutomationProperties.GetName(allBadges)!.Contains(ModInspectionPresentation.Text("Shader")) &&
                    !allBadges.GetLogicalDescendants().OfType<Button>().Any(), "live passive badge language");
                string details = window.GetModInspectionDebugDetails(mixed);
                Require(details.Contains("loader.dll") && details.Contains("resource/game.set"), "internal debug evidence retained");
            }
            // Refresh and internal diagnostics retain evidence without a user-facing details window.
            File.Delete(Path.Combine(mods.FullName, "mixed", "loader.dll"));
            var task = window.RefreshModsAsync();
            Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Require(!window.GetModInspectionDebugDetails(window.universalVars.modDic["mixed"]).Contains("loader.dll"), "fresh debug snapshot after removal");
            Require(File.ReadAllText(options) == before && window.universalVars.modLoaded.Select(mod => mod.folderName).SequenceEqual(order), "inspection must preserve options and load order");
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close(); LocalizedStrings.Instance.SetCulture(culture);
        }
        AppDiagnostics.Log("Mod inspection UI self-test passed: independent four-feature combinations and eligible Regular/Other rows, bilingual accessible legends matching actual badge styles, separate coverage/ID facts, narrow layout, removed detail/help UI, internal debug evidence, local installation association and read-only refresh.");
        ModActivationSmokeTest.Run(root);
    }
}
