using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            AppDiagnostics.ReportFatal("UI dispatcher", e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // This event runs on the finalizer thread
            // do not call Application.Shutdown here
            AppDiagnostics.Log("Unobserved background task exception", e.Exception);
            e.SetObserved();
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            AppDiagnostics.Log("Starting main window.");
            lifetime.MainWindow = new MainWindow();
            lifetime.Exit += (_, e) => AppDiagnostics.Log($"Application exit: {e.ApplicationExitCode}");
        }
        base.OnFrameworkInitializationCompleted();
    }
}