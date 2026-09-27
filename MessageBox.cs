using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using GOHShaderModdingSupportLauncher.Properties;

namespace GOHShaderModdingSupportLauncher;

internal enum MessageBoxButton { OK, YesNo }
internal enum MessageBoxImage { Information, Warning, Error }
internal enum MessageBoxResult { None, Yes, No }

internal static class MessageBox
{
    public static void Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.Information)
    {
        var dialog = Create(message, title, buttons, image);
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner?.IsVisible == true) dialog.Show(owner);
        else dialog.Show();
    }

    public static async Task<MessageBoxResult> ConfirmAsync(Window owner, string message, string title)
    {
        var dialog = Create(message, title, MessageBoxButton.YesNo, MessageBoxImage.Information);
        return await dialog.ShowDialog<MessageBoxResult>(owner);
    }

    private static Window Create(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            MinHeight = 160,
            MaxHeight = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 20 };
        content.Children.Add(new ScrollViewer { MaxHeight = 430, Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var chinese = (i18n.Culture ?? System.Globalization.CultureInfo.CurrentUICulture)
            .TwoLetterISOLanguageName == "zh";
        if (buttons == MessageBoxButton.YesNo)
        {
            var yes = new Button { Content = chinese ? "是" : "Yes", MinWidth = 80 };
            var no = new Button { Content = chinese ? "否" : "No", MinWidth = 80 };
            yes.Click += (_, _) => dialog.Close(MessageBoxResult.Yes);
            no.Click += (_, _) => dialog.Close(MessageBoxResult.No);
            actions.Children.Add(yes);
            actions.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = chinese ? "确定" : "OK", MinWidth = 80 };
            ok.Click += (_, _) => dialog.Close();
            actions.Children.Add(ok);
        }
        content.Children.Add(actions);
        dialog.Content = content;
        return dialog;
    }
}
