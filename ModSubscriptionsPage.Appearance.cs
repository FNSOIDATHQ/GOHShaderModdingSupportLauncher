using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private static IBrush ReadingBrush(string part) => (IBrush)Application.Current!.Resources["ModReading" + part + "Brush"]!;

    // Keep the surface outside the scroller so it also fills empty and loading readers.
    private static Border ReadingSurface(Control reader) => new()
    {
        Name = reader.Name + "Surface", Background = ReadingBrush("Background"),
        Padding = new Thickness(16), CornerRadius = new CornerRadius(4), Child = reader
    };
}
