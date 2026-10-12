using System.Reflection;

namespace GOHShaderModdingSupportLauncher;

internal static class AppVersion
{
    // Preserve the project version's formatting without the SDK's appended commit hash.
    public static string Current { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "Unknown";
}
