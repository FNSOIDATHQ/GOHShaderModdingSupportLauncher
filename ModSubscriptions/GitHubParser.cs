using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class GitHubParser
{
    internal static readonly Uri ApiOrigin = new("https://api.github.com");
    internal static readonly ModSourceCapabilities Capabilities = new()
    {
        CanBrowse = true, CanReadArticles = false, CanReadImages = true, CanListFiles = true,
        CanDownloadAnonymously = true, UpdateEvidence = UpdateEvidence.FileIdentityAndRevision
    };

    internal static bool IsApi(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) && uri.IdnHost == "api.github.com";
    internal static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    internal static string Required(JsonElement element, string name)
    {
        string value = Text(element, name);
        if (value.Length == 0) throw new FormatException("A required GitHub field is missing.");
        return value;
    }
    internal static string Id(JsonElement element, string name = "id") =>
        element.GetProperty(name).GetInt64() is > 0 and var id ? id.ToString(CultureInfo.InvariantCulture) : throw new FormatException("Invalid GitHub ID.");
    internal static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    internal static DateTimeOffset? Date(JsonElement element, string name) => DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    internal static Uri RepoPage(Uri uri)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(uri) || uri.IdnHost != "github.com" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Use a public https://github.com/owner/repository URL.", nameof(uri));
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("Use a repository URL without a file or page suffix.", nameof(uri));
        if (parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) parts[1] = parts[1][..^4];
        return new("https://github.com/" + string.Join('/', parts));
    }
    internal static string RepoName(Uri page) => RepoPage(page).AbsolutePath.Trim('/');
    internal static string NumericId(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0
        ? value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Invalid GitHub repository ID.", nameof(id));
    internal static Uri Api(string path) => new(ApiOrigin, path);
    internal static RemoteMod Mod(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("private", out var isPrivate) || isPrivate.ValueKind != JsonValueKind.False)
            throw new FormatException("Only public GitHub repositories are supported.");
        string name = Required(element, "full_name");
        Uri page = RepoPage(new("https://github.com/" + name));
        string[] topics = element.TryGetProperty("topics", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
        var kind = Classify(name, topics);
        return new()
        {
            Id = new("github", Id(element)), PageUrl = page, Title = name,
            Summary = Text(element, "description"), Capabilities = Capabilities, Kind = kind,
            InstallationHint = kind == RemoteProjectKind.Tool ? "Tools are download-only; do not run downloaded programs from the launcher."
                : "Installation requires a complete standard Mod archive. Builds and generators are never run."
        };
    }
    internal static RemoteProjectKind Classify(string name, string[] topics)
    {
        string lower = name.ToLowerInvariant();
        if (new[] { "launcher", "editor", "tool", "utility", "framework", "sdk" }.Any(word => lower.Contains(word, StringComparison.Ordinal)) ||
            topics.Any(topic => topic is "tool" or "tools" or "launcher" or "editor")) return RemoteProjectKind.Tool;
        return topics.Any(topic => topic is "goh-mod" or "game-mod" or "gates-of-hell-mod" or "mod") ? RemoteProjectKind.Mod : RemoteProjectKind.Unknown;
    }

    internal static string Readme(JsonElement element)
    {
        if (Text(element, "encoding") != "base64") throw new FormatException("Unsupported README encoding.");
        byte[] bytes = Convert.FromBase64String(element.GetProperty("content").GetString() ?? throw new FormatException("Missing README content."));
        if (bytes.Length > 1024 * 1024) throw new FormatException("The README is too large.");
        return Encoding.UTF8.GetString(bytes);
    }
    internal static ModImage[] Images(string markdown, Uri repository, string branch, string readmePath)
        => ModDocumentParser.Images(ReadmeDocument(markdown, repository, branch, readmePath)).DistinctBy(image => image.ImageUrl).Take(100).ToArray();

    internal static ModReadingDocument ReadmeDocument(string markdown, Uri repository, string branch, string readmePath)
    {
        string directory = readmePath.Contains('/') ? readmePath[..(readmePath.LastIndexOf('/') + 1)] : "";
        var relativeBase = new Uri("https://raw.githubusercontent.com/" + RepoName(repository) + "/" + Uri.EscapeDataString(branch) + "/" + directory);
        var linkBase = new Uri(repository.AbsoluteUri + "/blob/" + Uri.EscapeDataString(branch) + "/" + readmePath);
        return ModDocumentParser.Markdown(markdown, linkBase, image => ModDocumentParser.Image(image, relativeBase, NormalizeImage));
    }

    private static Uri? NormalizeImage(Uri uri)
    {
        if (!IsImageHost(uri)) return null;
        if (uri.Host == "github.com" && uri.AbsolutePath.Contains("/blob/", StringComparison.Ordinal))
        {
            uri = new Uri("https://raw.githubusercontent.com" + uri.AbsolutePath.Replace("/blob/", "/", StringComparison.Ordinal) + uri.Query);
        }
        return uri.Query.Length > 0 && uri.Query != "?raw=true" ? null : uri;
    }
    internal static bool IsImageHost(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) && uri.Host is "github.com" or "raw.githubusercontent.com" or "user-images.githubusercontent.com";
    internal static ModFileGroup Release(JsonElement release)
    {
        string id = Id(release);
        string title = Text(release, "name");
        if (string.IsNullOrWhiteSpace(title)) title = Text(release, "tag_name");
        if (string.IsNullOrWhiteSpace(title)) title = id;
        var page = new Uri(Required(release, "html_url"));
        string body = Text(release, "body");
        var document = ModDocumentParser.Markdown(body, page, image => ModDocumentParser.Image(image, page, NormalizeImage));
        return new()
        {
            Id = id, PageUrl = page, Title = title,
            Description = body, Document = document, PublishedAt = Date(release, "published_at")
        };
    }
    internal static RemoteModFile Asset(JsonElement asset, string repositoryId, RemoteProjectKind kind = RemoteProjectKind.Unknown)
    {
        if (Text(asset, "state") != "uploaded") throw new FormatException("A release asset is not fully uploaded.");
        string id = Id(asset); string name = Required(asset, "name");
        long size = asset.GetProperty("size").GetInt64();
        if (size < 0) throw new FormatException("A release asset has an invalid size.");
        string revision = string.Join(':', id, Text(asset, "updated_at"), size.ToString(CultureInfo.InvariantCulture), Text(asset, "digest"), name);
        return new()
        {
            Id = "asset:" + id, Revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revision))),
            PageUrl = Api($"/repositories/{NumericId(repositoryId)}/releases/assets/{id}"), Name = name,
            BrowserUrl = AssetBrowserPage(asset),
            Description = Text(asset, "label"), SizeBytes = size, PublishedAt = Date(asset, "created_at"), UpdatedAt = Date(asset, "updated_at"),
            DownloadOnly = kind == RemoteProjectKind.Tool || Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".msi" or ".dll" or ".bat" or ".cmd" or ".ps1"
        };
    }
    private static Uri? AssetBrowserPage(JsonElement asset)
    {
        if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var download) || download.Host != "github.com") return null;
        var parts = download.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 6 && parts[2] == "releases" && parts[3] == "download"
            ? new Uri("https://github.com/" + parts[0] + "/" + parts[1] + "/releases/tag/" + parts[4]) : null;
    }
    internal static RemoteModFile Snapshot(string repositoryId, string sha, string name)
    {
        if (sha.Length != 40 || sha.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("A source snapshot requires a complete commit SHA.");
        sha = sha.ToLowerInvariant();
        return new()
        {
            Id = "source:" + sha, Revision = sha, PageUrl = Api($"/repositories/{NumericId(repositoryId)}/zipball/{sha}"),
            Name = name.Replace('/', '-') + "-" + sha + ".zip", IsSourceSnapshot = true, DownloadOnly = true,
            Description = "Source code fixed to commit " + sha + ". Resources may need a build or generator; only download is supported."
        };
    }
}
