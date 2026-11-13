using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Markdig;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Display content is independent from file identities, revisions and translated text.
// Concrete records keep both the cache and the subscription library source-generated/AOT safe.
internal enum ModBlockKind { Paragraph, Heading, Image, List, ListItem, Quote, Code, Table, TableRow, TableCell, Separator }

internal sealed record ModTextRun
{
    public string Text { get; init; } = "";
    public Uri? Link { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Code { get; init; }
}

internal sealed record ModContentBlock
{
    public ModBlockKind Kind { get; init; }
    public ModTextRun[] Runs { get; init; } = [];
    public ModContentBlock[] Children { get; init; } = [];
    public ModImage? Image { get; init; }
    public Uri? Link { get; init; }
    public int Level { get; init; }
    public string Marker { get; init; } = "";
}

internal sealed record ModReadingDocument
{
    internal const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public ModContentBlock[] Blocks { get; init; } = [];
}

// Author HTML is converted into a small read-only vocabulary. It never reaches a browser.
internal static partial class ModDocumentParser
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().UseEmphasisExtras().Build();
    private static readonly MarkdownPipeline LegacyPipeline = new MarkdownPipelineBuilder()
        .UseSoftlineBreakAsHardlineBreak().Build();

    internal static ModReadingDocument Markdown(string text, Uri page, Func<HtmlNode, ModImage?> image,
        Func<Uri, bool>? linkAllowed = null) => Html(Markdig.Markdown.ToHtml(text, MarkdownPipeline), page, image, linkAllowed);

