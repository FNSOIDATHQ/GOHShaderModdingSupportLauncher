using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Parses the game category and public author content only. File hosting is handled separately.
internal static class MowPortalParser
{
    internal static readonly Uri Origin = new("https://mow-portal.com");
    internal const string GamePath = "/call_to_arms_gates_of_hell/";
    internal static ModSourceCapabilities Capabilities => new()
    {
        CanBrowse = true, CanReadArticles = true, CanReadImages = true,
        CanListFiles = true, CanDownloadAnonymously = true, UpdateEvidence = UpdateEvidence.PageOrLinkChange
    };

    internal static bool IsSite(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) && uri.Host == Origin.Host;
    internal static bool IsImage(Uri uri) => IsSite(uri) && uri.AbsolutePath.StartsWith("/uploads/posts/", StringComparison.Ordinal) &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    internal static Uri ModPage(Uri page)
    {
        if (!IsSite(page) || page.Query.Length != 0 || !page.AbsolutePath.StartsWith(GamePath, StringComparison.Ordinal))
            throw new ArgumentException("Expected a Mow Portal Gates of Hell mod page.", nameof(page));
        string filename = page.AbsolutePath[GamePath.Length..];
        int separator = filename.IndexOf('-');
        if (separator < 1 || filename.Contains('/') || !filename.EndsWith(".html", StringComparison.Ordinal) ||
            !filename[..separator].All(char.IsAsciiDigit))
            throw new ArgumentException("Expected a permanent Mow Portal mod page.", nameof(page));
        return new Uri(Origin, page.AbsolutePath);
    }

