using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

internal static class ModDocumentBlockView
{
    private static IBrush ReadingBrush(string part) => (IBrush)Application.Current!.Resources["ModReading" + part + "Brush"]!;

    internal static Control Render(ModContentBlock block, Func<ModContentBlock, Control> renderChild,
        Func<ModTextRun, double, Control>? renderLink = null)
    {
        if (block.Kind == ModBlockKind.Separator) return new Separator();
        if (block.Kind is ModBlockKind.Paragraph or ModBlockKind.Heading or ModBlockKind.Code)
        {
            var text = DocumentText(block.Runs, renderLink);
            if (block.Kind == ModBlockKind.Heading) { text.FontSize = Math.Max(16, 26 - block.Level * 2); text.FontWeight = FontWeight.SemiBold; text.Margin = new Thickness(0, 8, 0, 2); }
            if (block.Kind == ModBlockKind.Code)
            {
                text.FontFamily = new FontFamily("Consolas,monospace"); text.TextWrapping = TextWrapping.NoWrap;
                return new Border { Padding = new Thickness(10), Background = ReadingBrush("Code"),
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
                    var content = new StackPanel { Spacing = 8 }; content.MinWidth = 60; content.MaxWidth = 280;
                    foreach (var child in cell.Children) content.Children.Add(renderChild(child));
                    if (cell.Level == 1) content.SetValue(TextElement.FontWeightProperty, FontWeight.SemiBold);
                    var border = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(0, 0, 1, 1), BorderBrush = ReadingBrush("Border"), Child = content };
                    Grid.SetColumn(border, x); Grid.SetRow(border, y); table.Children.Add(border);
                }
            }
            return new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        var children = new StackPanel { Spacing = 8 };
        foreach (var child in block.Children) children.Children.Add(renderChild(child));
        if (block.Kind == ModBlockKind.ListItem)
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = block.Marker, FontSize = 14, TextWrapping = TextWrapping.Wrap }); children.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(children, 1); row.Children.Add(children); return row;
        }
        if (block.Kind == ModBlockKind.Quote)
            return new Border { BorderBrush = ReadingBrush("Border"), Background = ReadingBrush("Quote"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8), Child = children };
        return children;
    }

    private static SelectableTextBlock DocumentText(ModTextRun[] runs, Func<ModTextRun, double, Control>? renderLink)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ReadingBrush("Foreground"),
            SelectionBrush = ReadingBrush("Selection"), SelectionForegroundBrush = Brushes.White };
        if (runs.All(run => run.Link is null && !run.Bold && !run.Italic && !run.Code))
        { text.Text = string.Concat(runs.Select(run => run.Text)).Trim(); return text; }
        foreach (var run in runs)
        {
            if (renderLink is not null && run.Link is { } target && ModDocumentParser.IsLink(target))
                text.Inlines!.Add(new InlineUIContainer(renderLink(run, text.FontSize)));
            else
                text.Inlines!.Add(new Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
                    FontFamily = run.Code ? new FontFamily("Consolas,monospace") : text.FontFamily });
        }
        return text;
    }
}
