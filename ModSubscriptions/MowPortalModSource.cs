using System.Text.Json.Serialization.Metadata;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed class MowPortalModSource : IModSource, IDisposable
{
    private readonly AnonymousModHttpClient http;
    private readonly ModContentCache cache;
    private readonly YandexPublicFileResolver yandex;
    private readonly bool ownsHttp;
    public string Provider => "mowportal";
    public ModSourceCapabilities Capabilities => MowPortalParser.Capabilities;

    internal MowPortalModSource(AnonymousModHttpClient? http = null, ModContentCache? cache = null, Action<ModHttpDiagnostic>? diagnostic = null,
        ModVerificationState? verification = null)
    {
        this.http = http ?? new(new ModHttpOptions { Diagnostic = diagnostic, Verification = verification });
        this.cache = cache ?? new(Path.Combine(Path.GetDirectoryName(SubscriptionStore.DefaultPath)!, "mowportal-cache"));
        yandex = new(this.http); ownsHttp = http is null;
    }

    public Task<ModSourceResult<ModSourcePage<RemoteMod>>> BrowseAsync(string keyword = "", int page = 1,
        ModBrowseSort sort = ModBrowseSort.Updated, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (keyword.Length > 100 || page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        if (!Enum.IsDefined(sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        var url = new Uri(MowPortalParser.Origin, MowPortalParser.GamePath + (page == 1 ? "" : $"page/{page}/"));
        return ReadPageAsync(url, (html, uri) =>
        {
            var result = MowPortalParser.Mods(html, uri);
            IEnumerable<RemoteMod> items = result.Items.Where(mod => mod.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) || mod.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (sort == ModBrowseSort.Name) items = items.OrderBy(mod => mod.Title, StringComparer.OrdinalIgnoreCase);
            return result with { Items = items.ToArray() };
        }, ModContentJsonContext.Default.CachedModContentModSourcePageRemoteMod, mode, cancellationToken, $":{keyword}:{sort}");
    }

    public Task<ModSourceResult<RemoteMod>> GetModAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(MowPortalParser.ModPage(modPage), MowPortalParser.Mod, ModContentJsonContext.Default.CachedModContentRemoteMod, mode, cancellationToken);

    public Task<ModSourceResult<ModSourcePage<ModArticle>>> GetArticlesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = MowPortalParser.ModPage(page);
        return Task.FromResult(new ModSourceResult<ModSourcePage<ModArticle>>(new([], null), DateTimeOffset.UtcNow));
    }

    public Task<ModSourceResult<ModArticle>> GetArticleAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModSourceResult<ModArticle>(null, null,
            new(ModSourceError.NotFound, "Version notes are available with their files.")));
    }

    public async Task<ModSourceResult<ModSourcePage<ModImageReference>>> GetImagesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var mod = await GetModAsync(page, mode, cancellationToken).ConfigureAwait(false);
        return new(mod.Content is { } content ? new(content.Images.Select(image => new ModImageReference(image.Id, image.ImageUrl, image.ThumbnailUrl ?? image.ImageUrl, image.Caption)).ToArray(), null) : null,
            mod.FetchedAt, mod.Failure, mod.IsStale, mod.CacheWarning);
    }

    public Task<ModSourceResult<ModImage>> GetImageAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MowPortalParser.IsImage(page)) throw new ArgumentException("Unsupported Mow Portal image URL.", nameof(page));
        return Task.FromResult(new ModSourceResult<ModImage>(new() { Id = page.AbsoluteUri, ImageUrl = page }, DateTimeOffset.UtcNow));
    }

    public async Task<ModSourceResult<ModSourcePage<ModFileReference>>> GetFilesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var files = await GetFileSnapshotAsync(page, mode, cancellationToken).ConfigureAwait(false);
        return new(files.Content is { } content ? new(content.Files.Select(file => new ModFileReference(file.Id, file.PageUrl, file.Name, file.Description, file.PublishedAt)).ToArray(), null) : null,
            files.FetchedAt, files.Failure, files.IsStale, files.CacheWarning);
    }

    public async Task<ModSourceResult<RemoteModFile>> GetFileAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        if (!page.Fragment.StartsWith("#download-", StringComparison.Ordinal) || page.Fragment.Length != 74 || !page.Fragment[10..].All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Mow Portal download reference.", nameof(page));
        var files = await GetFileSnapshotAsync(page, mode, cancellationToken).ConfigureAwait(false);
        // Uri.Equals ignores fragments; each public mirror is identified by its exact fragment.
        var file = files.Content?.Files.SingleOrDefault(file => file.PageUrl.AbsoluteUri.Equals(page.AbsoluteUri, StringComparison.Ordinal));
        return new(file, files.FetchedAt, files.Failure ?? (file is null ? new(ModSourceError.NotFound, "The public download link is no longer listed on the author page.") : null), files.IsStale, files.CacheWarning);
    }

    public Task<ModSourceResult<ModFileSnapshot>> GetFileSnapshotAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadPageAsync(MowPortalParser.ModPage(modPage), MowPortalParser.Files, ModContentJsonContext.Default.CachedModContentModFileSnapshot, mode, cancellationToken, ":files");

    public async Task<ModSourceResult<ModFileHints>> ReadFileHintsAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var files = await GetFileSnapshotAsync(modPage, mode, cancellationToken).ConfigureAwait(false);
        return new(files.Content is { } content ? new(content.Files.Select(file => new ModFileHint(file.Id, file.PageUrl, file.Name, file.PublishedAt)).ToArray()) : null,
            files.FetchedAt, files.Failure, files.IsStale, files.CacheWarning);
    }

    public async Task<ModSourceResult<ModDownloadLink>> ResolveDownloadAsync(Uri filePage, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await GetFileAsync(filePage, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) throw new ModSourceException(result.Failure!);
            if (!result.Content!.CanDownload) throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "Anonymous downloading from this host has not been verified. Open the original author page.");
            var link = await yandex.ResolveDownloadAsync(result.Content, cancellationToken).ConfigureAwait(false);
            return new(link, link.ResolvedAt);
        }
        catch (ModSourceException ex) { return new(null, null, ex.Failure); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        { return new(null, null, new(ModSourceError.ParseFailed, "The public download reference is invalid.")); }
    }

    private Task<ModSourceResult<T>> ReadPageAsync<T>(Uri page, Func<string, Uri, T> parse, JsonTypeInfo<CachedModContent<T>> type,
        ModReadMode mode, CancellationToken token, string suffix = "") => ReadAsync("mowportal:" + page.AbsoluteUri + suffix, async ct =>
        {
            string html = await http.GetTextAsync(page, MowPortalParser.IsSite, ct).ConfigureAwait(false);
            try { return parse(html, page); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            { throw AnonymousModHttpClient.Error(ModSourceError.ParseFailed, "The Mow Portal game content structure changed."); }
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
            var content = await fetch(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return new(content, now, CacheWarning: cache.Write(key, content, now, type));
        }
        catch (ModSourceException ex) { return new(previous is null ? default : previous.Content, previous?.FetchedAt, ex.Failure, IsStale: previous is not null); }
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
