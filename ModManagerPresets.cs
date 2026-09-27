using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using GOHShaderModdingSupportLauncher.Properties;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    internal ModPresetStore PresetStore { get; set; } = new(ModPresetStore.DefaultDirectory);

    internal void RefreshPresets(string? select = null)
    {
        try
        {
            select ??= presetList.SelectedItem as string;
            var names = PresetStore.List();
            presetList.ItemsSource = names;
            presetList.SelectedItem = names.FirstOrDefault(name => string.Equals(name, select, StringComparison.OrdinalIgnoreCase));
            if (presetList.SelectedIndex < 0 && names.Length > 0) presetList.SelectedIndex = 0;
            UpdatePresetButtons();
        }
        catch (Exception ex) when (IsPresetError(ex))
        {
            presetList.ItemsSource = Array.Empty<string>();
            UpdatePresetButtons();
            ShowPresetError(ex);
        }
    }

    private void UpdatePresetButtons()
    {
        bool selected = presetList.SelectedItem is string;
        loadPreset.IsEnabled = selected;
        overwritePreset.IsEnabled = selected;
        renamePreset.IsEnabled = selected;
        deletePreset.IsEnabled = selected;
    }

    private void presetList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePresetButtons();

    internal ModPresetMatch ApplyPreset(string name)
    {
        var entries = PresetStore.Read(name);
        var available = main.ScanMods();
        var match = ModPresetStore.Match(entries, available);
        CommitLoadedMods(match.Mods, available);
        return match;
    }

    private void loadPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (presetList.SelectedItem is not string name) return;
        try
        {
            var match = ApplyPreset(name);
            if (match.Missing.Count > 0)
                MessageBox.Show(PresetText.Get("P_MissingMods") + "\n\n" +
                    string.Join("\n", match.Missing.Select(entry => $"{entry.Name} ({entry.Id})")),
                    i18n.Universal_Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (IsPresetError(ex)) { ShowPresetError(ex); }
    }

    private async void savePreset_Click(object? sender, RoutedEventArgs e)
    {
        // Capture exactly what the user chose to save before showing any dialogs.
        var mods = loadedItems.ToArray();
        var name = await PromptPresetName(PresetText.Get("P_Save"), "");
        if (name is null) return;
        try
        {
            bool overwrite = PresetStore.Exists(name);
            if (overwrite && await MessageBox.ConfirmAsync(main,
                string.Format(PresetText.Get("P_OverwriteConfirm"), name), i18n.Universal_Notice) != MessageBoxResult.Yes) return;
            PresetStore.Save(name, mods, overwrite);
            RefreshPresets(name);
        }
        catch (Exception ex) when (IsPresetError(ex)) { ShowPresetError(ex); }
    }

    private async void overwritePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (presetList.SelectedItem is not string name) return;
        var mods = loadedItems.ToArray();
        try
        {
            if (await MessageBox.ConfirmAsync(main,
                string.Format(PresetText.Get("P_OverwriteConfirm"), name), i18n.Universal_Notice) != MessageBoxResult.Yes) return;
            PresetStore.Save(name, mods, overwrite: true);
            RefreshPresets(name);
        }
        catch (Exception ex) when (IsPresetError(ex)) { ShowPresetError(ex); }
    }

    private async void renamePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (presetList.SelectedItem is not string current) return;
        var name = await PromptPresetName(PresetText.Get("P_Rename"), current);
        if (name is null) return;
        try { PresetStore.Rename(current, name); RefreshPresets(name); }
        catch (Exception ex) when (IsPresetError(ex)) { ShowPresetError(ex); }
    }

    private async void deletePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (presetList.SelectedItem is not string name) return;
        if (await MessageBox.ConfirmAsync(main, string.Format(PresetText.Get("P_DeleteConfirm"), name),
            i18n.Universal_Notice) != MessageBoxResult.Yes) return;
        try { PresetStore.Delete(name); RefreshPresets(); }
        catch (Exception ex) when (IsPresetError(ex)) { ShowPresetError(ex); }
    }

    private async Task<string?> PromptPresetName(string title, string initial)
    {
        var input = new TextBox { Text = initial, Watermark = PresetText.Get("P_Name") };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var accept = new Button { Content = PresetText.Get("P_OK"), IsDefault = true };
        var cancel = new Button { Content = PresetText.Get("P_Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = PresetText.Get("P_Name") }, input, error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8, Children = { accept, cancel } }
                }
            }
        };
        accept.Click += (_, _) =>
        {
            try
            {
                var name = (input.Text ?? "").Trim();
                if (name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
                dialog.Close(ModPresetStore.ValidateName(name));
            }
            catch (FormatException ex) { error.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await dialog.ShowDialog<string?>(main);
    }

    private static bool IsPresetError(Exception ex) => FileManager.IsFileError(ex) || ex is FormatException;

    private static void ShowPresetError(Exception ex)
    {
        AppDiagnostics.Log("Mod list / preset operation failed.", ex);
        MessageBox.Show(PresetText.Get("P_Error") + "\n\n" + ex.Message, i18n.Universal_Warning,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
