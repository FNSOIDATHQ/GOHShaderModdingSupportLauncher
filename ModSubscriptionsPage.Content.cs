using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private readonly HttpClient imageClient = CreateImageClient();
    private bool snapshotFresh;

    private static HttpClient CreateImageClient()
    {
        // The media service applies one timeout to headers, redirects and the complete body.
        var client = new HttpClient(ModAnonymousTransport.Create()) { Timeout = Timeout.InfiniteTimeSpan };
        string version = typeof(ModSubscriptionsPage).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GOHSMSLauncher/{version} (+https://github.com/FNSOIDATHQ/GOHShaderModdingSupportLauncher)");
        return client;
    }

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex ContentLinks();

    private void AddReadable(StackPanel panel, string content)
    {
        panel.Children.Add(new SelectableTextBlock { Text = ContentLinks().Replace(content, "$1"), TextWrapping = TextWrapping.Wrap });
        foreach (Match link in ContentLinks().Matches(content).Cast<Match>().DistinctBy(match => match.Groups[2].Value))
        {
            if (!Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out var url)) continue;
            panel.Children.Add(Action(link.Groups[1].Value + " ↗", () => FollowLinkAsync(url), independent: true));
        }
    }

    private async Task LoadFilesAsync(RemoteMod mod, CancellationToken token)
    {
        if (session is not { } current || current.Mod.Id != mod.Id) return;
        var adapter = SourceForProvider(mod.Id.Provider);
        await ReadContentAsync(current, current.Files, (mode, ct) => adapter.GetFileSnapshotAsync(mod.PageUrl, mode, ct), RenderFileState, token);
    }

    private async Task SubscribeAsync(RemoteMod mod, CancellationToken token)
    {
        if (session is not { } expected || expected.Mod.Id != mod.Id || !IsCurrent(expected, token)) return;
        string? game = main.SubscriptionGameDirectory;
        if (game is null) { ReportNotice(T("SelectGame")); return; }
        var chosen = fileChoices.Where(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true && pair.Key.CanDownload).Select(pair => pair.Key).ToArray();
        if (chosen.Length == 0) { ReportNotice(T("ChooseFiles")); return; }
        await runtime!.EnsureRecoveredAsync(game, token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(game, main.SubscriptionGameDirectory, StringComparison.OrdinalIgnoreCase)) return;
        var baseline = snapshotFresh ? snapshot : null;
        var subscription = await Task.Run(() =>
        {
            runtime.Store.SaveMod(mod);
            var subscribed = runtime.Store.Subscribe(mod.Id, game, chosen);
            if (baseline is not null)
                runtime.Store.ChangeFileTracking(subscribed.Id, previous => (previous.HasBaseline ? previous : new FileTrackingState
                { HasBaseline = true, KnownFiles = baseline.Files, PageRevision = baseline.PageRevision,
                    AcknowledgedPageRevision = baseline.PageRevision, LastSuccessfulCheck = DateTimeOffset.UtcNow }) with
                { Groups = ModUpdateCheckService.MergeGroups(previous.Groups, baseline.Groups) });
            return subscribed;
        }, token);
        await runtime.RefreshLibraryAsync(token);
        foreach (var download in subscription.Downloads.Where(download => chosen.Any(file => file.Id == download.File.Id && file.Revision == download.File.Revision)))
            _ = DownloadAsync(subscription.Id, download.Id);
        runtime.NotifyChanged(); RefreshTasks(); RefreshTracking(); ReportNotice(T("Queued"));
    }

    private async Task DownloadAsync(Guid subscriptionId, Guid downloadId)
    {
        string? game = main.SubscriptionGameDirectory;
        var owner = selected?.Id;
        try
        {
            var subscription = runtime!.Library.Subscriptions.Single(item => item.Id == subscriptionId);
            await runtime.GetDownloads(subscription.Source.Provider).EnqueueAsync(subscriptionId, downloadId, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!disposed) await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!disposed && game == main.SubscriptionGameDirectory && owner == selected?.Id)
                    ReportNotice(T("Failed") + ": " + ex.Message, failure: true, key: "download:" + downloadId);
            });
        }
        finally { runtime?.NotifyChanged(); }
    }

    private void RefreshTasks()
    {
        if (runtime is null || !runtime.Ready.IsCompletedSuccessfully || disposed) return;
        tasks.Children.Clear();
        string? game = main.SubscriptionGameDirectory;
        int running = runtime.Library.Subscriptions.Where(item => item.GameDirectory.Equals(game, SubscriptionPaths.Comparison))
            .Sum(item => item.Downloads.Count(download => download.State is DownloadState.Pending or DownloadState.Downloading));
        activity.Text = running > 0 ? string.Format(T("ActiveDownloads"), running) : "";
        if (session?.File is not { } key || !key.StartsWith("file:", StringComparison.Ordinal)) return;
        var sub = Subscription();
        if (sub is not { Downloads.Length: > 0 }) { AddLocalInspection(tasks, sub, null); return; }
        var downloads = sub.Downloads.Where(download => "file:" + download.File.Id == key).ToArray();
        if (downloads.Length == 0) { AddLocalInspection(tasks, sub, null); return; }
        tasks.Children.Add(Text(T("Downloads"), 16));
        foreach (var download in downloads)
        {
            var progress = runtime.Progress.TryGetValue(download.Id, out var current) && download.State is DownloadState.Pending or DownloadState.Downloading ? current : null;
            var state = progress?.State ?? download.State;
            bool waitingForVerification = state == DownloadState.Pending && download.Error == nameof(ModSourceError.VerificationRequired);
            var installed = sub.Installations.Where(installation => installation.DownloadId == download.Id).ToArray();
            var label = waitingForVerification ? T("DownloadVerification") : installed.Length > 0
                ? string.Join(", ", installed.Select(item => T("Install_" + item.State))) : T("Download_" + state);
            var line = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 }; line.Children.Add(Text(download.File.Name + " · " + label));
            tasks.Children.Add(line);
            long received = progress?.ReceivedBytes ?? download.ReceivedBytes;
            long? total = progress?.TotalBytes ?? download.File.SizeBytes;
            tasks.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, total ?? received), Value = received, IsIndeterminate = state == DownloadState.Downloading && total is null });
            tasks.Children.Add(Text(FormatBytes(received) + " / " + FormatBytes(total)));
            if (download.Error is { } error && state != DownloadState.NeedsManualInstall && !waitingForVerification) tasks.Children.Add(Text(T("Failed") + ": " + error));
            if (state == DownloadState.NeedsManualInstall) tasks.Children.Add(Text(T("ManualInstructions")));
            AddLocalInspection(tasks, sub, download.Id);
            if (sub.State != SubscriptionState.Active) continue;
            if (state is DownloadState.Pending or DownloadState.Downloading)
                line.Children.Add(Action(T("Cancel"), async () =>
                {
                    await Task.Run(() => runtime.GetDownloads(sub.Source.Provider).CancelAsync(download.Id, lifetime.Token), lifetime.Token);
                    runtime.NotifyChanged();
                }, independent: true));
            if (state is DownloadState.Failed or DownloadState.Cancelled or DownloadState.LocalFileMissing)
                line.Children.Add(Action(T("Retry"), () => { _ = DownloadAsync(sub.Id, download.Id); return Task.CompletedTask; }));
            if (state is DownloadState.Downloaded or DownloadState.NeedsManualInstall)
            {
                if (!download.File.DownloadOnly) line.Children.Add(Action(T("InspectInstall"), () => InstallAsync(sub, download)));
                else tasks.Children.Add(Text(T("GitHubDownloadOnly")));
                line.Children.Add(Action(T("DownloadOnly"), async () => { await Task.Run(() => runtime.Installer.MarkDownloadOnly(sub.Id, download.Id)); runtime.NotifyChanged(); }));
                line.Children.Add(Action(T("OpenDownloads"), () =>
                {
                    if (download.LocalPath is { } path && File.Exists(path))
                    { ManagedModFiles.EnsureSafe(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true }); }
                    return Task.CompletedTask;
                }));
            }
        }
    }

    private static string FormatBytes(long? bytes) => bytes is null ? "?" : bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GiB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : $"{bytes / 1024d:0.0} KiB";
}
