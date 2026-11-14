using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher;

public sealed record ModAliasRule(string OriginalName, string Alias);

internal sealed record ModAliasSettings
{
    public int Version { get; init; } = 1;
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
}

// Launcher settings, shared by every game. Keys are always original names, never aliases.
internal sealed class ModAliasStore
{
    private const int MaxRules = 10000;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private Dictionary<string, string> aliases = new(StringComparer.Ordinal);
    private readonly string path;
    internal Exception? LoadError { get; }
    internal event Action? Changed;

    internal ModAliasStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes) throw new FormatException(Text("MA_InvalidFile"));
            var settings = JsonSerializer.Deserialize(stream, ModAliasJsonContext.Default.ModAliasSettings);
            if (settings is not { Version: 1, Aliases: not null } || settings.Aliases.Count > MaxRules)
                throw new FormatException(Text("MA_InvalidFile"));
            foreach (var pair in settings.Aliases) Validate(pair.Key, pair.Value);
            aliases = new(settings.Aliases, StringComparer.Ordinal);
        }
        catch (Exception ex) when (IsError(ex))
        {
            LoadError = ex;
            AppDiagnostics.Log("Unable to load Mod aliases; keeping the existing file.", ex);
        }
    }

    internal ModAliasRule[] Rules => aliases.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ModAliasRule(pair.Key, pair.Value)).ToArray();

    internal string? Find(string originalName) => aliases.GetValueOrDefault(originalName);
    internal void Apply(Mod mod) => mod.SetAlias(Find(mod.name));

    // previousName is null when adding a rule; renaming cannot overwrite another rule.
    internal void Save(string? previousName, string originalName, string alias)
    {
        EnsureWritable();
        alias = alias.Trim();
        Validate(originalName, alias);
        if (previousName is not null && !aliases.ContainsKey(previousName))
            throw new FormatException(Text("MA_MissingRule"));
        if (!string.Equals(previousName, originalName, StringComparison.Ordinal) && aliases.ContainsKey(originalName))
            throw new FormatException(Text("MA_Duplicate"));
        var next = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        if (previousName is not null) next.Remove(previousName);
        next[originalName] = alias;
        Persist(next);
    }

    internal void Delete(string originalName)
    {
        EnsureWritable();
        if (!aliases.ContainsKey(originalName)) return;
        var next = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        next.Remove(originalName);
        Persist(next);
    }

    private void EnsureWritable()
    {
        if (LoadError is not null) throw new IOException(Text("MA_LoadError"), LoadError);
    }

    private void Persist(Dictionary<string, string> next)
    {
        if (next.Count > MaxRules) throw new FormatException(Text("MA_InvalidFile"));
        string json = JsonSerializer.Serialize(new ModAliasSettings { Aliases = next }, ModAliasJsonContext.Default.ModAliasSettings);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new FormatException(Text("MA_InvalidFile"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileManager.WriteTextAtomically(path, json, overwrite: true);
        aliases = next;
        Changed?.Invoke();
    }

    private static void Validate(string originalName, string alias)
    {
        if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(alias) ||
            originalName.Length > 1024 || alias.Length > 1024 || originalName.Any(char.IsControl) || alias.Any(char.IsControl))
            throw new FormatException(Text("MA_InvalidRule"));
    }

    internal static bool IsError(Exception ex) => FileManager.IsFileError(ex) || ex is JsonException or FormatException;
    internal static string Text(string key) => LocalizedStrings.Instance.Get(key);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ModAliasSettings))]
internal partial class ModAliasJsonContext : JsonSerializerContext { }
