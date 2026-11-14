using Avalonia;
using Avalonia.Controls;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private bool operationBusy, recoveryLoading;
    private ReadingContext? operationContext;

    private void CloseOperation()
    {
        if (operation is null) return;
        if (operation.IsVisible) SetReadingContext(operationContext);
        operationContext = null;
        operation.Content = null; operation.IsVisible = false; operationScroll.IsVisible = false; fileContent.IsVisible = true;
        if (cacheResetAfterOperation)
        {
            cacheResetAfterOperation = false;
            ClearDetail();
            ResumeVisibleCacheReading();
            return;
        }
        if (session is { } current) RestorePosition(fileScroll, current.FilePosition);
        if (translationPreferences.Enabled && session is { } expected) Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        { if (session == expected) RerenderTranslation(); });
    }

    private void ShowOperation(Control content)
    {
        CancelLinkNavigation();
        if (!operation.IsVisible) operationContext = session?.Context;
        SaveFileReadingPosition();
        details.SelectedIndex = 1;
        operation.Content = content; operation.IsVisible = true; operationScroll.IsVisible = true; fileContent.IsVisible = false; operationScroll.Offset = default;
        CancelTranslation();
        SetReadingContext(operationContext);
    }

    private StackPanel OperationPanel(string caption)
    {
        var panel = Column(); panel.Children.Add(Text(caption, 18));
        panel.Children.Add(Action(T("BackToFiles"), () => { if (!operationBusy) CloseOperation(); return Task.CompletedTask; }, independent: true));
        return panel;
    }

    private bool SameGame(string game) => !disposed && string.Equals(main.SubscriptionGameDirectory, game, SubscriptionPaths.Comparison);

    private async Task InstallAsync(GameModSubscription sub, ModDownload download)
    {
        if (!SameGame(sub.GameDirectory) || session is not { } expected || expected.Mod.Id != sub.Source) return;
        var token = DetailToken;
        var inspection = await Task.Run(() => runtime!.Installer.Inspect(sub.Id, download.Id, token), token);
        if (!IsCurrent(expected, token) || !SameGame(sub.GameDirectory)) return;
        var panel = OperationPanel(T("InspectInstall"));
        panel.Children.Add(Text(T("InstallReviewBoundary"))); AddReadable(panel, inspection.AuthorInstructions);
        panel.Children.Add(Text(T("GameScope") + " " + sub.GameDirectory));
        if (inspection.ManualInstallReason is not null || inspection.Candidates.Length == 0)
        {
            panel.Children.Add(Text(T("ManualInstructions"))); panel.Children.Add(Text(inspection.ManualInstallReason ?? ""));
            ShowOperation(panel); return;
        }
        var rootChoice = new ComboBox { Name = "subscriptionInstallRoot", ItemsSource = inspection.Candidates.Select(item => item.RootPath).ToArray(), PlaceholderText = T("ChooseRoot"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        panel.Children.Add(rootChoice);
        var replacements = new ComboBox { ItemsSource = new[] { T("NewInstall") }.Concat(sub.Installations.Select(item => T("Replace") + " " + item.DirectoryPath)).ToArray(), SelectedIndex = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        panel.Children.Add(replacements);
        var complete = new CheckBox { Name = "subscriptionCompletePackage", Content = Text(T("CompletePackage")) }; panel.Children.Add(complete);
        var error = Text(""); panel.Children.Add(error);
        var install = new Button { Name = "subscriptionConfirmInstall", Content = T("Install") }; panel.Children.Add(install);
        install.Click += async (_, _) =>
        {
            if (operationBusy || !IsCurrent(expected, token) || !SameGame(sub.GameDirectory)) return;
            if (rootChoice.SelectedIndex < 0 || complete.IsChecked != true) { error.Text = T("ConfirmPackage"); return; }
            operationBusy = true; install.IsEnabled = false;
            try
            {
                var candidate = inspection.Candidates[rootChoice.SelectedIndex];
                Guid? replacement = replacements.SelectedIndex > 0 ? sub.Installations[replacements.SelectedIndex - 1].Id : null;
                await Task.Run(() => runtime!.Installer.Install(new(sub.Id, download.Id, candidate.RootPath, true, replacement), token), token);
                await runtime!.RefreshLibraryAsync(lifetime.Token);
                if (IsCurrent(expected, token)) { CloseOperation(); RefreshTasks(); RefreshTracking(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (IsCurrent(expected, token)) error.Text = T("Failed") + ": " + ex.Message; }
            finally { operationBusy = false; install.IsEnabled = true; runtime!.NotifyChanged(); }
        };
        panel.Children.Add(Action(T("DownloadOnly"), async () =>
        {
            if (operationBusy || !IsCurrent(expected, token) || !SameGame(sub.GameDirectory)) return;
            await Task.Run(() => runtime!.Installer.MarkDownloadOnly(sub.Id, download.Id), token);
            if (IsCurrent(expected, token)) CloseOperation(); runtime!.NotifyChanged();
        }));
        ShowOperation(panel);
    }

    private async Task UnsubscribeAsync()
    {
        if (Subscription() is not { } sub || session is not { } expected) return;
        var token = DetailToken;
        if (sub.Downloads.Any(item => item.State is DownloadState.Pending or DownloadState.Downloading &&
            (!runtime!.Progress.TryGetValue(item.Id, out var progress) || progress.State is DownloadState.Pending or DownloadState.Downloading)))
        { ReportNotice(T("CancelBeforeRemoval")); await OpenFilesAsync(); return; }
        var pending = await Task.Run(() => runtime!.Installer.PreviewRecovery(sub.GameDirectory), token);
        if (!IsCurrent(expected, token) || !SameGame(sub.GameDirectory)) return;
        if (pending.Length > 0) { await ReviewRecoveryAsync(); return; }
        var plan = await Task.Run(() => runtime!.Installer.PreviewUnsubscribe(sub.Id), token);
        if (!IsCurrent(expected, token) || !SameGame(sub.GameDirectory)) return;
        var panel = OperationPanel(T("Unsubscribe"));
        panel.Children.Add(Text(T("CleanupBoundary"))); panel.Children.Add(Text(T("GameScope") + " " + plan.GameDirectory));
        panel.Children.Add(Text(T("WillDelete") + $" ({plan.Files.Length})"));
        foreach (var file in plan.Files) panel.Children.Add(Text(file.Path));
        panel.Children.Add(Text(T("WillKeep") + $" ({plan.PreservedPaths.Length})"));
        foreach (var path in plan.PreservedPaths) panel.Children.Add(Text(path));
        foreach (var reason in plan.BlockingReasons) panel.Children.Add(Text(T("Blocked") + ": " + reason));
        var error = Text(""); panel.Children.Add(error);
        var confirm = new Button { Name = "subscriptionConfirmRemoval", Content = Text(T("ConfirmRemoval")), IsEnabled = plan.BlockingReasons.Length == 0 };
        panel.Children.Add(confirm);
        confirm.Click += async (_, _) =>
        {
            if (operationBusy || !IsCurrent(expected, token) || !SameGame(sub.GameDirectory)) return;
            operationBusy = true; confirm.IsEnabled = false;
            try
            {
                await Task.Run(() => runtime!.Installer.Unsubscribe(plan, token), token);
                await runtime!.RefreshLibraryAsync(lifetime.Token);
                if (IsCurrent(expected, token))
                {
                    CloseOperation(); RefreshTasks(); RefreshTracking(); RenderFileState();
                    if (view.SelectedIndex != 0) await LoadListAsync();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (IsCurrent(expected, token)) error.Text = T("Failed") + ": " + ex.Message; }
            finally { operationBusy = false; confirm.IsEnabled = plan.BlockingReasons.Length == 0; runtime!.NotifyChanged(); }
        };
        ShowOperation(panel);
    }

    private async Task RefreshRecoveryAsync()
    {
        if (runtime?.Ready.IsCompletedSuccessfully != true || main.SubscriptionGameDirectory is not { } game || recoveryLoading) return;
        recoveryLoading = true;
        try
        {
            var operations = await Task.Run(() => runtime.Installer.PreviewRecovery(game), lifetime.Token);
            if (!SameGame(game)) return;
            recovery.Children.Clear();
            pageStatus.Remove("recovery"); RenderStatus();
            if (operations.Length == 0) return;
            recovery.Children.Add(Text(T("UnfinishedOperations"), 16));
            recovery.Children.Add(Text(T("RecoveryScope")));
            recovery.Children.Add(Action(T("Recover"), ReviewRecoveryAsync));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (SameGame(game))
            {
                recovery.Children.Clear();
                ReportNotice(T("RecoveryReadFailed") + ": " + ex.Message, failure: true, key: "recovery", retry: RefreshRecoveryAsync);
            }
        }
        finally
        {
            recoveryLoading = false;
            if (!disposed && !SameGame(game)) _ = RefreshRecoveryAsync();
        }
    }

    private async Task ReviewRecoveryAsync()
    {
        if (main.SubscriptionGameDirectory is not { } game) return;
        var token = DetailToken;
        var pending = await Task.Run(() => runtime!.Installer.PreviewRecovery(game), token);
        if (!SameGame(game) || token.IsCancellationRequested) return;
        var panel = OperationPanel(T("Recover"));
        panel.Children.Add(Text(T("RecoveryScope"))); panel.Children.Add(Text(T("GameScope") + " " + game));
        foreach (var item in pending)
        {
            panel.Children.Add(Text(T("Recovery_" + item.Kind), 16));
            var sub = runtime!.Library.Subscriptions.SingleOrDefault(sub => sub.Id == item.SubscriptionId);
            if (sub is not null) panel.Children.Add(Text(runtime.Library.Mods.SingleOrDefault(mod => mod.Id == sub.Source)?.Title ?? sub.Source.RemoteId));
            foreach (var path in item.Paths) panel.Children.Add(Text(path));
        }
        var downloads = runtime!.Library.Subscriptions.Where(sub => sub.GameDirectory.Equals(game, SubscriptionPaths.Comparison))
            .SelectMany(sub => sub.Downloads).Where(item => item.State is DownloadState.Pending or DownloadState.Downloading).ToArray();
        if (downloads.Length > 0)
        {
            panel.Children.Add(Text(T("RecoveryDownloads")));
            foreach (var download in downloads) panel.Children.Add(Text(download.File.Name));
        }
        var error = Text(""); panel.Children.Add(error);
        var confirm = new Button { Name = "subscriptionConfirmRecovery", IsEnabled = pending.Length > 0,
            Content = Text(T(pending.Any(item => item.Kind == "ContinueRemoval") ? "ConfirmRecoveryRemoval" : "ConfirmRecovery")) };
        panel.Children.Add(confirm);
        confirm.Click += async (_, _) =>
        {
            if (operationBusy || !SameGame(game) || token.IsCancellationRequested) return;
            operationBusy = true; confirm.IsEnabled = false;
            try
            {
                await runtime!.RecoverAgainAsync(game, token, pending);
                if (SameGame(game) && !token.IsCancellationRequested) { CloseOperation(); RefreshTasks(); RefreshTracking(); await RefreshRecoveryAsync(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (SameGame(game) && !token.IsCancellationRequested) error.Text = T("Failed") + ": " + ex.Message; }
            finally { operationBusy = false; confirm.IsEnabled = true; runtime!.NotifyChanged(); }
        };
        ShowOperation(panel);
    }
}
