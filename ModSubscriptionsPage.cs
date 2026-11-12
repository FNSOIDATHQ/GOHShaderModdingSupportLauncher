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
    private TextBlock status = null!, title = null!, tracking = null!;
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
        if (active || disposed) return;
        active = true;
        runtime = main.SubscriptionRuntime;
        runtime.Changed += RuntimeChanged;
        runtime.Verification.Changed += RuntimeChanged;
        timer.Start();
        _ = RunAsync(async () => { await runtime.Ready; LoadGitHubOptions(); RefreshVerification(); await LoadListAsync(); });
    }

    public void OnGameChanged()
    {
        SaveReadingPosition();
        detailCancellation?.Cancel(); listCancellation?.Cancel();
        verificationRetryAction = null;
        listGeneration++;
        selected = null; snapshot = null; session = null; fileChoices.Clear();
        CloseOperation();
        title.Text = T("ChooseMod");
        summary.Children.Clear(); articles.Children.Clear(); images.Children.Clear(); files.Children.Clear(); tasks.Children.Clear(); reading.Children.Clear(); recovery.Children.Clear();
        tracking.Text = ""; listOnly = true; RefreshTracking(); UpdateResponsiveLayout();
        if (active) _ = RefreshAfterGameChangeAsync();
    }

    private Task RefreshAfterGameChangeAsync() => ExecuteAsync(LoadListAsync);

    private async Task ApplySearchAsync()
    {
        string query = keyword.Text?.Trim() ?? "";
        if (query != appliedKeyword) browsePage = 1;
        appliedKeyword = query;
        await LoadListAsync();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(LocalizedStrings.Tab_ModSubscriptions)) return;
        var previousMod = selected;
        SaveReadingPosition(); detailCancellation?.Cancel(); listCancellation?.Cancel(); selected = null; snapshot = null; session = null; CloseOperation();
        verificationRetryAction = null;
        verificationNotice = null;
        BuildView();
        if (runtime?.Ready.IsCompletedSuccessfully == true) LoadGitHubOptions();
        RefreshVerification();
        if (active) _ = ExecuteAsync(async () => { await LoadListAsync(); if (previousMod is not null) await OpenModAsync(previousMod); });
    }

    private void RuntimeChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed)
        {
            RefreshTasks(); RefreshTracking();
            RefreshVerification();
            _ = RefreshRecoveryAsync();
            if (view.SelectedIndex != 0) RenderLibraryList();
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
        string provider = SelectedProvider;
        string? game = main.SubscriptionGameDirectory;
        var mod = selected?.Id;
        bool Current() => !disposed && provider == SelectedProvider && game == main.SubscriptionGameDirectory && mod == selected?.Id;
        try
        {
            status.Text = T("Working");
            if (runtime is not null) await runtime.Ready;
            await action();
            if (Current() && status.Text == T("Working")) status.Text = T("Ready");
        }
        catch (OperationCanceledException) { if (Current()) status.Text = T("Cancelled"); }
        catch (Exception ex) { if (Current()) status.Text = T("Failed") + ": " + ex.Message; }
    }

    private async Task LoadListAsync()
    {
        if (runtime is null) return;
        listCancellation?.Cancel(); listCancellation?.Dispose();
        listCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = listCancellation.Token;
        int generation = ++listGeneration;
        listLoading = true;
        UpdatePaging();
        string? game = main.SubscriptionGameDirectory;
        pageNotice.IsVisible = false;
        try
        {
            RemoteMod[] items;
            if (view.SelectedIndex == 0)
            {
                string query = appliedKeyword;
                int page = browsePage;
                var order = (ModBrowseSort)Math.Max(0, sort.SelectedIndex);
                var currentSource = SourceForProvider(SelectedProvider);
                listNotice.Text = T("Working");
                var cached = await Task.Run(() => currentSource.BrowseAsync(query, page, order, ModReadMode.CacheOnly, token), token);
                if (generation != listGeneration || disposed) return;
                if (cached.Content is { } old) { RenderModList(old.Items); listNotice.Text = T("CachedLoading"); }
                var result = await Task.Run(() => currentSource.BrowseAsync(query, page, order, cancellationToken: token), token);
                token.ThrowIfCancellationRequested();
                if (generation != listGeneration || disposed) return;
                string resultStatus = ReadStatus(result, LoadListAsync);
                status.Text = BriefStatus(result);
                listNotice.Text = result.IsSuccess && !result.IsStale ? (result.Content!.Items.Length == 0 ? T("Empty") : "") : resultStatus;
                if (result.Content is { } content) { browseItems = content.Items; hasNext = content.NextPage is not null; }
                else if (cached.Content is { } fallback) { browseItems = fallback.Items; hasNext = fallback.NextPage is not null; }
                else { browseItems = []; hasNext = false; }
                items = browseItems;
            }
            else
            {
                items = LibraryItems();
                status.Text = game is null && view.SelectedIndex != 2 ? T("SelectGame") : T("Ready");
                listNotice.Text = items.Length == 0 ? T("Empty") : "";
            }
            UpdatePaging(); RenderModList(items);
            await RefreshRecoveryAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
        var rows = items.Select(mod => new ModRow(mod, NoticeCount(mod), Subscription(mod) is not null)).ToArray();
        mods.ItemsSource = rows;
        mods.SelectedItem = rows.SingleOrDefault(row => row.Mod.Id == selected?.Id);
        rebuilding = false;
    }

    private sealed record ModRow(RemoteMod Mod, int Count, bool Subscribed)
    {
        public override string ToString() => Mod.Title + (Subscribed ? "\n" + T("Subscribed") : "") +
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

    private static string BriefStatus<TContent>(ModSourceResult<TContent> result) =>
        T(result.IsSuccess && !result.IsStale ? "Ready" : result.Content is null ? "RequestFailed" : "CachedStatus");

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
        if (main.SubscriptionGameDirectory is not { } game) { status.Text = T("SelectGame"); return; }
        await runtime!.EnsureRecoveredAsync(game, lifetime.Token);
        var results = await Task.Run(() => runtime.Updates.CheckGameAsync(game, cancellationToken: lifetime.Token), lifetime.Token);
        if (results.Any(result => result.Failure?.Kind == ModSourceError.VerificationRequired))
        { verificationRetryAction = CheckAsync; RefreshVerification(); }
        runtime.ReportCheckResults(results, game);
        await runtime.RefreshLibraryAsync(lifetime.Token);
        if (!string.Equals(game, main.SubscriptionGameDirectory, StringComparison.OrdinalIgnoreCase)) return;
        runtime.NotifyChanged(); RefreshTracking();
        if (view.SelectedIndex != 0) await LoadListAsync();
        status.Text = results.Any(result => !result.Succeeded) ? T("CheckFailed") : T("Checked");
    }

    private void RefreshTracking()
    {
        if (runtime is null || !runtime.Ready.IsCompletedSuccessfully) return;
        favorite.IsEnabled = selected is not null;
        favorite.Content = selected is { } mod && runtime.Library.Favorites.Any(f => f.Source == mod.Id) ? T("Unfavorite") : T("Favorite");
        var sub = Subscription();
        subscribe.IsVisible = selected is not null && sub is null;
        unsubscribe.IsVisible = sub is not null;
        unsubscribe.Content = T(sub?.State is SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed ? "ContinueCleanup" : "Unsubscribe");
        tracking.Text = selected is null ? "" : sub is null ? T("NotSubscribed") : T(sub.State == SubscriptionState.Active ? "Subscribed" : "CleanupIncomplete") + " · " +
            T("LastCheck") + ": " + (sub.Files.LastSuccessfulCheck?.ToLocalTime().ToString("g") ?? T("Never")) + " · " + T("Updates") + $": {UpdateCount(sub)}";
        if (runtime.Error is { } error) { pageNotice.Text = T("Failed") + ": " + error; pageNotice.IsVisible = true; }
    }

    private static void OpenUrl(Uri url)
    {
        if (url.Scheme is "https" or "http") Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); lifetime.Cancel(); detailCancellation?.Cancel(); verificationCancellation?.Cancel();
        LocalizedStrings.Instance.PropertyChanged -= LanguageChanged;
        if (runtime is not null)
        {
            runtime.Changed -= RuntimeChanged;
            runtime.Verification.Changed -= RuntimeChanged;
        }
        DisposeMedia(); imageClient.Dispose();
    }
}
