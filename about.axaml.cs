using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GOHShaderModdingSupportLauncher;

public partial class About : UserControl
{
    public About() => InitializeComponent();

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow main) return;
        checkUpdateButton.IsEnabled = false;
        try { await main.CheckLauncherUpdateAsync(manual: true); }
        finally { checkUpdateButton.IsEnabled = true; }
    }

    private void Github_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/FNSOIDATHQ/GOHShaderModdingSupportLauncher/issues");

    private void Workshop_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://steamcommunity.com/sharedfiles/filedetails/?id=3410344592");

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
