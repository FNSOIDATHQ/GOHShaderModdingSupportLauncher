using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    private void UpdateSubscriptionAction() => viewModSubscription.IsVisible =
        selectedRow.mods is { Count: 1 } selected && selected[0].SubscriptionLink is not null;

    private async void viewModSubscription_Click(object? sender, RoutedEventArgs e)
    {
        if (selectedRow.mods is not { Count: 1 } selected) return;
        try { await main.OpenModSubscriptionAsync(selected[0]); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppDiagnostics.Log("Unable to open installed Mod subscription.", ex); }
    }

    internal void RefreshSourceView()
    {
        if (!hasInit) return;
        var selection = unloadedMods.SelectedItems.Cast<Mod>().ToArray();
        var scroll = unloadedMods.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        isSyncingSelection = true;
        try
        {
            unloadedView.Refresh();
            unloadedMods.SelectedItems.Clear();
            foreach (var mod in selection) unloadedMods.SelectedItems.Add(mod);
            if (offset is { } position && scroll is not null) scroll.Offset = position;
        }
        finally { isSyncingSelection = false; }
        UpdateSubscriptionAction();
    }

    internal bool SelectInstalledDirectory(string directory)
    {
        string path;
        try { path = SubscriptionPaths.Normalize(directory); }
        catch (ArgumentException) { return false; }
        var mod = loadedItems.Concat(unloadedItems).FirstOrDefault(item =>
            item.Inspection?.Source == ModSourceKind.Local &&
            string.Equals(item.path, path, SubscriptionPaths.Comparison));
        if (mod is null) return false;
        var grid = loadedItems.Contains(mod) ? loadedMods : unloadedMods;
        grid.SelectedItem = mod;
        grid.ScrollIntoView(mod, null);
        grid.Focus();
        return true;
    }

    internal static bool CanCollectShaderCache(Mod mod) =>
        mod.Inspection?.Source == ModSourceKind.Local && !mod.IsMissingReference;
}