    internal static ModReadingDocument Html(string html, Uri page, Func<HtmlNode, ModImage?> image,
        Func<Uri, bool>? linkAllowed = null, Func<string, string>? cleanText = null)
    {
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 256 };
        document.LoadHtml(html);
        return Html(document.DocumentNode, page, image, linkAllowed, cleanText);
    }

    internal static ModReadingDocument Html(HtmlNode root, Uri page, Func<HtmlNode, ModImage?> image,
        Func<Uri, bool>? linkAllowed = null, Func<string, string>? cleanText = null)
    {
        int count = 0;
        string Clean(string value) => (cleanText ?? (text => text))(WebUtility.HtmlDecode(value));
        Uri? Link(HtmlNode node)
        {
            if (!Uri.TryCreate(page, Attribute(node, "href"), out var target) || !IsLink(target) || linkAllowed?.Invoke(target) == false) return null;
            return target;
        }
        List<ModContentBlock> Parse(IEnumerable<HtmlNode> nodes, int depth = 0)
        {
            if (depth > 32) throw new FormatException("The reading document is nested too deeply.");
            var blocks = new List<ModContentBlock>();
            var runs = new List<ModTextRun>();
            void Add(ModContentBlock block)
            {
                if (++count > 10000) throw new FormatException("The reading document has too many blocks.");
                blocks.Add(block);
            }
            void Flush()
            {
                if (runs.Any(run => !string.IsNullOrWhiteSpace(run.Text))) Add(new() { Kind = ModBlockKind.Paragraph, Runs = runs.ToArray() });
                runs.Clear();
            }
            void Visit(HtmlNode node, ModTextRun style)
            {
                if (node.NodeType == HtmlNodeType.Text)
                {
                    string text = WhiteSpace().Replace(Clean(node.InnerText), " ");
                    if (text.Length > 0) runs.Add(style with { Text = text });
                    return;
                }
                if (node.NodeType is not (HtmlNodeType.Element or HtmlNodeType.Document) ||
                    node.Name is "script" or "style" or "iframe" or "object" or "embed" or "form" or "input" or "button" or "svg" or "template" ||
                    Attribute(node, "class").Split(' ').Contains("tooltip")) return;
                if (node.Name == "a") style = style with { Link = Link(node) };
                if (node.Name is "strong" or "b") style = style with { Bold = true };
                if (node.Name is "em" or "i") style = style with { Italic = true };
                if (node.Name == "code") style = style with { Code = true };
                if (node.Name == "br") { runs.Add(style with { Text = "\n" }); return; }
                if (node.Name == "img")
                {
                    if (image(node) is { } media) { Flush(); Add(new() { Kind = ModBlockKind.Image, Image = media, Link = style.Link }); }
                    else if (Attribute(node, "alt") is { Length: > 0 } alt) runs.Add(style with { Text = Clean(alt) });
                    return;
                }
                if (node.Name == "pre")
                {
                    Flush(); Add(new() { Kind = ModBlockKind.Code, Runs = [new() { Text = Clean(node.InnerText).Replace("\r", ""), Code = true }] }); return;
                }
                if (node.Name == "hr") { Flush(); Add(new() { Kind = ModBlockKind.Separator }); return; }
                if (node.Name is "ul" or "ol" or "blockquote" or "table" or "tr" or "td" or "th" or "li")
                {
                    Flush();
                    var children = Parse(node.ChildNodes, depth + 1);
                    var kind = node.Name switch
                    {
                        "ul" or "ol" => ModBlockKind.List, "li" => ModBlockKind.ListItem, "blockquote" => ModBlockKind.Quote,
                        "table" => ModBlockKind.Table, "tr" => ModBlockKind.TableRow, _ => ModBlockKind.TableCell
                    };
                    if (kind == ModBlockKind.List)
                    {
                        int number = int.TryParse(Attribute(node, "start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ? start : 1;
                        for (int i = 0; i < children.Count; i++)
                            if (children[i].Kind == ModBlockKind.ListItem) children[i] = children[i] with { Marker = node.Name == "ol" ? (number++).ToString(CultureInfo.InvariantCulture) + "." : "•" };
                    }
                    Add(new() { Kind = kind, Children = children.ToArray(), Level = node.Name == "th" ? 1 : 0 }); return;
                }
                bool heading = node.Name.Length == 2 && node.Name[0] == 'h' && node.Name[1] is >= '1' and <= '6';
                bool block = heading || node.Name is "p" or "div" or "section" or "article" or "figure" or "figcaption" or "details" or "summary";
                if (block) Flush();
                int first = blocks.Count;
                foreach (var child in node.ChildNodes) Visit(child, style);
                if (block) Flush();
                if (heading)
                    for (int i = first; i < blocks.Count; i++)
                        if (blocks[i].Kind == ModBlockKind.Paragraph) blocks[i] = blocks[i] with { Kind = ModBlockKind.Heading, Level = node.Name[1] - '0' };
            }
            foreach (var node in nodes) Visit(node, new());
            Flush();
            return blocks;
        }
        return new() { Blocks = Parse(root.ChildNodes).ToArray() };
    }

    // Legacy HTML caches contain only flattened text; keep it readable without inventing image positions.
    internal static ModReadingDocument Legacy(string text, Uri page) => Html(Markdig.Markdown.ToHtml(text, LegacyPipeline), page, _ => null);

    internal static IEnumerable<ModImage> Images(ModReadingDocument document) => Walk(document.Blocks)
        .Where(block => block.Image is not null).Select(block => block.Image!);

    internal static IEnumerable<ModContentBlock> Walk(IEnumerable<ModContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            foreach (var child in Walk(block.Children)) yield return child;
        }
    }

    internal static bool IsLink(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
    internal static string Attribute(HtmlNode node, string name) => WebUtility.HtmlDecode(node.GetAttributeValue(name, ""));

    internal static ModImage? Image(HtmlNode node, Uri page, Func<Uri, Uri?> normalize)
    {
        // Prefer real lazy sources to a placeholder; retain src as a fallback. Do not upgrade HTTP by guessing.
        var addresses = new[] { "data-src", "data-original", "data-lazy-src" }.Select(name => Attribute(node, name)).ToList();
        foreach (string name in new[] { "data-srcset", "srcset" })
            addresses.AddRange(Attribute(node, name).Split(',').Select(part => part.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""));
        addresses.Add(Attribute(node, "src"));
        foreach (string address in addresses.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!Uri.TryCreate(page, address, out var resolved) || normalize(resolved) is not { } url) continue;
            Uri original = url;
            var anchor = node.Ancestors("a").FirstOrDefault();
            if (anchor is not null && Uri.TryCreate(page, Attribute(anchor, "href"), out var linked) &&
                Path.GetExtension(linked.AbsolutePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".avif" &&
                normalize(linked) is { } full) original = full;
            return new()
            {
                Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original.AbsoluteUri))),
                ImageUrl = original, ThumbnailUrl = original == url ? null : url,
                Caption = Attribute(node, "alt") is { Length: > 0 } alt ? alt : Attribute(node, "title")
            };
        }
        return null;
    }

    internal static bool HasDocument(ModReadingDocument? document)
    {
        if (document is not { Version: ModReadingDocument.CurrentVersion, Blocks: not null }) return false;
        int count = 0;
        bool Valid(ModContentBlock[] blocks, int depth)
        {
            if (depth > 32) return false;
            foreach (var block in blocks)
            {
                if (++count > 10000 || block is null || !Enum.IsDefined(block.Kind) || block.Runs is null || block.Children is null ||
                    block.Runs.Any(run => run is null || run.Text is null || run.Link is { } link && !IsLink(link)) ||
                    block.Link is { } target && !IsLink(target) ||
                    block.Kind == ModBlockKind.Image && block.Image is null ||
                    block.Image is { } image && (image.Caption is null || image.ImageUrl is null || !IsLink(image.ImageUrl) || image.ThumbnailUrl is { } thumbnail && !IsLink(thumbnail)) ||
                    !Valid(block.Children, depth + 1)) return false;
            }
            return true;
        }
        return Valid(document.Blocks, 0);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex WhiteSpace();
}
