using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    private void InitializeLocalPreview()
    {
        localPreview.CollapseRequested += CollapseLocalPreview;
        SizeChanged += (_, _) => UpdateLocalPreviewLayout();
    }

    private void UpdateLocalPreview(bool refresh = false) =>
        localPreview.SetMod(selectedRow.mods is { Count: 1 } selection ? selection[0] : null, refresh);

    private void UpdateLocalPreviewLayout()
    {
        if (Bounds.Width > 0)
            modPreviewSplit.OpenPaneLength = Math.Clamp(Bounds.Width * 0.36, 245, 440);
        if (Bounds.Height > 0) localPreview.SetHeight(Bounds.Height);
    }

    private void CollapseLocalPreview()
    {
        localPreview.SetActive(false);
        modPreviewSplit.IsPaneOpen = false;
        expandModPreview.IsVisible = true;
        expandModPreview.Focus();
    }

    private void ExpandModPreview_Click(object? sender, RoutedEventArgs e)
    {
        modPreviewSplit.IsPaneOpen = true;
        expandModPreview.IsVisible = false;
        localPreview.SetActive(true);
        Dispatcher.UIThread.Post(() =>
        {
            if (modPreviewSplit.IsPaneOpen && IsEffectivelyVisible) localPreview.FocusCollapse();
        }, DispatcherPriority.Loaded);
    }
}
