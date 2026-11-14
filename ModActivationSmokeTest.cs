using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

// Delivered with --self-test-ui; all files belong to its temporary fixture, never a real game.
internal static class ModActivationSmokeTest
{
    private sealed class TestWindow(string settings) : MainWindow(settings)
    { protected override void OnOpened(EventArgs e) { } }

    internal static void Pump(Task task, bool propagate = true)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            if (task.IsCompleted) break;
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Mod operation did not complete.");
            Thread.Sleep(1);
        } while (true);
        Dispatcher.UIThread.RunJobs();
        if (propagate) task.GetAwaiter().GetResult();
    }

    internal static void Run(string root)
    {
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Mod activation: " + message); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string fixture = Directory.CreateDirectory(Path.Combine(root, "activation-ui")).FullName;
        string game = Directory.CreateDirectory(Path.Combine(fixture, "game")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(game, "mods"));
        foreach (string id in new[] { "regular", "mixed", "loader", "unknown", "enabled_bad" })
        {
            string dir = Directory.CreateDirectory(Path.Combine(mods.FullName, id)).FullName;
            File.WriteAllText(Path.Combine(dir, "mod.info"), "{mod {name \"" + id + "\"}}");
            if (id is "regular" or "mixed")
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "resource")).FullName, "content.set"), "fixture");
            if (id is "mixed" or "loader" or "enabled_bad") File.WriteAllText(Path.Combine(dir, "loader.dll"), "static fixture");
        }
        string options = Path.Combine(fixture, "options.set");
        File.WriteAllText(options, "{options\n{mods\n\"regular:0\"\n\"enabled_bad:0\"\n}\n{after 1}\n}\n");
        var originalCulture = LocalizedStrings.Instance.Culture;
        var window = new TestWindow(Path.Combine(fixture, "settings.conf")) { SuppressSubscriptionActivation = true };
        window.universalVars.localDir = mods;
        window.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(game, "binaries", "x64"));
        window.universalVars.optionLoc = options;
        var manager = new ModManager(window);
        manager.PresetStore = new ModPresetStore(Path.Combine(fixture, "presets"));
        window.Content = manager;
        Require(!window.PendingModRefresh.IsCompleted, "opening manager starts a background scan without waiting");
        bool sawPending = false, sawProgress = false, dispatcherAvailable = false;
        void Observe(bool structure)
        {
            var rows = window.universalVars.modDic.Values.ToArray();
            if (structure && !sawPending && rows.Any(mod => mod.IsInspectionPending))
            {
                sawPending = rows.All(mod => mod.IsInspectionPending);
                foreach (var mod in rows)
                {
                    var view = new ModFeaturesView { Mod = mod };
                    var labels = view.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                    Require(labels.SequenceEqual(new[] { ModInspectionPresentation.Text("Checking") }), "pending row shows only Checking");
                }
                // Sorting existing invalid entries remains available while the worker is checking files.
                Require(manager.MoveLoadedMod(window.universalVars.modDic["enabled_bad"], window.universalVars.modDic["regular"]), "sort during inspection");
                Dispatcher.UIThread.Post(() => dispatcherAvailable = true);
            }
            if (rows.Any(mod => mod.IsInspectionPending) && rows.Any(mod => !mod.IsInspectionPending)) sawProgress = true;
        }
        window.ModInspectionChanged += Observe;
        window.Show();
        try
        {
            Pump(window.PendingModRefresh);
            window.ModInspectionChanged -= Observe;
            Require(sawPending && sawProgress && dispatcherAvailable, "discovery, incremental results and other UI work");
            Require(window.universalVars.modLoaded.Select(mod => mod.folderName).SequenceEqual(new[] { "enabled_bad", "regular" }), "scan preserves a concurrent reorder");
            var available = manager.FindControl<DataGrid>("unloadedMods")!;
            var enabled = manager.FindControl<DataGrid>("loadedMods")!;

            void Reject(string[] ids, bool dll)
            {
                string bytes = File.ReadAllText(options);
                var order = window.universalVars.modLoaded.Select(mod => mod.folderName).ToArray();
                available.SelectedItems.Clear();
                foreach (string id in ids) available.SelectedItems.Add(window.universalVars.modDic[id]);
                Click(manager.FindControl<Button>("loadMod")!);
                Pump(manager.PendingActivation, propagate: false);
                var dialog = window.OwnedWindows.Single();
                string message = ((TextBlock)((StackPanel)dialog.Content!).Children.OfType<ScrollViewer>().Single().Content!).Text!;
                string expected = ModInspectionPresentation.Text("ActivationBlocked") + (dll ? "\n" + ModInspectionPresentation.Text("DllManualInstall") : "");
                Require(message == expected && File.ReadAllText(options) == bytes &&
                    window.universalVars.modLoaded.Select(mod => mod.folderName).SequenceEqual(order), "exact rejection, unchanged file and order");
                if (LocalizedStrings.Instance.Culture.Name == "zh-CN") Require(message ==
                    "该mod无法通过激活使用" + (dll ? "\nDLL注入mod请根据作者说明手动安装" : ""), "exact requested Chinese text");
                dialog.Close(); Dispatcher.UIThread.RunJobs();
            }
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
                Reject(["unknown"], false);
                Reject(["loader"], true);
                Reject(["mixed", "loader"], true);
                var mixed = window.universalVars.modDic["mixed"];
                mixed.IsInspectionPending = true;
                Reject(["mixed"], false);
                mixed.IsInspectionPending = false;
            }
            var firstActivation = manager.ActivateModsAsync([window.universalVars.modDic["mixed"]]);
            var repeatedActivation = manager.ActivateModsAsync([window.universalVars.modDic["mixed"]]);
            Require(repeatedActivation.IsFaulted && repeatedActivation.Exception!.InnerException is ModActivationRejectedException,
                "a repeated click while Checking still rejects with the activation message");
            Pump(firstActivation);
            Require(window.universalVars.modLoaded.Last().folderName == "mixed", "mixed regular/DLL activation allowed");
            File.Delete(Path.Combine(mods.FullName, "regular", "resource", "content.set"));
            // Applying a preset rechecks files instead of trusting the old Regular badge.
            manager.PresetStore.Save("Changed", [window.universalVars.modDic["regular"]]);
            string before = File.ReadAllText(options), preset = File.ReadAllText(manager.PresetStore.PathFor("Changed"));
            bool refused = false;
            try { Pump(manager.ApplyPresetAsync("Changed")); } catch (ModActivationRejectedException) { refused = true; }
            Require(refused && File.ReadAllText(options) == before && File.ReadAllText(manager.PresetStore.PathFor("Changed")) == preset,
                "preset refuses existing invalid entries without partial commit or preset rewrite");
            manager.PresetStore.Save("Valid", [window.universalVars.modDic["mixed"], new Mod("missing", "", "", "gone", false)]);
            var valid = manager.ApplyPresetAsync("Valid"); Pump(valid);
            Require(valid.Result.Missing.Single().Id == "gone" && window.universalVars.modLoaded.Single().folderName == "mixed", "missing is distinct from invalid");

            // Invalid existing entries can always be disabled, including while a fresh scan is pending.
            File.WriteAllText(options, "{options\n{mods\n\"enabled_bad:0\"\n\"mixed:0\"\n}\n}\n");
            window.RefreshMods();
            var refresh = window.RefreshModsAsync();
            enabled.SelectedItem = window.universalVars.modDic["enabled_bad"];
            Click(manager.FindControl<Button>("unloadMod")!);
            Pump(refresh);
            Require(window.universalVars.modLoaded.Single().folderName == "mixed", "disable remains available during scan");

            // Exit restoration captures the old order but must not reenable a directory changed by the game/user.
            window.universalVars.NeedLockModList = true;
            File.WriteAllText(options, "{options\n{mods\n}\n{after 1}\n}\n");
            string exitOptions = File.ReadAllText(options);
            File.Delete(Path.Combine(mods.FullName, "mixed", "resource", "content.set"));
            var exitRefresh = window.RefreshModsAsync(afterGaming: true); Pump(exitRefresh);
            Require(!exitRefresh.Result && File.ReadAllText(options) == exitOptions && window.OwnedWindows.Count == 1,
                "asynchronous exit restoration rejects changed contents atomically");
            window.OwnedWindows.Single().Close();
            File.WriteAllText(Path.Combine(mods.FullName, "mixed", "resource", "content.set"), "fixture");
            window.universalVars.NeedLockModList = false;
            window.RefreshMods();
            var locked = new[] { window.universalVars.modDic["loader"] };
            before = File.ReadAllText(options);
            refused = false;
            try { window.RestoreLockedModList(locked); } catch (ModActivationRejectedException) { refused = true; }
            Require(refused && File.ReadAllText(options) == before, "locked-list restoration cannot reenable Other");

            // The launch guard runs before touching shader files or attempting a process start.
            File.WriteAllText(options, "{options\n{mods\n\"loader:0\"\n}\n}\n");
            string resource = Directory.CreateDirectory(Path.Combine(game, "resource")).FullName;
            string shader = Path.Combine(resource, "shader.pak"); File.WriteAllText(shader, "unchanged");
            window.universalVars.resourceDir = new DirectoryInfo(resource);
            var launcher = new Launcher(window);
            Pump(launcher.RunGameAsync("call_to_arms.exe", autoFix: true));
            Require(File.ReadAllText(shader) == "unchanged" && window.OwnedWindows.Count == 1, "launch rejection precedes shader/cache operations");
            window.OwnedWindows.Single().Close();
            File.WriteAllText(options, "{options\n{mods\n\"absent:0\"\n}\n}\n");
            bool missingRejected = false;
            try { Pump(window.CheckEnabledModsForLaunchAsync()); } catch (ModActivationRejectedException) { missingRejected = true; }
            Require(missingRejected && window.universalVars.modLoaded.Single().IsMissingReference, "unknown enabled references stay visible and cannot bypass launch guard");

            // A late activation result must never target the game selected after the user started it.
            string oldOptions = File.ReadAllText(options);
            var obsolete = manager.ActivateModsAsync([window.universalVars.modDic["mixed"]]);
            string otherGame = Directory.CreateDirectory(Path.Combine(fixture, "other-game")).FullName;
            var otherMods = Directory.CreateDirectory(Path.Combine(otherGame, "mods"));
            string otherRoot = Directory.CreateDirectory(Path.Combine(otherMods.FullName, "only")).FullName;
            File.WriteAllText(Path.Combine(otherRoot, "mod.info"), "{mod {name \"Only\"}}");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(otherRoot, "resource")).FullName, "content.set"), "fixture");
            string otherOptions = Path.Combine(otherGame, "options.set"); File.WriteAllText(otherOptions, "{options\n{mods\n}\n}\n");
            string otherBefore = File.ReadAllText(otherOptions);
            window.universalVars.localDir = otherMods;
            window.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(otherGame, "binaries", "x64"));
            window.universalVars.optionLoc = otherOptions;
            Pump(window.RefreshModsAsync()); Pump(obsolete, propagate: false);
            Require(obsolete.IsCanceled && File.ReadAllText(options) == oldOptions && File.ReadAllText(otherOptions) == otherBefore &&
                window.universalVars.modDic.Values.Single().folderName == "only", "obsolete activation canceled across game switch");
        }
        finally
        {
            window.ModInspectionChanged -= Observe;
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close(); LocalizedStrings.Instance.SetCulture(originalCulture);
        }
        AppDiagnostics.Log("Mod activation self-test passed: asynchronous discovery and progressive Checking labels, responsive reorder/disable, exact bilingual rejection, batch atomicity, mixed activation, fresh presets, locked restoration and pre-shader launch guard.");
        ModListRestorationSmokeTest.Run(root);
    }
}
