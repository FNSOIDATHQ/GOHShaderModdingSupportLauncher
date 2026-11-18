using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GOHShaderModdingSupportLauncher.ModSubscriptions;
using GOHShaderModdingSupportLauncher.Properties;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    private void UpdateOriginActions()
    {
        viewModSubscription.IsVisible = selectedRow.mods is [{ SubscriptionLink: not null }];
        openModRemotePage.IsVisible = SelectedModRemotePage is not null;
    }

    internal Uri? SelectedModRemotePage
    {
        get
        {
            if (selectedRow.mods is not [{ IsMissingReference: false } mod]) return null;
            if (mod.Inspection?.Source == ModSourceKind.Workshop)
                return mod.WorkshopItemId is { } id ? new Uri("steam://url/CommunityFilePage/" + id) : null;
            return mod.SubscriptionLink?.RemotePage is { } page && ModDocumentParser.IsLink(page) ? page : null;
        }
    }

    private void openModRemotePage_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedModRemotePage is not { } page) return;
        try { using var process = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex)
        {
            AppDiagnostics.Log("Unable to open Mod remote page.", ex);
            MessageBox.Show(main, LocalizedStrings.Instance.Get("M_OpenRemotePageFailed"), i18n.Universal_Notice,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void viewModSubscription_Click(object? sender, RoutedEventArgs e)
    {
        if (selectedRow.mods is not { Count: 1 } selected) return;
        try { await main.OpenModSubscriptionAsync(selected[0]); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppDiagnostics.Log("Unable to open installed Mod subscription.", ex); }
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
        bool isLoaded = loadedItems.Contains(mod);
        var view = isLoaded ? loadedView : unloadedView;
        if (!view.Contains(mod)) modSearch.Text = "";
        var grid = isLoaded ? loadedMods : unloadedMods;
        grid.SelectedItem = mod;
        grid.ScrollIntoView(mod, null);
        grid.Focus();
        return true;
    }

    internal static bool CanCollectShaderCache(Mod mod) =>
        mod.Inspection?.Source == ModSourceKind.Local && !mod.IsMissingReference;
}
