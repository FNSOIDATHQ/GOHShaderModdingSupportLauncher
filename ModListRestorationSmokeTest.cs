using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

// Part of the shipped offline --self-test-ui diagnostic; only touches its temporary fixture.
internal static class ModListRestorationSmokeTest
{
    private sealed class TestWindow(string settings) : MainWindow(settings)
    { protected override void OnOpened(EventArgs e) { } }

    internal static void Run(string root)
    {
        static void Require(bool ok, string message)
        { if (!ok) throw new InvalidOperationException("Mod list restoration: " + message); }
        static string Options(string[] ids, int after = 1) =>
            "{options\n{mods\n" + string.Concat(ids.Select(id => "\"" + id + ":0\"\n")) + "}\n{after " + after + "}\n}\n";
        string fixture = Directory.CreateDirectory(Path.Combine(root, "locked-list-ui")).FullName;
        string game = Directory.CreateDirectory(Path.Combine(fixture, "game")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(game, "mods"));
        foreach (string id in new[] { "a", "b", "c" })
        {
            string dir = Directory.CreateDirectory(Path.Combine(mods.FullName, id)).FullName;
            File.WriteAllText(Path.Combine(dir, "mod.info"), "{mod {name \"" + id + "\"}}");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "resource")).FullName, "content.set"), "fixture");
        }
        string options = Path.Combine(fixture, "options.set");
        File.WriteAllText(options, Options(["a", "b"]));
        var window = new TestWindow(Path.Combine(fixture, "settings.conf")) { SuppressSubscriptionActivation = true };
        window.universalVars.localDir = mods;
        window.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(game, "binaries", "x64"));
        window.universalVars.optionLoc = options;
        var manager = new ModManager(window) { PresetStore = new ModPresetStore(Path.Combine(fixture, "presets")) };
        window.Content = manager;
        window.Show();
        try
        {
            ModActivationSmokeTest.Pump(window.PendingModRefresh);
            void Reset()
            {
                window.universalVars.NeedLockModList = false;
                File.WriteAllText(options, Options(["a", "b"]));
                window.RefreshMods();
                window.universalVars.NeedLockModList = true;
            }
            void Expect(string[] ids, string message)
            {
                using var reader = File.OpenText(options);
                Require(FileManager.ReadLoadedModNames(reader).SequenceEqual(ids) &&
                    window.universalVars.modLoaded.Select(mod => mod.folderName).SequenceEqual(ids), message);
            }
            void DisableB()
            {
                manager.FindControl<DataGrid>("loadedMods")!.SelectedItem = window.universalVars.modDic["b"];
                manager.FindControl<Button>("unloadMod")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            void DuringRefresh(string label, Action edit, string[] expected, bool completion = false, bool afterGaming = true)
            {
                Reset();
                bool edited = false;
                string? editedOptions = null;
                void Observe(bool structure)
                {
                    if (!structure || edited || (window.ModScanProgress is not null) != completion) return;
                    edited = true;
                    edit();
                    editedOptions = File.ReadAllText(options);
                }
                window.ModInspectionChanged += Observe;
                try
                {
                    var refresh = window.RefreshModsAsync(afterGaming);
                    ModActivationSmokeTest.Pump(refresh);
                    Require(edited && refresh.Result && File.ReadAllText(options) == editedOptions, label + " preserves the exact new file");
                    Expect(expected, label + " keeps the latest displayed order");
                }
                finally { window.ModInspectionChanged -= Observe; }
            }

            Reset();
            // This disk difference predates refresh and is exactly what the lock is meant to undo.
            File.WriteAllText(options, Options(["c"], after: 2));
            long revision = window.ModListRevision;
            var normal = window.RefreshModsAsync(afterGaming: true); ModActivationSmokeTest.Pump(normal);
            Expect(["a", "b"], "uncontended exit restores the pre-game order");
            Require(normal.Result && window.ModListRevision == revision + 1 && File.ReadAllText(options).Contains("{after 2}"),
                "normal restoration preserves unrelated game settings");

            DuringRefresh("ordinary refresh reorder", () => Require(manager.MoveLoadedMod(window.universalVars.modDic["b"], window.universalVars.modDic["a"]), "reorder available"), ["b", "a"], afterGaming: false);
            DuringRefresh("exit discovery reorder", () => Require(manager.MoveLoadedMod(window.universalVars.modDic["b"], window.universalVars.modDic["a"]), "exit reorder available"), ["b", "a"]);
            DuringRefresh("exit discovery disable", DisableB, ["a"]);
            DuringRefresh("exit completion reorder", () => Require(manager.MoveLoadedMod(window.universalVars.modDic["b"], window.universalVars.modDic["a"]), "completion reorder available"), ["b", "a"], completion: true);
            DuringRefresh("subscription host removal", () => window.CreateModInstallationHost().RemoveEnabledMods(game, [Path.Combine(mods.FullName, "b")]), ["a"]);
            DuringRefresh("external options edit", () => File.WriteAllText(options, Options(["c"], after: 3)), ["c"]);
            DuringRefresh("external edit at completion", () => File.WriteAllText(options, Options(["c"], after: 4)), ["c"], completion: true);

            Reset();
            File.WriteAllText(options, Options(["c"]));
            bool settingsEdited = false;
            void EditSettings(bool structure)
            {
                if (!structure || settingsEdited) return;
                settingsEdited = true;
                File.WriteAllText(options, Options(["c"], after: 5));
            }
            window.ModInspectionChanged += EditSettings;
            try { ModActivationSmokeTest.Pump(window.RefreshModsAsync(afterGaming: true)); }
            finally { window.ModInspectionChanged -= EditSettings; }
            Expect(["c"], "an external settings-only edit also invalidates restoration");
            Require(settingsEdited && File.ReadAllText(options) == Options(["c"], after: 5), "external unrelated edits stay byte-for-byte intact");

            Reset();
            manager.PresetStore.Save("New order", [window.universalVars.modDic["c"], window.universalVars.modDic["a"]]);
            Task<ModPresetMatch>? preset = null;
            void ApplyPreset(bool structure)
            {
                if (structure && preset is null) preset = manager.ApplyPresetAsync("New order");
            }
            window.ModInspectionChanged += ApplyPreset;
            try
            {
                var refresh = window.RefreshModsAsync(afterGaming: true);
                ModActivationSmokeTest.Pump(refresh);
                Require(preset is not null, "preset started during exit scan");
                ModActivationSmokeTest.Pump(preset!);
                Expect(["c", "a"], "new preset supersedes old exit restoration");
            }
            finally { window.ModInspectionChanged -= ApplyPreset; }

            Reset();
            File.WriteAllText(options, Options([]));
            using (var cancellation = new CancellationTokenSource())
            {
                void Cancel(bool structure) { if (structure) cancellation.Cancel(); }
                window.ModInspectionChanged += Cancel;
                try
                {
                    var refresh = window.RefreshModsAsync(afterGaming: true, cancellation.Token);
                    ModActivationSmokeTest.Pump(refresh);
                    Require(!refresh.Result && File.ReadAllText(options) == Options([]), "canceled exit scan never writes its snapshot");
                }
                finally { window.ModInspectionChanged -= Cancel; }
            }

            Reset();
            var locked = window.universalVars.modLoaded.ToArray();
            File.WriteAllText(options, Options([]));
            revision = window.ModListRevision;
            bool failed = false;
            // Permit reads, deny atomic replacement, then verify the same valid list can be retried.
            using (File.Open(options, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { ModActivationSmokeTest.Pump(window.RefreshModsAsync(afterGaming: true)); }
                catch (Exception ex) when (FileManager.IsFileError(ex)) { failed = true; }
            }
            Require(failed && window.ModListRevision == revision && File.ReadAllText(options) == Options([]) &&
                !Directory.EnumerateFiles(fixture, "*.tmp").Any(), "failed restoration leaves the file and revision intact without temporary files");
            window.RestoreLockedModList(locked);
            Expect(["a", "b"], "restoration can be retried after a write failure");

            // Both exit restoration and a fully inspected activation must recheck the final game context.
            string otherGame = Directory.CreateDirectory(Path.Combine(fixture, "other-game")).FullName;
            var otherMods = Directory.CreateDirectory(Path.Combine(otherGame, "mods"));
            string otherOptions = Path.Combine(otherGame, "options.set"); File.WriteAllText(otherOptions, Options([]));
            void SwitchGame()
            {
                window.universalVars.localDir = otherMods;
                window.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(otherGame, "binaries", "x64"));
                window.universalVars.optionLoc = otherOptions;
            }
            Reset();
            bool switched = false;
            Task<bool>? replacement = null;
            string original = File.ReadAllText(options);
            void ChangeGame(bool structure)
            {
                if (!structure || switched) return;
                switched = true; SwitchGame(); replacement = window.RefreshModsAsync();
            }
            window.ModInspectionChanged += ChangeGame;
            try
            {
                ModActivationSmokeTest.Pump(window.RefreshModsAsync(afterGaming: true));
                Require(replacement is not null, "switch starts another scan");
                ModActivationSmokeTest.Pump(replacement!);
                Require(File.ReadAllText(options) == original && File.ReadAllText(otherOptions) == Options([]) &&
                    window.universalVars.modLoaded.Count == 0, "obsolete exit restoration cannot write either game's options");
            }
            finally { window.ModInspectionChanged -= ChangeGame; }

            window.universalVars.localDir = mods;
            window.universalVars.gameDir = Directory.CreateDirectory(Path.Combine(game, "binaries", "x64"));
            window.universalVars.optionLoc = options;
            Reset(); switched = false;
            var selected = window.universalVars.modDic["c"];
            void SwitchAfterInspection(bool structure)
            {
                if (structure || switched || selected.IsInspectionPending) return;
                switched = true; SwitchGame();
            }
            window.ModInspectionChanged += SwitchAfterInspection;
            try
            {
                var activation = manager.ActivateModsAsync([selected]);
                ModActivationSmokeTest.Pump(activation, propagate: false);
                Require(switched && activation.IsCanceled && File.ReadAllText(options) == original &&
                    File.ReadAllText(otherOptions) == Options([]), "final activation commit rejects a game changed after inspection");
            }
            finally { window.ModInspectionChanged -= SwitchAfterInspection; }
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
        AppDiagnostics.Log("Mod list restoration self-test passed: normal exit, discovery/completion reorder, disable, subscription removal, presets, external options/settings, cancellation, game switch, failed-write retry and final activation ownership.");
    }
}
