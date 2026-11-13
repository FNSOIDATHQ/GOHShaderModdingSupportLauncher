using System.Text.Json.Serialization.Metadata;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed class ModDbSource : IModSource, IDisposable
{
    private readonly AnonymousModHttpClient http;
    private readonly ModContentCache cache;
    private readonly bool ownsHttp;
    private readonly ModVerificationState verification;
    private readonly Action<ModHttpDiagnostic>? diagnostic;
    public string Provider => "moddb";
    public ModSourceCapabilities Capabilities => ModDbParser.Capabilities;

    internal ModDbSource(AnonymousModHttpClient? http = null, ModContentCache? cache = null,
        Action<ModHttpDiagnostic>? diagnostic = null, ModVerificationState? verification = null,
        ModBrowserClearance? clearance = null)
    {
        this.verification = verification ?? new();
        this.diagnostic = diagnostic;
        this.http = http ?? new(new ModHttpOptions { Diagnostic = diagnostic, Verification = this.verification, Clearance = clearance });
        this.cache = cache ?? new();
        ownsHttp = http is null;
    }

    public Task<ModSourceResult<ModSourcePage<RemoteMod>>> BrowseAsync(string keyword = "", int page = 1,
        ModBrowseSort sort = ModBrowseSort.Updated, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (keyword.Length > 50) throw new ArgumentOutOfRangeException(nameof(keyword), "Search uses up to 50 characters.");
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        string order = sort switch
        {
            ModBrowseSort.Updated => "dateup-desc", ModBrowseSort.Newest => "date-desc",
            ModBrowseSort.Popular => "ranktoday-asc", ModBrowseSort.Name => "name-asc",
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
        var url = new Uri(ModDbParser.Origin, ModDbParser.GamePath + "/mods" + (page == 1 ? "" : $"/page/{page}") +
            $"?filter=t&kw={Uri.EscapeDataString(keyword)}&sort={order}");
        return ReadPageAsync(url, ModDbParser.Mods, ModContentJsonContext.Default.CachedModContentModSourcePageRemoteMod, mode, cancellationToken);
    }

    public Task<ModSourceResult<RemoteMod>> GetModAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(ModDbParser.ModPage(modPage), ModDbParser.Mod, ModContentJsonContext.Default.CachedModContentRemoteMod, mode, cancellationToken);

    public Task<ModSourceResult<ModSourcePage<ModArticle>>> GetArticlesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(Section(page, "articles"), ModDbParser.Articles, ModContentJsonContext.Default.CachedModContentModSourcePageModArticle, mode, cancellationToken);

    public Task<ModSourceResult<ModArticle>> GetArticleAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(ModDbParser.ArticlePage(page), ModDbParser.Article, ModContentJsonContext.Default.CachedModContentModArticle, mode, cancellationToken);

    public Task<ModSourceResult<ModSourcePage<ModImageReference>>> GetImagesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        Uri url = page.AbsolutePath.TrimEnd('/') == ModDbParser.ModPage(page).AbsolutePath ? Section(page, "images") : page;
        if (url.AbsolutePath.TrimEnd('/') != ModDbParser.ModPage(url).AbsolutePath + "/images")
            url = ModDbParser.RequirePage(url, "images", detail: true);
        return ReadPageAsync(url, ModDbParser.Images, ModContentJsonContext.Default.CachedModContentModSourcePageModImageReference, mode, cancellationToken);
    }

    public Task<ModSourceResult<ModImage>> GetImageAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(ModDbParser.RequirePage(page, "images", detail: true), ModDbParser.Image, ModContentJsonContext.Default.CachedModContentModImage, mode, cancellationToken);

    public Task<ModSourceResult<ModSourcePage<ModFileReference>>> GetFilesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(Section(page, "downloads"), ModDbParser.Files, ModContentJsonContext.Default.CachedModContentModSourcePageModFileReference, mode, cancellationToken);

    public Task<ModSourceResult<RemoteModFile>> GetFileAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(ModDbParser.RequirePage(page, "downloads", detail: true), ModDbParser.File, ModContentJsonContext.Default.CachedModContentRemoteModFile, mode, cancellationToken);

    public Task<ModSourceResult<ModFileHints>> ReadFileHintsAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        Uri mod = ModDbParser.ModPage(modPage);
        var url = new Uri("https://rss.moddb.com" + mod.AbsolutePath + "/downloads/feed/rss.xml");
        return ReadAsync("rss:" + url.AbsoluteUri, async token =>
        {
            string xml = await http.GetTextAsync(url, uri => uri.Host == "rss.moddb.com" || ModDbParser.IsSite(uri), token).ConfigureAwait(false);
            return ModDbParser.FileHints(xml, mod);
        }, ModContentJsonContext.Default.CachedModContentModFileHints, mode, cancellationToken);
    }

    public Task<ModSourceResult<ModFileSnapshot>> GetFileSnapshotAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        Uri mod = ModDbParser.ModPage(modPage);
        return ReadAsync("snapshot:" + mod.AbsoluteUri, async token =>
        {
            Uri? next = new(mod.AbsoluteUri + "/downloads");
            var visited = new HashSet<Uri>();
            var files = new Dictionary<string, RemoteModFile>(StringComparer.Ordinal);
            while (next is not null)
            {
                token.ThrowIfCancellationRequested();
                if (visited.Count >= 100 || !visited.Add(next))
                    throw AnonymousModHttpClient.Error(ModSourceError.ParseFailed, "The Files list exceeded the pagination limit or looped.");
                var page = Fresh(await GetFilesAsync(next, cancellationToken: token).ConfigureAwait(false));
                foreach (var reference in page.Items)
                {
                    var file = Fresh(await GetFileAsync(reference.PageUrl, cancellationToken: token).ConfigureAwait(false));
                    if (file.Id != reference.Id || !files.TryAdd(file.Id, file))
                        throw AnonymousModHttpClient.Error(ModSourceError.ParseFailed, "The Files list changed during pagination. Retry the complete check.");
                }
                next = page.NextPage;
            }
            return new ModFileSnapshot(files.Values.ToArray());
        }, ModContentJsonContext.Default.CachedModContentModFileSnapshot, mode, cancellationToken);
    }

    public async Task<ModSourceResult<ModDownloadLink>> ResolveDownloadAsync(Uri filePage, CancellationToken cancellationToken = default)
    {
        Uri page = ModDbParser.RequirePage(filePage, "downloads", detail: true);
        try
        {
            // Always re-read the permanent file page and resolve its current mirror without a cache fallback.
            var file = Fresh(await GetFileAsync(page, cancellationToken: cancellationToken).ConfigureAwait(false));
            var start = new Uri(ModDbParser.Origin, "/downloads/start/" + file.Id);
            string html = await http.GetTextAsync(start, ModDbParser.IsSite, cancellationToken).ConfigureAwait(false);
            Uri mirror = ModDbParser.DownloadMirror(html, file.Id);
            Uri target = await http.ResolveFileAsync(mirror, ModDbParser.IsMirrorHost, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return new(new(file, target, now), now);
        }
        catch (ModSourceException ex) { return new(null, null, ex.Failure); }
    }

    private Task<ModSourceResult<T>> ReadPageAsync<T>(Uri url, Func<string, Uri, T> parse, JsonTypeInfo<CachedModContent<T>> type,
        ModReadMode mode, CancellationToken token) => ReadAsync("page:" + url.AbsoluteUri, async ct =>
        {
            string html = await http.GetTextAsync(url, ModDbParser.IsSite, ct).ConfigureAwait(false);
            ModMediaOperation? media = typeof(T) == typeof(ModSourcePage<ModImageReference>) ? ModMediaOperation.Gallery :
                typeof(T) == typeof(ModImage) ? ModMediaOperation.ImagePage : null;
            try
            {
                var content = parse(html, url);
                if (media is not null) ModHttpDiagnostics.Report(diagnostic,
                    new(ModHttpDiagnostics.Host(url), ModHttpStage.Parse, 1, Media: media));
                return content;
            }
            catch (Exception ex) when (ex is ModSourceException or ArgumentException or FormatException or InvalidOperationException)
            {
                if (media is not null) ModHttpDiagnostics.Report(diagnostic,
                    new(ModHttpDiagnostics.Host(url), ModHttpStage.Parse, 1, FailureKind: ModHttpFailureKind.Response,
                        ExceptionType: ex.GetType().Name, Media: media));
                if (ex is ModSourceException) throw;
                throw AnonymousModHttpClient.Error(ModSourceError.ParseFailed, "The ModDB content structure or field format changed.");
            }
        }, type, mode, token);

    private async Task<ModSourceResult<T>> ReadAsync<T>(string key, Func<CancellationToken, Task<T>> fetch,
        JsonTypeInfo<CachedModContent<T>> type, ModReadMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previous = cache.Read(key, type);
        if (mode == ModReadMode.CacheOnly)
            return new(previous is null ? default : previous.Content, previous?.FetchedAt,
                new(ModSourceError.Offline, previous is null ? "No offline content is available." : "Showing the last successful content without a network check."), IsStale: previous is not null);
        if (mode != ModReadMode.Refresh) throw new ArgumentOutOfRangeException(nameof(mode));
        try
        {
            T content = await fetch(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            string? warning = cache.Write(key, content, now, type);
            return new(content, now, CacheWarning: warning);
        }
        catch (ModSourceException ex)
        {
            if (ex.Failure.Kind == ModSourceError.VerificationRequired && ex.Failure.VerificationUri is { } origin)
                verification.Pause(origin);
            return new(previous is null ? default : previous.Content, previous?.FetchedAt, ex.Failure, IsStale: previous is not null);
        }
    }

    private static T Fresh<T>(ModSourceResult<T> result) => result.IsSuccess ? result.Content! : throw new ModSourceException(result.Failure!);
    private static Uri Section(Uri page, string section)
    {
        Uri mod = ModDbParser.ModPage(page);
        return page.AbsolutePath.TrimEnd('/') == mod.AbsolutePath ? new(mod.AbsoluteUri + "/" + section) : ModDbParser.RequirePage(page, section);
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
