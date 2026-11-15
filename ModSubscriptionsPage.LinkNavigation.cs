using Avalonia;
using Avalonia.Controls;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private CancellationTokenSource? linkCancellation;
    private int linkGeneration;
    private sealed record LinkLocation(ReadingSession? Session, int Source, int View, int Sort, int Page, string Query,
        string AppliedQuery, RemoteMod[] Items, bool HasNext, bool Fresh, bool ListOnly, int Tab, Uri? Article, string? File,
        ReadingContext? Context, ExpandedImage? Image, Vector Reading, Vector FileReading, Vector Articles, Vector Files, double Gallery, string? GallerySelection, bool FileNavigationOnly);
    private readonly List<LinkLocation> linkHistory = [];
    private sealed class PreparedLink(ModPageRoute route)
    {
        internal ModPageRoute Route = route;
        internal ModSourceResult<RemoteMod>? Overview;
        internal ModSourceResult<ModSourcePage<RemoteMod>>? List;
        internal ModSourceResult<ModArticle>? Article;
        internal ModSourceResult<ModSourcePage<ModArticle>>? Articles;
        internal ModSourceResult<ModSourcePage<ModImageReference>>? Images;
        internal ModSourceResult<ModFileSnapshot>? Files;
        internal ModImage? Image;
        internal string? FileKey;
        internal ReadingContext? Context;
    }

    private void CancelLinkNavigation(bool clearHistory = false)
    {
        linkGeneration++;
        linkCancellation?.Cancel(); linkCancellation = null;
        navigationOriginal = null;
        if (clearHistory) linkHistory.Clear();
        if (linkBack is not null) UpdateOriginal();
    }

    private void RememberLinkLocation()
    {
        SaveReadingPosition();
        if (linkHistory.Count >= 32) linkHistory.RemoveAt(0);
        int readingTab = details.SelectedIndex == 2 ? session?.Tab ?? 0 : details.SelectedIndex;
        linkHistory.Add(new(session, source.SelectedIndex, view.SelectedIndex, sort.SelectedIndex, browsePage, keyword.Text ?? "",
            appliedKeyword, browseItems, hasNext, listFresh, listOnly, readingTab, session?.Article, session?.File,
            session?.Context, readingTab == 0 ? session?.DescriptionImage : session?.FileImage,
            readingScroll.Offset, fileScroll.Offset, articleScroll.Offset, fileNavigationScroll.Offset, galleryScroll?.Offset.X ?? 0,
            session?.GallerySelections.GetValueOrDefault(ReadingKey), session?.FileNavigationOnly ?? false));
        UpdateOriginal();
    }

    private async Task<PreparedLink> PrepareLinkAsync(ModPageRoute route, CancellationToken token)
    {
        var prepared = new PreparedLink(route);
        var adapter = SourceForProvider(route.Provider);
        static T Require<T>(ModSourceResult<T> response) where T : class => response.IsSuccess && !response.IsStale ? response.Content! :
            throw new ModSourceException(response.Failure ?? new(ModSourceError.NotFound, "The linked content is not available."));
        if (route.Kind == ModPageKind.Category)
        {
            if (!adapter.Capabilities.CanBrowse) throw new InvalidOperationException(T("UnsupportedSource"));
            prepared.List = await Task.Run(() => adapter.BrowseAsync(route.Keyword, route.PageNumber, route.Sort, cancellationToken: token), token);
            Require(prepared.List); return prepared;
        }
        var known = sessions.Values.FirstOrDefault(state => state.Mod.Id.Provider == route.Provider && SamePage(state.Mod.PageUrl, route.ModPage!));
        prepared.Overview = known?.Overview.Result is { IsSuccess: true, IsStale: false } overview ? overview :
            await Task.Run(() => adapter.GetModAsync(route.ModPage!, cancellationToken: token), token);
        var mod = Require(prepared.Overview);
        if (mod.Id.Provider != route.Provider || !ModPageRouter.TryResolveSupportedPage(mod.PageUrl, out var final) || final!.Kind != ModPageKind.Overview || final.Provider != route.Provider)
            throw new InvalidDataException(T("IdentityChanged"));
        if (route.Provider == "github" && !SamePage(mod.PageUrl, route.ModPage!))
        {
            // The repository API supplies the canonical URL after a public rename/redirect.
            route = route with { Page = new(mod.PageUrl.AbsoluteUri + route.Page.AbsolutePath[route.ModPage!.AbsolutePath.TrimEnd('/').Length..]), ModPage = mod.PageUrl };
            prepared.Route = route;
        }
        else if (route.Provider != "github" && !SamePage(mod.PageUrl, route.ModPage!)) throw new InvalidDataException(T("IdentityChanged"));
        prepared.Context = new("overview", mod.PageUrl);
        if (route.Kind is ModPageKind.Article or ModPageKind.Articles)
        {
            if (!adapter.Capabilities.CanReadArticles) throw new InvalidOperationException(T("UnsupportedSource"));
            if (route.Kind == ModPageKind.Article)
            {
                prepared.Article = known?.Bodies.GetValueOrDefault(route.Page)?.Result is { IsSuccess: true, IsStale: false } body ? body :
                    await Task.Run(() => adapter.GetArticleAsync(route.Page, cancellationToken: token), token);
                prepared.Context = new("article", Require(prepared.Article).PageUrl);
            }
            else
            {
                prepared.Articles = await Task.Run(() => adapter.GetArticlesAsync(route.Page, cancellationToken: token), token);
                Require(prepared.Articles);
            }
        }
        if (route.Kind is ModPageKind.Files or ModPageKind.File or ModPageKind.Release or ModPageKind.Asset)
        {
            if (!adapter.Capabilities.CanListFiles) throw new InvalidOperationException(T("UnsupportedSource"));
            prepared.Files = known?.Files.Result is { IsSuccess: true, IsStale: false } snapshot ? snapshot :
                await Task.Run(() => adapter.GetFileSnapshotAsync(mod.PageUrl, cancellationToken: token), token);
            var files = Require(prepared.Files);
            RemoteModFile? file = route.Kind == ModPageKind.File ? files.Files.FirstOrDefault(file => file.PageUrl.AbsoluteUri == route.Page.AbsoluteUri) :
                route.Kind == ModPageKind.Asset ? MatchAsset(route.Page, files) : null;
            if (route.Kind is ModPageKind.File or ModPageKind.Asset && file is null)
                throw new ModSourceException(new(ModSourceError.NotFound, "The linked file is no longer listed."));
            if (route.Kind == ModPageKind.Release)
            {
                var group = files.Groups.FirstOrDefault(group => SamePage(group.PageUrl, route.Page)) ??
                    throw new ModSourceException(new(ModSourceError.NotFound, "The linked release is no longer listed."));
                file = files.Files.FirstOrDefault(file => file.GroupId == group.Id);
                prepared.FileKey = file is null ? "group:" + group.Id : "file:" + file.Id;
                prepared.Context = new("release", group.PageUrl);
            }
            else
            {
                file ??= files.Files.FirstOrDefault();
                prepared.FileKey = file is not null ? "file:" + file.Id : files.Groups.FirstOrDefault() is { } group ? "group:" + group.Id : null;
                prepared.Context = file is not null ? new("file", file.BrowserUrl ?? file.PageUrl) : files.Groups.FirstOrDefault() is { } version ? new("release", version.PageUrl) : null;
            }
        }
        if (route.Kind is ModPageKind.Image or ModPageKind.Images)
        {
            if (!adapter.Capabilities.CanReadImages) throw new InvalidOperationException(T("UnsupportedSource"));
            if (route.Kind == ModPageKind.Image)
            {
                var response = await Task.Run(() => adapter.GetImageAsync(route.Page, cancellationToken: token), token);
                prepared.Image = Require(response) with { PageUrl = route.Page };
            }
            else
            {
                prepared.Images = await Task.Run(() => adapter.GetImagesAsync(route.Page, cancellationToken: token), token);
                Require(prepared.Images);
            }
        }
        return prepared;
    }

    private static RemoteModFile? MatchAsset(Uri address, ModFileSnapshot files) => files.Files.FirstOrDefault(file =>
        file.GroupId is { } id && files.Groups.FirstOrDefault(group => group.Id == id) is { } release &&
        Uri.UnescapeDataString(address.AbsolutePath) == Uri.UnescapeDataString(release.PageUrl.AbsolutePath.Replace("/releases/tag/", "/releases/download/", StringComparison.Ordinal) + "/" + Uri.EscapeDataString(file.Name)));

    private bool IsKnownAsset(Uri address) => sessions.Values.Any(state => state.Files.Result?.Content is { } files &&
        state.Mod.Id.Provider == "github" && address.Host == state.Mod.PageUrl.Host && MatchAsset(address, files) is not null);

    private void SwitchLinkSource(int sourceIndex, int viewIndex, int sortIndex, string query)
    {
        rebuilding = true;
        source.SelectedIndex = sourceIndex; view.SelectedIndex = viewIndex;
        UpdateSortOptions(); sort.SelectedIndex = sortIndex; keyword.Text = query;
        rebuilding = false;
        UpdateSourceControls();
    }

    private void BeginLinkView()
    {
        CancelRefresh(); CancelTranslation(); SaveReadingPosition();
        detailCancellation?.Cancel(); detailCancellation?.Dispose(); readerCancellation?.Cancel(); listCancellation?.Cancel();
        detailCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        listGeneration++; readerGeneration++; fileGeneration++; listLoading = false;
        verificationRetryAction = null; CloseOperation();
        imageExpanded = false; fileImageExpanded = false; pendingImage = null; expandedMediaRefresh = null;
        renderedSession = null; renderedFileSession = null; fileChoices.Clear(); fileEntries = [];
        documentAnchors.Clear(); ResetStatus();
    }

    private void ActivateLink(PreparedLink prepared)
    {
        BeginLinkView();
        var route = prepared.Route;
        int sourceIndex = route.Provider == "github" ? 1 : route.Provider == "mowportal" ? 2 : 0;
        bool changedSource = source.SelectedIndex != sourceIndex;
        SwitchLinkSource(sourceIndex, route.Kind == ModPageKind.Category || changedSource ? 0 : view.SelectedIndex,
            route.Kind == ModPageKind.Category ? (int)route.Sort : changedSource ? 0 : sort.SelectedIndex,
            route.Kind == ModPageKind.Category ? route.Keyword : changedSource ? "" : keyword.Text ?? "");
        if (changedSource) { browseItems = []; browsePage = 1; hasNext = false; appliedKeyword = ""; listFresh = false; }
        if (prepared.List is { } list)
        {
            selected = null; session = null; snapshot = null;
            browseItems = list.Content!.Items; hasNext = list.Content.NextPage is not null;
            browsePage = route.PageNumber; appliedKeyword = route.Keyword; listFresh = true; listOnly = true;
            title.Text = T("ChooseMod"); reading.Children.Clear(); files.Children.Clear(); articles.Children.Clear(); fileReader.Children.Clear(); tasks.Children.Clear();
            listNotice.Text = browseItems.Length == 0 ? T("Empty") : "";
        }
        else
        {
            var mod = prepared.Overview!.Content!;
            session = ReadingState(mod); var current = session;
            current.Mod = mod; current.Overview.Result = prepared.Overview; current.Overview.Attempted = true;
            current.Overview.Generation++; current.Overview.Loading = false;
            current.ManualRepository = true; // Explicit URL navigation remains readable outside discovery results.
            selected = mod; title.Text = mod.Title; listOnly = false;
            current.Article = prepared.Article?.Content?.PageUrl;
            current.Tab = prepared.Files is null ? 0 : 1;
            current.DescriptionImage = null; current.FileImage = null;
            if (prepared.Article is { Content: { } article } body)
                current.Bodies[article.PageUrl] = new() { Attempted = true, Result = body, Address = article.PageUrl, Label = "Articles" };
            if (prepared.Articles is { } articles) current.ArticlePages[route.Page] = new() { Attempted = true, Result = articles, Label = "Articles", Address = route.Page };
            if (prepared.Images is { } images) current.ImagePages[route.Page] = new() { Attempted = true, Result = images, Label = "Images", Address = route.Page };
            if (prepared.Files is { } files)
            {
                current.Files.Generation++; current.Files.Loading = false;
                current.Files.Result = files; current.Files.Attempted = true; current.File = prepared.FileKey; current.FileNavigationOnly = false;
            }
            current.Context = prepared.Context;
            if (view.SelectedIndex == 0) browseItems = [mod, .. browseItems.Where(item => item.Id != mod.Id)];
            rebuilding = true; details.SelectedIndex = current.Tab; rebuilding = false;
            RenderDescriptionNavigation(); RenderReading(); RenderFileState();
            current.Context = prepared.Context; // A release link selects its shared notes rather than its first attachment.
        }
        RenderModList(view.SelectedIndex == 0 ? browseItems : LibraryItems());
        RefreshTracking(); UpdatePaging(); UpdateResponsiveLayout(); UpdateOriginal();
    }

    private async Task BackFromLinkAsync()
    {
        if (linkHistory.Count == 0 || operationBusy) return;
        CancelLinkNavigation();
        var location = linkHistory[^1]; linkHistory.RemoveAt(linkHistory.Count - 1);
        BeginLinkView();
        SwitchLinkSource(location.Source, location.View, location.Sort, location.Query);
        browsePage = location.Page; appliedKeyword = location.AppliedQuery; browseItems = location.Items;
        hasNext = location.HasNext; listFresh = location.Fresh; listOnly = location.ListOnly;
        session = location.Session; selected = session?.Mod;
        if (session is { } current)
        {
            current.Tab = location.Tab; current.Article = location.Article; current.File = location.File;
            if (location.GallerySelection is { } key) current.GallerySelections[ReadingKey] = key;
            current.GalleryPositions[ReadingKey] = location.Gallery;
            current.Context = location.Context; current.FileNavigationOnly = location.FileNavigationOnly;
            current.DescriptionImage = null; current.FileImage = null;
            title.Text = current.Mod.Title;
            rebuilding = true; details.SelectedIndex = current.Tab; rebuilding = false;
            RenderDescriptionNavigation(); RenderReading(); RenderFileState();
            if (location.Image is { } expanded)
            {
                current.Context = expanded.ReturnContext;
                var token = DetailToken;
                var imageTask = ShowImageAsync(expanded.Image, token);
                int generation = readerGeneration;
                await imageTask;
                if (!IsCurrent(current, token) || generation != readerGeneration) return;
            }
            current.Context = location.Context;
            RestorePosition(readingScroll, location.Reading);
            if (location.Image is not null) RestorePosition(fileScroll, location.FileReading); else RestoreFilePosition(location.FileReading);
            RestorePosition(articleScroll, location.Articles); RestorePosition(fileNavigationScroll, location.Files);
            if (galleryScroll is not null) RestorePosition(galleryScroll, new Vector(location.Gallery, 0));
        }
        else
        {
            snapshot = null; title.Text = T("ChooseMod"); reading.Children.Clear(); articles.Children.Clear(); files.Children.Clear(); fileReader.Children.Clear(); tasks.Children.Clear();
        }
        RenderModList(location.View == 0 ? browseItems : LibraryItems());
        RefreshTracking(); UpdatePaging(); UpdateResponsiveLayout(); UpdateOriginal();
    }
}
