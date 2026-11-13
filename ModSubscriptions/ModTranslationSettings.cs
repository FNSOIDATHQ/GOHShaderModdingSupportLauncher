using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModTranslationSettings
{
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; }
    // Empty follows the interface language, including after a language change.
    public string Language { get; init; } = "";
    public bool ShowOriginal { get; init; }
    internal static readonly string[] Languages = ["en", "zh-CN", "zh-TW", "ja", "ko", "ru", "de", "fr", "es", "pt", "uk", "pl"];
    internal static bool IsLanguage(string language) => Languages.Contains(language, StringComparer.Ordinal);
}

// Only preferences are persisted. Translations never enter the subscription library/content cache.
internal sealed class ModTranslationOptionsStore(string path)
{
    internal ModTranslationSettings Load()
    {
        if (!File.Exists(path)) return new();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 4096) throw new IOException("Invalid translation preferences.");
        var value = JsonSerializer.Deserialize(stream, ModTranslationJsonContext.Default.ModTranslationSettings);
        if (value is null || value.Version != 1 || value.Language is null || value.Language != "" && !ModTranslationSettings.IsLanguage(value.Language))
            throw new IOException("Invalid translation preferences.");
        return value;
    }

    internal void Save(ModTranslationSettings value)
    {
        if (value.Version != 1 || value.Language is null || value.Language != "" && !ModTranslationSettings.IsLanguage(value.Language))
            throw new ArgumentException("Invalid translation preferences.", nameof(value));
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ModTranslationJsonContext.Default.ModTranslationSettings);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ModTranslationSettings))]
[JsonSerializable(typeof(ModReadingDocument))]
internal partial class ModTranslationJsonContext : JsonSerializerContext { }
