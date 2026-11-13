using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private sealed class ContentState<T> where T : class
    {
        internal ModSourceResult<T>? Result;
        internal bool Loading, Attempted;
        internal CancellationToken Token;
        internal int Generation;
        internal TaskCompletionSource? Completion;
    }

    private sealed class ReadingSession(RemoteMod mod)
    {
        internal RemoteMod Mod = mod;
        internal readonly ContentState<RemoteMod> Overview = new();
        internal readonly Dictionary<Uri, ContentState<ModSourcePage<ModArticle>>> ArticlePages = [];
        internal readonly Dictionary<Uri, ContentState<ModArticle>> Bodies = [];
        internal readonly Dictionary<Uri, ContentState<ModSourcePage<ModImageReference>>> ImagePages = [];
        internal readonly ContentState<ModFileSnapshot> Files = new();
        internal readonly Dictionary<string, Vector> Positions = [];
        internal readonly Dictionary<string, double> GalleryPositions = [];
        internal readonly HashSet<(string, string)> Chosen = [];
        internal Uri? Article;
        internal int Tab;
        internal Vector NavigationPosition, FilePosition;
    }

    private readonly Dictionary<(string Game, ModSourceId Mod), ReadingSession> sessions = [];
    private ReadingSession? session;
    private CancellationTokenSource? readerCancellation;
    private int readerGeneration;
    private bool restoringPosition;
    private bool imageExpanded;
    private ReadingSession? renderedSession;
    private Uri? renderedArticle;
    private object? renderedBody;
    private ModReadingDocument? renderedDocument;
    private TextBlock? readerNotice;
    private Button? readerRetry;

    private bool IsCurrent(ReadingSession expected, CancellationToken token) =>
        !disposed && !token.IsCancellationRequested && ReferenceEquals(session, expected) && selected?.Id == expected.Mod.Id;

    private CancellationToken DetailToken => detailCancellation?.Token ?? lifetime.Token;

    private void SaveReadingPosition()
    {
        if (session is null) return;
        if (!imageExpanded) session.Positions[session.Article?.AbsoluteUri ?? "overview"] = readingScroll.Offset;
        if (!imageExpanded && renderedSession == session && galleryScroll is not null) session.GalleryPositions[ReadingKey] = galleryScroll.Offset.X;
        session.NavigationPosition = articleScroll.Offset; session.FilePosition = fileScroll.Offset;
        session.Chosen.Clear();
        foreach (var item in fileChoices.Where(item => item.Value.IsChecked == true)) session.Chosen.Add((item.Key.Id, item.Key.Revision));
    }

    private void RestorePosition(ScrollViewer scroll, Vector position)
    {
        var expected = session;
        int generation = readerGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed || session != expected || (scroll == readingScroll || scroll == galleryScroll) && generation != readerGeneration) return;
            restoringPosition = true;
            scroll.Offset = position;
            restoringPosition = false;
        }, DispatcherPriority.Loaded);
    }

    private Task OpenModAsync(RemoteMod mod) => OpenModCoreAsync(mod, refresh: true);

    private ReadingSession ReadingState(RemoteMod mod)
    {
        var key = ((main.SubscriptionGameDirectory ?? "").ToUpperInvariant(), mod.Id);
        if (sessions.TryGetValue(key, out var current)) return current;
        if (sessions.Count >= 16) sessions.Remove(sessions.Keys.First());
        sessions.Add(key, current = new(mod));
        return current;
    }

    private async Task OpenModCoreAsync(RemoteMod mod, bool refresh)
    {
        SaveReadingPosition();
        detailCancellation?.Cancel(); detailCancellation?.Dispose(); readerCancellation?.Cancel();
        detailCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = detailCancellation.Token;
        verificationRetryAction = null; CloseOperation();
        pageNotice.IsVisible = false;
        selected = mod; snapshot = null; snapshotFresh = false; fileChoices.Clear();
        imageExpanded = false; renderedSession = null;
        var current = ReadingState(mod);
        session = current;
        if (!refresh && current.Overview.Result is null)
        { current.Mod = mod; current.Overview.Result = new(mod, DateTimeOffset.UtcNow); current.Overview.Attempted = true; }
        selected = current.Mod; title.Text = current.Mod.Title;
        listOnly = false; UpdateResponsiveLayout(); RefreshTracking(); RefreshTasks();
        rebuilding = true; details.SelectedIndex = current.Tab; rebuilding = false;
        RenderDescriptionNavigation(); RenderReading(); RenderFileState();
        RestorePosition(articleScroll, current.NavigationPosition); RestorePosition(fileScroll, current.FilePosition);
        try
        {
            await EnsureOverviewAsync(current, token);
            if (!IsCurrent(current, token)) return;
            var work = new List<Task> { LoadArticlesAsync(current.Mod.PageUrl, token) };
            // GitHub and Mow Portal already return all embedded media with the overview.
            if (current.Mod.Id.Provider == "moddb") work.Add(LoadImagesAsync(current.Mod.PageUrl, token));
            if (current.Tab == 1) work.Add(EnsureFilesAsync());
            if (current.Article is { } article) work.Add(SelectArticleAsync(article, savePosition: false));
            await Task.WhenAll(work);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent(current, token)) return;
            pageNotice.Text = T("Failed") + ": " + ex.Message; pageNotice.IsVisible = true; status.Text = T("Failed");
        }
    }

    // A content read owns its state and response. Cancelled or superseded reads cannot update a new selection.
    private async Task ReadContentAsync<TContent>(ReadingSession expected, ContentState<TContent> state,
        Func<ModReadMode, CancellationToken, Task<ModSourceResult<TContent>>> fetch, Action render, CancellationToken token, bool force = false) where TContent : class
    {
        if (state.Loading && !state.Token.IsCancellationRequested)
        {
            try { if (state.Completion is { } pending) await pending.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return;
        }
        if (state.Attempted && !force) return;
        state.Loading = true;
        state.Token = token;
        int generation = ++state.Generation;
        var completion = state.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrent(expected, token)) render();
        try
        {
            if (state.Result?.Content is null)
            {
                var cached = await Task.Run(() => fetch(ModReadMode.CacheOnly, token), token);
                if (!IsCurrent(expected, token)) return;
                if (cached.Content is not null) { state.Result = cached; render(); }
            }
            var result = await Task.Run(() => fetch(ModReadMode.Refresh, token), token);
            if (!IsCurrent(expected, token)) return;
            if (result.Content is null && state.Result?.Content is { } previous)
                result = result with { Content = previous, IsStale = true };
            state.Result = result; state.Attempted = true;
            ReadStatus(result, () => ReadContentAsync(expected, state, fetch, render, DetailToken, force: true));
            status.Text = BriefStatus(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent(expected, token)) return;
            state.Result = new(state.Result is { } old ? old.Content : default, state.Result?.FetchedAt,
                new(ModSourceError.RequestFailed, ex.Message), state.Result?.Content is not null);
            state.Attempted = true;
        }
        finally
        {
            try
            {
                if (state.Generation == generation)
                {
                    state.Loading = false;
                    if (IsCurrent(expected, token)) render();
                }
            }
            finally { completion.TrySetResult(); }
        }
    }

    private string ContentStatus<TContent>(ContentState<TContent> state) where TContent : class
    {
        if (state.Loading) return T(state.Result?.Content is null ? "Working" : "CachedLoading");
        if (state.Result is not { } result) return T("NotLoaded");
        return result.IsSuccess && !result.IsStale ? "" : ReadStatus(result);
    }

    private async Task EnsureOverviewAsync(ReadingSession current, CancellationToken token, bool force = false)
    {
        bool previouslyLoaded = current.Overview.Attempted;
        var adapter = SourceForProvider(current.Mod.Id.Provider);
        await ReadContentAsync(current, current.Overview, async (mode, ct) =>
        {
            var result = adapter is GitHubModSource github
                ? await github.GetModByIdAsync(current.Mod.Id.RemoteId, mode, ct)
                : await adapter.GetModAsync(current.Mod.PageUrl, mode, ct);
            if (result.Content is { } full && full.Id != current.Mod.Id) throw new InvalidDataException(T("IdentityChanged"));
            return result;
        }, () =>
        {
            if (current.Overview.Result?.Content is { } full)
            {
                current.Mod = full; selected = full; title.Text = full.Title;
            }
            if (current.Article is null) RenderReading();
        }, token, force);
        if ((!previouslyLoaded || force) && IsCurrent(current, token) && current.Overview.Result?.Content is { } full)
        {
            await Task.Run(() => runtime!.Store.SaveMod(full), token);
            await runtime!.RefreshLibraryAsync(token);
        }
    }

    private async Task LoadArticlesAsync(Uri page, CancellationToken token)
    {
        if (session is not { } current) return;
        if (current.ArticlePages.Count >= 100 && !current.ArticlePages.ContainsKey(page)) return;
        if (!current.ArticlePages.TryGetValue(page, out var state)) current.ArticlePages.Add(page, state = new());
        var adapter = SourceForProvider(current.Mod.Id.Provider);
        await ReadContentAsync(current, state, (mode, ct) => adapter.GetArticlesAsync(page, mode, ct), RenderDescriptionNavigation, token);
    }

    private void RenderDescriptionNavigation()
    {
        if (session is not { } current) return;
        var position = articleScroll.Offset;
        articles.Children.Clear();
        void AddEntry(string caption, Uri? url)
        {
            var button = Action(caption, () => SelectArticleAsync(url), independent: true);
            button.Content = Text(caption); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (url == current.Article) button.FontWeight = FontWeight.Bold;
            articles.Children.Add(button);
        }
        AddEntry(T("Summary"), null);
        var entries = current.ArticlePages.Values.SelectMany(page => page.Result?.Content?.Items ?? [])
            .Concat(current.Mod.Articles).DistinctBy(item => item.Id).ToArray();
        foreach (var article in entries) AddEntry(article.Title, article.PageUrl);
        foreach (var pair in current.ArticlePages)
        {
            var message = ContentStatus(pair.Value);
            if (message.Length > 0) articles.Children.Add(Text(message, 12));
            if (pair.Value.Attempted && pair.Value.Result?.IsSuccess != true)
                articles.Children.Add(Action(T("RetryContent"), async () => { pair.Value.Attempted = false; await LoadArticlesAsync(pair.Key, DetailToken); }, independent: true));
        }
        var nextPage = current.ArticlePages.Values.LastOrDefault()?.Result?.Content?.NextPage;
        if (nextPage is not null && !current.ArticlePages.ContainsKey(nextPage) && current.ArticlePages.Count < 100)
            articles.Children.Add(PageSentinel(() => LoadArticlesAsync(nextPage, DetailToken)));
        else if (nextPage is not null && current.ArticlePages.Count >= 100) articles.Children.Add(Text(T("PaginationLimit"), 12));
        RestorePosition(articleScroll, position);
    }

    private Control PageSentinel(Func<Task> load)
    {
        var sentinel = Text(T("MoreOnScroll"), 12);
        sentinel.Margin = new Thickness(0, 12);
        bool started = false;
        sentinel.EffectiveViewportChanged += async (_, args) =>
        {
            if (started || restoringPosition || !new Rect(sentinel.Bounds.Size).Intersects(args.EffectiveViewport) || disposed) return;
            started = true;
            await ExecuteAsync(load);
        };
        return sentinel;
    }

    private async Task SelectArticleAsync(Uri? url, bool savePosition = true)
    {
        if (session is not { } current) return;
        if (savePosition) SaveReadingPosition();
        readerCancellation?.Cancel(); readerCancellation?.Dispose();
        readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
        var token = readerCancellation.Token;
        current.Article = url; readerGeneration++;
        imageExpanded = false;
        RenderDescriptionNavigation(); RenderReading();
        if (url is null) return;
        if (!current.Bodies.TryGetValue(url, out var state)) current.Bodies.Add(url, state = new());
        var adapter = SourceForProvider(current.Mod.Id.Provider);
        await ReadContentAsync(current, state, (mode, ct) => adapter.GetArticleAsync(url, mode, ct),
            () => { if (current.Article == url) RenderReading(); }, token);
    }

    private void RenderReading()
    {
        if (session is not { } current) return;
        if (imageExpanded) return;
        var position = current.Positions.GetValueOrDefault(current.Article?.AbsoluteUri ?? "overview");
        if (renderedSession == current && renderedArticle == current.Article) position = readingScroll.Offset;
        current.Bodies.TryGetValue(current.Article ?? current.Mod.PageUrl, out var articleState);
        object? body = current.Article is null ? current.Mod : articleState?.Result?.Content;
        bool rebuild = renderedSession != current || renderedArticle != current.Article || !ReferenceEquals(renderedBody, body);
        if (rebuild)
        {
            if (renderedSession == current && renderedArticle == current.Article && galleryScroll is not null)
                current.GalleryPositions[ReadingKey] = galleryScroll.Offset.X;
            renderedSession = current; renderedArticle = current.Article; renderedBody = body;
            reading.Children.Clear();
            readerNotice = Text("", 12); reading.Children.Add(readerNotice);
            CreateGallery(); readerRetry = null;
        }
        if (current.Article is { } url)
        {
            if (articleState is not { } state) { readerNotice!.Text = T("Working"); return; }
            readerNotice!.Text = ContentStatus(state);
            if (state.Result?.Content is { } article && rebuild)
            {
                renderedDocument = ReadingDocument(article.Document, article.Body, article.PageUrl);
                reading.Children.Add(Text(article.Title, 20)); reading.Children.Add(galleryHost!);
                AddDocument(reading, renderedDocument);
                reading.Children.Add(Action(T("Original"), () => { OpenUrl(article.PageUrl); return Task.CompletedTask; }, independent: true));
            }
            if (state.Result?.Content is { } content && renderedDocument is not null)
                RenderGallery(current, renderedDocument, content.Images, overview: false);
            if (readerRetry is not null) reading.Children.Remove(readerRetry);
            if (state.Attempted && state.Result?.IsSuccess != true)
                reading.Children.Add(readerRetry = Action(T("RetryContent"), async () => { state.Attempted = false; await SelectArticleAsync(url); }, independent: true));
        }
        else
        {
            readerNotice!.Text = ContentStatus(current.Overview);
            if (rebuild)
            {
                summary.Children.Clear();
                renderedDocument = ReadingDocument(current.Mod.Document, current.Mod.Summary, current.Mod.PageUrl);
                ShowSummary(current.Mod);
                if (current.Mod.InstallationHint.Length > 0) AddReadable(summary, current.Mod.InstallationHint);
                summary.Children.Add(Action(T("RefreshContent"), () => EnsureOverviewAsync(current, DetailToken, force: true), independent: true));
                reading.Children.Add(galleryHost!); reading.Children.Add(summary);
            }
            RenderGallery(current, renderedDocument!, current.Mod.Images, overview: true);
        }
        readerNotice!.IsVisible = !string.IsNullOrWhiteSpace(readerNotice.Text);
        if (rebuild)
        {
            RestorePosition(readingScroll, position);
            RestorePosition(galleryScroll!, new Vector(current.GalleryPositions.GetValueOrDefault(ReadingKey), 0));
        }
    }

    private async Task LoadImagesAsync(Uri page, CancellationToken token)
    {
        if (session is not { } current) return;
        if (current.ImagePages.Count >= 100 && !current.ImagePages.ContainsKey(page)) return;
        if (!current.ImagePages.TryGetValue(page, out var state)) current.ImagePages.Add(page, state = new());
        var adapter = SourceForProvider(current.Mod.Id.Provider);
        await ReadContentAsync(current, state, (mode, ct) => adapter.GetImagesAsync(page, mode, ct),
            () => { if (current.Article is null) RenderReading(); }, token);
    }

    private async Task OpenFilesAsync()
    {
        details.SelectedIndex = 1;
        await EnsureFilesAsync();
    }

    private Task EnsureFilesAsync() => selected is { } mod ? LoadFilesAsync(mod, DetailToken) : Task.CompletedTask;

    private void RenderFileState()
    {
        if (session is not { } current) return;
        snapshot = current.Files.Result?.Content;
        snapshotFresh = !current.Files.Loading && current.Files.Result is { IsSuccess: true, IsStale: false };
        RenderFiles(current.Mod, DetailToken);
    }
}
