using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private void AddContextDocument(StackPanel panel, ModReadingDocument document, ReadingContext? context)
    {
        var previous = documentContext;
        documentContext = context;
        try { AddDocument(panel, document); }
        finally { documentContext = previous; }
    }

    private ModReadingDocument ReadingDocument(ModReadingDocument? document, string text, Uri page) =>
        ModDocumentParser.HasDocument(document) ? document! : ModDocumentParser.Legacy(text, page);

    internal void AddDocument(StackPanel panel, ModReadingDocument document)
    {
        document = DisplayDocument(panel, document);
        foreach (var block in document.Blocks) panel.Children.Add(RenderBlock(block));
        if (document.Blocks.Length == 0) panel.Children.Add(Text(T("Empty")));
    }

    private Control RenderBlock(ModContentBlock block)
    {
        var control = RenderBlockContent(block);
        var context = block.Image is { } image ? new ReadingContext("image", image.PageUrl ?? image.ImageUrl) :
            documentContext ?? (details.SelectedIndex == 0 ? DescriptionContext() : FileContext());
        BindReadingContext(control, context);
        if (block.Anchors.Length > 0)
        {
            documentAnchors[control] = (block.Anchors, context);
            control.AttachedToVisualTree += (_, _) => documentAnchors[control] = (block.Anchors, context);
            control.DetachedFromVisualTree += (_, _) => documentAnchors.Remove(control);
        }
        return control;
    }

    private Control RenderBlockContent(ModContentBlock block)
    {
        if (block is { Kind: ModBlockKind.Image, Image: { } image })
        {
            var media = InlineImage(image, embedded: true);
            if (block.Link is { } target && target != image.ImageUrl)
            {
                var link = new HyperlinkButton { Content = T("ImageLink") };
                ToolTip.SetTip(link, target.AbsoluteUri);
                link.Click += async (_, _) => await ExecuteAsync(() => FollowLinkAsync(target));
                media.Children.Add(link);
            }
            return media;
        }
        return ModDocumentBlockView.Render(block, RenderBlock, RenderDocumentLink);
    }

    private Control RenderDocumentLink(ModTextRun run, double fontSize)
    {
        var target = run.Link!;
        var label = Text(run.Text, fontSize);
        label.Foreground = ReadingBrush("Link");
        label.FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal;
        label.FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal;
        if (run.Code) label.FontFamily = new FontFamily("Consolas,monospace");
        var link = new HyperlinkButton { Content = label, Padding = new Thickness(0), MinHeight = 0, MaxWidth = 280 };
        BindReadingContext(link, documentContext ?? (details.SelectedIndex == 0 ? DescriptionContext() : FileContext()));
        ToolTip.SetTip(link, target.AbsoluteUri);
        link.Click += async (_, _) => await ExecuteAsync(() => FollowLinkAsync(target));
        return link;
    }
}
