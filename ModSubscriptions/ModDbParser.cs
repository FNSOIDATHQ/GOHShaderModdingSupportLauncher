using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// All site layout assumptions live here. Missing structure is an error, never an empty snapshot.
internal static partial class ModDbParser
{
    internal const string GamePath = "/games/gates-of-hell";
    internal static readonly Uri Origin = new("https://www.moddb.com");
    internal static ModSourceCapabilities Capabilities => new()
    {
        CanBrowse = true, CanReadArticles = true, CanReadImages = true, CanListFiles = true,
        CanDownloadAnonymously = true, UpdateEvidence = UpdateEvidence.FileIdentityAndRevision
    };

    internal static bool IsSite(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) && uri.Host == Origin.Host;
    internal static bool IsMirrorHost(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) &&
        (uri.Host == "moddb.com" || uri.Host.EndsWith(".moddb.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".dbolical.com", StringComparison.OrdinalIgnoreCase));

    internal static Uri ModPage(Uri page)
    {
        if (!IsSite(page)) throw new ArgumentException("Use an HTTPS ModDB Mod page.", nameof(page));
        var match = ModPath().Match(page.AbsolutePath);
        if (!match.Success) throw new ArgumentException("The address is not a ModDB Mod page.", nameof(page));
        return new(Origin, match.Value);
    }

    internal static Uri RequirePage(Uri page, string section, bool detail = false)
    {
        Uri mod = ModPage(page);
        string path = page.AbsolutePath.TrimEnd('/');
        string prefix = mod.AbsolutePath + "/" + section;
        bool valid = detail ? path.StartsWith(prefix + "/", StringComparison.Ordinal) &&
                             Slug().IsMatch(path[(prefix.Length + 1)..])
                           : path == prefix || (path.StartsWith(prefix + "/page/", StringComparison.Ordinal) &&
                             int.TryParse(path[(prefix.Length + 6)..], out int number) && number > 0);
        if (!valid) throw new ArgumentException("The ModDB content address does not match the requested section.", nameof(page));
        return new UriBuilder(page) { Fragment = "" }.Uri;
    }

    private static HtmlNode Document(string html)
    {
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 256 };
        try { document.LoadHtml(html); }
        catch (Exception ex) when (ex is ArgumentException or ApplicationException)
        { throw ParseError("The ModDB page could not be parsed."); }
        var root = document.DocumentNode;
        if (ModChallengeDetector.IsChallengeDocument(root))
            throw ModChallengeDetector.Required(Origin);
        return root;
    }

    private static HtmlNode ScopedDocument(string html, Uri page, bool gameList = false)
    {
        var root = Document(html);
        var header = root.Descendants("div").FirstOrDefault(n => Class(n, "headerbox"))
            ?? throw ParseError("The ModDB page header is missing.");
        var title = header.Descendants("h2").FirstOrDefault()?.Descendants("a").FirstOrDefault();
        string expected = gameList ? GamePath : ModPage(page).AbsolutePath;
        if (title is null || Page(Attr(title, "href")).AbsolutePath != expected)
            throw ParseError("The ModDB page does not match the requested game or Mod.");
        if (!gameList)
        {
            var game = header.Descendants("h3").FirstOrDefault()?.Descendants("a").FirstOrDefault();
            if (game is null || Page(Attr(game, "href")).AbsolutePath != GamePath)
                throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "This Mod is outside the Gates of Hell game scope.");
        }
        return root;
    }

    internal static ModSourcePage<RemoteMod> Mods(string html, Uri page)
    {
        var root = ScopedDocument(html, page, gameList: true);
        var box = RequiredId(root, "modsbrowse");
        var items = Rows(box, "mods").Select(row =>
        {
            var link = RowLink(row);
            var url = Page(Attr(link, "href"));
            if (url.AbsolutePath != ModPage(url).AbsolutePath) throw ParseError("A Mod list entry has an invalid address.");
            return new RemoteMod
            {
                Id = new("moddb", ImageIdentity(row, "mods")), PageUrl = url, Title = RequiredText(link),
                Summary = Text(row.Descendants("p").FirstOrDefault(n => !Class(n, "filtersearch"))), Capabilities = Capabilities
            };
        }).ToArray();
        Unique(items.Select(item => item.Id.RemoteId));
        return new(items, NextPage(box, page));
    }

