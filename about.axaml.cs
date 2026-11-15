using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class About : UserControl
{
    public About()
    {
        InitializeComponent();
        versionText.Text = $"v{AppVersion.Current}    by Federation Studio 2026";
        SizeChanged += (_, _) => UpdateDonationSidebarWidth();
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow main) return;
        checkUpdateButton.IsEnabled = false;
        try { await main.CheckLauncherUpdateAsync(manual: true); }
        finally { checkUpdateButton.IsEnabled = true; }
    }

    private void Github_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/FNSOIDATHQ/GOHShaderModdingSupportLauncher/issues");

    internal void OpenDonationSidebar()
    {
        UpdateDonationSidebarWidth();
        aboutSupportView.IsPaneOpen = true;
        openDonationSidebarButton.IsVisible = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (aboutSupportView.IsPaneOpen && IsEffectivelyVisible) koFiButton.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OpenDonationSidebar_Click(object? sender, RoutedEventArgs e) => OpenDonationSidebar();

    private void CloseDonationSidebar_Click(object? sender, RoutedEventArgs e)
    {
        aboutSupportView.IsPaneOpen = false;
        openDonationSidebarButton.IsVisible = true;
        openDonationSidebarButton.Focus();
    }

    private void UpdateDonationSidebarWidth()
    {
        if (Bounds.Width > 0)
            aboutSupportView.OpenPaneLength = Math.Clamp(Bounds.Width * 0.44, 260, 320);
    }

    private void KoFi_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://ko-fi.com/federationstudio");

    private void MianBaoDuo_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://mbd.pub/o/fedStudio");

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
