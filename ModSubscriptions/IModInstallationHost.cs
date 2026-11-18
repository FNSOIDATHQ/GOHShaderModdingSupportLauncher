using System.ComponentModel;
using System.Diagnostics;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Filesystem services call this bridge after their durable transaction is committed.
internal interface IModInstallationHost
{
    bool IsGameRunning(string gameDirectory);
    void RemoveEnabledMods(string gameDirectory, IReadOnlyList<string> directories);
    void RefreshMods(string gameDirectory);
    void RefreshMods(string gameDirectory, IReadOnlyList<string> directories) => RefreshMods(gameDirectory);
}

internal sealed record ModGameProcess(string? ExecutablePath);

internal static class ModGameProcessGuard
{
    internal static bool IsGameRunning(string gameDirectory) => IsGameRunning(gameDirectory, Inspect);

    // An unreadable candidate is treated as running, rather than risking a live replacement.
    internal static bool IsGameRunning(string gameDirectory, Func<string, IReadOnlyList<ModGameProcess>> inspect)
    {
        string root = SubscriptionPaths.Normalize(gameDirectory) + Path.DirectorySeparatorChar;
        try
        {
            foreach (string name in new[] { "call_to_arms", "call_to_arms_ed" })
                foreach (var process in inspect(name))
                {
                    if (process.ExecutablePath is null) return true;
                    if (SubscriptionPaths.Normalize(process.ExecutablePath).StartsWith(root, SubscriptionPaths.Comparison))
                        return true;
                }
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            return true;
        }
    }

    private static IReadOnlyList<ModGameProcess> Inspect(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var result = new List<ModGameProcess>();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited) result.Add(new(process.MainModule?.FileName));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                {
                    // A process can exit between enumeration and reading its executable.
                    try { if (process.HasExited) continue; }
                    catch (Exception gone) when (gone is Win32Exception or InvalidOperationException or NotSupportedException) { }
                    result.Add(new(null));
                }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return result;
    }
}