    internal static RemoteMod Mod(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var profile = RequiredId(root, "modsinfo");
        var report = profile.Descendants("a").Select(n => Attr(n, "href"))
            .Select(value => ModIdentity().Match(value)).FirstOrDefault(match => match.Success)
            ?? throw ParseError("The stable ModDB Mod ID is missing.");
        var description = RequiredId(root, "profiledescription");
        return new()
        {
            Id = new("moddb", report.Groups[1].Value), PageUrl = ModPage(page),
            Title = RequiredText(root.Descendants("h2").FirstOrDefault()?.Descendants("a").FirstOrDefault()),
            Summary = Readable(description, page), Images = EmbeddedImages(description, page), Capabilities = Capabilities
        };
    }

    internal static ModSourcePage<ModArticle> Articles(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var box = RequiredId(root, "articlesbrowse");
        var items = Rows(box, "articles").Select(row =>
        {
            var link = RowLink(row);
            var url = ArticlePage(Page(Attr(link, "href")), ModPage(page));
            return new ModArticle
            {
                Id = url.AbsolutePath, PageUrl = url, Title = RequiredText(link),
                Body = Text(row.Descendants("p").FirstOrDefault(n => !Class(n, "filtersearch"))), PublishedAt = Time(row)
            };
        }).ToArray();
        Unique(items.Select(item => item.Id));
        return new(items, NextPage(box, page));
    }

    internal static Uri ArticlePage(Uri page, Uri? mod = null)
    {
        mod ??= ModPage(page);
        if (ModPage(page) != mod) throw ParseError("An article belongs to a different Mod.");
        string tail = page.AbsolutePath[(mod.AbsolutePath.Length + 1)..].TrimEnd('/');
        var pieces = tail.Split('/');
        if (pieces.Length != 2 || pieces[0] is not ("articles" or "features" or "news" or "tutorials") ||
            !Slug().IsMatch(pieces[1])) throw ParseError("The article address is invalid.");
        return new(Origin, mod.AbsolutePath + "/" + tail);
    }

    internal static ModArticle Article(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var box = RequiredId(root, "readarticle");
        var body = RequiredId(box, "articlecontent");
        return new()
        {
            Id = page.AbsolutePath, PageUrl = ArticlePage(page),
            Title = RequiredText(box.Descendants().FirstOrDefault(n => Attr(n, "itemprop") == "headline")),
            Body = RequiredTextContent(Readable(body, page)), PublishedAt = Time(box), Images = EmbeddedImages(body, page)
        };
    }

    internal static ModSourcePage<ModImageReference> Images(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var box = root.Descendants().FirstOrDefault(n => n.Id == "imagebox");
        bool empty = root.Descendants("li").Any(n => Class(n, "off") && n.Descendants("a").Any(a =>
            Attr(a, "href") == ModPage(page).AbsolutePath + "/images"));
        if (box is null)
        {
            if (empty || VisibleEmpty(root, "images")) return new([], null);
            throw ParseError("The ModDB image gallery is missing.");
        }
        var items = box.Descendants("a").Where(n => n.Descendants("img").Any()).Select(link =>
        {
            var url = RequirePage(Page(Attr(link, "href")), "images", detail: true);
            if (ModPage(url) != ModPage(page)) throw ParseError("An image belongs to another Mod.");
            return new ModImageReference(url.AbsolutePath, url,
                Media(Attr(link.Descendants("img").First(), "src"), page), Attr(link, "title"));
        }).ToArray();
        if (items.Length == 0 && !empty && !VisibleEmpty(root, "images")) throw ParseError("The image gallery has no readable entries.");
        Unique(items.Select(item => item.Id));
        var next = box.Descendants("a").FirstOrDefault(n => n.Id == "nextmedia");
        Uri? nextPage = next is null ? null : RequirePage(Page(Attr(next, "href")), "images", detail: true);
        if (nextPage is not null && (ModPage(nextPage) != ModPage(page) || nextPage == page))
            throw ParseError("The image gallery has an invalid next page.");
        return new(items, nextPage);
    }

