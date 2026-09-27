using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GOHShaderModdingSupportLauncher;

// Shared by the headless regression runner and the published Native AOT UI check.
internal static class PresetUiSmokeTest
{
    internal static void Run(ModManager manager, MainWindow window, string root)
    {
        static void Require(bool ok) { if (!ok) throw new InvalidOperationException("Preset UI check failed."); }
        var originalStore = manager.PresetStore;
        var originalOptions = window.universalVars.optionLoc;
        var originalLocal = window.universalVars.localDir;
        var originalWorkshop = window.universalVars.workshopDir;
        var fixture = Directory.CreateDirectory(Path.Combine(root, "preset-ui"));
        var mods = Directory.CreateDirectory(Path.Combine(fixture.FullName, "mods"));
        foreach (var id in new[] { "z", "a", "b" })
        {
            var dir = Directory.CreateDirectory(Path.Combine(mods.FullName, id));
            File.WriteAllText(Path.Combine(dir.FullName, "mod.info"), "{name \"Same\"}\n");
        }
        var options = Path.Combine(fixture.FullName, "options.set");
        File.WriteAllText(options, "{options\n{mods\n\"a:0\"\n}\n{after 1}\n}\n");
        try
        {
            window.universalVars.optionLoc = options;
            window.universalVars.localDir = mods;
            window.universalVars.workshopDir = null;
            window.RefreshMods();
            manager.RefreshView();
            var store = manager.PresetStore = new ModPresetStore(Path.Combine(fixture.FullName, "presets"));
            var available = window.universalVars.modDic;
            store.Save("Ordered", [available["z"], new Mod("Missing", "", "", "missing", false), available["b"]]);
            manager.RefreshPresets("Ordered");
            var list = manager.FindControl<ComboBox>("presetList")!;
            Require(list.SelectedItem as string == "Ordered" && manager.FindControl<Button>("loadPreset")!.IsEnabled);
            Require(manager.ApplyPreset("Ordered").Missing.Single().Id == "missing");
            void CheckOrder(params string[] expected)
            {
                Require(window.universalVars.modLoaded.Select(m => m.folderName).SequenceEqual(expected));
                Require(manager.FindControl<DataGrid>("loadedMods")!.ItemsSource!.Cast<Mod>().Select(m => m.folderName).SequenceEqual(expected));
                Require(FileManager.ReadLoadedModNames(new StringReader(File.ReadAllText(options))).SequenceEqual(expected));
                Require(window.universalVars.modDic.Values.All(m => m.hasLoad == expected.Contains(m.folderName)));
            }
            CheckOrder("z", "b");
            Require(File.ReadAllText(options).Contains("{after 1}"));
            store.Save("Saved", window.universalVars.modLoaded);
            Require(store.Read("Saved").Select(e => e.Id).SequenceEqual(new[] { "z", "b" }));
            // A fresh scan/read, as after restarting, must retain the saved order.
            window.RefreshMods();
            manager.RefreshView();
            CheckOrder("z", "b");
            var before = window.universalVars.modLoaded;
            var dictionary = window.universalVars.modDic;
            store.Save("Empty", []);
            using (var locked = new FileStream(options, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { manager.ApplyPreset("Empty"); } catch (IOException) { failed = true; }
                Require(failed && ReferenceEquals(before, window.universalVars.modLoaded) && ReferenceEquals(dictionary, window.universalVars.modDic));
            }
            CheckOrder("z", "b");
            File.WriteAllText(store.PathFor("Bad"), "version = 99\nmods = []");
            bool invalid = false;
            try { manager.ApplyPreset("Bad"); } catch (FormatException) { invalid = true; }
            Require(invalid);
            CheckOrder("z", "b");
            store.Save("AllMissing", [new Mod("Missing", "", "", "gone", false)]);
            Require(manager.ApplyPreset("AllMissing").Missing.Count == 1);
            CheckOrder();
            manager.ApplyPreset("Saved");
            manager.ApplyPreset("Empty");
            CheckOrder();
            store.Rename("Saved", "Renamed");
            manager.RefreshPresets("Renamed");
            Require(list.SelectedItem as string == "Renamed");
            manager.FindControl<Button>("loadPreset")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CheckOrder("z", "b");
            foreach (var name in store.List()) store.Delete(name);
            manager.FindControl<Button>("refresh")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(list.ItemCount == 0 && !manager.FindControl<Button>("loadPreset")!.IsEnabled);
            CheckOrder("z", "b");
        }
        finally
        {
            manager.PresetStore = originalStore;
            window.universalVars.optionLoc = originalOptions;
            window.universalVars.localDir = originalLocal;
            window.universalVars.workshopDir = originalWorkshop;
            window.RefreshMods();
            manager.RefreshView();
            manager.RefreshPresets();
        }
    }
}
