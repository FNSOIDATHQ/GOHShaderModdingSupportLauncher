using Avalonia;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private bool cacheResetAfterOperation;
    private sealed record CacheReturn(string Scope, ReadingSession? Navigation, bool ListOnly);
    private CacheReturn? cacheReturn;
    private Task? cacheReloadTask;

    private static RemoteMod NavigationMod(RemoteMod mod) => new()
    { Id = mod.Id, PageUrl = mod.PageUrl, Title = mod.Title, Capabilities = mod.Capabilities, Kind = mod.Kind };

    private CacheReturn RememberCacheNavigation()
    {
        if (cacheReturn is { } pending && pending.Scope == ListKey) return pending;
        SaveReadingPosition();
        ReadingSession? navigation = null;
        if (session is { } previous)
        {
            navigation = new(NavigationMod(previous.Mod))
            {
                Article = previous.Article, File = previous.File, Tab = previous.Tab,
                ManualRepository = previous.ManualRepository, FileNavigationOnly = previous.FileNavigationOnly,
                NavigationPosition = previous.NavigationPosition, FilePosition = previous.FilePosition,
                FileNavigationPosition = previous.FileNavigationPosition
            };
            foreach (var pair in previous.Positions) navigation.Positions.Add(pair.Key, pair.Value);
            foreach (var pair in previous.FilePositions) navigation.FilePositions.Add(pair.Key, pair.Value);
            foreach (var pair in previous.GalleryPositions) navigation.GalleryPositions.Add(pair.Key, pair.Value);
            foreach (var pair in previous.GallerySelections) navigation.GallerySelections.Add(pair.Key, pair.Value);
            navigation.Chosen.UnionWith(previous.Chosen);
        }
        return new(ListKey, navigation, listOnly);
    }

    private Task ResumeAfterCacheClearAsync()
    {
        if (disposed || cacheReturn is null || operationBusy || operation.IsVisible) return Task.CompletedTask;
        if (cacheReloadTask is { IsCompleted: false }) return cacheReloadTask;
        var expected = cacheReturn;
        var reloading = ExecuteAsync(async () =>
        {
            await runtime!.CacheMaintenance.WaitForClearAsync(lifetime.Token);
            if (disposed || cacheReturn != expected) return;
            cacheReturn = null;
            if (ListKey != expected.Scope) return;
            var loading = LoadListAsync();
            int generation = listGeneration;
            await loading;
            if (disposed || ListKey != expected.Scope || generation != listGeneration || selected is not null) return;
            if (expected.Navigation is not { } navigation) return;
            string? file = navigation.File;
            var chosen = navigation.Chosen.ToArray();
            var gallerySelections = navigation.GallerySelections.ToArray();
            string readingKey = navigation.Article?.AbsoluteUri ?? "overview";
            var readingPosition = navigation.Positions.GetValueOrDefault(readingKey);
            var filePosition = navigation.FilePositions.GetValueOrDefault(file ?? "");
            var navigationPosition = navigation.NavigationPosition;
            var fileNavigationPosition = navigation.FileNavigationPosition;
            double galleryPosition = navigation.GalleryPositions.GetValueOrDefault(readingKey);
            sessions[((main.SubscriptionGameDirectory ?? "").ToUpperInvariant(), navigation.Mod.Id)] = navigation;
            await OpenModCoreAsync(navigation.Mod, refresh: true);
            if (!IsCurrent(navigation, DetailToken) || ListKey != expected.Scope) return;
            fileChoices.Clear(); navigation.Chosen.Clear(); navigation.Chosen.UnionWith(chosen);
            navigation.File = file;
            if (navigation.Tab == 1) RenderFileState();
            foreach (var pair in gallerySelections) navigation.GallerySelections[pair.Key] = pair.Value;
            if (navigation.Tab == 0) { RenderReading(); UpdateGallerySelection(); }
            listOnly = expected.ListOnly;
            RenderModList(view.SelectedIndex == 0 ? browseItems : LibraryItems());
            UpdateResponsiveLayout();
            RestorePosition(readingScroll, readingPosition); RestorePosition(fileScroll, filePosition);
            RestorePosition(articleScroll, navigationPosition); RestorePosition(fileNavigationScroll, fileNavigationPosition);
            if (galleryScroll is not null) RestorePosition(galleryScroll, new Vector(galleryPosition, 0));
        });
        cacheReloadTask = reloading;
        _ = reloading.ContinueWith(_ => ResumeVisibleCacheReading(), TaskScheduler.Default);
        return reloading;
    }

    private void ResumeVisibleCacheReading() => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed && Avalonia.Controls.TopLevel.GetTopLevel(this) is not null) _ = ResumeAfterCacheClearAsync();
    });

    private void CachesClearing()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Invoke(CachesClearing); return; }
        if (disposed) return;
        var returning = RememberCacheNavigation();
        CancelRefresh(); CancelLinkNavigation(clearHistory: true); CancelTranslation();
        documentTranslator?.ClearCache();
        (textTranslator as IModTranslationCache)?.ClearCache();
        listCancellation?.Cancel(); readerCancellation?.Cancel();
        listGeneration++; readerGeneration++; fileGeneration++;
        listLoading = listFresh = false; listTask = null; loadingListKey = null;
        browseItems = browseItems.Select(NavigationMod).ToArray(); hasNext = false;
        var readingStates = sessions.Values.ToHashSet();
        if (session is { } currentSession) readingStates.Add(currentSession);
        foreach (var current in readingStates)
        {
            Invalidate(current.Overview); Invalidate(current.Files);
            foreach (var state in current.ArticlePages.Values) Invalidate(state);
            foreach (var state in current.Bodies.Values) Invalidate(state);
            foreach (var state in current.ImagePages.Values) Invalidate(state);
            current.ArticlePages.Clear(); current.Bodies.Clear(); current.ImagePages.Clear();
            current.Positions.Clear(); current.FilePositions.Clear(); current.GalleryPositions.Clear();
            current.GallerySelections.Clear(); current.ImageSizes.Clear(); current.Chosen.Clear();
            current.DescriptionImage = current.FileImage = null; current.Context = null;
            current.Mod = current.Mod with { Summary = "", InstallationHint = "", Document = null, Images = [], Articles = [] };
        }
        sessions.Clear();
        if (session is { } owner) selected = owner.Mod;
        documentAnchors.Clear(); galleryTiles.Clear(); visibleMediaRefresh.Clear();
        pendingImage = null; expandedMediaRefresh = null;
        renderedBody = null; renderedDocument = null; renderedSession = renderedFileSession = null;
        renderedArticle = null; renderedFileKey = null; documentContext = null;
        imageExpanded = fileImageExpanded = false; snapshot = null; snapshotFresh = false;
        fileChoices.Clear(); fileEntries = [];
        reading.Children.Clear(); summary.Children.Clear(); articles.Children.Clear(); images.Children.Clear();
        files.Children.Clear(); fileReader.Children.Clear(); fileNotice.Children.Clear();
        if (galleryScroll is not null) galleryScroll.Content = null;
        galleryScroll = null; galleryHost = null; galleryMessages = null;
        // A running install/removal and its reviewed confirmation own their token and
        // snapshot. Keep that shell until it closes; all reading data above is gone.
        if (operation.IsVisible || operationBusy)
        {
            cacheResetAfterOperation = true;
            operationContext = null;
            SetReadingContext(null);
        }
        else ClearDetail();
        cacheReturn = returning;
        RenderModList(view.SelectedIndex == 0 ? browseItems : LibraryItems());
        ResetStatus(); UpdatePaging(); UpdateOriginal();
    }

    private static void Invalidate<T>(ContentState<T> state) where T : class
    {
        state.Generation++; state.Result = null; state.Loading = state.Attempted = false;
        state.Completion?.TrySetResult(); state.Completion = null;
    }
}