    internal static ModImage Image(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var image = RequiredId(root, "mediaimage");
        var original = root.Descendants("a").FirstOrDefault(n => Class(n, "mediahd"))
            ?? throw ParseError("The original image address is missing.");
        return new()
        {
            Id = new UriBuilder(page) { Fragment = "", Query = "" }.Uri.AbsolutePath,
            ImageUrl = Media(Attr(original, "href"), page), ThumbnailUrl = Media(Attr(image, "src"), page),
            Caption = Attr(image, "alt")
        };
    }

    internal static ModSourcePage<ModFileReference> Files(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var box = RequiredId(root, "downloadsbrowse");
        var items = Rows(box, "files").Select(row =>
        {
            var link = RowLink(row);
            var url = RequirePage(Page(Attr(link, "href")), "downloads", detail: true);
            if (ModPage(url) != ModPage(page)) throw ParseError("A file belongs to another Mod.");
            return new ModFileReference(ImageIdentity(row, "downloads"), url, RequiredText(link),
                Text(row.Descendants("p").FirstOrDefault(n => !Class(n, "filtersearch"))), Time(row));
        }).ToArray();
        Unique(items.Select(item => item.Id));
        return new(items, NextPage(box, page));
    }

    internal static RemoteModFile File(string html, Uri page)
    {
        var root = ScopedDocument(html, page);
        var box = RequiredId(root, "downloadsinfo");
        var start = box.Descendants("a").Select(n => StartPath().Match(Attr(n, "href"))).FirstOrDefault(match => match.Success)
            ?? throw ParseError("The stable download ID is missing.");
        var fields = box.Descendants("div").Where(n => Class(n, "row") && n.Elements("h5").Any())
            .ToDictionary(n => Text(n.Element("h5")), n => n, StringComparer.OrdinalIgnoreCase);
        HtmlNode Field(string name) => fields.GetValueOrDefault(name)?.Descendants("span").FirstOrDefault(n => Class(n, "summary"))
            ?? throw ParseError($"The ModDB file field '{name}' is missing.");
        string name = RequiredText(Field("Filename"));
        var sizeMatch = ByteSize().Match(Text(Field("Size")));
        if (!sizeMatch.Success || !long.TryParse(sizeMatch.Groups[1].Value.Replace(",", ""), out long size) || size < 0)
            throw ParseError("The exact file size is missing.");
        string checksum = Text(Field("MD5 Hash")).ToLowerInvariant();
        if (!Md5().IsMatch(checksum)) throw ParseError("The file checksum is invalid.");
        DateTimeOffset? added = Time(Field("Added"));
        DateTimeOffset? updated = fields.TryGetValue("Updated", out var changed) ? Time(changed) : null;
        if (added is null) throw ParseError("The file upload date is missing.");
        string revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{name}\n{size.ToString(CultureInfo.InvariantCulture)}\n{checksum}\n{updated?.ToString("O", CultureInfo.InvariantCulture)}")));
        var description = RequiredId(root, "downloadsummary");
        var instructions = root.Descendants().FirstOrDefault(n => n.Id == "downloaddescription");
        return new()
        {
            Id = start.Groups[1].Value, Revision = revision, PageUrl = RequirePage(page, "downloads", detail: true),
            Name = name, SizeBytes = size, Md5 = checksum, PublishedAt = added, UpdatedAt = updated,
            Description = (Readable(description, page) + "\n\n" + (instructions is null ? "" : Readable(instructions, page))).Trim()
        };
    }

