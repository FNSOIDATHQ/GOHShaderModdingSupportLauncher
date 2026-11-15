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
        var selection = unloadedMods.SelectedItems.Cast<Mod>().ToArray();
        isSyncingSelection = true;
        try
        {
            unloadedView.Refresh();
            unloadedMods.SelectedItems.Clear();
            foreach (var mod in selection) unloadedMods.SelectedItems.Add(mod);
        }
        finally { isSyncingSelection = false; }
        UpdateAliasAction();
    }

    private async void editModAlias_Click(object? sender, RoutedEventArgs e)
    {
        if (selectedRow.mods is not { Count: 1 }) return;
        string name = selectedRow.mods[0].name;
        var input = new TextBox { Name = "modAliasInput", Text = main.ModAliases.Find(name) ?? "", MaxLength = 1024 };
        var error = new TextBlock { Name = "modAliasError", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
        var save = new Button { Name = "saveModAlias", Content = ModAliasStore.Text("MA_Save"), IsDefault = true };
        var clear = new Button { Name = "clearModAlias", Content = ModAliasStore.Text("MA_Clear"), IsEnabled = main.ModAliases.Find(name) is not null };
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
                    new TextBlock { Text = ModAliasStore.Text("MA_Alias") }, input,
                    new TextBlock { Text = ModAliasStore.Text("MA_Hint"), TextWrapping = TextWrapping.Wrap }, error,
                    new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { save, clear, cancel } }
                }
            }
        };
        foreach (var button in new[] { save, clear, cancel }) button.Margin = new Thickness(4, 0, 0, 4);
        void Commit(bool remove)
        {
            try
            {
                if (remove) main.ModAliases.Delete(name);
                else main.ModAliases.Save(main.ModAliases.Find(name) is null ? null : name, name, input.Text ?? "");
                dialog.Close();
            }
            catch (Exception ex) when (ModAliasStore.IsError(ex)) { error.Text = ModAliasStore.Text("MA_SaveError") + "\n" + ex.Message; }
        }
        save.Click += (_, _) => Commit(false);
        clear.Click += (_, _) => Commit(true);
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await dialog.ShowDialog(main);
    }
}
