using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GOHShaderModdingSupportLauncher;

public partial class Settings
{
    private bool aliasesReady, refreshingAliases;
    private string? editingAliasName;

    private void InitializeAliases()
    {
        aliasesReady = true;
        main.ModAliases.Changed += RefreshAliasRules;
        RefreshAliasRules();
        if (main.ModAliases.LoadError is not null)
        {
            aliasError.Text = ModAliasStore.Text("MA_LoadError");
            aliasError.IsVisible = true;
            newAliasRule.IsEnabled = saveAliasRule.IsEnabled = false;
        }
    }

    private void RefreshAliasRules()
    {
        if (!aliasesReady) return;
        string query = aliasSearch.Text ?? "";
        var rules = main.ModAliases.Rules.Where(rule => rule.OriginalName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            rule.Alias.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        refreshingAliases = true;
        try
        {
            aliasRules.ItemsSource = rules;
            aliasRules.SelectedItem = rules.FirstOrDefault(rule => rule.OriginalName == editingAliasName);
        }
        finally { refreshingAliases = false; }
        aliasEmpty.IsVisible = rules.Length == 0;
    }

    private void aliasSearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshAliasRules();

    private void aliasRules_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingAliases || !aliasesReady) return;
        var rule = aliasRules.SelectedItem as ModAliasRule;
        editingAliasName = rule?.OriginalName;
        aliasOriginalName.Text = rule?.OriginalName ?? "";
        aliasValue.Text = rule?.Alias ?? "";
        deleteAliasRule.IsEnabled = rule is not null;
        aliasError.IsVisible = false;
    }

    private void newAliasRule_Click(object? sender, RoutedEventArgs e)
    {
        aliasRules.SelectedItem = null;
        editingAliasName = null;
        aliasOriginalName.Text = aliasValue.Text = "";
        deleteAliasRule.IsEnabled = false;
        aliasError.IsVisible = false;
        aliasOriginalName.Focus();
    }

    private void saveAliasRule_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string name = aliasOriginalName.Text ?? "";
            main.ModAliases.Save(editingAliasName, name, aliasValue.Text ?? "");
            editingAliasName = name;
            aliasValue.Text = main.ModAliases.Find(name);
            deleteAliasRule.IsEnabled = true;
            RefreshAliasRules();
            aliasError.IsVisible = false;
        }
        catch (Exception ex) when (ModAliasStore.IsError(ex)) { ShowAliasError(ex); }
    }

    private void deleteAliasRule_Click(object? sender, RoutedEventArgs e)
    {
        if (editingAliasName is null) return;
        try
        {
            main.ModAliases.Delete(editingAliasName);
            newAliasRule_Click(sender, e);
        }
        catch (Exception ex) when (ModAliasStore.IsError(ex)) { ShowAliasError(ex); }
    }

    private void ShowAliasError(Exception ex)
    {
        AppDiagnostics.Log("Unable to save Mod aliases.", ex);
        aliasError.Text = ModAliasStore.Text("MA_SaveError") + "\n" + ex.Message;
        aliasError.IsVisible = true;
    }
}
