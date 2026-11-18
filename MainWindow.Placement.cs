using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private MainWindowPlacement? lastWindowPlacement;
    private bool windowPlacementCapturePending;

    internal void SetRememberWindowPlacement(bool remember) =>
        UpdateInterfacePreferences(preferences => preferences with
        {
            DisableWindowPlacement = !remember,
            WindowPlacement = remember ? preferences.WindowPlacement : null
        });

    public override void Hide()
    {
        CaptureNormalWindowPlacement();
        base.Hide();
    }

    private void RestoreWindowPlacement()
    {
        if (InterfacePreferences.DisableWindowPlacement ||
            InterfacePreferences.WindowPlacement is not { Width: > 0, Height: > 0 } saved) return;

        var savedPosition = new PixelPoint(saved.X, saved.Y);
        var savedScreen = Screens.All.FirstOrDefault(screen => screen.Bounds.Contains(savedPosition));
        var screen = savedScreen ?? Screens.Primary;
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, area.Width / scaling));
        var height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, area.Height / scaling));
        var horizontalRoom = Math.Max(0, area.Width - (int)Math.Ceiling(width * scaling));
        var verticalRoom = Math.Max(0, area.Height - (int)Math.Ceiling(height * scaling));
        int x = savedScreen is null ? area.X + horizontalRoom / 2 : Math.Clamp(saved.X, area.X, area.X + horizontalRoom);
        int y = savedScreen is null ? area.Y + verticalRoom / 2 : Math.Clamp(saved.Y, area.Y, area.Y + verticalRoom);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(x, y);
        Width = width;
        Height = height;
        lastWindowPlacement = new(x, y, width, height, saved.IsMaximized);
        if (saved.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void TrackWindowState()
    {
        if (WindowState == WindowState.Maximized && lastWindowPlacement is { IsMaximized: false } placement)
            lastWindowPlacement = placement with { IsMaximized = true };
        else
            CaptureNormalWindowPlacement();
    }

    private void ScheduleWindowPlacementCapture()
    {
        if (windowPlacementCapturePending) return;
        windowPlacementCapturePending = true;
        // On Windows the move and resize notifications may arrive before the new window state.
        Dispatcher.UIThread.Post(() =>
        {
            windowPlacementCapturePending = false;
            CaptureNormalWindowPlacement();
        });
    }

    private void CaptureNormalWindowPlacement()
    {
        if (!IsVisible || PlatformImpl is null || WindowState != WindowState.Normal ||
            ClientSize is not { Width: > 0, Height: > 0 } size) return;

        var position = Position;
        lastWindowPlacement = new(position.X, position.Y, size.Width, size.Height, false);
    }

    private void SaveWindowPlacement()
    {
        if (InterfacePreferences.DisableWindowPlacement || lastWindowPlacement is not { } placement ||
            placement == InterfacePreferences.WindowPlacement) return;

        UpdateInterfacePreferences(preferences => preferences.DisableWindowPlacement
            ? preferences
            : preferences with { WindowPlacement = placement });
    }
}
