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
    private readonly HttpClient imageClient = new(ModAnonymousTransport.Create())
    { Timeout = TimeSpan.FromSeconds(25) };
    private bool snapshotFresh;

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

    private async Task LoadArticlesAsync(Uri page, CancellationToken token)
    {
        var response = await Task.Run(() => runtime!.Source.GetArticlesAsync(page, cancellationToken: token), token);
        token.ThrowIfCancellationRequested(); status.Text = ReadStatus(response);
        if (response.Content is not { } content) return;
        articles.Children.Clear();
        if (content.Items.Length == 0) articles.Children.Add(Text(T("Empty")));
        foreach (var article in content.Items)
        {
            articles.Children.Add(Action(article.Title, async () =>
            {
                var detail = await Task.Run(() => runtime!.Source.GetArticleAsync(article.PageUrl, cancellationToken: token), token);
                token.ThrowIfCancellationRequested(); status.Text = ReadStatus(detail);
                if (detail.Content is not { } body) return;
                var panel = Column(); panel.Children.Add(Text(body.Title, 20)); AddReadable(panel, body.Body);
                panel.Children.Add(Action(T("Original"), () => { OpenUrl(body.PageUrl); return Task.CompletedTask; }, independent: true));
                foreach (var image in body.Images) panel.Children.Add(ImageButton(image));
                await ShowReaderAsync(body.Title, panel);
            }));
        }
        if (content.NextPage is { } nextPage) articles.Children.Add(Action(T("Next"), () => LoadArticlesAsync(nextPage, token)));
    }

    private async Task LoadImagesAsync(Uri page, CancellationToken token)
    {
        var response = await Task.Run(() => runtime!.Source.GetImagesAsync(page, cancellationToken: token), token);
        token.ThrowIfCancellationRequested(); status.Text = ReadStatus(response);
        if (response.Content is not { } content) return;
        images.Children.Clear();
        if (content.Items.Length == 0) images.Children.Add(Text(T("Empty")));
        foreach (var image in content.Items)
            images.Children.Add(Action(image.Caption.Length > 0 ? image.Caption : image.Id, async () =>
            {
                var full = await Task.Run(() => runtime!.Source.GetImageAsync(image.PageUrl, cancellationToken: token), token);
                token.ThrowIfCancellationRequested(); status.Text = ReadStatus(full);
                if (full.Content is { } item) await ShowImageAsync(item, token);
            }));
        if (content.NextPage is { } nextPage) images.Children.Add(Action(T("Next"), () => LoadImagesAsync(nextPage, token)));
    }

    private Button ImageButton(ModImage image) => Action(T("ViewImage") + " · " + image.Caption,
        () => ShowImageAsync(image, detailCancellation?.Token ?? lifetime.Token), independent: true);

    private async Task ShowImageAsync(ModImage image, CancellationToken token)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(image.ImageUrl)) throw new InvalidDataException(T("InvalidImage"));
        using var response = await imageClient.GetAsync(image.ImageUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 20 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException(T("ImageTooLarge"));
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > maxBytes) throw new InvalidDataException(T("ImageTooLarge"));
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        bytes.Position = 0;
        using var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(bytes, 1600), token);
        token.ThrowIfCancellationRequested();
        var panel = Column();
        panel.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        panel.Children.Add(Text(image.Caption));
        panel.Children.Add(Action(T("Original"), () => { OpenUrl(image.ImageUrl); return Task.CompletedTask; }, independent: true));
        await ShowReaderAsync(T("Images"), panel);
    }

    private Task ShowReaderAsync(string caption, Control content)
    {
        var window = new Window { Title = caption, Width = 880, Height = 680, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = Scroll(content) };
        return window.ShowDialog(main);
    }

    private async Task LoadFilesAsync(RemoteMod mod, CancellationToken token)
    {
        var response = await Task.Run(() => runtime!.Source.GetFileSnapshotAsync(mod.PageUrl, cancellationToken: token), token);
        token.ThrowIfCancellationRequested(); status.Text = ReadStatus(response);
        snapshot = response.Content; snapshotFresh = response.IsSuccess && !response.IsStale;
        RenderFiles(mod, token);
    }

    private void RenderFiles(RemoteMod mod, CancellationToken token)
    {
        var chosen = fileChoices.Where(pair => pair.Value.IsChecked == true).Select(pair => (pair.Key.Id, pair.Key.Revision)).ToHashSet();
        files.Children.Clear(); fileChoices.Clear();
        files.Children.Add(Text(T("InstallBoundary")));
        if (snapshot is null) return;
        if (snapshot.Files.Length == 0) files.Children.Add(Text(T("Empty")));
        var notices = Subscription(mod) is { } subscription ? ModUpdateCheckService.GetNotices(subscription) : [];
        var displayed = snapshot.Files.Concat(notices.Select(item => item.File)
            .Where(file => !snapshot.Files.Any(current => current.Id == file.Id))).ToArray();
        foreach (var file in displayed)
        {
            var row = Column();
            var notice = notices.SingleOrDefault(item => item.File.Id == file.Id && item.File.Revision == file.Revision);
            bool available = snapshot.Files.Any(current => current.Id == file.Id && current.Revision == file.Revision);
            var choice = new CheckBox { IsEnabled = available, IsChecked = available && chosen.Contains((file.Id, file.Revision)),
                Content = file.Name + " · " + FormatBytes(file.SizeBytes) + (notice is null ? "" : " · " + T(notice.Kind == FileUpdateKind.NewFile ? "NewFile" : "ChangedFile")) };
            fileChoices.Add(file, choice); row.Children.Add(choice);
            AddReadable(row, file.Description);
            if (!available) row.Children.Add(Text(T("FileUnavailable")));
            var actions = Row();
            actions.Children.Add(Action(T("Original"), () => { OpenUrl(file.PageUrl); return Task.CompletedTask; }));
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
        files.Children.Add(Action(T("SubscribeDownload"), () => SubscribeAsync(mod, token)));
    }

    private async Task SubscribeAsync(RemoteMod mod, CancellationToken token)
    {
        string? game = main.SubscriptionGameDirectory;
        if (game is null) { status.Text = T("SelectGame"); return; }
        var chosen = fileChoices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
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
                { HasBaseline = true, KnownFiles = baseline.Files, LastSuccessfulCheck = DateTimeOffset.UtcNow });
            return subscribed;
        }, token);
        await runtime.RefreshLibraryAsync(token);
        foreach (var download in subscription.Downloads.Where(download => chosen.Any(file => file.Id == download.File.Id && file.Revision == download.File.Revision)))
            _ = DownloadAsync(subscription.Id, download.Id);
        runtime.NotifyChanged(); RefreshTasks(); RefreshTracking(); status.Text = T("Queued");
    }

    private async Task DownloadAsync(Guid subscriptionId, Guid downloadId)
    {
        try { await runtime!.Downloads.EnqueueAsync(subscriptionId, downloadId, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) await Dispatcher.UIThread.InvokeAsync(() => status.Text = T("Failed") + ": " + ex.Message); }
        finally { runtime?.NotifyChanged(); }
    }

    private void RefreshTasks()
    {
        if (runtime is null || !runtime.Ready.IsCompletedSuccessfully || disposed) return;
        tasks.Children.Clear();
        if (Subscription() is not { } sub) return;
        tasks.Children.Add(Text(T("Downloads"), 16));
        foreach (var download in sub.Downloads)
        {
            var progress = runtime.Progress.TryGetValue(download.Id, out var current) && download.State is DownloadState.Pending or DownloadState.Downloading ? current : null;
            var state = progress?.State ?? download.State;
            var installed = sub.Installations.Where(installation => installation.DownloadId == download.Id).ToArray();
            var label = installed.Length > 0 ? string.Join(", ", installed.Select(item => T("Install_" + item.State))) : T("Download_" + state);
            var line = Row(); line.Children.Add(Text(download.File.Name + " · " + label));
            tasks.Children.Add(line);
            long received = progress?.ReceivedBytes ?? download.ReceivedBytes;
            long? total = progress?.TotalBytes ?? download.File.SizeBytes;
            tasks.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, total ?? received), Value = received, IsIndeterminate = state == DownloadState.Downloading && total is null });
            tasks.Children.Add(Text(FormatBytes(received) + " / " + FormatBytes(total)));
            if (download.Error is { } error && state != DownloadState.NeedsManualInstall) tasks.Children.Add(Text(T("Failed") + ": " + error));
            if (state == DownloadState.NeedsManualInstall) tasks.Children.Add(Text(T("ManualInstructions")));
            if (sub.State != SubscriptionState.Active) continue;
            if (state is DownloadState.Pending or DownloadState.Downloading)
                line.Children.Add(Action(T("Cancel"), () => { runtime.Downloads.Cancel(download.Id); return Task.CompletedTask; }, independent: true));
            if (state is DownloadState.Failed or DownloadState.Cancelled or DownloadState.LocalFileMissing)
                line.Children.Add(Action(T("Retry"), () => { _ = DownloadAsync(sub.Id, download.Id); return Task.CompletedTask; }));
            if (state is DownloadState.Downloaded or DownloadState.NeedsManualInstall)
            {
                line.Children.Add(Action(T("InspectInstall"), () => InstallAsync(sub, download)));
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

    private async Task InstallAsync(GameModSubscription sub, ModDownload download)
    {
        var inspection = await Task.Run(() => runtime!.Installer.Inspect(sub.Id, download.Id, lifetime.Token));
        var panel = Column(); panel.Margin = new Thickness(16);
        panel.Children.Add(Text(T("InstallBoundary")));
        AddReadable(panel, inspection.AuthorInstructions);
        if (inspection.ManualInstallReason is not null || inspection.Candidates.Length == 0)
        {
            panel.Children.Add(Text(T("ManualInstructions")));
            panel.Children.Add(Text(inspection.ManualInstallReason ?? ""));
            await Task.Run(() => runtime!.Installer.MarkDownloadOnly(sub.Id, download.Id));
            await ShowReaderAsync(T("DownloadOnly"), panel); runtime!.NotifyChanged(); return;
        }
        var window = new Window { Title = T("InspectInstall"), Width = 760, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var rootChoice = new ComboBox { ItemsSource = inspection.Candidates.Select(item => item.RootPath).ToArray(), PlaceholderText = T("ChooseRoot") };
        panel.Children.Add(rootChoice);
        // Replacement is always explicit, including when a folder happens to have the same name.
        var replacements = new ComboBox { ItemsSource = new[] { T("NewInstall") }.Concat(sub.Installations.Select(item => T("Replace") + " " + item.DirectoryPath)).ToArray(), SelectedIndex = 0 };
        panel.Children.Add(replacements);
        var complete = new CheckBox { Content = T("CompletePackage") }; panel.Children.Add(complete);
        var error = Text(""); panel.Children.Add(error);
        var install = new Button { Content = T("Install") }; panel.Children.Add(install);
        install.Click += async (_, _) =>
        {
            if (rootChoice.SelectedIndex < 0 || complete.IsChecked != true) { error.Text = T("ConfirmPackage"); return; }
            if (!string.Equals(main.SubscriptionGameDirectory, sub.GameDirectory, StringComparison.OrdinalIgnoreCase)) { error.Text = T("SelectGame"); return; }
            install.IsEnabled = false;
            try
            {
                var candidate = inspection.Candidates[rootChoice.SelectedIndex];
                Guid? replacement = replacements.SelectedIndex > 0 ? sub.Installations[replacements.SelectedIndex - 1].Id : null;
                await Task.Run(() => runtime!.Installer.Install(new(sub.Id, download.Id, candidate.RootPath, true, replacement), lifetime.Token));
                runtime!.NotifyChanged(); window.Close();
            }
            catch (Exception ex) { error.Text = T("Failed") + ": " + ex.Message; }
            finally { install.IsEnabled = true; }
        };
        window.Content = Scroll(panel); await window.ShowDialog(main);
    }

    private async Task UnsubscribeAsync()
    {
        if (Subscription() is not { } sub) return;
        if (sub.Downloads.Any(item => item.State is DownloadState.Pending or DownloadState.Downloading &&
            (!runtime!.Progress.TryGetValue(item.Id, out var progress) || progress.State is DownloadState.Pending or DownloadState.Downloading)))
        { status.Text = T("CancelBeforeRemoval"); return; }
        var plan = await Task.Run(() => runtime!.Installer.PreviewUnsubscribe(sub.Id));
        var panel = Column(); panel.Margin = new Thickness(16);
        panel.Children.Add(Text(T("CleanupBoundary")));
        panel.Children.Add(Text(T("WillDelete") + $" ({plan.Files.Length})"));
        foreach (var file in plan.Files) panel.Children.Add(Text(file.Path));
        panel.Children.Add(Text(T("WillKeep") + $" ({plan.PreservedPaths.Length})"));
        foreach (var path in plan.PreservedPaths) panel.Children.Add(Text(path));
        foreach (var reason in plan.BlockingReasons) panel.Children.Add(Text(T("Blocked") + ": " + reason));
        var window = new Window { Title = T("Unsubscribe"), Width = 800, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var confirm = new Button { Content = T("ConfirmRemoval"), IsEnabled = plan.BlockingReasons.Length == 0 };
        var error = Text(""); panel.Children.Add(error); panel.Children.Add(confirm);
        confirm.Click += async (_, _) =>
        {
            if (!string.Equals(main.SubscriptionGameDirectory, sub.GameDirectory, StringComparison.OrdinalIgnoreCase)) { error.Text = T("SelectGame"); return; }
            confirm.IsEnabled = false;
            try { await Task.Run(() => runtime!.Installer.Unsubscribe(plan, lifetime.Token)); runtime!.NotifyChanged(); window.Close(); }
            catch (Exception ex) { error.Text = T("Failed") + ": " + ex.Message; }
            finally { confirm.IsEnabled = true; }
        };
        window.Content = Scroll(panel); await window.ShowDialog(main);
        RefreshTasks(); RefreshTracking(); if (view.SelectedIndex != 0) await LoadListAsync();
    }

    private static string FormatBytes(long? bytes) => bytes is null ? "?" : bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GiB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : $"{bytes / 1024d:0.0} KiB";
}
