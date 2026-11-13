namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal enum ModPageKind { Overview, Articles, Article, Files, File, Images, Image, Release, Asset, Category }

internal sealed record ModPageRoute(string Provider, ModPageKind Kind, Uri Page, Uri? ModPage, int PageNumber = 1,
    string Keyword = "", ModBrowseSort Sort = ModBrowseSort.Updated);

// Only pages with an existing native reader are routes. This never probes arbitrary external URLs.
internal static class ModPageRouter
{
    internal static bool TryResolveSupportedPage(Uri address, out ModPageRoute? route)
    {
        route = null;
        if (!AnonymousModHttpClient.IsPublicHttps(address)) return false;
        var page = new UriBuilder(address) { Fragment = "" }.Uri;
        string path = page.AbsolutePath.TrimEnd('/');
        string[] parts = path.Trim('/').Split('/');
        // Encoded path separators must not change the structure after validation.
        if (parts.Any(part => Uri.UnescapeDataString(part).IndexOfAny(['/', '\\', '?', '#']) >= 0) &&
            !(page.Host == "github.com" && parts.Length >= 5 && parts[2] == "releases")) return false;
        try
        {
            if (page.Host is "www.moddb.com" or "moddb.com")
            {
                page = new UriBuilder(page) { Host = "www.moddb.com" }.Uri;
                path = page.AbsolutePath.TrimEnd('/');
                page = new UriBuilder(page) { Path = path }.Uri;
                if (path == ModDbParser.GamePath + "/mods" || path.StartsWith(ModDbParser.GamePath + "/mods/page/", StringComparison.Ordinal))
                {
                    string tail = path[(ModDbParser.GamePath.Length + 5)..];
                    int number = tail.Length == 0 ? 1 : tail.StartsWith("/page/", StringComparison.Ordinal) &&
                        int.TryParse(tail[6..], out int value) && value is > 0 and <= 1000 ? value : 0;
                    if (number == 0 || !BrowseQuery(page, out string keyword, out var sort)) return false;
                    route = new("moddb", ModPageKind.Category, page, null, number, keyword, sort); return true;
                }
                if (page.Query.Length != 0) return false;
                Uri mod = ModDbParser.ModPage(page);
                string tailPath = path[mod.AbsolutePath.Length..];
                ModPageKind kind;
                if (tailPath.Length == 0) kind = ModPageKind.Overview;
                else if (parts.Length == 3 && parts[2] == "articles") kind = ModPageKind.Articles;
                else if (parts.Length == 4 && parts[2] is "articles" or "features" or "news" or "tutorials")
                { _ = ModDbParser.ArticlePage(page); kind = ModPageKind.Article; }
                else if (parts.Length == 3 && parts[2] == "downloads") kind = ModPageKind.Files;
                else if (parts.Length == 4 && parts[2] == "downloads")
                { _ = ModDbParser.RequirePage(page, "downloads", true); kind = ModPageKind.File; }
                else if (parts.Length == 3 && parts[2] == "images") kind = ModPageKind.Images;
                else if (parts.Length == 4 && parts[2] == "images")
                { _ = ModDbParser.RequirePage(page, "images", true); kind = ModPageKind.Image; }
                else if (parts.Length == 5 && parts[3] == "page" && int.TryParse(parts[4], out int number) && number is > 0 and <= 1000 && parts[2] is "articles" or "downloads" or "images")
                    kind = parts[2] == "articles" ? ModPageKind.Articles : parts[2] == "images" ? ModPageKind.Images : ModPageKind.Files;
                else return false;
                route = new("moddb", kind, page, mod); return true;
            }
            if (page.Host == "github.com" && page.Query.Length == 0 && parts.Length >= 2)
            {
                page = new UriBuilder(page) { Path = path }.Uri;
                Uri mod = GitHubParser.RepoPage(new("https://github.com/" + parts[0] + "/" + parts[1]));
                // Reserved site areas are not repository pages.
                if (parts[0] is "login" or "signup" or "topics" or "settings" or "orgs" or "users" or "search" or "marketplace" or "features") return false;
                ModPageKind kind;
                if (parts.Length == 2) kind = ModPageKind.Overview;
                else if (parts.Length == 3 && parts[2] == "releases") kind = ModPageKind.Files;
                else if (parts.Length >= 5 && parts[2] == "releases" && parts[3] == "tag") kind = ModPageKind.Release;
                else if (parts.Length >= 6 && parts[2] == "releases" && parts[3] == "download") kind = ModPageKind.Asset;
                else return false;
                route = new("github", kind, page, mod); return true;
            }
            if (page.Host == MowPortalParser.Origin.Host && page.Query.Length == 0)
            {
                string category = MowPortalParser.GamePath.TrimEnd('/');
                if (path == category || path.StartsWith(category + "/page/", StringComparison.Ordinal))
                {
                    int number = path == category ? 1 : int.TryParse(path[(category.Length + 6)..], out int value) && value is > 0 and <= 1000 ? value : 0;
                    if (number == 0) return false;
                    route = new("mowportal", ModPageKind.Category, page, null, number); return true;
                }
                Uri mod = MowPortalParser.ModPage(page);
                bool file = address.Fragment.StartsWith("#download-", StringComparison.Ordinal) && address.Fragment.Length == 74 && address.Fragment[10..].All(Uri.IsHexDigit);
                route = new("mowportal", file ? ModPageKind.File : ModPageKind.Overview, file ? address : page, mod); return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ModSourceException or FormatException) { }
        return false;
    }

    private static bool BrowseQuery(Uri page, out string keyword, out ModBrowseSort sort)
    {
        keyword = ""; sort = ModBrowseSort.Updated;
        var seen = new HashSet<string>();
        foreach (string pair in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(fields[0]);
            string value = fields.Length == 2 ? Uri.UnescapeDataString(fields[1].Replace('+', ' ')) : "";
            if (!seen.Add(key)) return false;
            switch (key)
            {
                case "filter" when value == "t": break;
                case "kw" when value.Length <= 50: keyword = value; break;
                case "sort":
                    sort = value switch { "dateup-desc" => ModBrowseSort.Updated, "date-desc" => ModBrowseSort.Newest,
                        "ranktoday-asc" => ModBrowseSort.Popular, "name-asc" => ModBrowseSort.Name, _ => (ModBrowseSort)(-1) };
                    if ((int)sort < 0) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }
}
