using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage : UserControl, IDisposable
{
    private readonly MainWindow main;
    private ModSubscriptionRuntime? runtime;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? detailCancellation;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private bool active, disposed, rebuilding;
    private int browsePage = 1;
    private int listGeneration;
    private bool hasNext;
    private bool listLoading;
    private bool listFresh;
    private Task? listTask;
    private string? loadingListKey;
    private RemoteMod[] browseItems = [];
    private RemoteMod? selected;
    private ModFileSnapshot? snapshot;
    private readonly Dictionary<RemoteModFile, CheckBox> fileChoices = [];
    private ComboBox view = null!, sort = null!;
    private TabStrip source = null!;
    private CancellationTokenSource? listCancellation;
    private string appliedKeyword = "";
    private TextBox keyword = null!;
    private ListBox mods = null!;
    private TextBlock status = null!, title = null!;
    private StackPanel summary = null!, articles = null!, images = null!, files = null!, tasks = null!;
    private Button favorite = null!, previous = null!, next = null!;
    private Border verificationBanner = null!;
    private TextBlock verificationMessage = null!, verificationPrivacy = null!;
    private Button verificationOpen = null!, verificationRetry = null!, verificationCancel = null!;
    private Uri? verificationOpenedFor;
    private string? verificationNotice;
    private bool verificationBusy;
    private DateTimeOffset? verificationRequestAt;
    private CancellationTokenSource? verificationCancellation;
    private Func<Task>? verificationRetryAction;

    public ModSubscriptionsPage(MainWindow main)
    {
        this.main = main;
        BuildView();
        LocalizedStrings.Instance.PropertyChanged += LanguageChanged;
        timer.Tick += (_, _) => RefreshTasks();
    }

    internal ModSubscriptionsPage(MainWindow main, ModSubscriptionRuntime diagnosticRuntime) : this(main) => runtime = diagnosticRuntime;

    private static string T(string key) => LocalizedStrings.Instance.Get("MS_" + key);
    private static TextBlock Text(string value, double size = 14) => new()
    { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Column() => new() { Spacing = 8 };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private Button Action(string text, Func<Task> action, bool independent = false)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { if (independent) await ExecuteAsync(action); else await RunAsync(action); }
            finally { button.IsEnabled = true; if (!disposed) { RefreshVerification(); RefreshTracking(); UpdatePaging(); } }
        };
        return button;
    }
    private static ScrollViewer Scroll(Control content) => new()
    { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    public void Activate()
    {
        if (disposed) return;
        if (active) { _ = ResumeAfterCacheClearAsync(); return; }
        active = true;
        runtime ??= main.SubscriptionRuntime;
        runtime.Changed += RuntimeChanged;
        runtime.CachesClearing += CachesClearing;
        runtime.Verification.Changed += RuntimeChanged;
        timer.Start();
        _ = RunAsync(async () => { await runtime.Ready; await LoadTranslationOptionsAsync(); LoadGitHubOptions(); RefreshVerification(); await LoadListAsync(); });
    }

    public void OnGameChanged()
    {
        if (!string.Equals(workflowGame, main.SubscriptionGameDirectory, SubscriptionPaths.Comparison))
        { workflowCancellation?.Cancel(); workflowCancellation?.Dispose(); workflowCancellation = null; }
        renderedTaskSignature = null;
        cacheReturn = null;
        CancelRefresh(); ResetStatus();
        ClearDetail();
        if (active) _ = RefreshAfterGameChangeAsync();
    }

    private void ClearDetail()
    {
        installedOriginLocation = null;
        CancelLinkNavigation(clearHistory: true);
        CancelTranslation();
        SaveReadingPosition();
        detailCancellation?.Cancel(); listCancellation?.Cancel();
        readerCancellation?.Cancel();
        verificationRetryAction = null;
        listGeneration++;
        selected = null; snapshot = null; session = null; fileChoices.Clear();
        documentAnchors.Clear(); UpdateOriginal();
        CloseOperation();
        title.Text = T("ChooseMod");
        summary.Children.Clear(); articles.Children.Clear(); images.Children.Clear(); files.Children.Clear(); tasks.Children.Clear(); reading.Children.Clear(); recovery.Children.Clear();
        fileReader.Children.Clear(); fileNotice.Children.Clear(); fileEntries = []; renderedFileSession = null; fileImageExpanded = false; fileGeneration++;
        UpdateFileSelection();
        listOnly = true; RefreshTracking(); UpdateResponsiveLayout();
        imageExpanded = false; renderedSession = null; pendingImage = null; expandedMediaRefresh = null;
    }

    private Task RefreshAfterGameChangeAsync() => ExecuteAsync(LoadListAsync);

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(LocalizedStrings.Tab_ModSubscriptions)) return;
        var previousMod = selected;
        var previousOrigin = CurrentInstalledOrigin();
        int previousReadingTab = session?.Tab ?? 0;
        var previousWorkflow = details.SelectedIndex == 2 ? workflowView : WorkflowView.None;
        var previousSubscription = workflowSubscription;
        var previousDownload = workflowDownload;
        var previousPicker = previousWorkflow == WorkflowView.LocateInstallation ? installationPicker : null;
        CancelLinkNavigation();
        CancelRefresh(); CancelTranslation(); ResetStatus();
        SaveReadingPosition(); detailCancellation?.Cancel(); listCancellation?.Cancel(); selected = null; snapshot = null; session = null; CloseOperation();
        verificationRetryAction = null;
        verificationNotice = null;
        BuildView();
        if (runtime?.Ready.IsCompletedSuccessfully == true) LoadGitHubOptions();
        RefreshVerification();
        if (active) _ = ExecuteAsync(async () =>
        {
            if (previousOrigin is { } origin)
            {
                if (!await OpenInstalledSubscriptionCoreAsync(origin.SubscriptionId, origin.InstallationId)) return;
                if (previousReadingTab == 0 && session is { } current)
                {
                    current.Tab = 0; rebuilding = true; details.SelectedIndex = 0; rebuilding = false;
                    SelectVisibleContext(); RenderReading(); UpdateResponsiveLayout();
                }
            }
            else { await LoadListAsync(); if (previousMod is not null) await OpenModAsync(previousMod); }
            if (previousWorkflow == WorkflowView.None) return;
            await ShowTasksAsync();
            if (operationBusy) return;
            if (previousWorkflow == WorkflowView.Cleanup && previousSubscription is { } cleanup) await ReviewUnsubscribeAsync(cleanup, previousDownload);
            else if (previousPicker is not null) RestoreInstallationPicker(previousPicker);
            else if (previousWorkflow == WorkflowView.Install && runtime!.Library.Subscriptions.SingleOrDefault(item => item.Id == previousSubscription) is { } sub &&
                sub.Downloads.SingleOrDefault(item => item.Id == previousDownload) is { } download) await InstallAsync(sub, download);
        });
    }

    private void RuntimeChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed)
        {
            RefreshTasks(); RefreshInstallationPicker(); RefreshTracking();
            RefreshVerification();
            _ = RefreshRecoveryAsync();
            RenderModList(view.SelectedIndex == 0 ? browseItems : LibraryItems());
        }
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (disposed || !await actionGate.WaitAsync(0)) return;
        try { await ExecuteAsync(action); }
        finally { actionGate.Release(); }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (disposed) return;
        var context = BeginStatus("action:" + Guid.NewGuid().ToString("N"), "Fetch");
        try
        {
            if (runtime is not null) await runtime.Ready;
            await action();
            EndStatus(context, ModPageOperationState.Succeeded);
        }
        catch (OperationCanceledException) { EndStatus(context, ModPageOperationState.Cancelled, T("Cancelled")); }
        catch (Exception ex) { EndStatus(context, ModPageOperationState.Failed, T("Failed") + ": " + ex.Message); }
    }

    private string ListKey => string.Join("\n", main.SubscriptionGameDirectory, SelectedProvider, view.SelectedIndex, appliedKeyword, browsePage, sort.SelectedIndex);

    private Task LoadListAsync()
    {
        if (runtime is null) return Task.CompletedTask;
        string key = ListKey;
        if (listTask is { IsCompleted: false } && loadingListKey == key && listCancellation?.IsCancellationRequested == false) return listTask;
        listCancellation?.Cancel(); listCancellation?.Dispose();
        listCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loadingListKey = key;
        return listTask = LoadListCoreAsync(++listGeneration, listCancellation.Token);
    }

    private async Task LoadListCoreAsync(int generation, CancellationToken token)
    {
        if (runtime is null) return;
        listLoading = true; listFresh = false;
        UpdatePaging();
        string? game = main.SubscriptionGameDirectory;
        var progress = BeginStatus("list", "Cache", T("Browse"));
        try
        {
            RemoteMod[] items;
            if (view.SelectedIndex == 0)
            {
                string query = appliedKeyword;
                int page = browsePage;
                var order = (ModBrowseSort)Math.Max(0, sort.SelectedIndex);
                var currentSource = SourceForProvider(SelectedProvider);
                var cached = await Task.Run(() => currentSource.BrowseAsync(query, page, order, ModReadMode.CacheOnly, token), token);
                if (generation != listGeneration || disposed) return;
                if (cached.Content is { } old) { RenderModList(old.Items); }
                pageStatus.Update(progress, "Fetch", cached.Content is null ? "" : T("CachedLoading"), ModPageOperationState.Running); RenderStatus();
                var result = await Task.Run(() => currentSource.BrowseAsync(query, page, order, cancellationToken: token), token);
                token.ThrowIfCancellationRequested();
                if (generation != listGeneration || disposed) return;
                ReportResult(progress, result);
                listFresh = result.IsSuccess && !result.IsStale;
                if (result.Content is { } content) { browseItems = content.Items; hasNext = content.NextPage is not null; }
                else if (cached.Content is { } fallback) { browseItems = fallback.Items; hasNext = fallback.NextPage is not null; }
                // A failed request cannot prove that the previous list or selection disappeared.
                items = browseItems;
            }
            else
            {
                items = LibraryItems();
                listFresh = true;
                EndStatus(progress, ModPageOperationState.Succeeded, game is null && view.SelectedIndex != 2 ? T("SelectGame") : T("Ready"));
            }
            listNotice.Text = items.Length == 0 && listFresh ? T("Empty") : "";
            UpdatePaging(); RenderModList(items);
            await RefreshRecoveryAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { EndStatus(progress, ModPageOperationState.Cancelled); }
        catch (Exception ex) { if (generation == listGeneration) EndStatus(progress, ModPageOperationState.Failed, T("Failed") + ": " + ex.Message, retry: ApplySearchAsync); }
        finally { if (generation == listGeneration) { listLoading = false; UpdatePaging(); } }
    }

    private RemoteMod[] LibraryItems()
    {
        var library = runtime!.Library;
        string? game = main.SubscriptionGameDirectory;
        var subscribed = library.Subscriptions.Where(sub => sub.GameDirectory.Equals(game, SubscriptionPaths.Comparison)).ToArray();
        return library.Mods.Where(mod => mod.Id.Provider == SelectedProvider && (view.SelectedIndex switch
        {
            1 => subscribed.Any(sub => sub.Source == mod.Id),
            2 => library.Favorites.Any(favorite => favorite.Source == mod.Id),
            3 => subscribed.Any(sub => sub.Source == mod.Id && UpdateCount(sub) > 0),
            _ => false
        })).Where(mod => mod.Title.Contains(appliedKeyword, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void RenderLibraryList() => RenderModList(LibraryItems());
    private void RenderModList(RemoteMod[] items)
    {
        rebuilding = true;
        var rows = items.Select(mod => new ModRow(mod, NoticeCount(mod), Subscription(mod) is not null,
            runtime?.Library.Favorites.Any(item => item.Source == mod.Id) == true)).ToArray();
        mods.ItemsSource = rows;
        mods.SelectedItem = rows.SingleOrDefault(row => row.Mod.Id == selected?.Id);
        rebuilding = false;
    }

    private sealed record ModRow(RemoteMod Mod, int Count, bool Subscribed, bool Favorited)
    {
        public override string ToString() => Mod.Title + (Subscribed ? "\n" + T("Subscribed") : "") +
            (Favorited ? "\n" + T("Favorited") : "") +
            (Mod.Id.Provider == "github" ? "\n" + T("Kind_" + Mod.Kind) : "") +
            (Count > 0 ? " · " + T("Updates") + $" ({Count})" : "");
    }
    private int NoticeCount(RemoteMod mod) => Subscription(mod) is { } sub ? UpdateCount(sub) : 0;
    private static int UpdateCount(GameModSubscription subscription) => ModUpdateCheckService.GetNotices(subscription).Length +
        (ModUpdateCheckService.GetPageNotice(subscription) is null ? 0 : 1);
    private GameModSubscription? Subscription(RemoteMod? mod = null) => runtime is not null && (mod ?? selected) is { } item && main.SubscriptionGameDirectory is { } game
        ? runtime.Library.Subscriptions.SingleOrDefault(sub => sub.Source == item.Id && sub.GameDirectory.Equals(game, SubscriptionPaths.Comparison)) : null;

    private string ReadStatus<TContent>(ModSourceResult<TContent> result, Func<Task>? retry = null)
    {
        if (result.IsSuccess && !result.IsStale) return T("Ready");
        string message = T(result.Content is null ? "RequestFailed" : "Cached");
        if (result.Failure is not { } failure) return message + " · " + T("Offline");
        if (failure.Kind == ModSourceError.VerificationRequired)
        {
            if (failure.VerificationUri is { } verificationUri && AnonymousModHttpClient.IsPublicHttps(verificationUri))
                runtime?.Verification.Pause(verificationUri);
            if (retry is not null) verificationRetryAction = retry;
            verificationNotice = "VerificationRequired";
            RefreshVerification();
        }
        AppDiagnostics.Log($"Mod source content failure: kind={failure.Kind}, HTTP={(int?)failure.StatusCode}, diagnostic={failure.DiagnosticKind}.");
        message += " · " + T("Error_" + failure.Kind);
        if (failure.StatusCode is { } code) message += $" (HTTP {(int)code})";
        if (failure.RetryAfter is { } retryAfter) message += " · " + T("RetryAfter") + ": " + retryAfter.ToLocalTime().ToString("g");
        if (failure.DiagnosticKind is { } diagnostic && diagnostic != ModHttpFailureKind.Unknown)
            message += " · " + T("Network_" + diagnostic);
        return message + " · " + T("NetworkLog");
    }

    private async Task ToggleFavoriteAsync()
    {
        if (selected is not { } mod || runtime is null) return;
        await Task.Run(() =>
        {
            runtime.Store.SaveMod(mod);
            runtime.Store.SetFavorite(mod.Id, !runtime.Store.Snapshot().Favorites.Any(f => f.Source == mod.Id));
        });
        await runtime.RefreshLibraryAsync();
        RefreshTracking(); runtime.NotifyChanged();
        if (view.SelectedIndex == 2) await LoadListAsync();
    }

    private async Task CheckAsync()
    {
        if (main.SubscriptionGameDirectory is not { } game) { ReportNotice(T("SelectGame")); return; }
        var progress = BeginStatus("subscriptionCheck", "Fetch", T("CheckScope"));
        try
        {
            await runtime!.EnsureRecoveredAsync(game, lifetime.Token);
            var results = await Task.Run(() => runtime.Updates.CheckGameAsync(game, cancellationToken: lifetime.Token), lifetime.Token);
            if (results.Any(result => result.Failure?.Kind == ModSourceError.VerificationRequired))
            { verificationRetryAction = CheckAsync; RefreshVerification(); }
            runtime.ReportCheckResults(results, game);
            await runtime.RefreshLibraryAsync(lifetime.Token);
            if (!string.Equals(game, main.SubscriptionGameDirectory, StringComparison.OrdinalIgnoreCase)) return;
            runtime.NotifyChanged(); RefreshTracking();
            if (view.SelectedIndex != 0) await LoadListAsync();
            EndStatus(progress, results.Any(result => !result.Succeeded) ? ModPageOperationState.Failed : ModPageOperationState.Succeeded,
                results.Any(result => !result.Succeeded) ? T("CheckFailed") : T("Checked"), retry: results.Any(result => !result.Succeeded) ? CheckAsync : null);
        }
        catch (OperationCanceledException) { EndStatus(progress, ModPageOperationState.Cancelled); }
        catch (Exception ex) { EndStatus(progress, ModPageOperationState.Failed, T("Failed") + ": " + ex.Message, retry: CheckAsync); }
    }

    private void RefreshTracking()
    {
        if (runtime is null || !runtime.Ready.IsCompletedSuccessfully) return;
        favorite.IsEnabled = selected is not null;
        favorite.Content = selected is { } mod && runtime.Library.Favorites.Any(f => f.Source == mod.Id) ? T("Unfavorite") : T("Favorite");
        var sub = Subscription();
        FillStateBadges(titleBadges, sub is not null,
            selected is { } item && runtime.Library.Favorites.Any(f => f.Source == item.Id));
        unsubscribe.IsVisible = sub is not null;
        unsubscribe.Content = T(sub?.State is SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed ? "ContinueCleanup" : "Unsubscribe");
        foreach (var pair in fileChoices)
            pair.Value.IsEnabled = sub?.State is not (SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed) &&
                snapshot?.Files.Any(file => file.Id == pair.Key.Id && file.Revision == pair.Key.Revision && file.CanDownload) == true;
        UpdateFileSelection();
        if (runtime.Error is { } error) ReportNotice(T("Failed") + ": " + error, failure: true, key: "runtime");
        else { pageStatus.Remove("runtime"); RenderStatus(); }
    }

    private static void OpenUrl(Uri url)
    {
        if (ModDocumentParser.IsLink(url)) Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    public void Dispose()
    {
        if (disposed) return;
        CancelRefresh(); CancelTranslation();
        disposed = true; timer.Stop(); lifetime.Cancel(); detailCancellation?.Cancel(); verificationCancellation?.Cancel();
        workflowCancellation?.Cancel(); workflowCancellation?.Dispose();
        LocalizedStrings.Instance.PropertyChanged -= LanguageChanged;
        if (runtime is not null)
        {
            runtime.Changed -= RuntimeChanged;
            runtime.CachesClearing -= CachesClearing;
            runtime.Verification.Changed -= RuntimeChanged;
        }
        DisposeMedia(); imageClient.Dispose(); DisposeTranslationProviders();
        CancelLinkNavigation(clearHistory: true);
    }
}
