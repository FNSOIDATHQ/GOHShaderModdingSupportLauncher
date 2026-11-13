using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private GridSplitter fileSplitter = null!;
    private Button fileNavigationToggle = null!;
    private double fileNavigationWidth = 420;
    private bool updatingFileNavigation;

    private void BuildFileNavigationLayout(WrapPanel actions)
    {
        fileSplitter = new GridSplitter
        {
            Name = "subscriptionFileSplitter", ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, KeyboardIncrement = 16
        };
        ToolTip.SetTip(fileSplitter, T("ResizeFileNavigation"));
        AutomationProperties.SetName(fileSplitter, T("ResizeFileNavigation"));
        Grid.SetRow(fileSplitter, 1); Grid.SetColumn(fileSplitter, 1); fileContent.Children.Add(fileSplitter);
        // Width changes here include both pointer dragging and keyboard resizing.
        fileContent.ColumnDefinitions[0].PropertyChanged += (_, args) =>
        {
            if (!updatingFileNavigation && args.Property == ColumnDefinition.WidthProperty &&
                args.NewValue is GridLength { IsAbsolute: true } width)
                fileNavigationWidth = Math.Clamp(width.Value, 240, 480);
        };
        fileNavigationToggle = new Button { Name = "subscriptionFileNavigationToggle", VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        fileNavigationToggle.Click += (_, _) =>
        {
            if (session is not { File: not null } current) return;
            SaveFileReadingPosition();
            current.FileNavigationOnly = !current.FileNavigationOnly;
            UpdateFileNavigationLayout();
        };
        actions.Children.Insert(0, fileNavigationToggle);
        fileContent.SizeChanged += (_, _) => UpdateFileNavigationLayout();
    }

    private void UpdateFileNavigationLayout()
    {
        if (fileSplitter is null) return;
        double available = fileContent.Bounds.Width;
        bool compact = available is > 0 and < 680;
        bool showNavigation = !compact || session?.FileNavigationOnly == true || session?.File is null;
        bool showReader = !compact || !showNavigation;
        bool restoreNavigation = !fileNavigationScroll.IsVisible && showNavigation;
        bool restoreReader = !fileReadingSurface.IsVisible && showReader;
        bool moveFocus = !showNavigation && fileNavigationScroll.IsKeyboardFocusWithin || !showReader && fileReadingSurface.IsKeyboardFocusWithin || compact && fileSplitter.IsKeyboardFocusWithin;
        if (fileNavigationScroll.IsVisible != showNavigation || fileReadingSurface.IsVisible != showReader) SaveFileReadingPosition();
        updatingFileNavigation = true;
        try
        {
            var navigation = fileContent.ColumnDefinitions[0];
            navigation.MinWidth = 0;
            navigation.MaxWidth = double.PositiveInfinity;
            double maximum = available > 0 ? Math.Clamp(available - 12 - 320, 240, 480) : 480;
            navigation.Width = compact ? new(1, GridUnitType.Star) : new(Math.Clamp(fileNavigationWidth, 240, maximum));
            if (!compact) { navigation.MinWidth = 240; navigation.MaxWidth = maximum; }
            fileContent.ColumnDefinitions[1].Width = new(compact ? 0 : 12);
            fileContent.ColumnDefinitions[2].MinWidth = compact ? 0 : 320;
            fileContent.ColumnDefinitions[2].Width = new(compact ? 0 : 1, compact ? GridUnitType.Pixel : GridUnitType.Star);
        }
        finally { updatingFileNavigation = false; }
        fileSplitter.IsVisible = !compact;
        fileNavigationScroll.IsVisible = showNavigation;
        fileReadingSurface.IsVisible = showReader;
        Grid.SetColumn(fileReadingSurface, compact ? 0 : 2);
        fileNavigationToggle.IsVisible = compact;
        fileNavigationToggle.IsEnabled = session?.File is not null;
        string caption = T(showNavigation ? "ReadSelectedFile" : "ShowFileList");
        fileNavigationToggle.Content = caption;
        AutomationProperties.SetName(fileNavigationToggle, caption);
        if (restoreNavigation && session is { } current) RestorePosition(fileNavigationScroll, current.FileNavigationPosition);
        if (restoreReader && session is { } readingSession) RestoreFilePosition(readingSession.FilePositions.GetValueOrDefault(readingSession.File ?? ""));
        if (moveFocus) fileNavigationToggle.Focus(NavigationMethod.Tab);
    }
}
