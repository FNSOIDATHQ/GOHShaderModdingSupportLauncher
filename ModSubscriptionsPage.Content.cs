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
            panel.Children.Add(Action(link.Groups[1].Value + " ↗", () => { OpenUrl(url); return Task.CompletedTask; }, independent: true));
        }
    }

    private async Task LoadFilesAsync(RemoteMod mod, CancellationToken token)
    {
        if (session is not { } current || current.Mod.Id != mod.Id) return;
        var adapter = SourceForProvider(mod.Id.Provider);
        await ReadContentAsync(current, current.Files, (mode, ct) => adapter.GetFileSnapshotAsync(mod.PageUrl, mode, ct), RenderFileState, token);
    }

    private void RenderFiles(RemoteMod mod, CancellationToken token)
    {
        var chosen = fileChoices.Count > 0 ? fileChoices.Where(pair => pair.Value.IsChecked == true).Select(pair => (pair.Key.Id, pair.Key.Revision)).ToHashSet() : session?.Chosen ?? [];
        files.Children.Clear(); fileChoices.Clear();
        files.Children.Add(Text(T("InstallBoundary")));
        if (session is { } current)
        {
            files.Children.Add(Text(ContentStatus(current.Files), 12));
            files.Children.Add(Action(T("RefreshContent"), async () => { current.Files.Attempted = false; await EnsureFilesAsync(); }, independent: true));
        }
        AddPageNotice(mod, token);
        if (snapshot is null) return;
        if (snapshot.Files.Length == 0) files.Children.Add(Text(T("Empty")));
        var notices = Subscription(mod) is { } subscription ? ModUpdateCheckService.GetNotices(subscription) : [];
        var displayed = snapshot.Files.Concat(notices.Select(item => item.File)
            .Where(file => !snapshot.Files.Any(current => current.Id == file.Id))).ToArray();
        foreach (var file in displayed)
        {
            var row = Column();
            var notice = notices.SingleOrDefault(item => item.File.Id == file.Id && item.File.Revision == file.Revision);
            bool present = snapshot.Files.Any(current => current.Id == file.Id && current.Revision == file.Revision);
            bool available = present && file.CanDownload;
            var choice = new CheckBox { IsEnabled = available && Subscription(mod)?.State is not (SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed), IsChecked = available && chosen.Contains((file.Id, file.Revision)),
                Content = Text(file.Name + " · " + FormatBytes(file.SizeBytes) + (notice is null ? "" : " · " + T(notice.Kind == FileUpdateKind.NewFile ? "NewFile" : "ChangedFile"))) };
            fileChoices.Add(file, choice); row.Children.Add(choice);
            if (file.IsSourceSnapshot) row.Children.Add(Text(T("SourceSnapshot")));
            if (file.DownloadOnly) row.Children.Add(Text(T("GitHubDownloadOnly")));
            AddReadable(row, file.Description);
            if (!present) row.Children.Add(Text(T("FileUnavailable")));
            else if (!file.CanDownload)
            {
                row.Children.Add(Text(T("DownloadUnavailable")));
                if (!string.IsNullOrWhiteSpace(file.DownloadUnavailableReason)) row.Children.Add(Text(file.DownloadUnavailableReason));
            }
            var actions = Row();
            actions.Children.Add(Action(T("Original"), () =>
            {
                OpenUrl(file.BrowserUrl ?? (mod.Id.Provider == "github" ? new Uri(mod.PageUrl.AbsoluteUri.TrimEnd('/') + "/releases") : file.PageUrl));
                return Task.CompletedTask;
            }));
            if (notice is not null)
            {
                foreach (var state in new[] { FileNoticeState.Read, FileNoticeState.Ignored })
                    actions.Children.Add(Action(T(state == FileNoticeState.Read ? "MarkRead" : "Ignore"), async () =>
                    {
                        if (Subscription(mod) is not { } current) return;
                        await Task.Run(() => runtime!.Updates.Acknowledge(current.Id, file.Id, file.Revision, state), token);
                        await runtime!.RefreshLibraryAsync(token);
                        token.ThrowIfCancellationRequested(); runtime!.NotifyChanged(); RefreshTracking(); RenderFiles(mod, token);
                    }));
            }
            row.Children.Add(actions); files.Children.Add(row);
        }
        if (Subscription(mod)?.State is not (SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed))
            files.Children.Add(Action(T("SubscribeDownload"), () => SubscribeAsync(mod, token)));
    }

    private async Task SubscribeAsync(RemoteMod mod, CancellationToken token)
    {
        if (session is not { } expected || expected.Mod.Id != mod.Id || !IsCurrent(expected, token)) return;
        string? game = main.SubscriptionGameDirectory;
        if (game is null) { status.Text = T("SelectGame"); return; }
        var chosen = fileChoices.Where(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true && pair.Key.CanDownload).Select(pair => pair.Key).ToArray();
        if (chosen.Length == 0) { status.Text = T("ChooseFiles"); return; }
        await runtime!.EnsureRecoveredAsync(game, token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(game, main.SubscriptionGameDirectory, StringComparison.OrdinalIgnoreCase)) return;
        var baseline = snapshotFresh ? snapshot : null;
        var subscription = await Task.Run(() =>
        {
            runtime.Store.SaveMod(mod);
            var subscribed = runtime.Store.Subscribe(mod.Id, game, chosen);
            if (!subscribed.Files.HasBaseline && baseline is not null)
                runtime.Store.ChangeFileTracking(subscribed.Id, previous => previous.HasBaseline ? previous : new FileTrackingState
                { HasBaseline = true, KnownFiles = baseline.Files, PageRevision = baseline.PageRevision,
                    AcknowledgedPageRevision = baseline.PageRevision, LastSuccessfulCheck = DateTimeOffset.UtcNow });
            return subscribed;
        }, token);
        await runtime.RefreshLibraryAsync(token);
        foreach (var download in subscription.Downloads.Where(download => chosen.Any(file => file.Id == download.File.Id && file.Revision == download.File.Revision)))
            _ = DownloadAsync(subscription.Id, download.Id);
        runtime.NotifyChanged(); RefreshTasks(); RefreshTracking(); status.Text = T("Queued");
    }

    private async Task DownloadAsync(Guid subscriptionId, Guid downloadId)
    {
        try
        {
            var subscription = runtime!.Library.Subscriptions.Single(item => item.Id == subscriptionId);
            await runtime.GetDownloads(subscription.Source.Provider).EnqueueAsync(subscriptionId, downloadId, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) await Dispatcher.UIThread.InvokeAsync(() => status.Text = T("Failed") + ": " + ex.Message); }
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
        if (Subscription() is not { Downloads.Length: > 0 } sub) return;
        tasks.Children.Add(Text(T("Downloads"), 16));
        foreach (var download in sub.Downloads)
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
