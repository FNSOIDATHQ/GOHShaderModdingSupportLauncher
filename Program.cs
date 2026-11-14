using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Win32;

namespace GOHShaderModdingSupportLauncher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool diagnosticMode = Array.Exists(args, arg =>
            arg.Equals("--validate-native-bundle", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--self-test-ui", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--check-moddb", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--check-google-translation", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--check-transmart-translation", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--check-moddb-images", StringComparison.OrdinalIgnoreCase));
        AppDiagnostics.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppDiagnostics.ReportFatal("Unhandled exception", e.ExceptionObject);
        try { return RunApplication(args); }
        catch (Exception ex)
        {
            if (diagnosticMode)
                AppDiagnostics.Log("Launcher diagnostic failed", ex);
            else AppDiagnostics.ReportFatal("Application startup / run", ex);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(string[] args)
    {
        if (Array.Exists(args, arg => arg.Equals("--check-transmart-translation", StringComparison.OrdinalIgnoreCase)))
            return ModSourceDiagnostics.RunTranslationAsync(tranSmart: true).GetAwaiter().GetResult();
        if (Array.Exists(args, arg => arg.Equals("--check-google-translation", StringComparison.OrdinalIgnoreCase)))
            return ModSourceDiagnostics.RunTranslationAsync().GetAwaiter().GetResult();
        if (Array.Exists(args, arg => arg.Equals("--check-moddb", StringComparison.OrdinalIgnoreCase)))
            return ModSourceDiagnostics.RunAsync().GetAwaiter().GetResult();
        NativeBundle.Initialize();
        if (Array.Exists(args, arg => arg.Equals("--validate-native-bundle", StringComparison.OrdinalIgnoreCase)))
            return NativeBundle.IsEmbedded ? 0 : 2;
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (Array.Exists(args, arg => arg.Equals("--software-rendering", StringComparison.OrdinalIgnoreCase)))
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            });
            AppDiagnostics.Log("Software rendering enabled.");
        }
        if (Array.Exists(args, arg => arg.Equals("--self-test-ui", StringComparison.OrdinalIgnoreCase)))
        {
            builder.SetupWithoutStarting();
            return UiSmokeTest.Run();
        }
        if (Array.Exists(args, arg => arg.Equals("--check-moddb-images", StringComparison.OrdinalIgnoreCase)))
        {
            builder.SetupWithoutStarting();
            return ModSourceDiagnostics.RunImagesAsync().GetAwaiter().GetResult();
        }
        return builder.StartWithClassicDesktopLifetime(args);
    }
}
