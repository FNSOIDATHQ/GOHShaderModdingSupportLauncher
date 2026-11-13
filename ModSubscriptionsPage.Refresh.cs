using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private Task? refreshTask;
    private string? refreshingKey;
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;
    private bool refreshingNavigation;

    private Func<Task> DocumentRetry(ReadingSession expected, int tab, Uri? article, string? file, Action? prepare = null) => async () =>
    {
        if (!IsCurrent(expected, DetailToken)) return;
        if (!operationBusy && !operation.IsVisible)
        {
            details.SelectedIndex = tab;
            if (tab == 0 && expected.Article != article) await SelectArticleAsync(article);
            if (tab == 1 && file is not null && expected.File != file) SelectFile(file);
            prepare?.Invoke();
        }
        await ApplySearchAsync();
    };

    private Func<Task> ContentRetry<TContent>(ReadingSession expected, ContentState<TContent> state) where TContent : class =>
        DocumentRetry(expected, ReferenceEquals(state, expected.Files) ? 1 : 0,
            state.Label == "Articles" && typeof(TContent) == typeof(ModArticle) ? state.Address : null, expected.File);

    private Func<Task> MediaRetry() => session is { } current ? DocumentRetry(current, details.SelectedIndex, current.Article, current.File) : ApplySearchAsync;

    private void CancelRefresh()
    {
        refreshGeneration++;
        if (refreshTask is { IsCompleted: false }) listCancellation?.Cancel();
        refreshCancellation?.Cancel(); refreshCancellation?.Dispose(); refreshCancellation = null;
        refreshingNavigation = false;
    }

    private Task ApplySearchAsync()
    {
        if (disposed || runtime is null) return Task.CompletedTask;
        if (operationBusy || operation.IsVisible)
        {
            ReportNotice(T("RefreshAfterOperation"));
            return Task.CompletedTask;
        }
        string query = keyword.Text?.Trim() ?? "";
        bool changed = query != appliedKeyword;
        if (changed) browsePage = 1;
        string key = string.Join("\n", ListKey, query, selected?.Id, details.SelectedIndex, session?.Article, session?.File);
        if (!changed && refreshTask is { IsCompleted: false } && refreshingKey == key && refreshCancellation?.IsCancellationRequested == false)
            return refreshTask;
        CancelRefresh();
        if (!changed) PrepareTranslationRetry();
        if (changed)
        {
            CancelTranslation();
            // Search supersedes reads that were started before the new query.
            detailCancellation?.Cancel(); detailCancellation?.Dispose(); readerCancellation?.Cancel();
            detailCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        appliedKeyword = query;
        pageStatus.Remove("action");
        pageStatus.Remove("fileChoices");
        refreshingKey = string.Join("\n", ListKey, query, selected?.Id, details.SelectedIndex, session?.Article, session?.File);
        refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
        return refreshTask = RefreshPageAsync(refreshGeneration, session, refreshCancellation.Token);
    }

    private async Task RefreshPageAsync(int generation, ReadingSession? expected, CancellationToken token)
    {
        SaveReadingPosition();
        var pending = new HashSet<object>();
        void Track<TContent>(ContentState<TContent> state) where TContent : class
        { if (state.Loading && !state.Token.IsCancellationRequested) pending.Add(state); }
        if (expected is not null)
        {
            Track(expected.Overview); Track(expected.Files);
            foreach (var state in expected.ArticlePages.Values) Track(state);
            foreach (var state in expected.ImagePages.Values) Track(state);
            foreach (var state in expected.Bodies.Values) Track(state);
        }
        try
        {
            await LoadListAsync().WaitAsync(token);
            if (token.IsCancellationRequested || generation != refreshGeneration || disposed || session != expected) return;
            if (expected is null) return;
            bool listed = view.SelectedIndex == 0 ? browseItems.Any(item => item.Id == expected.Mod.Id)
                : LibraryItems().Any(item => item.Id == expected.Mod.Id);
            if (listFresh && !listed && !expected.ManualRepository)
            {
                ClearDetail();
                ResetStatus();
                ReportNotice(T("SelectionOutsideResults"));
                return;
            }
            if (details.SelectedIndex == 1)
            {
                var adapter = SourceForProvider(expected.Mod.Id.Provider);
                await ReadContentAsync(expected, expected.Files, (mode, ct) => adapter.GetFileSnapshotAsync(expected.Mod.PageUrl, mode, ct),
                    RenderFileState, token, force: !pending.Contains(expected.Files));
            }
            else
            {
                var adapter = SourceForProvider(expected.Mod.Id.Provider);
                var work = new List<Task> { EnsureOverviewAsync(expected, token, force: !pending.Contains(expected.Overview)) };
                refreshingNavigation = true;
                if (adapter.Capabilities.CanReadArticles)
                {
                    work.Add(RefreshNavigationAsync(expected, expected.ArticlePages, adapter.GetArticlesAsync, RenderDescriptionNavigation, pending, token));
                    if (expected.Article is { } article)
                    {
                        if (!expected.Bodies.TryGetValue(article, out var state)) expected.Bodies.Add(article, state = new() { Label = "Articles", Address = article });
                        work.Add(ReadContentAsync(expected, state, (mode, ct) => adapter.GetArticleAsync(article, mode, ct),
                            () => { if (expected.Article == article) RenderReading(); }, token, force: !pending.Contains(state)));
                    }
                }
                if (expected.Mod.Id.Provider == "moddb")
                    work.Add(RefreshNavigationAsync(expected, expected.ImagePages, adapter.GetImagesAsync, RenderReading, pending, token));
                await Task.WhenAll(work);
            }
            if (IsCurrent(expected, token) && generation == refreshGeneration)
            {
                await RefreshVisibleMediaAsync();
                await RefreshRecoveryAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (generation == refreshGeneration) refreshingNavigation = false; }
    }

    private async Task RefreshNavigationAsync<TItem>(ReadingSession expected, Dictionary<Uri, ContentState<ModSourcePage<TItem>>> pages,
        Func<Uri, ModReadMode, CancellationToken, Task<ModSourceResult<ModSourcePage<TItem>>>> fetch, Action render, HashSet<object> pending, CancellationToken token)
    {
        // Only the number of navigation pages already opened is refreshed. Following the
        // fresh chain also removes pages made unreachable by a new end or changed link.
        int limit = Math.Max(1, pages.Count);
        var visited = new HashSet<Uri>();
        Uri? page = expected.Mod.PageUrl;
        bool complete = true;
        for (int i = 0; page is not null && i < limit; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(page)) { ReportNotice(T("PaginationLimit"), failure: true, key: "pagination"); complete = false; break; }
            if (!pages.TryGetValue(page, out var state)) pages.Add(page, state = new() { Label = typeof(TItem) == typeof(ModArticle) ? "Articles" : "Images", Address = page });
            Uri address = page;
            await ReadContentAsync(expected, state, (mode, ct) => fetch(address, mode, ct), render, token, force: !pending.Contains(state));
            if (!IsCurrent(expected, token)) return;
            if (state.Result is not { IsSuccess: true, IsStale: false, Content: { } content }) { complete = false; break; }
            page = content.NextPage;
        }
        if (complete && IsCurrent(expected, token))
        {
            foreach (var old in pages.Keys.Where(key => !visited.Contains(key)).ToArray())
            { pageStatus.Remove(pages[old].StatusKey); pages.Remove(old); }
            RenderStatus();
            render();
        }
    }
}
