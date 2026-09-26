using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GOHShaderModdingSupportLauncher
{
    internal static class AppDiagnostics
    {
        public static string? LogPath { get; private set; }
        public static void Initialize()
        {
            try
            {
                foreach (var root in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    , Path.GetTempPath()
                })
                {
                    try
                    {
                        var directory = Path.Combine(root, "GOHSMSLauncher", "Logs");
                        Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, "diag.log");
                        File.WriteAllText(path, "GOH Shader Modding Support Launcher startup\n", Encoding.UTF8);
                        LogPath = path;
                        break;
                    }
                    catch { /* Try the fallback directory. */ }
                }

                Log(
                    $"Version: {typeof(AppDiagnostics).Assembly.GetName().Version}; \n" 
                    +$"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; \n"
                    +$"Process: {RuntimeInformation.ProcessArchitecture}; OS architecture: {RuntimeInformation.OSArchitecture}; \n"
                    +$"Culture: {CultureInfo.CurrentUICulture.Name}; Base directory: {AppContext.BaseDirectory} \n"
                    +$"Startup Time: {DateTime.Now:yyyyMMdd-HHmmss} \n"
                    );
            }
            catch { /* Logging should not prevent startup. */ }
        }

        private static readonly object Sync = new();
        public static void Log(string message, Exception? exception = null)
        {
            try
            {
                lock (Sync)
                {
                    if (LogPath != null)
                    {
                        File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}\n{exception}\n", Encoding.UTF8);
                    }
                }
            }
            catch { /* A full/read-only disk must not cause recursive failures. */ }
        }

        private static int reportingFatal;
        public static bool IsFatal => Volatile.Read(ref reportingFatal) != 0;
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int NativeMessageBox(IntPtr owner, string text, string caption, uint type);
        public static void ReportFatal(string stage, object? error)
        {
            Log(stage, error as Exception);

            // only show first fatal error on message box
            if (Interlocked.Exchange(ref reportingFatal, 1) != 0) return;
            try
            {
                var details = error?.ToString() ?? "Unknown error (no exception object).";
                var location = LogPath ?? "Unavailable";
                var message = $"Launcher error\n\n{stage}\n\n{details}\n\nLog: {location}";
                NativeMessageBox(IntPtr.Zero, message, "GOH Shader Modding Support Launcher", 0x10 | 0x10000);
            }
            catch { /* Preserve the original failure even if the native dialog fails. */ }
        }
    }
}
