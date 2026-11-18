using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    private void UpdateAliasAction()
    {
        editModAlias.IsEnabled = selectedRow.mods?.Count == 1;
        UpdateSubscriptionAction();
    }

    private void RefreshAliases()
    {
        foreach (var mod in loadedItems.Concat(unloadedItems).Concat(main.universalVars.modDic.Values).Distinct()) main.ModAliases.Apply(mod);
        RefreshSourceView();
    }

    private async void editModAlias_Click(object? sender, RoutedEventArgs e)
    {
        if (selectedRow.mods is not { Count: 1 }) return;
        var mod = selectedRow.mods[0];
        string name = mod.name;
        var context = main.ModDirectoryContext;
        var store = main.ModAliases;
        var item = store.ItemMappingEnabled ? ModAliasItems.ForMod(mod) : null;
        var scope = new ComboBox { Name = "modAliasScope", IsVisible = item is not null,
            ItemsSource = new[] { ModAliasStore.Text("MA_OnlyItem"), ModAliasStore.KindLabel(ModAliasKind.Field) }, SelectedIndex = 0 };
        var input = new TextBox { Name = "modAliasInput", MaxLength = 1024 };
        var error = new TextBlock { Name = "modAliasError", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
        var save = new Button { Name = "saveModAlias", Content = ModAliasStore.Text("MA_Save"), IsDefault = true };
        var clear = new Button { Name = "clearModAlias", Content = ModAliasStore.Text("MA_Clear") };
        var cancel = new Button { Content = ModAliasStore.Text("MA_Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = ModAliasStore.Text("MA_EditAlias"), Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = ModAliasStore.Text("MA_OriginalName") },
                    new TextBox { Text = name, IsReadOnly = true },
                    scope,
                    new TextBlock { Text = ModAliasStore.Text("MA_Alias") }, input,
                    error,
                    new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { save, clear, cancel } }
                }
            }
        };
        foreach (var button in new[] { save, clear, cancel }) button.Margin = new Thickness(4, 0, 0, 4);
        ModAliasKey SelectedKey() => item is not null && scope.SelectedIndex == 0
            ? new(ModAliasKind.Item, item.Key) : new(ModAliasKind.Field, name);
        void LoadRule()
        {
            var rule = store.Find(SelectedKey());
            input.Text = rule?.Alias ?? "";
            clear.IsEnabled = rule is not null;
            error.Text = "";
        }
        void Commit(bool remove)
        {
            try
            {
                var key = SelectedKey();
                if (context != main.ModDirectoryContext || (key.Kind == ModAliasKind.Item
                    ? !store.ItemMappingEnabled || ModAliasItems.ForMod(mod)?.Key != key.Target
                    : mod.name != name)) throw new FormatException(ModAliasStore.Text("MA_TargetChanged"));
                if (remove) store.Delete(key);
                else store.Save(new(key.Target, input.Text ?? "")
                    { Kind = key.Kind, ItemTitle = key.Kind == ModAliasKind.Item ? item?.Title : null });
                dialog.Close();
            }
            catch (Exception ex) when (ModAliasStore.IsError(ex)) { error.Text = ModAliasStore.Text("MA_SaveError") + "\n" + ex.Message; }
        }
        scope.SelectionChanged += (_, _) => LoadRule();
        LoadRule();
        save.Click += (_, _) => Commit(false);
        clear.Click += (_, _) => Commit(true);
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await dialog.ShowDialog(main);
    }
}
