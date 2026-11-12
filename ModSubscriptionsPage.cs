using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
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
    private RemoteMod[] browseItems = [];
    private RemoteMod? selected;
    private ModFileSnapshot? snapshot;
    private readonly Dictionary<RemoteModFile, CheckBox> fileChoices = [];
    private ComboBox view = null!, sort = null!, source = null!;
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
            finally { button.IsEnabled = true; if (!disposed) RefreshVerification(); }
        };
        return button;
    }
    private static ScrollViewer Scroll(Control content) => new()
    { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    private void BuildView()
    {
        rebuilding = true;
        int sourceIndex = source?.SelectedIndex ?? 0;
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new("Auto,Auto,Auto,Auto,*") };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        view = new ComboBox { Name = "subscriptionView", ItemsSource = new[] { T("Browse"), T("Subscribed"), T("Favorites"), T("Updates") }, SelectedIndex = 0, MinWidth = 135 };
        source = new ComboBox { Name = "subscriptionSource", ItemsSource = new[] { "ModDB", "GitHub" }, SelectedIndex = sourceIndex, MinWidth = 90 };
        keyword = new TextBox { Name = "subscriptionSearch", PlaceholderText = T("Search"), MaxLength = 50, Width = 170 };
        sort = new ComboBox { ItemsSource = new[] { T("Updated"), T("Newest"), T("Popular"), T("Name") }, SelectedIndex = 0 };
        UpdateSortOptions();
        toolbar.Children.Add(view); toolbar.Children.Add(source); toolbar.Children.Add(keyword); toolbar.Children.Add(sort);
        toolbar.Children.Add(Action(T("Search"), async () => { browsePage = 1; await LoadListAsync(); }));
        previous = Action(T("Previous"), async () => { browsePage = Math.Max(1, browsePage - 1); await LoadListAsync(); });
        next = Action(T("Next"), async () => { browsePage++; await LoadListAsync(); });
        toolbar.Children.Add(previous); toolbar.Children.Add(next);
        toolbar.Children.Add(Action(T("Check"), CheckAsync));
        root.Children.Add(toolbar);
        var github = BuildGitHubOptions(); Grid.SetRow(github, 1); root.Children.Add(github);
        status = Text(T("Ready")); status.Name = "subscriptionStatus";
        status.Margin = new Thickness(0, 8); Grid.SetRow(status, 2); root.Children.Add(status);
        var verification = Column();
        verification.Children.Add(Text(T("VerificationTitle"), 16));
        verificationMessage = Text(T("VerificationRequired"));
        verification.Children.Add(verificationMessage);
        verificationPrivacy = Text(T("VerificationPrivacy"));
        verification.Children.Add(verificationPrivacy);
        var verificationActions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        verificationOpen = Action(T("VerificationOpen"), OpenVerificationAsync, independent: true);
        verificationOpen.Name = "subscriptionVerificationOpen";
        verificationRetry = Action(T("VerificationRetry"), CompleteVerificationAsync, independent: true);
        verificationRetry.Name = "subscriptionVerificationRetry";
        verificationCancel = Action(T("VerificationCancel"), CloseVerificationAsync, independent: true);
        verificationCancel.Name = "subscriptionVerificationCancel";
        verificationActions.Children.Add(verificationOpen); verificationActions.Children.Add(verificationRetry); verificationActions.Children.Add(verificationCancel);
        verificationActions.Children.Add(Action(T("VerificationOriginal"), () =>
        {
            OpenUrl(CurrentVerificationOrigin ?? new Uri("https://www.moddb.com/games/gates-of-hell/mods"));
            return Task.CompletedTask;
        }, independent: true));
        verification.Children.Add(verificationActions);
        verificationBanner = new Border { Name = "subscriptionVerification", IsVisible = false, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.DarkOrange, Child = verification };
        Grid.SetRow(verificationBanner, 3); root.Children.Add(verificationBanner);
        var body = new Grid { ColumnDefinitions = new("220,10,*") }; Grid.SetRow(body, 4);
        mods = new ListBox { Name = "subscriptionMods" };
        mods.SelectionChanged += async (_, _) =>
        {
            if (!rebuilding && mods.SelectedItem is ModRow item) await OpenModAsync(item.Mod);
        };
        body.Children.Add(mods);
        var detail = new Grid { RowDefinitions = new("Auto,Auto,*,180") }; Grid.SetColumn(detail, 2);
        var heading = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        title = Text(T("ChooseMod"), 20); title.MaxWidth = 420; heading.Children.Add(title);
        favorite = Action(T("Favorite"), ToggleFavoriteAsync); heading.Children.Add(favorite);
        heading.Children.Add(Action(T("Original"), () => { if (selected is { } mod) OpenUrl(mod.PageUrl); return Task.CompletedTask; }));
        heading.Children.Add(Action(T("Unsubscribe"), UnsubscribeAsync));
        heading.Children.Add(Action(T("Recover"), async () =>
        {
            if (main.SubscriptionGameDirectory is not { } game) { status.Text = T("SelectGame"); return; }
            await runtime!.RecoverAgainAsync(game, lifetime.Token);
            runtime.NotifyChanged(); RefreshTasks(); RefreshTracking();
        }));
        detail.Children.Add(heading);
        tracking = Text(""); Grid.SetRow(tracking, 1); detail.Children.Add(tracking);
        summary = Column(); articles = Column(); images = Column(); files = Column(); tasks = Column();
        var tabs = new TabControl { Name = "subscriptionDetails", ItemsSource = new[]
        {
            new TabItem { Header = T("Summary"), Content = Scroll(summary) },
            new TabItem { Header = T("Articles"), Content = Scroll(articles) },
            new TabItem { Header = T("Images"), Content = Scroll(images) },
            new TabItem { Header = T("Files"), Content = Scroll(files) }
        } };
        Grid.SetRow(tabs, 2); detail.Children.Add(tabs);
        var taskScroll = Scroll(tasks); Grid.SetRow(taskScroll, 3); detail.Children.Add(taskScroll);
        body.Children.Add(detail); root.Children.Add(body); Content = root;
        view.SelectionChanged += (_, _) =>
        {
            if (rebuilding || !active) return;
            listGeneration++;
            if (view.SelectedIndex != 0 && runtime?.Ready.IsCompletedSuccessfully == true)
            {
                RenderLibraryList(); previous.IsEnabled = false; next.IsEnabled = false;
                status.Text = T("Ready");
            }
            _ = RefreshAfterGameChangeAsync();
        };
        source.SelectionChanged += (_, _) =>
        {
            github.IsVisible = source.SelectedIndex == 1;
            UpdateSortOptions();
            if (rebuilding || !active) return;
            browsePage = 1; browseItems = []; hasNext = false;
            OnGameChanged();
        };
        rebuilding = false;
    }

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
        detailCancellation?.Cancel();
        verificationRetryAction = null;
        listGeneration++;
        selected = null; snapshot = null;
        title.Text = T("ChooseMod");
        summary.Children.Clear(); articles.Children.Clear(); images.Children.Clear(); files.Children.Clear(); tasks.Children.Clear(); tracking.Text = "";
        if (active) _ = RefreshAfterGameChangeAsync();
    }

    private async Task RefreshAfterGameChangeAsync()
    {
        bool acquired = false;
        try { await actionGate.WaitAsync(lifetime.Token); acquired = true; await ExecuteAsync(LoadListAsync); }
        catch (OperationCanceledException) { }
        finally { if (acquired) actionGate.Release(); }
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(LocalizedStrings.Tab_ModSubscriptions)) return;
        detailCancellation?.Cancel(); selected = null; snapshot = null;
        verificationRetryAction = null;
        verificationNotice = null;
        BuildView();
        if (runtime?.Ready.IsCompletedSuccessfully == true) LoadGitHubOptions();
        RefreshVerification();
        if (active) _ = RefreshAfterGameChangeAsync();
    }

    private void RuntimeChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed)
        {
            RefreshTasks(); RefreshTracking();
            RefreshVerification();
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
        try
        {
            status.Text = T("Working");
            if (runtime is not null) await runtime.Ready;
            await action();
            if (!disposed && status.Text == T("Working")) status.Text = T("Ready");
        }
        catch (OperationCanceledException) { if (!disposed) status.Text = T("Cancelled"); }
        catch (Exception ex) { if (!disposed) status.Text = T("Failed") + ": " + ex.Message; }
    }

    private async Task LoadListAsync()
    {
        if (runtime is null) return;
        int generation = ++listGeneration;
        string? game = main.SubscriptionGameDirectory;
        RemoteMod[] items;
        if (view.SelectedIndex == 0)
        {
            string query = keyword.Text ?? "";
            int page = browsePage;
            var order = (ModBrowseSort)Math.Max(0, sort.SelectedIndex);
            var currentSource = SourceForProvider(SelectedProvider);
            var result = await Task.Run(() => currentSource.BrowseAsync(query, page, order, cancellationToken: lifetime.Token), lifetime.Token);
            if (generation != listGeneration || disposed) return;
            status.Text = ReadStatus(result, LoadListAsync);
            browseItems = result.Content?.Items ?? [];
            hasNext = result.Content?.NextPage is not null;
            items = browseItems;
        }
        else
        {
            items = LibraryItems();
            status.Text = game is null && view.SelectedIndex != 2 ? T("SelectGame") : T("Ready");
        }
        previous.IsEnabled = view.SelectedIndex == 0 && browsePage > 1;
        next.IsEnabled = view.SelectedIndex == 0 && hasNext;
        RenderModList(items);
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
            3 => subscribed.Any(sub => sub.Source == mod.Id && ModUpdateCheckService.GetNotices(sub).Length > 0),
            _ => false
        })).Where(mod => mod.Title.Contains(keyword.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void RenderLibraryList() => RenderModList(LibraryItems());
    private void RenderModList(RemoteMod[] items)
    {
        rebuilding = true;
        var rows = items.Select(mod => new ModRow(mod, NoticeCount(mod))).ToArray();
        mods.ItemsSource = rows;
        mods.SelectedItem = rows.SingleOrDefault(row => row.Mod.Id == selected?.Id);
        rebuilding = false;
    }

    private sealed record ModRow(RemoteMod Mod, int Count)
    {
        public override string ToString() => Mod.Title + "\n" + ProviderName(Mod.Id.Provider) +
            (Mod.Id.Provider == "github" ? " · " + T("Kind_" + Mod.Kind) : "") +
            (Count > 0 ? " · " + T("Updates") + $" ({Count})" : "");
    }
    private int NoticeCount(RemoteMod mod) => Subscription(mod) is { } sub ? ModUpdateCheckService.GetNotices(sub).Length : 0;
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
            verificationRetryAction = retry;
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

    private Task OpenModAsync(RemoteMod mod) => OpenModCoreAsync(mod, refresh: true);

    private async Task OpenModCoreAsync(RemoteMod mod, bool refresh)
    {
        if (selected?.Id != mod.Id) verificationRetryAction = null;
        detailCancellation?.Cancel(); detailCancellation?.Dispose();
        detailCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = detailCancellation.Token;
        selected = mod; snapshot = null; fileChoices.Clear();
        title.Text = mod.Title + " · " + ProviderName(mod.Id.Provider);
        summary.Children.Clear(); articles.Children.Clear(); images.Children.Clear(); files.Children.Clear();
        ShowSummary(mod); RefreshTasks(); RefreshTracking();
        try
        {
            var currentSource = SourceForProvider(mod.Id.Provider);
            var result = refresh
                ? await Task.Run(() => currentSource is GitHubModSource github
                    ? github.GetModByIdAsync(mod.Id.RemoteId, cancellationToken: token)
                    : currentSource.GetModAsync(mod.PageUrl, cancellationToken: token), token)
                : new ModSourceResult<RemoteMod>(mod, DateTimeOffset.UtcNow);
            token.ThrowIfCancellationRequested(); status.Text = ReadStatus(result, () => OpenModAsync(mod));
            if (result.Content is { } full)
            {
                if (full.Id != mod.Id) throw new InvalidDataException(T("IdentityChanged"));
                selected = full; mod = full;
                title.Text = full.Title + " · " + ProviderName(full.Id.Provider);
                await Task.Run(() => runtime!.Store.SaveMod(full), token);
                await runtime!.RefreshLibraryAsync(token);
                token.ThrowIfCancellationRequested();
                summary.Children.Clear(); ShowSummary(full);
                foreach (var image in full.Images) summary.Children.Add(ImageButton(image));
            }
            articles.Children.Add(Action(T("LoadArticles"), () => LoadArticlesAsync(mod.PageUrl, token)));
            images.Children.Add(Action(T("LoadImages"), () => LoadImagesAsync(mod.PageUrl, token)));
            files.Children.Add(Action(T("LoadFiles"), () => LoadFilesAsync(mod, token)));
            RefreshTracking();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) status.Text = T("Failed") + ": " + ex.Message; }
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
        favorite.Content = selected is { } mod && runtime.Library.Favorites.Any(f => f.Source == mod.Id) ? T("Unfavorite") : T("Favorite");
        var sub = Subscription();
        tracking.Text = sub is null ? "" : T("LastCheck") + ": " + (sub.Files.LastSuccessfulCheck?.ToLocalTime().ToString("g") ?? T("Never")) +
            " · " + T("Updates") + $": {ModUpdateCheckService.GetNotices(sub).Length}" +
            (sub.State != SubscriptionState.Active ? " · " + T("RemovalPending") : "");
        if (runtime.Error is { } error) status.Text = T("Failed") + ": " + error;
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
        imageClient.Dispose();
    }
}
