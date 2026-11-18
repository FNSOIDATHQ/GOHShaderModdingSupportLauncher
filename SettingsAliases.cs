using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public partial class Settings
{
    private bool aliasesReady, refreshingAliases;
    private ModAliasKey? selectedAlias;
    private ModAliasItem? aliasItem;
    private string aliasItemAddress = "";
    private CancellationTokenSource? aliasLookup;

    private ModAliasKind AliasKind => aliasRuleKind.SelectedIndex == 1 ? ModAliasKind.Item : ModAliasKind.Field;

    private void InitializeAliases()
    {
        aliasesReady = true;
        main.ModAliases.Changed += RefreshAliasRules;
        Loaded += (_, _) =>
        {
            main.ModInspectionChanged += AliasModsChanged;
            main.ModOriginsChanged += RefreshAliasRules;
            LocalizedStrings.Instance.PropertyChanged += AliasLanguageChanged;
            RefreshAliasRules();
            RefreshWorkshopAliases();
        };
        Unloaded += (_, _) =>
        {
            main.ModInspectionChanged -= AliasModsChanged;
            main.ModOriginsChanged -= RefreshAliasRules;
            LocalizedStrings.Instance.PropertyChanged -= AliasLanguageChanged;
            CancelAliasLookup();
        };
        RefreshAliasRules();
        UpdateAliasEditor();
        if (main.ModAliases.LoadError is not null)
        {
            aliasError.Text = ModAliasStore.Text("MA_LoadError");
            aliasError.IsVisible = true;
            saveAliasRule.IsEnabled = enableItemAliases.IsEnabled = false;
        }
    }

    private void AliasLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizedStrings.MA_Title)) RefreshAliasRules();
    }

    private void AliasModsChanged(bool structureChanged)
    {
        RefreshWorkshopAliases();
        if (structureChanged) RefreshAliasRules();
    }

    private void RefreshAliasRules()
    {
        if (!aliasesReady) return;
        string query = aliasSearch.Text ?? "";
        var saved = main.ModAliases.Rules;
        var titles = saved.Any(rule => rule.Kind == ModAliasKind.Item)
            ? CurrentAliasItems().Where(item => item.Title is not null).ToLookup(item => item.Key, item => item.Title!) : null;
        var rules = saved.Select(rule =>
        {
            var currentTitles = rule.Kind == ModAliasKind.Item ? titles?[rule.OriginalName] : null;
            bool matches = Contains(rule.TargetLabel, query) || Contains(rule.Alias, query) || Contains(rule.KindLabel, query) ||
                currentTitles?.Any(title => Contains(title, query)) == true;
            string? title = rule.ItemTitle ?? currentTitles?.FirstOrDefault();
            return (Matches: matches, Rule: title == rule.ItemTitle ? rule : rule with { ItemTitle = title });
        }).Where(row => row.Matches).Select(row => row.Rule).ToArray();
        refreshingAliases = true;
        try
        {
            aliasRules.ItemsSource = rules;
            aliasRules.SelectedItem = rules.FirstOrDefault(rule => rule.Key == selectedAlias);
            deleteAliasRule.IsEnabled = aliasRules.SelectedItem is ModAliasRule;
            enableItemAliases.IsChecked = main.ModAliases.ItemMappingEnabled;
        }
        finally { refreshingAliases = false; }
        aliasEmpty.IsVisible = rules.Length == 0;
        PreviewAlias();
    }

    private void aliasSearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshAliasRules();

    private void aliasRules_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingAliases || !aliasesReady) return;
        EditAliasRule(aliasRules.SelectedItem as ModAliasRule);
    }

    private void EditAliasRule(ModAliasRule? rule)
    {
        CancelAliasLookup();
        selectedAlias = rule?.Key;
        refreshingAliases = true;
        try
        {
            aliasRuleKind.SelectedIndex = rule?.Kind == ModAliasKind.Item ? 1 : 0;
            aliasItem = rule is { Kind: ModAliasKind.Item } ? new(rule.OriginalName, rule.ItemTitle) : null;
            aliasUrl.Text = aliasItemAddress = "";
            aliasOriginalName.Text = rule is { Kind: not ModAliasKind.Item } ? rule.OriginalName : "";
            aliasValue.Text = rule?.Alias ?? "";
            aliasPreviewName.Text = "";
        }
        finally { refreshingAliases = false; }
        deleteAliasRule.IsEnabled = rule is not null;
        aliasError.IsVisible = false;
        UpdateAliasEditor();
    }

    private void aliasRuleKind_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!aliasesReady || refreshingAliases) return;
        if (AliasKind != ModAliasKind.Item) CancelAliasLookup();
        UpdateAliasEditor();
    }

    private void UpdateAliasEditor()
    {
        bool item = AliasKind == ModAliasKind.Item;
        aliasNameEditor.IsVisible = aliasPreviewName.IsVisible = !item;
        aliasItemEditor.IsVisible = item;
        RefreshWorkshopAliases();
        UpdateAliasTarget();
    }

    private void enableItemAliases_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            main.ModAliases.SetItemMapping(enableItemAliases.IsChecked == true);
            aliasError.IsVisible = false;
        }
        catch (Exception ex) when (ModAliasStore.IsError(ex))
        {
            enableItemAliases.IsChecked = main.ModAliases.ItemMappingEnabled;
            ShowAliasError(ex);
        }
    }

    private IEnumerable<ModAliasItem> CurrentAliasItems(bool workshopOnly = false)
    {
        var root = main.ModDirectoryContext.Workshop;
        var game = workshopOnly ? null : main.SubscriptionGameDirectory;
        foreach (var mod in main.universalVars.modDic.Values)
        {
            bool current = mod.Inspection is { Source: ModSourceKind.Workshop } workshop
                ? root is not null && string.Equals(Path.GetDirectoryName(workshop.DirectoryPath), root, SubscriptionPaths.Comparison)
                : game is not null && string.Equals(mod.SubscriptionLink?.GameDirectory, game, SubscriptionPaths.Comparison);
            if (current && ModAliasItems.ForMod(mod) is { } item) yield return item;
        }
    }

    private void aliasWorkshopSearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshWorkshopAliases();

    private void RefreshWorkshopAliases()
    {
        if (!aliasesReady || refreshingAliases || AliasKind != ModAliasKind.Item) return;
        string query = aliasWorkshopSearch.Text ?? "";
        var items = CurrentAliasItems(workshopOnly: true).DistinctBy(item => item.Key)
            .Where(item => Contains(item.Title ?? "", query) || Contains(item.Key, query))
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        refreshingAliases = true;
        try
        {
            aliasWorkshopResults.ItemsSource = items;
            aliasWorkshopResults.SelectedItem = items.FirstOrDefault(item => item.Key == aliasItem?.Key);
        }
        finally { refreshingAliases = false; }
    }

    private void aliasWorkshopResults_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!aliasesReady || refreshingAliases || aliasWorkshopResults.SelectedItem is not ModAliasItem item) return;
        CancelAliasLookup();
        aliasUrl.Text = aliasItemAddress = "";
        aliasItem = item;
        aliasError.IsVisible = false;
        UpdateAliasTarget();
    }

    private void aliasUrl_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!aliasesReady || refreshingAliases || (aliasUrl.Text ?? "") == aliasItemAddress) return;
        CancelAliasLookup();
        aliasItem = null;
        RefreshWorkshopAliases();
        UpdateAliasTarget();
    }

    private async void aliasResolve_Click(object? sender, RoutedEventArgs e)
    {
        string input = aliasUrl.Text ?? "";
        CancelAliasLookup();
        using var lookup = new CancellationTokenSource();
        aliasLookup = lookup;
        aliasResolve.IsEnabled = false;
        aliasError.IsVisible = false;
        try
        {
            if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var address)) throw ModAliasItems.InvalidAddress();
            var resolved = ModAliasItems.DirectItem(address);
            if (resolved is null)
            {
                if (!ModPageRouter.TryResolveSupportedPage(address, out var route) ||
                    route is not { Provider: "moddb" or "github", Kind: ModPageKind.Overview })
                    throw ModAliasItems.InvalidAddress();
                var runtime = main.SubscriptionRuntime;
                resolved = await Task.Run(() => ModAliasItems.ResolveProjectAsync(route, runtime, lookup.Token), lookup.Token);
            }
            if (aliasLookup != lookup || AliasKind != ModAliasKind.Item || aliasUrl.Text != input) return;
            var installed = CurrentAliasItems().FirstOrDefault(item => item.Key == resolved.Key);
            aliasItem = resolved.Title is not null ? resolved : installed ?? resolved;
            aliasItemAddress = input;
            RefreshWorkshopAliases();
            UpdateAliasTarget();
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (Exception ex) when (ModAliasStore.IsError(ex))
        {
            if (aliasLookup == lookup) ShowAliasError(ex, "MA_ResolveError");
        }
        finally
        {
            if (aliasLookup == lookup)
            {
                aliasLookup = null;
                aliasResolve.IsEnabled = true;
            }
        }
    }

    private void CancelAliasLookup()
    {
        var pending = aliasLookup;
        aliasLookup = null;
        pending?.Cancel();
        aliasResolve.IsEnabled = true;
    }

    private void UpdateAliasTarget()
    {
        aliasItemTarget.Text = aliasItem?.ToString();
        aliasItemTarget.IsVisible = aliasItem is not null;
        PreviewAlias();
    }

    private ModAliasRule DraftAliasRule() => new(AliasKind == ModAliasKind.Item ? aliasItem?.Key ?? "" : aliasOriginalName.Text ?? "",
        (aliasValue.Text ?? "").Trim()) { Kind = AliasKind, ItemTitle = AliasKind == ModAliasKind.Item ? aliasItem?.Title : null };

    private void aliasDraft_TextChanged(object? sender, TextChangedEventArgs e) => PreviewAlias();

    private void PreviewAlias()
    {
        if (!aliasesReady || refreshingAliases) return;
        var draft = DraftAliasRule();
        aliasPreview.IsVisible = ModAliasStore.ValidText(draft.OriginalName) && ModAliasStore.ValidText(draft.Alias);
        if (!aliasPreview.IsVisible) return;
        string title = AliasKind == ModAliasKind.Item ? aliasItem?.Title ?? draft.OriginalName :
            string.IsNullOrEmpty(aliasPreviewName.Text) ? draft.OriginalName : aliasPreviewName.Text;
        aliasPreview.Text = string.Format(ModAliasStore.Text("MA_Preview"), main.ModAliases.Preview(title, draft));
    }

    private void saveAliasRule_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (AliasKind == ModAliasKind.Item && (aliasItem is null || (aliasUrl.Text ?? "") != aliasItemAddress))
                throw ModAliasItems.InvalidAddress();
            var draft = DraftAliasRule();
            main.ModAliases.Save(draft);
            selectedAlias = draft.Key;
            aliasValue.Text = draft.Alias;
            RefreshAliasRules();
            aliasError.IsVisible = false;
        }
        catch (Exception ex) when (ModAliasStore.IsError(ex)) { ShowAliasError(ex); }
    }

    private void deleteAliasRule_Click(object? sender, RoutedEventArgs e)
    {
        if (aliasRules.SelectedItem is not ModAliasRule rule) return;
        try
        {
            main.ModAliases.Delete(rule.Key);
            EditAliasRule(null);
            aliasOriginalName.Focus();
        }
        catch (Exception ex) when (ModAliasStore.IsError(ex)) { ShowAliasError(ex); }
    }

    private void ShowAliasError(Exception ex, string message = "MA_SaveError")
    {
        AppDiagnostics.Log(ModAliasStore.Text(message), ex);
        aliasError.Text = ModAliasStore.Text(message) + "\n" + ex.Message;
        aliasError.IsVisible = true;
    }

    private static bool Contains(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
