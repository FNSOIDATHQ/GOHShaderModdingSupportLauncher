using System.Globalization;
using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// Part of the shipped offline UI diagnostic; uses a cancellable synthetic transfer only.
internal static class LauncherUpdateUiSmokeTest
{
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(token);
    }

    internal static void Run(string root)
    {
        static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Launcher update UI: " + reason); }
        static Button FindButton(Window dialog, string name) => dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string directory = Directory.CreateDirectory(Path.Combine(root, "launcher-update-ui")).FullName;
        string target = Path.Combine(directory, "launcher.exe");
        File.WriteAllText(target, "unchanged launcher");
        var original = LocalizedStrings.Instance.Culture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                LocalizedStrings.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
                var limiter = new ModHostLimiter();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var client = new GitHubLauncherUpdateClient(Path.Combine(directory, language + ".json"),
                    new AnonymousModHttpClient(new Handler(_ => throw new Exception("Unexpected UI metadata request")),
                        new ModHttpOptions { HostInterval = TimeSpan.Zero, MaxRetries = 0 }, limiter),
                    new ModFileTransferClient(new Handler(async token =>
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }), new ModTransferOptions { HostInterval = TimeSpan.Zero, MaxRetries = 0,
                        AllowHost = GitHubLauncherUpdateClient.IsDownloadHost }, limiter));
                var release = new LauncherRelease("v4.01", "First line\nSecond line", DateTimeOffset.UtcNow,
                    GitHubLauncherUpdateClient.ReleasesPage, LauncherUpdateState.Available,
                    new("456", new($"https://github.com/{GitHubLauncherUpdateClient.Repository}/releases/download/v4.01/{GitHubLauncherUpdateClient.AssetName}"), 512, new string('0', 64)));
                var check = new LauncherUpdateCheck(release, DateTimeOffset.UtcNow, false);
                var dialog = new LauncherUpdateDialog(check, client, target);
                dialog.Show();
                try
                {
                    Require(FindButton(dialog, "downloadUpdateButton").Content as string == LocalizedStrings.Instance.Get("LU_Download"), "localized action");
                    Require(dialog.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(AppVersion.Current, StringComparison.Ordinal) == true), "current version");
                    Click(FindButton(dialog, "downloadUpdateButton"));
                    ModActivationSmokeTest.Pump(started.Task);
                    Require(!FindButton(dialog, "downloadUpdateButton").IsEnabled, "prevent concurrent download");
                    Click(FindButton(dialog, "cancelUpdateButton"));
                    ModActivationSmokeTest.Pump(dialog.PendingDownload);
                    Require(FindButton(dialog, "downloadUpdateButton").IsEnabled && File.ReadAllText(target) == "unchanged launcher", "cancel and retry preserve installed file");
                }
                finally { dialog.Close(); }
                var unsupported = new LauncherUpdateDialog(check with { Release = release with
                    { State = LauncherUpdateState.Unsupported, Asset = null, IssueKey = "LU_MissingDigest" } }, client, target);
                unsupported.Show();
                try
                {
                    Require(!FindButton(unsupported, "downloadUpdateButton").IsEnabled && FindButton(unsupported, "openReleaseButton").IsEnabled, "manual review for unsupported release");
                }
                finally { unsupported.Close(); }
                var main = new MainWindow(Path.Combine(directory, language + ".conf"))
                    { SuppressSubscriptionActivation = true, SuppressLauncherUpdates = true };
                try
                {
                    main.UpdatePathAvailability();
                    var footer = main.FindControl<ListBox>("footerNavigation")!;
                    Require(footer.Items.OfType<ListBoxItem>().Single(i => i.Tag as string == "About").IsEnabled, "About remains enabled without game paths");
                    footer.SelectedIndex = 1;
                    Require(main.FindControl<ContentControl>("pageHost")!.Content is About, "About navigation without game paths");
                    Require(!main.PathsReady, "fixture has no game paths");
                    ModActivationSmokeTest.Pump(main.CheckLauncherUpdateAsync(manual: true));
                }
                finally { main.Close(); }
            }
        }
        finally { LocalizedStrings.Instance.SetCulture(original); }
        AppDiagnostics.Log("Launcher update UI self-test passed: English/Chinese, cancellation, unsupported release and no-game navigation.");
    }
}
