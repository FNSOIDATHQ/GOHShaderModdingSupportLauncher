using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Globalization;

namespace GOHShaderModdingSupportLauncher;

// Shipped with --self-test-ui to exercise alias persistence and compiled controls in Native AOT.
internal static class ModAliasUiSmokeTest
{
    internal static void Run(ModManager manager, MainWindow window, string root)
    {
        CheckMatching(Path.Combine(root, "alias-matching.json"));
        var store = window.ModAliases;
        var loaded = window.universalVars.modLoaded.ToArray();
        string options = File.ReadAllText(window.universalVars.optionLoc);
        var original = loaded[0].name;
        var presets = new ModPresetStore(Path.Combine(root, "alias-presets"));
        try
        {
            store.Save(original, "Display alias");
            store.Save("Display alias", "No recursive substitution");
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
            Require(rules.Columns.Count == 3 && rules.ItemsSource!.Cast<ModAliasRule>().Count() == 2);
            Require(settings.FindControl<Button>("newAliasRule") is null);
            settings.FindControl<TextBox>("aliasOriginalName")!.Text = "Not installed";
            settings.FindControl<TextBox>("aliasValue")!.Text = "Offline rule";
            Click(settings.FindControl<Button>("saveAliasRule")!);
            Require(store.Find("Not installed") == "Offline rule");
            settings.FindControl<TextBox>("aliasOriginalName")!.Text = "Another rule";
            settings.FindControl<TextBox>("aliasValue")!.Text = "Updated rule";
            Click(settings.FindControl<Button>("saveAliasRule")!);
            Require(store.Find("Not installed") == "Offline rule" && store.Find("Another rule") == "Updated rule");
            settings.FindControl<TextBox>("aliasOriginalName")!.Text = "Not installed";
            settings.FindControl<TextBox>("aliasValue")!.Text = "New offline alias";
            Click(settings.FindControl<Button>("saveAliasRule")!);
            Require(store.Find("Not installed") == "New offline alias" && store.Find("Another rule") == "Updated rule");
            settings.FindControl<TextBox>("aliasSearch")!.Text = "Updated rule";
            Dispatcher.UIThread.RunJobs();
            rules.SelectedItem = rules.ItemsSource!.Cast<ModAliasRule>().Single(rule => rule.OriginalName == "Another rule");
            Click(settings.FindControl<Button>("deleteAliasRule")!);
            Require(store.Find("Another rule") is null && store.Find("Not installed") == "New offline alias");
            CheckExtendedEditor(settings, window);
            Require(window.universalVars.modLoaded.SequenceEqual(loaded) &&
                File.ReadAllText(window.universalVars.optionLoc) == options);
            Require(manager.FindControl<DataGrid>("loadedMods")!.Columns[0] is DataGridTemplateColumn &&
                manager.FindControl<DataGrid>("unloadedMods")!.Columns[0] is DataGridTemplateColumn);
        }
        finally
        {
            foreach (var rule in store.Rules) store.Delete(rule.Key);
            store.SetItemMapping(false);
            presets.Delete("Original");
        }
        Require(loaded.All(mod => !mod.HasAlias && mod.DisplayName == mod.name));
        AppDiagnostics.Log("Mod alias self-test passed: legacy migration, field priority, save by target, Workshop ID and URL, bilingual preview, and load order and presets.");
    }

    private static void CheckMatching(string path)
    {
        File.WriteAllText(path, """{"version":1,"aliases":{"system imp v0.1":"Legacy alias","平衡":"Second pass"}}""");
        var store = new ModAliasStore(path);
        Require(store.LoadError is null && !store.ItemMappingEnabled && store.Find("system imp v0.1") == "Legacy alias");
        store.Save("system", "Longer destination");
        store.Save("system imp", "系统改进");
        store.Save("stem imp v", "Overlap");
        store.Save("balance", "平衡");
        store.Save("平衡", "Recursive");
        Require(store.Match("system imp v0.1 extended").DisplayName == "Legacy alias extended");
        Require(store.Match("system imp + balance; system").DisplayName == "系统改进 + 平衡; Longer destination");
        Require(store.Match("system impbalance").DisplayName == "系统改进平衡");
        Require(store.Match("SYSTEM IMP").Kind is null);
        store.Delete("system imp v0.1");
        Require(store.Match("system imp v0.1").DisplayName == "系统改进 v0.1");

        string url = "https://steamcommunity.com/sharedfiles/filedetails/?id=000123&utm_source=test#part";
        var item = ModAliasItems.DirectItem(new(url));
        Require(item?.Key == ModAliasItems.DirectItem(new("https://steamcommunity.com/workshop/filedetails/?id=123"))?.Key && item is not null);
        foreach (string invalid in new[] { url.Replace("000123", "123&id=124"), url.Replace("000123", "0"),
            url.Replace("steamcommunity.com", "steamcommunity.com.example"), url.Replace("filedetails", "browse") })
            Require(ModAliasItems.DirectItem(new(invalid)) is null);
        var saved = new ModAliasRule(item!.Key, "Workshop alias") { Kind = ModAliasKind.Item };
        store.Save(saved);
        Require(store.Match("system imp v0.1", item.Key).DisplayName == "系统改进 v0.1");
        store.SetItemMapping(true);
        var reloaded = new ModAliasStore(path);
        Require(reloaded.LoadError is null && reloaded.ItemMappingEnabled &&
            reloaded.Match("Renamed by its author", item.Key).DisplayName == "Workshop alias" &&
            reloaded.Match("system imp", "workshop:400750:124").DisplayName == "系统改进");
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool rejected = false;
            try { reloaded.SetItemMapping(false); }
            catch (IOException) { rejected = true; }
            Require(rejected && reloaded.ItemMappingEnabled && reloaded.Find(saved.Key)?.Alias == "Workshop alias");
        }

