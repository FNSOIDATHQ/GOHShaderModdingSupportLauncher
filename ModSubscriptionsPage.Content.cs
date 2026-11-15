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
        await ShowTasksAsync();
    }

    private async Task DownloadAsync(Guid subscriptionId, Guid downloadId)
    {
        string? game = main.SubscriptionGameDirectory;
        var owner = selected?.Id;
        try
        {
            var subscription = runtime!.Library.Subscriptions.Single(item => item.Id == subscriptionId);
            runtime.AllowSubscriptionDownloads(subscriptionId);
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

    private string? renderedTaskSignature;
    private readonly Dictionary<Guid, (ProgressBar Bar, TextBlock Bytes)> taskProgressViews = [];

    private string ModTitle(GameModSubscription sub) => runtime!.Library.Mods.FirstOrDefault(mod => mod.Id == sub.Source)?.Title ?? sub.Source.RemoteId;

    private void RefreshTasks()
    {
        if (runtime is null || !runtime.Ready.IsCompletedSuccessfully || disposed) return;
        string? game = main.SubscriptionGameDirectory;
        if (game is not null) runtime.PrepareDownloadsForGame(game);
        var subscriptions = runtime.Library.Subscriptions.Where(item => item.GameDirectory.Equals(game, SubscriptionPaths.Comparison)).ToArray();
        int running = subscriptions.Sum(sub => sub.Downloads.Count(download => DownloadProgress(download)?.State is DownloadState.Pending or DownloadState.Downloading ||
            DownloadProgress(download) is null && download.State is DownloadState.Pending or DownloadState.Downloading));
        activity.Text = running > 0 ? string.Format(T("ActiveDownloads"), running) : "";
        if (workflowView != WorkflowView.Tasks || !operation.IsVisible) return;
        string signature = game + "|" + main.ModScan?.GetHashCode() + "|" + string.Join("|", subscriptions.SelectMany(sub => sub.Downloads.Select(download =>
        {
            runtime.PreparedDownloads.TryGetValue(download.Id, out var prepared);
            return $"{sub.Id}:{sub.State}:{download.Id}:{DownloadProgress(download)?.State ?? download.State}:{download.Error}:{prepared?.Checking}:{prepared?.Error}:{prepared?.Inspection?.ManualInstallReason}:{prepared?.DownloadUpdatedAt}:{installingDownloads.Contains(download.Id)}:{installationErrors.GetValueOrDefault(download.Id)}:" +
                string.Join(",", sub.Installations.Where(item => item.DownloadId == download.Id).Select(item => item.Id + ":" + item.State));
        })));
        if (signature != renderedTaskSignature)
        {
            renderedTaskSignature = signature; tasks.Children.Clear(); taskProgressViews.Clear();
            if (subscriptions.Length == 0) tasks.Children.Add(Text(T("NoTasks")));
            foreach (var sub in subscriptions)
                foreach (var download in sub.Downloads) tasks.Children.Add(TaskCard(sub, download));
        }
        foreach (var sub in subscriptions)
            foreach (var download in sub.Downloads)
                if (taskProgressViews.TryGetValue(download.Id, out var controls))
                {
                    var progress = DownloadProgress(download);
                    long received = progress?.ReceivedBytes ?? download.ReceivedBytes;
                    long? total = progress?.TotalBytes ?? download.File.SizeBytes;
                    controls.Bar.Maximum = Math.Max(1, total ?? received); controls.Bar.Value = received;
                    controls.Bar.IsIndeterminate = installingDownloads.Contains(download.Id) ||
                        download.State is DownloadState.Downloaded or DownloadState.NeedsManualInstall &&
                        runtime.PreparedDownloads.TryGetValue(download.Id, out var preparation) && preparation.Checking &&
                        !sub.Installations.Any(item => item.DownloadId == download.Id) ||
                        (progress?.State ?? download.State) == DownloadState.Downloading && total is null;
                    controls.Bytes.Text = FormatBytes(received) + " / " + FormatBytes(total);
                }
    }

    private ModDownloadProgress? DownloadProgress(ModDownload download) => download.State is DownloadState.Pending or DownloadState.Downloading &&
        runtime!.Progress.TryGetValue(download.Id, out var progress) ? progress : null;

    private Control TaskCard(GameModSubscription sub, ModDownload download)
    {
        var panel = Column();
        var fileName = Text(download.File.Name, 16); fileName.Name = "subscriptionTaskFileName"; panel.Children.Add(fileName);
        var state = DownloadProgress(download)?.State ?? download.State;
        bool verifiedWait = state == DownloadState.Pending && download.Error == nameof(ModSourceError.VerificationRequired);
        var installed = sub.Installations.Where(item => item.DownloadId == download.Id).ToArray();
        runtime!.PreparedDownloads.TryGetValue(download.Id, out var prepared);
        bool ready = state is DownloadState.Downloaded or DownloadState.NeedsManualInstall;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1 }; var bytes = Text("", 12);
        bytes.IsVisible = !ready && installed.Length == 0;
        bar.IsVisible = bytes.IsVisible || installingDownloads.Contains(download.Id) || ready && prepared?.Checking == true && installed.Length == 0;
        panel.Children.Add(bar); panel.Children.Add(bytes);
        taskProgressViews[download.Id] = (bar, bytes);
        if (!verifiedWait && state != DownloadState.NeedsManualInstall && download.Error is { } error) panel.Children.Add(Text(T("Failed") + ": " + error));
        if (installationErrors.TryGetValue(download.Id, out var installError)) panel.Children.Add(Text(T("Failed") + ": " + installError));
        if (ready && (prepared?.Error ?? prepared?.Inspection?.ManualInstallReason) is { } checkError) panel.Children.Add(Text(checkError));
        AddLocalInspection(panel, sub, download.Id);
        var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 }; panel.Children.Add(actions);
        if (sub.State == SubscriptionState.Active)
        {
            if (state is DownloadState.Pending or DownloadState.Downloading)
                actions.Children.Add(Action(T("Cancel"), async () =>
                {
                    await runtime.GetDownloads(sub.Source.Provider).CancelAsync(download.Id, lifetime.Token); runtime.NotifyChanged();
                }, independent: true));
            if (state is DownloadState.Failed or DownloadState.Cancelled or DownloadState.LocalFileMissing)
            {
                actions.Children.Add(Action(T("Retry"), () => { _ = DownloadAsync(sub.Id, download.Id); return Task.CompletedTask; }, independent: true));
            }
            if (state is not (DownloadState.Pending or DownloadState.Downloading) && !installingDownloads.Contains(download.Id))
            {
                if (ready && (installed.Length > 0 || prepared is { Checking: false, Error: null, Inspection.ManualInstallReason: null }))
                {
                    var review = Action(T("ReviewPlacement"), () => InstallAsync(sub, download), independent: true);
                    review.Name = "subscriptionReviewInstall"; review.IsEnabled = !operationBusy; actions.Children.Add(review);
                }
                var unsubscribe = Action(T("Unsubscribe"), () => ReviewUnsubscribeAsync(sub.Id, download.Id), independent: true);
                unsubscribe.Name = "subscriptionUnsubscribeFile"; unsubscribe.IsEnabled = !operationBusy; actions.Children.Add(unsubscribe);
                if (ready && prepared is not { Checking: true }) actions.Children.Add(Action(T("Recheck"), () =>
                { runtime.RecheckDownload(download.Id); renderedTaskSignature = null; RefreshTasks(); return Task.CompletedTask; }, independent: true));
                if (ready) actions.Children.Add(Action(T("OpenDownloads"), () =>
                { if (download.LocalPath is { } path) OpenDirectory(Path.GetDirectoryName(path)!); return Task.CompletedTask; }, independent: true));
            }
        }
        foreach (var installation in installed)
        {
            panel.Children.Add(Text(installation.DirectoryPath, 12));
            actions.Children.Add(Action(T("OpenInstallation"), () => { OpenDirectory(installation.DirectoryPath); return Task.CompletedTask; }, independent: true));
        }
        if (installed.Any(item => item.State == InstallationState.Installed))
            actions.Children.Add(Action(T("ViewManager"), () => ViewInstalledModsAsync(sub.Id, download.Id), independent: true));
        var card = new Border { Name = "subscriptionTaskCard", Tag = download.Id, Padding = new Thickness(12),
            CornerRadius = new CornerRadius(4), Background = ReadingBrush("Background"), Child = panel };
        string identity = ModTitle(sub) + " · " + download.File.Name;
        Avalonia.Automation.AutomationProperties.SetName(card, identity);
        ToolTip.SetTip(card, identity);
        return card;
    }

    private static string FormatBytes(long? bytes) => bytes is null ? "?" : bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GiB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : $"{bytes / 1024d:0.0} KiB";
}
