using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

// Shipped with --self-test-ui to exercise alias persistence and compiled controls in Native AOT.
internal static class ModAliasUiSmokeTest
{
    internal static void Run(ModManager manager, MainWindow window, string root)
    {
        static void Require(bool ok) { if (!ok) throw new InvalidOperationException("Mod alias UI check failed."); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var store = window.ModAliases;
        var loaded = window.universalVars.modLoaded.ToArray();
        string options = File.ReadAllText(window.universalVars.optionLoc);
        var original = loaded[0].name;
        var presets = new ModPresetStore(Path.Combine(root, "alias-presets"));
        try
        {
            store.Save(null, original, "Display alias");
            store.Save(null, "Display alias", "No recursive substitution");
            Require(loaded[0].name == original && loaded[0].DisplayName == "Display alias" && loaded[0].HasAlias);
            var duplicate = new Mod(original, "Other source", "other-directory", "other-id", false);
            store.Apply(duplicate);
            Require(duplicate.DisplayName == "Display alias" && duplicate.folderName == "other-id");
            var unmatched = new Mod(original.ToUpperInvariant(), "", "", "unmatched", false);
            store.Apply(unmatched);
            Require(!unmatched.HasAlias);
            var reloaded = new ModAliasStore(Path.Combine(root, "mod-aliases.json"));
            Require(reloaded.LoadError is null && reloaded.Find(original) == "Display alias");
            presets.Save("Original", loaded);
            Require(presets.Read("Original").Select(entry => entry.Name).SequenceEqual(loaded.Select(mod => mod.name)));
            Require(ModPresetStore.Match(presets.Read("Original"), window.universalVars.modDic).Mods.SequenceEqual(loaded));

            var settings = new Settings(window);
            var rules = settings.FindControl<DataGrid>("aliasRules")!;
            Require(rules.Columns.Count == 2 && rules.ItemsSource!.Cast<ModAliasRule>().Count() == 2);
            Click(settings.FindControl<Button>("newAliasRule")!);
            settings.FindControl<TextBox>("aliasOriginalName")!.Text = "Not installed";
            settings.FindControl<TextBox>("aliasValue")!.Text = "Offline rule";
            Click(settings.FindControl<Button>("saveAliasRule")!);
            Require(store.Find("Not installed") == "Offline rule");
            settings.FindControl<TextBox>("aliasOriginalName")!.Text = "Renamed rule";
            settings.FindControl<TextBox>("aliasValue")!.Text = "Updated rule";
            Click(settings.FindControl<Button>("saveAliasRule")!);
            Require(store.Find("Not installed") is null && store.Find("Renamed rule") == "Updated rule");
            settings.FindControl<TextBox>("aliasSearch")!.Text = "Updated rule";
            Dispatcher.UIThread.RunJobs();
            Require(rules.ItemsSource!.Cast<ModAliasRule>().Single().OriginalName == "Renamed rule");
            Click(settings.FindControl<Button>("deleteAliasRule")!);
            Require(store.Find("Renamed rule") is null);
            Require(window.universalVars.modLoaded.SequenceEqual(loaded) &&
                File.ReadAllText(window.universalVars.optionLoc) == options);
            Require(manager.FindControl<DataGrid>("loadedMods")!.Columns[0] is DataGridTemplateColumn &&
                manager.FindControl<DataGrid>("unloadedMods")!.Columns[0] is DataGridTemplateColumn);
        }
        finally
        {
            foreach (var rule in store.Rules) store.Delete(rule.OriginalName);
            presets.Delete("Original");
        }
        Require(loaded.All(mod => !mod.HasAlias && mod.DisplayName == mod.name));
        AppDiagnostics.Log("Mod alias self-test passed: exact names, shared rules, nonrecursive display, source-generated persistence, settings CRUD, unchanged load order and presets.");
    }
}
