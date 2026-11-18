using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    private const double MinimumModPreviewWidth = 245;
    private const double MinimumModListAreaWidth = 440;

    private ColumnDefinition ModPreviewColumn => modManagerLayout.ColumnDefinitions[2];

    private double MaximumModPreviewWidth =>
        Math.Max(MinimumModPreviewWidth, Bounds.Width - modPreviewSplitter.Width - MinimumModListAreaWidth);

    private void InitializeLocalPreview()
    {
        localPreview.CollapseRequested += CollapseLocalPreview;
        SizeChanged += (_, _) => UpdateLocalPreviewLayout();
        modPreviewSplitter.DragCompleted += (_, _) => SaveLocalPreviewWidth();
        modPreviewSplitter.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled && e.Key is Key.Left or Key.Right) SaveLocalPreviewWidth();
        }, handledEventsToo: true);
    }

    private void UpdateLocalPreview(bool refresh = false) =>
        localPreview.SetMod(selectedRow.mods is { Count: 1 } selection ? selection[0] : null, refresh);

    private void UpdateLocalPreviewLayout()
    {
        bool open = localPreviewPane.IsVisible;
        var column = ModPreviewColumn;
        column.MinWidth = open ? MinimumModPreviewWidth : 0;
        column.MaxWidth = MaximumModPreviewWidth;
        column.Width = new GridLength(open ? GetLocalPreviewWidth() : 0);
        if (Bounds.Height > 0) localPreview.SetHeight(Bounds.Height);
    }

    private double GetLocalPreviewWidth()
    {
        double preferredWidth = main.InterfacePreferences.ModPreviewWidth ??
            Math.Clamp(Bounds.Width * 0.36, MinimumModPreviewWidth, 440);
        return Math.Clamp(preferredWidth, MinimumModPreviewWidth, MaximumModPreviewWidth);
    }

    private void SaveLocalPreviewWidth()
    {
        double width = ModPreviewColumn.Width.Value;
        if (localPreviewPane.IsVisible && Math.Abs(width - GetLocalPreviewWidth()) >= 0.5)
            main.SetModPreviewWidth(width);
    }

    private void CollapseLocalPreview()
    {
        localPreview.SetActive(false);
        localPreviewPane.IsVisible = modPreviewSplitter.IsVisible = false;
        UpdateLocalPreviewLayout();
        expandModPreview.IsVisible = true;
        expandModPreview.Focus();
    }

    private void ExpandModPreview_Click(object? sender, RoutedEventArgs e)
    {
        localPreviewPane.IsVisible = modPreviewSplitter.IsVisible = true;
        UpdateLocalPreviewLayout();
        expandModPreview.IsVisible = false;
        localPreview.SetActive(true);
        Dispatcher.UIThread.Post(() =>
        {
            if (localPreviewPane.IsEffectivelyVisible) localPreview.FocusCollapse();
        }, DispatcherPriority.Loaded);
    }
}
