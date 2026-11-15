using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher;

internal sealed record UiPreferences
{
    public int Version { get; init; } = 1;
    public bool NavigationCollapsed { get; init; }
    public bool ModListCollapsed { get; init; }
    public bool HideDonationBanner { get; init; }
}

// Interface preferences belong to the launcher, independently of games and subscription data.
internal sealed class UiPreferencesStore(string path)
{
    internal UiPreferences Load()
    {
        if (!File.Exists(path)) return new();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 4096) throw new IOException("Invalid interface preferences.");
        var value = JsonSerializer.Deserialize(stream, UiPreferencesJsonContext.Default.UiPreferences);
        if (value is null || value.Version != 1) throw new IOException("Unsupported interface preferences.");
        return value;
    }

    internal void Save(UiPreferences value)
    {
        if (value.Version != 1) throw new ArgumentException("Unsupported interface preferences.", nameof(value));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileManager.WriteTextAtomically(path, JsonSerializer.Serialize(value, UiPreferencesJsonContext.Default.UiPreferences), overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiPreferences))]
internal partial class UiPreferencesJsonContext : JsonSerializerContext { }
