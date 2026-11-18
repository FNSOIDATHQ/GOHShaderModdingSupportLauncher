#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

internal sealed class ModInspectionDialog : Window
{
    private readonly MainWindow owner;
    private readonly string? directory;
    private Mod? mod;
    private readonly SelectableTextBlock description = new() { Name = "modInspectionDescription", TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock help = new() { Name = "modInspectionHelp", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
    private readonly Button folder = new() { Name = "inspectionOpenFolder" };
    private readonly Button refresh = new() { Name = "inspectionRefresh" };
    private readonly Button close = new() { Name = "inspectionClose", IsCancel = true, IsDefault = true };
    private readonly CancellationTokenSource lifetime = new();
    private bool busy, closed;

    internal ModInspectionDialog(MainWindow owner, Mod? mod)
    {
        this.owner = owner;
        this.mod = mod;
        directory = mod?.path;
        Width = 700; Height = 650; MinWidth = 360; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { folder, refresh, close } };
        var reader = new StackPanel { Spacing = 18, Children = { description, help } };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new("*,Auto,Auto"), RowSpacing = 10 };
        grid.Children.Add(new ScrollViewer { Content = reader, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(error, 1); grid.Children.Add(error);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
        folder.Click += (_, _) => OpenFolder();
        refresh.Click += async (_, _) => await RefreshAsync();
        close.Click += (_, _) => Close();
        Opened += (_, _) => close.Focus(NavigationMethod.Tab);
        Closed += (_, _) => { closed = true; lifetime.Cancel(); LocalizedStrings.Instance.PropertyChanged -= LanguageChanged; };
        LocalizedStrings.Instance.PropertyChanged += LanguageChanged;
        Render();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LocalizedStrings.MI_Features)) Render(); }

    private void Render()
    {
        Title = ModInspectionPresentation.Text("HelpTitle");
        description.IsVisible = directory is not null;
        description.Text = ModInspectionPresentation.Details(mod);
        help.Text = string.Join("\n\n", new[] { "HelpRegular", "HelpDll", "HelpShader", "HelpMixed", "HelpUnknown", "HelpDisable", "HelpLimits" }
            .Select(ModInspectionPresentation.Text));
        folder.Content = ModInspectionPresentation.Text("OpenFolder");
        refresh.Content = ModInspectionPresentation.Text(busy ? "Checking" : "Recheck");
        close.Content = ModInspectionPresentation.Text("Close");
        folder.IsVisible = refresh.IsVisible = directory is not null;
        folder.IsEnabled = directory is not null && Directory.Exists(directory);
        refresh.IsEnabled = !busy;
        AutomationProperties.SetName(description, ModInspectionPresentation.Text("Details"));
    }

    private void OpenFolder()
    {
        if (directory is null) return;
        try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception ex) when (FileManager.IsFileError(ex) || ex is Win32Exception)
        { error.Text = ModInspectionPresentation.Text("OpenFailed") + "\n" + ex.Message; }
    }

    internal async Task RefreshAsync()
    {
        if (busy || directory is null) return;
        busy = true; error.Text = ""; Render();
        try
        {
            bool applied = await owner.CheckModsAsync(() =>
                owner.FindInspectedLocalMod(directory) is { } candidate ? [candidate] : [],
                lifetime.Token, force: true, protectOptions: false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (closed) return;
                mod = owner.FindInspectedLocalMod(directory);
                if (!applied || mod is null) error.Text = ModInspectionPresentation.Text("SnapshotUnavailable");
            });
        }
        catch (Exception ex) when (FileManager.IsFileError(ex))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            { if (!closed) { mod = null; error.Text = ModInspectionPresentation.Text("CheckFailed") + "\n" + ex.Message; } });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { busy = false; if (!closed) Render(); });
        }
    }
}
#endif