    internal static ModFileHints FileHints(string xml, Uri modPage)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
            });
            var document = XDocument.Load(reader);
            var channel = document.Root?.Name == "rss" ? document.Root.Element("channel") : null;
            if (channel is null || Page(channel.Element("link")?.Value ?? "").AbsolutePath != modPage.AbsolutePath + "/downloads")
                throw ParseError("The RSS feed does not identify the requested Mod's files.");
            var items = channel.Elements("item").Select(item =>
            {
                var url = RequirePage(Page(item.Element("link")?.Value ?? ""), "downloads", detail: true);
                var id = RssIdentity().Match(item.Element("guid")?.Value ?? "");
                if (!id.Success || ModPage(url) != modPage) throw ParseError("The RSS file identity is missing or outside the requested Mod.");
                return new ModFileHint(id.Groups[1].Value, url, RequiredTextContent(item.Element("title")?.Value ?? ""),
                    Date(item.Element("pubDate")?.Value));
            }).ToArray();
            Unique(items.Select(item => item.Id));
            return new(items);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or FormatException or InvalidOperationException)
        { throw ParseError("The ModDB Files RSS is invalid."); }
    }

    internal static Uri DownloadMirror(string html, string fileId)
    {
        var root = Document(html);
        foreach (var link in root.Descendants("a"))
        {
            string address = Attr(link, "href");
            if (!Uri.TryCreate(Origin, address, out var url) || !IsSite(url)) continue;
            if (url.AbsolutePath.StartsWith("/downloads/mirror/" + fileId + "/", StringComparison.Ordinal)) return url;
        }
        throw ParseError("ModDB did not provide an anonymous mirror for this file.");
    }

    private static IEnumerable<HtmlNode> Rows(HtmlNode box, string noun)
    {
        var rows = box.Descendants("div").Where(n => Class(n, "rowcontent")).ToArray();
        if (rows.Length == 0 && !VisibleEmpty(box, noun)) throw ParseError($"The ModDB {noun} list structure changed.");
        return rows;
    }

    private static bool VisibleEmpty(HtmlNode node, string noun) => node.Descendants("p").Any(n => Class(n, "viewbrowse") &&
        !Hidden().IsMatch(Attr(n, "style")) && Text(n).StartsWith($"No {noun} were found", StringComparison.OrdinalIgnoreCase));

    private static Uri? NextPage(HtmlNode box, Uri page)
    {
        var pagination = box.Descendants("div").FirstOrDefault(n => Class(n, "pagination"));
        if (pagination is null) return null;
        var next = pagination.Descendants("a").FirstOrDefault(n => Class(n, "next") || Attr(n, "rel") == "next");
        if (next is null) return null;
        var url = Page(Attr(next, "href"), keepQuery: true);
        string currentBase = PageNumber().Replace(page.AbsolutePath.TrimEnd('/'), "");
        string nextBase = PageNumber().Replace(url.AbsolutePath.TrimEnd('/'), "");
        if (currentBase != nextBase || url == page) throw ParseError("The ModDB next page changed the search scope.");
        // Filters and sort are the caller's scope; pagination must never silently drop them.
        return new UriBuilder(url) { Query = page.Query.TrimStart('?') }.Uri;
    }

    private static ModImage[] EmbeddedImages(HtmlNode body, Uri page) => body.Descendants("img").Select(image =>
    {
        // Authors also embed images from other public sites. Keep metadata without fetching these URLs here.
        string address = Attr(image, "src");
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(page, address, out var url) || url.Scheme is not ("https" or "http") || url.UserInfo.Length != 0)
            return null;
        return new ModImage { Id = url.AbsoluteUri, ImageUrl = url, Caption = Attr(image, "alt") };
    }).OfType<ModImage>().DistinctBy(image => image.Id).ToArray();

    private static string ImageIdentity(HtmlNode row, string category)
    {
        var image = row.Descendants("img").FirstOrDefault() ?? throw ParseError("The list entry image identity is missing.");
        var match = ImageId().Match(Attr(image, "src"));
        if (!match.Success || match.Groups[1].Value != category) throw ParseError("The stable list entry ID is missing.");
        return match.Groups[2].Value;
    }

    private static HtmlNode RowLink(HtmlNode row) => row.Descendants("h4").FirstOrDefault()?.Descendants("a").FirstOrDefault()
        ?? throw ParseError("A list entry title is missing.");
    private static HtmlNode RequiredId(HtmlNode root, string id) => root.Descendants().FirstOrDefault(n => n.Id == id)
        ?? throw ParseError($"The ModDB '{id}' section is missing.");
    private static bool Class(HtmlNode node, string value) => Attr(node, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal);
    private static string Attr(HtmlNode node, string name) => WebUtility.HtmlDecode(node.GetAttributeValue(name, ""));
    private static string Text(HtmlNode? node) => Spaces().Replace(WebUtility.HtmlDecode(node?.InnerText ?? ""), " ").Trim();
    private static string RequiredText(HtmlNode? node) => RequiredTextContent(Text(node));
    private static string RequiredTextContent(string text) => !string.IsNullOrWhiteSpace(text) ? text : throw ParseError("A required ModDB text field is empty.");
    private static DateTimeOffset? Time(HtmlNode node) => Date((node.Name == "time" ? node : node.Descendants("time").FirstOrDefault())?.GetAttributeValue("datetime", ""));
    private static DateTimeOffset? Date(string? text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    private static void Unique(IEnumerable<string> ids)
    {
        var values = ids.ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw ParseError("ModDB returned duplicate content identities.");
    }

    private static Uri Page(string address, bool keepQuery = false)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(Origin, address, out var url) || !IsSite(url))
            throw ParseError("A ModDB content address is invalid.");
        return new UriBuilder(url) { Path = url.AbsolutePath.TrimEnd('/'), Query = keepQuery ? url.Query.TrimStart('?') : "", Fragment = "" }.Uri;
    }

    private static Uri Media(string address, Uri page)
    {
        if (!Uri.TryCreate(page, address, out var url) || !IsMirrorHost(url)) throw ParseError("A ModDB image address is invalid.");
        return url;
    }

    // Read-only text and ordinary links. Never execute or retain author HTML, scripts, styles or embeds.
    private static string Readable(HtmlNode body, Uri page)
    {
        var text = new StringBuilder();
        void Visit(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text) { text.Append(WebUtility.HtmlDecode(node.InnerText)); return; }
            if (node.NodeType != HtmlNodeType.Element || node.Name is "script" or "style" or "iframe" or "object" or "form" || Class(node, "tooltip")) return;
            if (node.Name is "br" or "hr") { text.AppendLine(); return; }
            bool block = node.Name is "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "pre" or "tr";
            if (block) text.AppendLine();
            bool link = node.Name == "a" && Uri.TryCreate(page, Attr(node, "href"), out var target) &&
                target.Scheme is "http" or "https" && target.UserInfo.Length == 0 && !target.AbsolutePath.StartsWith("/downloads/mirror/", StringComparison.Ordinal);
            if (link) text.Append('[');
            foreach (var child in node.ChildNodes) Visit(child);
            if (link) text.Append("](").Append(new Uri(page, Attr(node, "href")).AbsoluteUri).Append(')');
            if (block) text.AppendLine();
        }
        Visit(body);
        return NewLines().Replace(HorizontalSpace().Replace(text.ToString().Replace("\r", ""), " "), "\n\n").Trim();
    }

    private static ModSourceException ParseError(string message) => AnonymousModHttpClient.Error(ModSourceError.ParseFailed, message);

    [GeneratedRegex(@"^/mods/[a-zA-Z0-9_-]+(?=/|$)")] private static partial Regex ModPath();
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")] private static partial Regex Slug();
    [GeneratedRegex(@"sitearea=mods&siteareaid=(\d+)(?:&|$)")] private static partial Regex ModIdentity();
    [GeneratedRegex(@"/images/(mods|downloads)/\d+/\d+/(\d+)/")] private static partial Regex ImageId();
    [GeneratedRegex(@"^/downloads/start/(\d+)$")] private static partial Regex StartPath();
    [GeneratedRegex(@"\(([\d,]+) bytes\)", RegexOptions.IgnoreCase)] private static partial Regex ByteSize();
    [GeneratedRegex(@"^[a-f0-9]{32}$")] private static partial Regex Md5();
    [GeneratedRegex(@"^downloads(\d+)$")] private static partial Regex RssIdentity();
    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase)] private static partial Regex Hidden();
    [GeneratedRegex(@"/page/\d+$")] private static partial Regex PageNumber();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[^\S\n]+")] private static partial Regex HorizontalSpace();
    [GeneratedRegex(@"\n(?: *\n){2,}")] private static partial Regex NewLines();
}