    internal static ModSourcePage<RemoteMod> Mods(string html, Uri page)
    {
        if (!IsSite(page) || !IsCategory(page)) throw new ArgumentException("Invalid Mow Portal category page.", nameof(page));
        var document = Document(html);
        var container = document.DocumentNode.SelectSingleNode("//*[@id='content-block']") ?? throw Invalid();
        var heading = document.DocumentNode.SelectSingleNode("//h1[" + Class("sect__title") + "]") ?? throw Invalid();
        if (!Text(heading).Contains("Gates of Hell", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var cards = container.SelectNodes("./article[" + Class("short") + "]");
        if (cards is null) throw Invalid();
        var result = new Dictionary<string, RemoteMod>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            var title = card.SelectSingleNode(".//h1[" + Class("short__title") + "]/a[@href]") ?? throw Invalid();
            if (!Uri.TryCreate(page, title.GetAttributeValue("href", ""), out var link)) throw Invalid();
            // Cross-posted news and general tools can occur in this category. Only game mod pages are included.
            if (!IsSite(link) || !link.AbsolutePath.StartsWith(GamePath, StringComparison.Ordinal)) continue;
            link = ModPage(link);
            var mod = new RemoteMod
            {
                Id = Identity(link), PageUrl = link, Title = Required(Text(title)),
                Summary = Text(card.SelectSingleNode(".//*[" + Class("short__text") + "]")),
                Kind = RemoteProjectKind.Mod, Capabilities = Capabilities
            };
            if (!result.TryAdd(mod.Id.RemoteId, mod)) throw Invalid();
        }
        int number = CategoryNumber(page);
        Uri? next = null;
        foreach (var anchor in document.DocumentNode.SelectNodes("//*[@id='pagination']//a[@href]") ?? EmptyNodes())
        {
            if (!Uri.TryCreate(page, anchor.GetAttributeValue("href", ""), out var candidate) || !IsSite(candidate) || !IsCategory(candidate)) throw Invalid();
            if (CategoryNumber(candidate) == number + 1) next = candidate;
        }
        return new(result.Values.ToArray(), next);
    }

    internal static RemoteMod Mod(string html, Uri page)
    {
        page = ModPage(page);
        var article = Detail(html, page);
        string title = Required(Text(article.SelectSingleNode(".//h1[" + Class("short__title") + "]")));
        string versions = Versions(article);
        string body = Readable(Description(article));
        string install = InstallationNotes(article);
        string summary = string.Join("\n\n", new[] { versions, body, install }.Where(text => text.Length > 0));
        var document = ModDocumentParser.Html(Description(article), page,
            image => ModDocumentParser.Image(image, page, uri => IsImage(uri) ? uri : null) is { } media
                ? media with { Caption = RedactDeliveryLinks(media.Caption) } : null, PublicLink, RedactDeliveryLinks);
        var overviewDocument = new ModReadingDocument { Blocks = [.. ModDocumentParser.Legacy(versions, page).Blocks,
            .. document.Blocks, .. ModDocumentParser.Legacy(install, page).Blocks] };
        var notes = new ModArticle
        {
            Id = "version-notes", PageUrl = new(page.AbsoluteUri + "#version-notes"),
            Title = versions.Length > 0 ? string.Join(" · ", versions.Split('\n')) : title, Body = body, Document = document
        };
        return new()
        {
            Id = Identity(page), PageUrl = page, Title = title, Summary = summary, Document = overviewDocument,
            Capabilities = Capabilities, Kind = RemoteProjectKind.Mod, Articles = [notes], Images = Images(article, page)
        };
    }

    internal static ModFileSnapshot Files(string html, Uri page)
    {
        page = ModPage(page);
        var article = Detail(html, page);
        string title = Required(Text(article.SelectSingleNode(".//h1[" + Class("short__title") + "]")));
        var description = Description(article);
        string authorNotes = string.Join("\n\n", new[] { Versions(article), Readable(description), InstallationNotes(article) }.Where(text => text.Length > 0));
        var downloads = article.SelectSingleNode(".//*[" + Class("page_downloads") + "]");
        // A known detail layout without any links is a valid empty snapshot. Missing author content is not.
        var anchors = (downloads?.SelectNodes(".//a[@href][not(" + Class("source-link") + ")]") ?? EmptyNodes())
            .Concat((description.SelectNodes(".//a[@href]") ?? EmptyNodes()).Where(anchor => IsKnownShare(Absolute(page, anchor.GetAttributeValue("href", "")))));
        var files = new Dictionary<string, RemoteModFile>(StringComparer.Ordinal);
        var linkEvidence = new List<string>();
        foreach (var anchor in anchors)
        {
            Uri? link = Absolute(page, HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", "")));
            if (link is null || !AnonymousModHttpClient.IsPublicHttps(link) || IsSite(link)) continue;
            string stableLink = PublicLink(link) ? link.AbsoluteUri : "unsupported:" + Fingerprint(link.AbsoluteUri);
            string id = Fingerprint(stableLink);
            linkEvidence.Add(stableLink);
            bool available = YandexPublicFileResolver.IsShare(link);
            string name = title + " — " + link.IdnHost;
            if (files.ContainsKey(id)) continue;
            files.Add(id, new()
            {
                Id = id, Revision = Fingerprint(stableLink + "\n" + name),
                PageUrl = new(page.AbsoluteUri + "#download-" + id),
                BrowserUrl = PublicLink(link) ? link : page, Name = name,
                Description = Text(anchor) + "\n\n" + authorNotes + (available ? "" : "\nAnonymous downloading from this host is not supported. Open the original page."),
                CanDownload = available, DownloadUnavailableReason = available ? "" : "Anonymous downloading from this host has not been verified."
            });
        }
        string evidence = title + "\n" + authorNotes + "\n" + string.Join('\n', linkEvidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string revision = Fingerprint(evidence);
        // This is selection evidence, not proof that a hosted file changed. A changed author page
        // must allow the user to choose a fresh download without reusing an old completed task.
        return new(files.Values.Select(file => file with { Revision = Fingerprint(revision + "\n" + file.Revision) }).ToArray()) { PageRevision = revision };
    }

    private static bool PublicLink(Uri link) => AnonymousModHttpClient.IsPublicHttps(link) && link.Fragment.Length == 0 &&
        !YandexPublicFileResolver.IsDownloadHost(link) && link.Host != "cloud-api.yandex.net" &&
        (YandexPublicFileResolver.IsShare(link) || link.Query.Length == 0 || link.Host == "drive.google.com" && link.Query is "?usp=sharing" or "?usp=drive_link");
    private static bool IsKnownShare(Uri? link) => link is not null && AnonymousModHttpClient.IsPublicHttps(link) &&
        (YandexPublicFileResolver.IsShare(link) || link.Host is "modsfire.com" or "drive.google.com" or "www.mediafire.com" or "mega.nz");
    private static ModSourceId Identity(Uri page) => new("mowportal", page.AbsolutePath[GamePath.Length..].Split('-')[0]);
    private static bool IsCategory(Uri page) => page.Query.Length == 0 && page.Fragment.Length == 0 &&
        (page.AbsolutePath == GamePath || page.AbsolutePath.StartsWith(GamePath + "page/", StringComparison.Ordinal) &&
         int.TryParse(page.AbsolutePath[(GamePath.Length + 5)..].TrimEnd('/'), out int number) && number is > 0 and <= 1000);
    private static int CategoryNumber(Uri page) => page.AbsolutePath == GamePath ? 1 : int.Parse(page.AbsolutePath[(GamePath.Length + 5)..].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
    private static HtmlNode Detail(string html, Uri page)
    {
        var document = Document(html);
        var canonical = document.DocumentNode.SelectSingleNode("//link[@rel='canonical']");
        if (canonical is not null && (!Uri.TryCreate(canonical.GetAttributeValue("href", ""), UriKind.Absolute, out var canonicalPage) ||
            Identity(ModPage(canonicalPage)) != Identity(page))) throw Invalid();
        var article = document.DocumentNode.SelectSingleNode("//*[@id='content-block']/article[" + Class("page") + "]") ?? throw Invalid();
        var newsId = article.SelectSingleNode("//*[@data-news-id]")?.GetAttributeValue("data-news-id", "");
        if (newsId is not null && newsId != Identity(page).RemoteId) throw Invalid();
        var category = article.SelectSingleNode(".//*[" + Class("post-cats") + "]//a[@href='https://mow-portal.com" + GamePath + "' or @href='" + GamePath + "']");
        if (category is null) throw Invalid();
        _ = Description(article);
        return article;
    }
    private static HtmlNode Description(HtmlNode article) => article.SelectSingleNode(".//*[" + Class("mod-description__text") + "]") ??
        article.SelectSingleNode(".//*[" + Class("short__desc") + "][not(.//article)]") ?? throw Invalid();
    private static string Versions(HtmlNode article) => string.Join("\n", (article.SelectNodes(".//td") ?? EmptyNodes())
        .Where(node => Text(node).StartsWith("Версия игры:", StringComparison.OrdinalIgnoreCase) || Text(node).StartsWith("Версия мода:", StringComparison.OrdinalIgnoreCase))
        .Select(Text));
    private static string InstallationNotes(HtmlNode article) => string.Join("\n\n", (article.SelectNodes(".//table") ?? EmptyNodes())
        .Where(table => Text(table).Contains("Как установить мод", StringComparison.OrdinalIgnoreCase)).Select(Readable));
    private static ModImage[] Images(HtmlNode article, Uri page)
    {
        var images = new Dictionary<string, ModImage>(StringComparer.Ordinal);
        foreach (var anchor in article.SelectNodes(".//*[" + Class("mod-gallery__thumbs") + "]//a[@href] | .//figure[" + Class("short__img") + "]/a[@href]") ?? EmptyNodes())
        {
            Uri? image = Absolute(page, anchor.GetAttributeValue("href", ""));
            if (image is null || !IsImage(image)) continue;
            var thumbnail = anchor.SelectSingleNode(".//img");
            Uri? small = thumbnail is null ? null : Absolute(page, thumbnail.GetAttributeValue("src", ""));
            if (small is not null && !IsImage(small)) small = null;
            images.TryAdd(image.AbsoluteUri, new()
            {
                Id = image.AbsoluteUri, ImageUrl = image, ThumbnailUrl = small,
                Caption = thumbnail?.GetAttributeValue("alt", "") is { } caption ? RedactDeliveryLinks(HtmlEntity.DeEntitize(caption)) : ""
            });
        }
        return images.Values.ToArray();
    }
    private static HtmlDocument Document(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) throw Invalid();
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 256 };
        try { document.LoadHtml(html); }
        catch (Exception ex) when (ex is ArgumentException or ApplicationException || ex.GetType() == typeof(Exception)) { throw Invalid(); }
        return document;
    }
    private static string Readable(HtmlNode node)
    {
        var builder = new StringBuilder();
        void Visit(HtmlNode current)
        {
            if (current.NodeType == HtmlNodeType.Text) { builder.Append(RedactDeliveryLinks(HtmlEntity.DeEntitize(current.InnerText))); return; }
            if (current.Name is "script" or "style" || current.NodeType == HtmlNodeType.Comment) return;
            if (current.Name == "a" && Uri.TryCreate(Origin, current.GetAttributeValue("href", ""), out var link) && PublicLink(link))
            {
                string label = Text(current).Replace("[", "(").Replace("]", ")");
                builder.Append('[').Append(label.Length > 0 ? label : link.IdnHost).Append("](").Append(link.AbsoluteUri).Append(')');
                return;
            }
            bool block = current.Name is "br" or "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3";
            if (block) builder.Append('\n');
            foreach (var child in current.ChildNodes) Visit(child);
            if (block) builder.Append('\n');
        }
        Visit(node);
        return string.Join('\n', builder.ToString().Split('\n').Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(line => line.Length > 0));
    }
    private static string Text(HtmlNode? node) => node is null ? "" : string.Join(' ', RedactDeliveryLinks(HtmlEntity.DeEntitize(node.InnerText)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string RedactDeliveryLinks(string text) => Regex.Replace(text, "https?://[^\\s<>\\\"']+", match =>
        Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) &&
        (uri.Host is "downloader.disk.yandex.ru" or "cloud-api.yandex.net" || uri.Host.EndsWith(".storage.yandex.net", StringComparison.Ordinal))
            ? "[temporary download link omitted]" : match.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Required(string text) => text.Length > 0 ? text : throw Invalid();
    private static Uri? Absolute(Uri page, string value) => Uri.TryCreate(page, value, out var uri) ? uri : null;
    private static HtmlNodeCollection EmptyNodes() => new(null);
    private static string Class(string name) => "contains(concat(' ',normalize-space(@class),' '),' " + name + " ')";
    private static FormatException Invalid() => new("The Mow Portal game content structure changed.");
    internal static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