        string versionTwoPath = path + ".v2";
        File.WriteAllText(versionTwoPath, """{"version":2,"rules":[{"originalName":"shared","alias":"Existing field","kind":1},{"originalName":"shared","alias":"Old exact"},{"originalName":"legacy","alias":"Migrated field","kind":0}]}""");
        var migrated = new ModAliasStore(versionTwoPath);
        Require(migrated.LoadError is null && migrated.Rules.Length == 2 &&
            migrated.Match("shared + legacy").DisplayName == "Existing field + Migrated field");
    }

    private static void CheckExtendedEditor(Settings settings, MainWindow window)
    {
        T Find<T>(string name) where T : Control => settings.FindControl<T>(name)!;
        Find<TextBox>("aliasSearch").Text = "";
        var kind = Find<ComboBox>("aliasRuleKind");
        var alias = Find<TextBox>("aliasValue");
        var save = Find<Button>("saveAliasRule");
        Require(kind.Items.Count == 2);
        kind.SelectedIndex = 0;
        Find<TextBox>("aliasOriginalName").Text = "system imp";
        alias.Text = "系统改进";
        Find<TextBox>("aliasPreviewName").Text = "system imp v0.1";
        Dispatcher.UIThread.RunJobs();
        Require(Find<TextBlock>("aliasPreview").Text == string.Format(ModAliasStore.Text("MA_Preview"), "系统改进 v0.1"));
        Click(save);
        Require(window.ModAliases.Match("system imp v0.1").Kind == ModAliasKind.Field);

        var workshop = window.universalVars.modDic.Values.Single(mod => mod.Inspection?.Source == ModSourceKind.Workshop);
        kind.SelectedIndex = 1;
        Find<TextBox>("aliasWorkshopSearch").Text = workshop.name.ToUpperInvariant();
        Dispatcher.UIThread.RunJobs();
        var choices = Find<ComboBox>("aliasWorkshopResults");
        Require(choices.Items.Count == 1);
        choices.SelectedIndex = 0;
        alias.Text = "Workshop from name";
        Click(save);
        Require(!workshop.HasAlias);
        var enable = Find<CheckBox>("enableItemAliases");
        enable.IsChecked = true;
        Click(enable);
        Require(workshop.DisplayName == "Workshop from name");

        Find<TextBox>("aliasUrl").Text = "https://steamcommunity.com/sharedfiles/filedetails/?id=456";
        Dispatcher.UIThread.RunJobs();
        Click(Find<Button>("aliasResolve"));
        alias.Text = "Future Workshop item";
        Click(save);
        Require(window.ModAliases.Match("New title", "workshop:400750:456").DisplayName == "Future Workshop item");
        enable.IsChecked = false;
        Click(enable);
        string workshopKey = ModAliasItems.ForMod(workshop)!.Key;
        Require(!workshop.HasAlias && window.ModAliases.Find(new ModAliasKey(ModAliasKind.Item, "workshop:400750:456")) is not null &&
            window.ModAliases.Find(new ModAliasKey(ModAliasKind.Item, workshopKey))?.Alias == "Workshop from name");

        var previous = LocalizedStrings.Instance.Culture;
        try
        {
            foreach (var (language, field, item) in new[] { ("zh-CN", "字段映射", "指定物品"), ("en-US", "Field mapping", "Specific item") })
            {
                LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
                Dispatcher.UIThread.RunJobs();
                Require((kind.Items[0] as ComboBoxItem)?.Content as string == field &&
                    (kind.Items[1] as ComboBoxItem)?.Content as string == item);
            }
            LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
            Dispatcher.UIThread.RunJobs();
            Require(Find<TextBox>("aliasPreviewName").PlaceholderText == "输入测试名称预览映射结果" &&
                ModAliasStore.Text("MA_Preview") == "映射结果： {0}");
        }
        finally { LocalizedStrings.Instance.SetCulture(previous); }
    }

    private static void Require(bool ok, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!ok) throw new InvalidOperationException($"Mod alias UI check failed at line {line}.");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
