using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

internal sealed class LauncherUpdateDialog : Window
{
    private readonly LauncherRelease release;
    private readonly GitHubLauncherUpdateClient client;
    private readonly string? target;
    private readonly Button download, cancel;
    private readonly TextBlock status;
    private readonly ProgressBar progress;
    private CancellationTokenSource? cancellation;
    private bool busy, closeRequested, closed;
    private int operation;
    internal Task PendingDownload { get; private set; } = Task.CompletedTask;
    private static string Text(string key) => LocalizedStrings.Instance.Get(key);

    internal LauncherUpdateDialog(LauncherUpdateCheck check, GitHubLauncherUpdateClient client, string? target)
    {
        release = check.Release;
        this.client = client;
        this.target = target;
        Title = Text("Update_Title");
        Width = 620;
        MinWidth = 440;
        MaxHeight = 740;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = Text("LU_CurrentVersion") + " " + AppVersion.Current,
            FontSize = 18, TextWrapping = TextWrapping.Wrap });
        if (release.Tag.Length > 0)
            panel.Children.Add(new TextBlock { Text = Text("LU_ReleaseVersion") + " " + release.Tag, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        if (release.PublishedAt is { } published)
            panel.Children.Add(new TextBlock { Text = Text("LU_Published") + " " + published.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) });
        if (release.Asset is { } asset)
            panel.Children.Add(new TextBlock { Text = Text("LU_DownloadSize") + " " + (asset.Size / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) + " MiB" });
        string key = release.State switch
        {
            LauncherUpdateState.Available => "LU_Available",
            LauncherUpdateState.Current => "LU_Current",
            LauncherUpdateState.LocalNewer => "LU_LocalNewer",
            LauncherUpdateState.NoRelease => "LU_NoRelease",
            _ => release.IssueKey ?? "LU_UnsupportedRelease"
        };
        status = new TextBlock { Text = Text(key), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        if (release.Notes.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = Text("LU_Notes"), FontWeight = FontWeight.Bold });
            panel.Children.Add(new ScrollViewer { MaxHeight = 260,
                Content = new TextBlock { Text = release.Notes, TextWrapping = TextWrapping.Wrap } });
        }
        panel.Children.Add(new TextBlock { Text = Text(check.FromCache ? "LU_CachedCheck" : "LU_LastCheck") + " " +
            check.CheckedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Opacity = .7, TextWrapping = TextWrapping.Wrap });
        progress = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false };
        panel.Children.Add(progress);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        download = new Button { Name = "downloadUpdateButton", Content = Text("LU_Download"), Margin = new Thickness(4),
            IsEnabled = release.State == LauncherUpdateState.Available };
        if (download.IsEnabled && (target is null || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)))
        { download.IsEnabled = false; status.Text = Text("LU_UnsupportedTarget"); }
        download.Click += async (_, _) => { PendingDownload = DownloadAsync(); await PendingDownload; };
        var open = new Button { Name = "openReleaseButton", Content = Text("LU_OpenRelease"), Margin = new Thickness(4) };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(release.PageUrl.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = Text("LU_Failed") + "\n" + ex.Message; }
        };
        cancel = new Button { Name = "cancelUpdateButton", Content = Text("LU_Close"), Margin = new Thickness(4) };
        cancel.Click += (_, _) => { if (busy) cancellation?.Cancel(); else Close(); };
        buttons.Children.Add(download);
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, MaxHeight = 700 };
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); };
    }

    private async Task DownloadAsync()
    {
        if (busy || target is null) return;
        busy = true;
        int current = ++operation;
        cancellation = new();
        var token = cancellation.Token;
        download.IsEnabled = false;
        cancel.Content = Text("LU_CancelDownload");
        progress.IsVisible = true;
        progress.Value = 0;
        status.Text = Text("LU_Downloading");
        LauncherUpdate.Candidate? candidate = null;
        LauncherUpdate.Prepared? ready = null;
        try
        {
            candidate = await client.DownloadAsync(release, target, (received, total) => Dispatcher.UIThread.Post(() =>
            {
                if (closed || !busy || operation != current || token.IsCancellationRequested) return;
                progress.Value = total is > 0 ? 100d * received / total.Value : 0;
                status.Text = Text("LU_Downloading") + " " + (received / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) +
                    " / " + ((total ?? 0) / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) + " MiB";
            }), token);
            status.Text = Text("LU_Preparing");
            ready = await Task.Run(() => LauncherUpdate.Prepare(candidate), token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status.Text = Text("LU_Cancelled"); }
        catch (Exception ex)
        {
            AppDiagnostics.Log("Launcher update download/preparation failed.", ex);
            status.Text = Text("LU_Failed") + "\n" + ex.Message;
            if (ex is ModSubscriptions.ModSourceException { Failure.RetryAfter: { } after })
                status.Text += "\n" + Text("LU_RetryAfter") + " " + after.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }
        finally
        {
            if (candidate is not null) client.CleanDownload(candidate);
            busy = false;
            download.IsEnabled = true;
            cancel.Content = Text("LU_Close");
            if (ready is not null)
            {
                if (token.IsCancellationRequested || closeRequested || closed)
                {
                    try { LauncherUpdate.Discard(ready); }
                    catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Unable to discard cancelled update.", ex); }
                }
                else Close(ready);
            }
            cancellation.Dispose();
            cancellation = null;
            if (closeRequested && !closed) Close();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!busy) return;
        e.Cancel = true;
        closeRequested = true;
        cancellation?.Cancel();
    }
}
