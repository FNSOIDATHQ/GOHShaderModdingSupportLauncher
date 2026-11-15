using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private bool operationBusy, recoveryLoading;
    private ReadingContext? operationContext;
    private enum WorkflowView { None, Tasks, Install, Cleanup, Recovery, Result, LocateInstallation }
    private WorkflowView workflowView;
    private Guid? workflowSubscription, workflowDownload;
    private int operationGeneration;
    private int detailTabGeneration;
    private string? workflowGame;
    private CancellationTokenSource? workflowCancellation;
    private readonly HashSet<Guid> installingDownloads = [];
    private readonly Dictionary<Guid, string> installationErrors = [];
    private sealed record InstallationReviewState(string Game, Guid SubscriptionId, Guid DownloadId,
        string? CandidateRoot, Guid? ReplacementId, string TargetDirectory);
    private InstallationReviewState? installationReview;

    private CancellationToken WorkflowToken
    {
        get
        {
            string? game = main.SubscriptionGameDirectory;
            if (workflowCancellation is null || !string.Equals(game, workflowGame, SubscriptionPaths.Comparison))
            {
                workflowCancellation?.Cancel(); workflowCancellation?.Dispose();
                workflowCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                workflowGame = game;
            }
            return workflowCancellation.Token;
        }
    }

    private void CloseOperation()
    {
        if (operation is null) return;
        operationGeneration++;
        if (operation.IsVisible) SetReadingContext(operationContext);
        operationContext = null; workflowView = WorkflowView.None;
        installationPicker = null; renderedInstallationPicker = null;
        installationReview = null;
        workflowSubscription = workflowDownload = null;
        operation.Content = null; operation.IsVisible = false; operationScroll.IsVisible = false;
        if (details.SelectedIndex == 2)
        {
            bool previousRebuilding = rebuilding; rebuilding = true;
            details.SelectedIndex = session?.Tab ?? 0; rebuilding = previousRebuilding;
        }
        UpdateResponsiveLayout();
        if (cacheResetAfterOperation)
        {
            cacheResetAfterOperation = false; ClearDetail(); ResumeVisibleCacheReading(); return;
        }
        if (session is { } current) RestorePosition(fileScroll, current.FilePosition);
        if (translationPreferences.Enabled && session is { } expected) Dispatcher.UIThread.Post(() =>
        { if (session == expected) RerenderTranslation(); });
    }

    private void ShowOperation(Control content) => SetOperationContent(content, activate: true);

    private void SetOperationContent(Control content, bool activate)
    {
        if (activate)
        {
            CancelLinkNavigation();
            if (!operation.IsVisible) { operationContext = session?.Context; SaveReadingPosition(); }
            bool previousRebuilding = rebuilding; rebuilding = true;
            details.SelectedIndex = 2; rebuilding = previousRebuilding;
            listOnly = false;
        }
        operationGeneration++;
        operation.Content = content;
        operation.IsVisible = operationScroll.IsVisible = details.SelectedIndex == 2;
        operationScroll.Offset = default;
        if (operation.IsVisible) { CancelTranslation(); SetReadingContext(operationContext); }
        UpdateResponsiveLayout();
    }

    private async Task ChangeDetailTabAsync()
    {
        detailTabGeneration++;
        CancelLinkNavigation(); SaveReadingPosition(); CancelRefresh(); CancelTranslation();
        if (details.SelectedIndex == 2)
        {
            if (operation.Content is null || workflowView == WorkflowView.None)
                await ExecuteAsync(ShowTasksAsync);
            else
            {
                operationContext = session?.Context;
                operation.IsVisible = operationScroll.IsVisible = true;
                listOnly = false; SetReadingContext(operationContext); RefreshTasks();
                RefreshInstallationPicker();
            }
        }
        else
        {
            operation.IsVisible = operationScroll.IsVisible = false;
            if (cacheResetAfterOperation && !operationBusy) { CloseOperation(); return; }
            if (session is { } current)
            {
                current.Tab = details.SelectedIndex;
                if (details.SelectedIndex == 1) await ExecuteAsync(() => EnsureFilesAsync());
                else RerenderTranslation();
            }
            SelectVisibleContext();
        }
        UpdateResponsiveLayout();
    }

    private StackPanel OperationPanel(string? caption = null, bool taskBack = true)
    {
        var panel = Column();
        var navigation = new WrapPanel { Name = "subscriptionWorkflowNavigation", ItemSpacing = 6, LineSpacing = 6 };
        if (taskBack)
        {
            var back = Action(T("Back"), ShowTasksAsync, independent: true); back.Name = "subscriptionWorkflowBack";
            navigation.Children.Add(back);
        }
        if (taskBack) panel.Children.Add(navigation);
        if (caption is not null) panel.Children.Add(new TextBlock { Name = "subscriptionWorkflowTitle", Text = caption, FontSize = 20, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return panel;
    }

    private bool SameGame(string game) => !disposed && string.Equals(main.SubscriptionGameDirectory, game, SubscriptionPaths.Comparison);
    private bool SameWorkflow(int generation, string game) => SameGame(game) && workflowView != WorkflowView.None && operationGeneration == generation;
    private bool CurrentOperation(int generation, string game) => operation.IsVisible && SameWorkflow(generation, game);

    private static async Task<TValue> DiskReviewAsync<TValue>(Func<TValue> review, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return review(); }
                catch (System.IO.IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, token); }
            }
        }, token);
    }

    internal Task ShowTasksAsync()
    {
        installationReview = null;
        installationPicker = null; renderedInstallationPicker = null;
        var panel = OperationPanel(taskBack: false);
        if (main.SubscriptionGameDirectory is not { } game) panel.Children.Add(Text(T("SelectGame")));
        else
        {
            _ = WorkflowToken;
            panel.Children.Add(Text(T("GameScope") + " " + game));
            tasks = Column(); tasks.Name = "subscriptionTaskList"; panel.Children.Add(tasks);
            if (recovery.Parent is Panel parent) parent.Children.Remove(recovery);
            panel.Children.Add(recovery);
            foreach (var sub in runtime!.Library.Subscriptions.Where(sub => sub.GameDirectory.Equals(game, SubscriptionPaths.Comparison) && sub.State == SubscriptionState.Active))
                runtime.AllowSubscriptionDownloads(sub.Id);
        }
        workflowView = WorkflowView.Tasks; workflowSubscription = workflowDownload = null;
        renderedTaskSignature = null; ShowOperation(panel); RefreshTasks(); _ = RefreshRecoveryAsync();
        return Task.CompletedTask;
    }

    internal Task InstallAsync(GameModSubscription sub, ModDownload download) => PrepareInstallAsync(sub, download);

    private async Task PrepareInstallAsync(GameModSubscription sub, ModDownload download,
        InstallationReviewState? restored = null, bool activate = true)
    {
        if (operationBusy || !SameGame(sub.GameDirectory)) return;
        var token = WorkflowToken;
        int opening = operationGeneration, navigation = detailTabGeneration;
        var pending = await Task.Run(() => runtime!.Installer.PreviewRecovery(sub.GameDirectory), token);
        if (!SameGame(sub.GameDirectory) || token.IsCancellationRequested || opening != operationGeneration || navigation != detailTabGeneration) return;
        if (pending.Length > 0) { await PrepareRecoveryAsync(activate); return; }
        var inspection = await DiskReviewAsync(() => runtime!.Installer.Inspect(sub.Id, download.Id, token), token);
        if (!SameGame(sub.GameDirectory) || token.IsCancellationRequested || opening != operationGeneration || navigation != detailTabGeneration) return;
        var panel = OperationPanel();
        panel.Children.Add(Text(T("GameScope") + " " + sub.GameDirectory));
        panel.Children.Add(Text(T("PlacementBoundary")));
        if (inspection.ManualInstallReason is not null || inspection.Candidates.Length == 0)
        {
            panel.Children.Add(Text(T("PlacementCheckFailed") + ": " + inspection.ManualInstallReason));
            SetOperationContent(panel, activate); return;
        }
        if (restored is not null && (restored.SubscriptionId != sub.Id || restored.DownloadId != download.Id ||
            !restored.Game.Equals(sub.GameDirectory, SubscriptionPaths.Comparison))) restored = null;
        int rootIndex = restored is null ? inspection.Candidates.Length == 1 ? 0 : -1 :
            Array.FindIndex(inspection.Candidates, candidate => candidate.RootPath == restored.CandidateRoot);
        int replacementIndex = 0;
        if (restored?.ReplacementId is { } replacementId)
        {
            int found = Array.FindIndex(sub.Installations, installation => installation.Id == replacementId);
            replacementIndex = found >= 0 ? found + 1 : -1;
        }
        var rootChoice = new ComboBox { Name = "subscriptionInstallRoot", ItemsSource = inspection.Candidates.Select(item => item.PreserveAsFile ? T("OriginalFilePlacement") : item.RootPath.Length == 0 ? T("EntirePackage") : item.RootPath).ToArray(),
            PlaceholderText = T("ChooseRoot"), SelectedIndex = rootIndex, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Text(T("PlacementLayout"))); panel.Children.Add(rootChoice);
        var replacements = new ComboBox { Name = "subscriptionInstallMode", ItemsSource = new[] { T("NewInstall") }.Concat(sub.Installations.Select(item => T("Replace") + " " + item.DirectoryPath)).ToArray(), SelectedIndex = replacementIndex,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(replacements);
        var target = new TextBox { Name = "subscriptionInstallDirectory", MaxLength = 120,
            IsReadOnly = replacementIndex > 0,
            Text = restored?.TargetDirectory ?? (rootChoice.SelectedIndex >= 0 ? inspection.Candidates[rootChoice.SelectedIndex].DirectoryName : "") };
        panel.Children.Add(Text(T("TargetFolder"))); panel.Children.Add(target);
        var preview = Column(); preview.Name = "subscriptionPlacementPreview"; panel.Children.Add(preview);
        var error = Text(""); error.Name = "subscriptionInstallError"; panel.Children.Add(error);
        var install = new Button { Name = "subscriptionConfirmInstall", Content = T("ConfirmInstall"), IsEnabled = false }; panel.Children.Add(install);
        panel.Children.Add(AuthorNotes(inspection.AuthorInstructions));
        workflowView = WorkflowView.Install; workflowSubscription = sub.Id; workflowDownload = download.Id;
        SetOperationContent(panel, activate);
        int generation = operationGeneration, reviewGeneration = 0;
        ModInstallRequest? reviewedRequest = null;
        async Task ReviewTargetAsync()
        {
            if (!SameWorkflow(generation, sub.GameDirectory)) return;
            int review = ++reviewGeneration;
            installationReview = new(sub.GameDirectory, sub.Id, download.Id,
                rootChoice.SelectedIndex >= 0 ? inspection.Candidates[rootChoice.SelectedIndex].RootPath : null,
                replacements.SelectedIndex > 0 ? sub.Installations[replacements.SelectedIndex - 1].Id :
                    replacements.SelectedIndex < 0 ? restored?.ReplacementId : null, target.Text ?? "");
            reviewedRequest = null; install.IsEnabled = false; preview.Children.Clear(); error.Text = "";
            if (rootChoice.SelectedIndex < 0) { error.Text = T("ChooseRoot"); return; }
            if (replacements.SelectedIndex < 0) { error.Text = T("ChooseInstallMode"); return; }
            var candidate = inspection.Candidates[rootChoice.SelectedIndex];
            Guid? replacement = replacements.SelectedIndex > 0 ? sub.Installations[replacements.SelectedIndex - 1].Id : null;
            var request = new ModInstallRequest(sub.Id, download.Id, candidate.RootPath, true, replacement, target.Text ?? "", candidate);
            try
            {
                await Task.Delay(100, token);
                if (review != reviewGeneration || !SameWorkflow(generation, sub.GameDirectory)) return;
                var plan = await DiskReviewAsync(() => runtime!.Installer.PreviewPlacement(request, token), token);
                if (review != reviewGeneration || !SameWorkflow(generation, sub.GameDirectory)) return;
                preview.Children.Add(Text(T("Destination") + " " + plan.Destination));
                if (!ModInspectionService.IsValidLoadId(Path.GetFileName(plan.Destination)))
                {
                    var warning = Text(T("InvalidLoadDirectory")); warning.Name = "subscriptionLoadDirectoryWarning";
                    preview.Children.Add(warning);
                }
                preview.Children.Add(Text(string.Format(T("PlacementScope"), candidate.Files.Length, FormatBytes(candidate.SizeBytes), plan.Previous?.OwnedFiles.Length ?? 0)));
                if (plan.Previous is not null) preview.Children.Add(Text(T("ReplacementBoundary")));
                var paths = Column(); foreach (string path in candidate.Files) paths.Children.Add(Text(path, 12));
                preview.Children.Add(new Expander { Header = T("FileDetails"), Content = paths, HorizontalAlignment = HorizontalAlignment.Stretch });
                reviewedRequest = request; install.IsEnabled = !operationBusy;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (review == reviewGeneration && SameWorkflow(generation, sub.GameDirectory)) error.Text = T("PlacementCheckFailed") + ": " + ex.Message; }
        }
        rootChoice.SelectionChanged += async (_, _) =>
        {
            if (!SameWorkflow(generation, sub.GameDirectory)) return;
            if (replacements.SelectedIndex == 0 && rootChoice.SelectedIndex >= 0) target.Text = inspection.Candidates[rootChoice.SelectedIndex].DirectoryName;
            await ReviewTargetAsync();
        };
        replacements.SelectionChanged += async (_, _) =>
        {
            if (!SameWorkflow(generation, sub.GameDirectory)) return;
            target.IsReadOnly = replacements.SelectedIndex > 0;
            target.Text = replacements.SelectedIndex > 0 ? Path.GetFileName(sub.Installations[replacements.SelectedIndex - 1].DirectoryPath) :
                rootChoice.SelectedIndex >= 0 ? inspection.Candidates[rootChoice.SelectedIndex].DirectoryName : "";
            await ReviewTargetAsync();
        };
        target.TextChanged += async (_, _) => await ReviewTargetAsync();
        install.Click += async (_, _) =>
        {
            if (operationBusy || reviewedRequest is not { } request || !CurrentOperation(generation, sub.GameDirectory) || token.IsCancellationRequested) return;
            operationBusy = true; installingDownloads.Add(download.Id); installationErrors.Remove(download.Id);
            install.IsEnabled = false; rootChoice.IsEnabled = replacements.IsEnabled = target.IsEnabled = false;
            error.Text = T("Installing"); preview.Children.Add(new ProgressBar { IsIndeterminate = true });
            string? failure = null;
            try
            {
                var installed = await Task.Run(() => runtime!.Installer.Install(request, token), token);
                await runtime!.RefreshLibraryAsync(lifetime.Token);
                if (SameWorkflow(generation, sub.GameDirectory)) ShowInstallationResult(sub, download, installed, activate: operation.IsVisible);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                installationErrors[download.Id] = ex.Message;
                failure = ex.Message;
                if (SameWorkflow(generation, sub.GameDirectory)) error.Text = T("Failed") + ": " + ex.Message;
            }
            finally
            {
                operationBusy = false; installingDownloads.Remove(download.Id);
                rootChoice.IsEnabled = replacements.IsEnabled = target.IsEnabled = true;
                if (SameWorkflow(generation, sub.GameDirectory)) await ReviewTargetAsync();
                if (failure is not null && SameWorkflow(generation, sub.GameDirectory)) error.Text = T("Failed") + ": " + failure;
                if (cacheResetAfterOperation && !operation.IsVisible) CloseOperation();
                runtime!.NotifyChanged();
            }
        };
        await ReviewTargetAsync();
    }

    private Expander AuthorNotes(string instructions)
    {
        var content = Column(); AddReadable(content, instructions.Length == 0 ? T("NoAuthorNotes") : instructions);
        return new Expander { Name = "subscriptionAuthorNotes", Header = Text(T("AuthorNotes")), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private void ShowInstallationResult(GameModSubscription sub, ModDownload download, ModInstallation installed, bool activate)
    {
        var panel = OperationPanel(T("PlacementComplete"));
        panel.Children.Add(Text(installed.DirectoryPath));
        panel.Children.Add(Action(T("OpenInstallation"), () => { OpenDirectory(installed.DirectoryPath); return Task.CompletedTask; }, independent: true));
        panel.Children.Add(Action(T("ViewManager"), () => OpenInstallationInManagerAsync(sub.Id, installed.Id), independent: true));
        AddLocalInspection(panel, runtime!.Library.Subscriptions.SingleOrDefault(item => item.Id == sub.Id), download.Id);
        workflowView = WorkflowView.Result; SetOperationContent(panel, activate);
    }

    private static void OpenDirectory(string path)
    {
        ManagedModFiles.EnsureSafe(path);
        if (Directory.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private Task UnsubscribeAsync() => Subscription() is { } sub ? ReviewUnsubscribeAsync(sub.Id) : Task.CompletedTask;

    internal Task ReviewUnsubscribeAsync(Guid subscriptionId, Guid? downloadId = null) => PrepareUnsubscribeAsync(subscriptionId, activate: true, downloadId);

    private async Task PrepareUnsubscribeAsync(Guid subscriptionId, bool activate, Guid? downloadId = null)
    {
        if (operationBusy || runtime!.Library.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId) is not { } sub || !SameGame(sub.GameDirectory)) return;
        if (downloadId is { } fileId && (sub.State != SubscriptionState.Active ||
            sub.Downloads.SingleOrDefault(item => item.Id == fileId) is not { State: not (DownloadState.Pending or DownloadState.Downloading) })) return;
        var token = WorkflowToken; int opening = operationGeneration, navigation = detailTabGeneration;
        var panel = OperationPanel();
        panel.Children.Add(Text(T("GameScope") + " " + sub.GameDirectory));
        if (downloadId is null && sub.Downloads.Any(item => item.State is DownloadState.Pending or DownloadState.Downloading))
        {
            panel.Children.Add(Text(T("StopTasksFirst")));
            var stop = new Button { Name = "subscriptionStopTasks", Content = T("StopAndPreview") }; panel.Children.Add(stop);
            var stopStatus = Text(""); panel.Children.Add(stopStatus);
            workflowView = WorkflowView.Cleanup; workflowSubscription = sub.Id; workflowDownload = downloadId;
            SetOperationContent(panel, activate); int stopping = operationGeneration;
            stop.Click += async (_, _) =>
            {
                if (operationBusy || !CurrentOperation(stopping, sub.GameDirectory)) return;
                operationBusy = true; stop.IsEnabled = false; stopStatus.Text = T("StoppingTasks");
                try
                {
                    await runtime.StopSubscriptionTasksAsync(sub, token);
                    operationBusy = false;
                    if (SameWorkflow(stopping, sub.GameDirectory)) await PrepareUnsubscribeAsync(sub.Id, activate: operation.IsVisible, downloadId);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { if (SameWorkflow(stopping, sub.GameDirectory)) stopStatus.Text = T("Failed") + ": " + ex.Message; }
                finally { operationBusy = false; stop.IsEnabled = true; runtime.NotifyChanged(); }
            };
            return;
        }
        if (downloadId is { } checking) await runtime.WaitForDownloadPreparationAsync(checking, token);
        var pending = await Task.Run(() => runtime.Installer.PreviewRecovery(sub.GameDirectory), token);
        if (!SameGame(sub.GameDirectory) || token.IsCancellationRequested || opening != operationGeneration || activate && navigation != detailTabGeneration) return;
        if (pending.Length > 0) { await PrepareRecoveryAsync(activate); return; }
        var plan = await DiskReviewAsync(() => runtime.Installer.PreviewUnsubscribe(sub.Id, downloadId), token);
        if (!SameGame(sub.GameDirectory) || token.IsCancellationRequested || opening != operationGeneration || activate && navigation != detailTabGeneration) return;
        panel.Children.Add(Text(T("CleanupBoundary")));
        if (downloadId is { } selectedFile) panel.Children.Add(Text(sub.Downloads.Single(item => item.Id == selectedFile).File.Name, 16));
        panel.Children.Add(Text(T("WillDelete") + $" ({plan.Files.Length})"));
        AddCleanupGroups(panel, sub, plan.Files.Select(file => file.Path), T("WillDelete"));
        panel.Children.Add(Text(T("WillKeep") + $" ({plan.PreservedPaths.Length})"));
        AddCleanupGroups(panel, sub, plan.PreservedPaths, T("WillKeep"));
        foreach (var reason in plan.BlockingReasons) panel.Children.Add(Text(T("Blocked") + ": " + reason));
        var error = Text(""); panel.Children.Add(error);
        var confirm = new Button { Name = "subscriptionConfirmRemoval", Content = Text(T("ConfirmRemoval")), IsEnabled = plan.BlockingReasons.Length == 0 };
        panel.Children.Add(confirm);
        panel.Children.Add(Action(T("RefreshCleanup"), () => ReviewUnsubscribeAsync(sub.Id, downloadId), independent: true));
        workflowView = WorkflowView.Cleanup; workflowSubscription = sub.Id; workflowDownload = downloadId;
        SetOperationContent(panel, activate); int generation = operationGeneration;
        confirm.Click += async (_, _) =>
        {
            if (operationBusy || !CurrentOperation(generation, sub.GameDirectory) || token.IsCancellationRequested || plan.BlockingReasons.Length > 0) return;
            operationBusy = true; confirm.IsEnabled = false; error.Text = T("Cleaning");
            try
            {
                await Task.Run(() => runtime.Installer.Unsubscribe(plan, token), token);
                await runtime.RefreshLibraryAsync(lifetime.Token);
                if (downloadId is { } removed) runtime.RecheckDownload(removed);
                if (SameWorkflow(generation, sub.GameDirectory))
                {
                    var result = OperationPanel(T("CleanupComplete"));
                    result.Children.Add(Text(T("WillDelete") + $" ({plan.Files.Length})"));
                    result.Children.Add(Text(T("WillKeep") + $" ({plan.PreservedPaths.Length})"));
                    AddCleanupGroups(result, sub, plan.PreservedPaths, T("WillKeep"));
                    workflowView = WorkflowView.Result; SetOperationContent(result, activate: operation.IsVisible);
                    RefreshTracking(); RenderModList(view.SelectedIndex == 0 ? browseItems : LibraryItems());
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (SameWorkflow(generation, sub.GameDirectory))
                {
                    error.Text = T("CleanupIncomplete") + ": " + ex.Message;
                    panel.Children.Add(Action(T("ContinueCleanup"), () => ReviewUnsubscribeAsync(sub.Id, downloadId), independent: true));
                }
            }
            finally { operationBusy = false; if (cacheResetAfterOperation && !operation.IsVisible) CloseOperation(); runtime.NotifyChanged(); }
        };
    }

    private void AddCleanupGroups(StackPanel panel, GameModSubscription sub, IEnumerable<string> paths, string action)
    {
        string Group(string path) => sub.Installations.Select(item => item.DirectoryPath)
            .FirstOrDefault(directory => path.StartsWith(directory + Path.DirectorySeparatorChar, SubscriptionPaths.Comparison)) ??
            sub.Downloads.Select(item => ModDownloadService.DownloadDirectory(sub.GameDirectory, item.Id))
            .FirstOrDefault(directory => path.StartsWith(directory + Path.DirectorySeparatorChar, SubscriptionPaths.Comparison)) ?? Path.GetDirectoryName(path)!;
        foreach (var group in paths.GroupBy(Group, StringComparer.OrdinalIgnoreCase))
        {
            var details = Column(); foreach (string path in group) details.Children.Add(Text(path, 12));
            panel.Children.Add(new Expander { Header = Text(action + $" ({group.Count()}) · " + group.Key), Content = details, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
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

    private Task ReviewRecoveryAsync() => PrepareRecoveryAsync(activate: true);

    private async Task PrepareRecoveryAsync(bool activate)
    {
        if (operationBusy || main.SubscriptionGameDirectory is not { } game) return;
        var token = WorkflowToken;
        int opening = operationGeneration, navigation = detailTabGeneration;
        var pending = await Task.Run(() => runtime!.Installer.PreviewRecovery(game), token);
        if (!SameGame(game) || token.IsCancellationRequested || opening != operationGeneration || activate && navigation != detailTabGeneration) return;
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
        int generation = 0;
        confirm.Click += async (_, _) =>
        {
            if (operationBusy || !CurrentOperation(generation, game) || token.IsCancellationRequested) return;
            operationBusy = true; confirm.IsEnabled = false;
            try
            {
                await runtime!.RecoverAgainAsync(game, token, pending);
                if (SameWorkflow(generation, game) && !token.IsCancellationRequested) { CloseOperation(); RefreshTasks(); RefreshTracking(); await RefreshRecoveryAsync(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (SameWorkflow(generation, game) && !token.IsCancellationRequested) error.Text = T("Failed") + ": " + ex.Message; }
            finally { operationBusy = false; confirm.IsEnabled = true; runtime!.NotifyChanged(); }
        };
        workflowView = WorkflowView.Recovery; SetOperationContent(panel, activate); generation = operationGeneration;
    }
}
