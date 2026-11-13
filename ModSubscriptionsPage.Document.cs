using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private ModReadingDocument ReadingDocument(ModReadingDocument? document, string text, Uri page) =>
        ModDocumentParser.HasDocument(document) ? document! : ModDocumentParser.Legacy(text, page);

    internal void AddDocument(StackPanel panel, ModReadingDocument document)
    {
        foreach (var block in document.Blocks) panel.Children.Add(RenderBlock(block));
        if (document.Blocks.Length == 0) panel.Children.Add(Text(T("Empty")));
    }

    private Control RenderBlock(ModContentBlock block)
    {
        if (block is { Kind: ModBlockKind.Image, Image: { } image })
        {
            var media = InlineImage(image, embedded: true);
            if (block.Link is { } target && target != image.ImageUrl)
                media.Children.Add(Action(T("Original"), () => { OpenUrl(target); return Task.CompletedTask; }, independent: true));
            return media;
        }
        if (block.Kind == ModBlockKind.Separator) return new Separator();
        if (block.Kind is ModBlockKind.Paragraph or ModBlockKind.Heading or ModBlockKind.Code)
        {
            var text = DocumentText(block.Runs);
            if (block.Kind == ModBlockKind.Heading) { text.FontSize = Math.Max(16, 26 - block.Level * 2); text.FontWeight = FontWeight.SemiBold; text.Margin = new Thickness(0, 8, 0, 2); }
            if (block.Kind == ModBlockKind.Code)
            {
                text.FontFamily = new FontFamily("Consolas,monospace"); text.TextWrapping = TextWrapping.NoWrap;
                return new Border { Padding = new Thickness(10), Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                    Child = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } };
            }
            return text;
        }
        if (block.Kind == ModBlockKind.Table)
        {
            var rows = block.Children.Where(row => row.Kind == ModBlockKind.TableRow).ToArray();
            int columns = rows.Select(row => row.Children.Length).DefaultIfEmpty().Max();
            var table = new Grid();
            for (int i = 0; i < columns; i++) table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            for (int y = 0; y < rows.Length; y++)
            {
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int x = 0; x < rows[y].Children.Length; x++)
                {
                    var cell = rows[y].Children[x];
                    var content = Column(); content.MinWidth = 60; content.MaxWidth = 280;
                    foreach (var child in cell.Children) content.Children.Add(RenderBlock(child));
                    if (cell.Level == 1) content.SetValue(TextElement.FontWeightProperty, FontWeight.SemiBold);
                    var border = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(0, 0, 1, 1), BorderBrush = Brushes.Gray, Child = content };
                    Grid.SetColumn(border, x); Grid.SetRow(border, y); table.Children.Add(border);
                }
            }
            return new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        var children = Column();
        foreach (var child in block.Children) children.Children.Add(RenderBlock(child));
        if (block.Kind == ModBlockKind.ListItem)
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(Text(block.Marker)); children.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(children, 1); row.Children.Add(children); return row;
        }
        if (block.Kind == ModBlockKind.Quote)
            return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2), Child = children };
        return children;
    }

    private SelectableTextBlock DocumentText(ModTextRun[] runs)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (runs.All(run => run.Link is null && !run.Bold && !run.Italic && !run.Code))
        { text.Text = string.Concat(runs.Select(run => run.Text)).Trim(); return text; }
        foreach (var run in runs)
        {
            if (run.Link is { } target && ModDocumentParser.IsLink(target))
            {
                var label = Text(run.Text); label.FontSize = text.FontSize;
                label.FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal;
                label.FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal;
                if (run.Code) label.FontFamily = new FontFamily("Consolas,monospace");
                var link = new HyperlinkButton { Content = label, Padding = new Thickness(0), MinHeight = 0, MaxWidth = 280 };
                ToolTip.SetTip(link, target.AbsoluteUri);
                link.Click += (_, _) => OpenUrl(target);
                text.Inlines!.Add(new InlineUIContainer(link));
            }
            else
                text.Inlines!.Add(new Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
                    FontFamily = run.Code ? new FontFamily("Consolas,monospace") : text.FontFamily });
        }
        return text;
    }
}
