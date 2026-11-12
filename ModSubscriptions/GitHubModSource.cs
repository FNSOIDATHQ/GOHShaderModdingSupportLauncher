using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// All network entry points are public GETs. Neither settings nor cache contain tokens or signed URLs.
internal sealed class GitHubModSource : IModSource, IDisposable
{
    private const int PageSize = 30;
    private readonly AnonymousModHttpClient http;
    private readonly ModContentCache cache;
    private readonly GitHubOptionsStore settings;
    private readonly bool ownsHttp;
    public string Provider => "github";
    public ModSourceCapabilities Capabilities => GitHubParser.Capabilities;

    internal GitHubModSource(AnonymousModHttpClient? http = null, ModContentCache? cache = null,
        GitHubOptionsStore? settings = null, Action<ModHttpDiagnostic>? diagnostic = null)
    {
        this.http = http ?? new(new ModHttpOptions { Diagnostic = diagnostic });
        this.cache = cache ?? new(Path.Combine(Path.GetDirectoryName(SubscriptionStore.DefaultPath)!, "github-cache"));
        this.settings = settings ?? new();
        ownsHttp = http is null;
    }

    public Task<ModSourceResult<ModSourcePage<RemoteMod>>> BrowseAsync(string keyword = "", int page = 1,
        ModBrowseSort sort = ModBrowseSort.Updated, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (keyword.Length > 100 || page is < 1 or > 34) throw new ArgumentOutOfRangeException(nameof(page));
        var options = settings.Snapshot();
        if (options.Topics.Length == 0)
            return Task.FromResult(new ModSourceResult<ModSourcePage<RemoteMod>>(new([], null), DateTimeOffset.UtcNow));
        string order = sort switch { ModBrowseSort.Updated => "updated", ModBrowseSort.Newest => "updated", ModBrowseSort.Popular => "stars", ModBrowseSort.Name => "", _ => throw new ArgumentOutOfRangeException(nameof(sort)) };
        // Keywords are literal text. Quotes, qualifier separators and boolean operators cannot widen topic scope.
        string words = string.Join(' ', new string(keyword.Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '-' or '_' or '.' ? c : ' ').ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => "\"" + word + "\""));
        string prefix = words.Length == 0 ? "" : words + " in:name,description,readme ";
        string[] groups = options.MatchAll ? [string.Join(' ', options.Topics.Select(topic => "topic:" + topic))] : options.Topics.Select(topic => "topic:" + topic).ToArray();
        string key = $"github:search:{string.Join('|', groups)}:{words}:{page}:{sort}";
        return ReadAsync(key, async token =>
        {
            var results = new Dictionary<string, RemoteMod>(StringComparer.Ordinal);
            bool hasNext = false;
            foreach (string group in groups)
            {
                var url = GitHubParser.Api("/search/repositories?q=" + Uri.EscapeDataString(prefix + group + " is:public") +
                    $"&per_page={PageSize}&page={page}" + (order.Length == 0 ? "" : "&sort=" + order + "&order=desc"));
                using var json = await JsonAsync(url, token).ConfigureAwait(false);
                var root = json.RootElement;
                if (GitHubParser.Flag(root, "incomplete_results")) throw Parse("GitHub returned an incomplete search. Retry later.");
                var items = RequireArray(root.GetProperty("items"));
                foreach (var item in items.EnumerateArray())
                {
                    var mod = GitHubParser.Mod(item);
                    results.TryAdd(mod.Id.RemoteId, mod);
                }
                hasNext |= root.GetProperty("total_count").GetInt32() > page * PageSize && page * PageSize < 1000;
            }
            IEnumerable<RemoteMod> ordered = results.Values;
            if (sort == ModBrowseSort.Name) ordered = ordered.OrderBy(mod => mod.Title, StringComparer.OrdinalIgnoreCase);
            return new ModSourcePage<RemoteMod>(ordered.ToArray(), hasNext ? new Uri($"https://github.com/topics/{options.Topics[0]}?page={page + 1}") : null);
        }, ModContentJsonContext.Default.CachedModContentModSourcePageRemoteMod, mode, cancellationToken);
    }

    public Task<ModSourceResult<RemoteMod>> GetModAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        Uri page = GitHubParser.RepoPage(modPage);
        return ReadModAsync("github:mod:" + page.AbsoluteUri, GitHubParser.Api("/repos/" + GitHubParser.RepoName(page)), mode, cancellationToken);
    }
    internal Task<ModSourceResult<RemoteMod>> GetModByIdAsync(string id, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadModAsync("github:mod-id:" + GitHubParser.NumericId(id), GitHubParser.Api("/repositories/" + GitHubParser.NumericId(id)), mode, cancellationToken, GitHubParser.NumericId(id));

    private Task<ModSourceResult<RemoteMod>> ReadModAsync(string key, Uri api, ModReadMode mode, CancellationToken token, string? expectedId = null) =>
        ReadAsync(key, async ct =>
        {
            using var json = await JsonAsync(api, ct).ConfigureAwait(false);
            var mod = GitHubParser.Mod(json.RootElement);
            if (expectedId is not null && mod.Id.RemoteId != expectedId) throw Parse("GitHub returned a different repository identity.");
            string branch = GitHubParser.Required(json.RootElement, "default_branch");
            try
            {
                using var readme = await JsonAsync(RepoApi(mod.PageUrl, "/readme"), ct).ConfigureAwait(false);
                string body = GitHubParser.Readme(readme.RootElement);
                var images = GitHubParser.Images(body, mod.PageUrl, branch, GitHubParser.Text(readme.RootElement, "path", "README.md"));
                mod = mod with { Summary = mod.Summary + (mod.Summary.Length > 0 ? "\n\n" : "") + body, Images = images };
            }
            catch (ModSourceException ex) when (ex.Failure.Kind == ModSourceError.NotFound) { }
            using var releasePage = await JsonAsync(RepoApi(mod.PageUrl, "/releases?per_page=1&page=1"), ct).ConfigureAwait(false);
            if (RequireArray(releasePage.RootElement).GetArrayLength() == 0 && mod.Kind != RemoteProjectKind.Tool)
                mod = mod with { Kind = RemoteProjectKind.SourceOnly, InstallationHint = "Source snapshots are download-only. The launcher never builds or generates resources." };
            return mod;
        }, ModContentJsonContext.Default.CachedModContentRemoteMod, mode, token);

    public Task<ModSourceResult<ModSourcePage<ModArticle>>> GetArticlesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default) =>
        ReadAsync("github:articles:" + GitHubParser.RepoPage(page).AbsoluteUri, async token =>
        {
            var releases = await ReleasesAsync(page, token).ConfigureAwait(false);
            return new ModSourcePage<ModArticle>(releases.Select(GitHubParser.Article).ToArray(), null);
        }, ModContentJsonContext.Default.CachedModContentModSourcePageModArticle, mode, cancellationToken);

    public Task<ModSourceResult<ModArticle>> GetArticleAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(page) || page.Host != "github.com") throw new ArgumentException("Invalid GitHub release URL.", nameof(page));
        var parts = page.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 5 || parts[2] != "releases" || parts[3] != "tag") throw new ArgumentException("Invalid GitHub release URL.", nameof(page));
        Uri repo = GitHubParser.RepoPage(new("https://github.com/" + parts[0] + "/" + parts[1]));
        return ReadAsync("github:article:" + page.AbsoluteUri, async token =>
        {
            using var json = await JsonAsync(RepoApi(repo, "/releases/tags/" + string.Join('/', parts.Skip(4))), token).ConfigureAwait(false);
            return GitHubParser.Article(json.RootElement);
        }, ModContentJsonContext.Default.CachedModContentModArticle, mode, cancellationToken);
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
        if (!GitHubParser.IsImageHost(page)) throw new ArgumentException("Unsupported GitHub README image host.", nameof(page));
        return Task.FromResult(new ModSourceResult<ModImage>(new() { Id = page.AbsoluteUri, ImageUrl = page }, DateTimeOffset.UtcNow));
    }

    public async Task<ModSourceResult<ModSourcePage<ModFileReference>>> GetFilesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetFileSnapshotAsync(page, mode, cancellationToken).ConfigureAwait(false);
        return new(snapshot.Content is { } content ? new(content.Files.Select(file => new ModFileReference(file.Id, file.PageUrl, file.Name, file.Description, file.PublishedAt)).ToArray(), null) : null,
            snapshot.FetchedAt, snapshot.Failure, snapshot.IsStale, snapshot.CacheWarning);
    }

    public Task<ModSourceResult<RemoteModFile>> GetFileAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var reference = FileReference(page);
        return ReadAsync("github:file:" + page.AbsoluteUri, async token =>
        {
            var repo = await RepositoryByIdAsync(reference.RepositoryId, token).ConfigureAwait(false);
            if (reference.Sha is { } sha) return GitHubParser.Snapshot(reference.RepositoryId, sha, repo.Title);
            using var asset = await JsonAsync(RepoApi(repo.PageUrl, "/releases/assets/" + reference.AssetId), token).ConfigureAwait(false);
            var file = GitHubParser.Asset(asset.RootElement, reference.RepositoryId, repo.Kind);
            if (file.Id != "asset:" + reference.AssetId) throw Parse("GitHub returned a different asset identity.");
            return file;
        }, ModContentJsonContext.Default.CachedModContentRemoteModFile, mode, cancellationToken);
    }

    public Task<ModSourceResult<ModFileSnapshot>> GetFileSnapshotAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var page = GitHubParser.RepoPage(modPage);
        return ReadAsync("github:snapshot:" + page.AbsoluteUri, token => SnapshotAsync(page, null, token), ModContentJsonContext.Default.CachedModContentModFileSnapshot, mode, cancellationToken);
    }
    internal Task<ModSourceResult<ModFileSnapshot>> GetFileSnapshotByIdAsync(string id, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        id = GitHubParser.NumericId(id);
        return ReadAsync("github:snapshot-id:" + id, async token =>
        {
            var repo = await RepositoryByIdAsync(id, token).ConfigureAwait(false);
            return await SnapshotAsync(repo.PageUrl, repo, token).ConfigureAwait(false);
        }, ModContentJsonContext.Default.CachedModContentModFileSnapshot, mode, cancellationToken);
    }
    private async Task<ModFileSnapshot> SnapshotAsync(Uri page, RemoteMod? repo, CancellationToken token)
    {
        repo ??= await RepositoryAsync(RepoApi(page, ""), token).ConfigureAwait(false);
        var releases = await ReleasesAsync(repo.PageUrl, token).ConfigureAwait(false);
        var files = new Dictionary<string, RemoteModFile>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            string releaseId = GitHubParser.Id(release);
            // The nested assets array can be truncated. Always enumerate the asset endpoint's pages.
            var assets = await PagesAsync(RepoApi(repo.PageUrl, "/releases/" + releaseId + "/assets"), token).ConfigureAwait(false);
            foreach (var asset in assets)
            {
                if (GitHubParser.Text(asset, "state") != "uploaded") throw Parse("A release asset is still uploading. Retry the complete check later.");
                var file = GitHubParser.Asset(asset, repo.Id.RemoteId, repo.Kind) with { BrowserUrl = new(GitHubParser.Required(release, "html_url")) };
                if (!files.TryAdd(file.Id, file)) throw Parse("The release assets changed during pagination. Retry the complete check.");
            }
        }
        if (releases.Length == 0)
        {
            using var commit = await JsonAsync(RepoApi(repo.PageUrl, "/commits/HEAD"), token).ConfigureAwait(false);
            var file = GitHubParser.Snapshot(repo.Id.RemoteId, GitHubParser.Required(commit.RootElement, "sha"), repo.Title);
            files.Add(file.Id, file);
        }
        return new(files.Values.ToArray());
    }

    public async Task<ModSourceResult<ModFileHints>> ReadFileHintsAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetFileSnapshotAsync(modPage, mode, cancellationToken).ConfigureAwait(false);
        return new(snapshot.Content is { } content ? new(content.Files.Select(file => new ModFileHint(file.Id, file.PageUrl, file.Name, file.PublishedAt)).ToArray()) : null,
            snapshot.FetchedAt, snapshot.Failure, snapshot.IsStale, snapshot.CacheWarning);
    }

    public async Task<ModSourceResult<ModDownloadLink>> ResolveDownloadAsync(Uri filePage, CancellationToken cancellationToken = default)
    {
        var reference = FileReference(filePage);
        try
        {
            var repo = await RepositoryByIdAsync(reference.RepositoryId, cancellationToken).ConfigureAwait(false);
            RemoteModFile file; Uri start;
            if (reference.Sha is { } sha)
            {
                file = GitHubParser.Snapshot(reference.RepositoryId, sha, repo.Title);
                start = new("https://codeload.github.com/" + GitHubParser.RepoName(repo.PageUrl) + "/zip/" + sha);
            }
            else
            {
                using var asset = await JsonAsync(RepoApi(repo.PageUrl, "/releases/assets/" + reference.AssetId), cancellationToken).ConfigureAwait(false);
                file = GitHubParser.Asset(asset.RootElement, reference.RepositoryId, repo.Kind);
                if (file.Id != "asset:" + reference.AssetId) throw Parse("GitHub returned a different asset identity.");
                start = new(GitHubParser.Required(asset.RootElement, "browser_download_url"));
                if (start.Host != "github.com" || !start.AbsolutePath.StartsWith(repo.PageUrl.AbsolutePath + "/releases/download/", StringComparison.Ordinal))
                    throw Parse("The asset download URL does not belong to its repository.");
            }
            Uri download = await http.ResolveFileAsync(start, IsDownloadHost, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return new(new(file, download, now), now);
        }
        catch (ModSourceException ex) { return new(null, null, ex.Failure); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new(null, null, new(ModSourceError.ParseFailed, "The GitHub download metadata is invalid.")); }
    }

    internal static bool IsDownloadHost(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) && uri.Host is
        "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "codeload.github.com";
    private static Uri RepoApi(Uri repo, string suffix) => GitHubParser.Api("/repos/" + GitHubParser.RepoName(repo) + suffix);
    private async Task<RemoteMod> RepositoryByIdAsync(string id, CancellationToken token)
    {
        id = GitHubParser.NumericId(id);
        var mod = await RepositoryAsync(GitHubParser.Api("/repositories/" + id), token).ConfigureAwait(false);
        if (mod.Id.RemoteId != id) throw Parse("GitHub returned a different repository identity.");
        return mod;
    }
    private async Task<RemoteMod> RepositoryAsync(Uri api, CancellationToken token)
    {
        using var json = await JsonAsync(api, token).ConfigureAwait(false);
        return GitHubParser.Mod(json.RootElement);
    }
    private async Task<JsonDocument> JsonAsync(Uri uri, CancellationToken token)
    {
        string text = await http.GetJsonAsync(uri, GitHubParser.IsApi, token).ConfigureAwait(false);
        return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
    }
    private async Task<JsonElement[]> ReleasesAsync(Uri repo, CancellationToken token) =>
        (await PagesAsync(RepoApi(repo, "/releases"), token).ConfigureAwait(false)).Where(release => !GitHubParser.Flag(release, "draft")).ToArray();
    private async Task<JsonElement[]> PagesAsync(Uri endpoint, CancellationToken token)
    {
        var items = new List<JsonElement>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 1; page <= 100; page++)
        {
            using var json = await JsonAsync(new Uri(endpoint.AbsoluteUri + $"?per_page=100&page={page}"), token).ConfigureAwait(false);
            var array = RequireArray(json.RootElement);
            foreach (var item in array.EnumerateArray())
            {
                if (!ids.Add(GitHubParser.Id(item))) throw Parse("The GitHub list changed during pagination. Retry the complete check.");
                items.Add(item.Clone());
            }
            if (array.GetArrayLength() < 100) return items.ToArray();
        }
        throw Parse("The GitHub list exceeded its pagination limit.");
    }
    private static JsonElement RequireArray(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value : throw new FormatException("Expected a GitHub array.");
    private static (string RepositoryId, string? AssetId, string? Sha) FileReference(Uri page)
    {
        if (!GitHubParser.IsApi(page) || page.Query.Length > 0 || page.Fragment.Length > 0) throw new ArgumentException("Invalid stable GitHub file URL.", nameof(page));
        var parts = page.AbsolutePath.Trim('/').Split('/');
        if (parts.Length == 5 && parts[0] == "repositories" && parts[2] == "releases" && parts[3] == "assets")
            return (GitHubParser.NumericId(parts[1]), GitHubParser.NumericId(parts[4]), null);
        if (parts.Length == 4 && parts[0] == "repositories" && parts[2] == "zipball" && parts[3].Length == 40 && parts[3].All(Uri.IsHexDigit))
            return (GitHubParser.NumericId(parts[1]), null, parts[3].ToLowerInvariant());
        throw new ArgumentException("Invalid stable GitHub file URL.", nameof(page));
    }
    private async Task<ModSourceResult<T>> ReadAsync<T>(string key, Func<CancellationToken, Task<T>> fetch,
        JsonTypeInfo<CachedModContent<T>> type, ModReadMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previous = cache.Read(key, type);
        if (mode == ModReadMode.CacheOnly)
            return new(previous is null ? default : previous.Content, previous?.FetchedAt,
                new(ModSourceError.Offline, previous is null ? "No offline content is available." : "Showing the last successful GitHub content without a network check."), IsStale: previous is not null);
        if (mode != ModReadMode.Refresh) throw new ArgumentOutOfRangeException(nameof(mode));
        try
        {
            T content = await fetch(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return new(content, now, CacheWarning: cache.Write(key, content, now, type));
        }
        catch (ModSourceException ex) { return new(previous is null ? default : previous.Content, previous?.FetchedAt, ex.Failure, IsStale: previous is not null); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { return new(previous is null ? default : previous.Content, previous?.FetchedAt, new(ModSourceError.ParseFailed, "The GitHub content structure or field format changed."), IsStale: previous is not null); }
    }
    private static ModSourceException Parse(string message) => AnonymousModHttpClient.Error(ModSourceError.ParseFailed, message);
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
