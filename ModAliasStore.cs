using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher;

public enum ModAliasKind { Field = 1, Item = 2 }

public sealed record ModAliasRule(string OriginalName, string Alias)
{
    public ModAliasKind Kind { get; init; } // Missing kind was an exact rule in version 2.
    public string? ItemTitle { get; init; }

    [JsonIgnore] public string KindLabel => ModAliasStore.KindLabel(Kind);
    [JsonIgnore] public string TargetLabel => ItemTitle is null ? OriginalName : $"{ItemTitle} ({OriginalName})";
    [JsonIgnore] internal ModAliasKey Key => new(Kind, OriginalName);
}

internal readonly record struct ModAliasKey(ModAliasKind Kind, string Target);
internal readonly record struct ModAliasMatch(string DisplayName, ModAliasKind? Kind);

internal sealed record ModAliasSettings
{
    public int Version { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ItemMappingEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Aliases { get; init; } = new(StringComparer.Ordinal);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModAliasRule[]? Rules { get; init; }
}

internal sealed class ModAliasStore
{
    private const int MaxRules = 10000;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const ModAliasKind LegacyExact = 0;
    private Dictionary<ModAliasKey, ModAliasRule> rules = [];
    private (int Length, Dictionary<string, string> Aliases)[] fieldRules = [];
    private readonly string path;
    internal Exception? LoadError { get; }
    internal bool ItemMappingEnabled { get; private set; }
    internal event Action? Changed;

    internal ModAliasStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes) throw InvalidFile();
            var settings = JsonSerializer.Deserialize(stream, ModAliasJsonContext.Default.ModAliasSettings);
            var loaded = settings switch
            {
                { Version: 1, Aliases: not null } => settings.Aliases.Select(pair => new ModAliasRule(pair.Key, pair.Value)).ToArray(),
                { Version: 2, Rules: not null } => settings.Rules,
                _ => throw InvalidFile()
            };
            if (loaded.Length > MaxRules) throw InvalidFile();
            var index = new Dictionary<ModAliasKey, ModAliasRule>();
            var seen = new HashSet<ModAliasKey>();
            foreach (var rule in loaded)
            {
                if (rule is null || !seen.Add(rule.Key)) throw InvalidFile();
                bool legacy = rule.Kind == LegacyExact;
                var migrated = legacy ? rule with { Kind = ModAliasKind.Field } : rule;
                Validate(migrated);
                // A saved partial rule wins over a legacy exact rule for the same source.
                if (legacy) index.TryAdd(migrated.Key, migrated);
                else index[migrated.Key] = migrated;
            }
            SetRules(index, settings.Version == 2 && settings.ItemMappingEnabled);
        }
        catch (Exception ex) when (IsError(ex))
        {
            LoadError = ex;
            AppDiagnostics.Log("Unable to load Mod aliases; keeping the existing file.", ex);
        }
    }

    internal ModAliasRule[] Rules => rules.Values.OrderBy(rule => rule.Kind)
        .ThenBy(rule => rule.TargetLabel, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(rule => rule.OriginalName, StringComparer.Ordinal).ToArray();

    internal ModAliasRule? Find(ModAliasKey key) => rules.GetValueOrDefault(key);
    internal string? Find(string originalName) => Find(new ModAliasKey(ModAliasKind.Field, originalName))?.Alias;

    internal ModAliasMatch Match(string originalName, string? itemKey = null)
    {
        if (ItemMappingEnabled && itemKey is not null && Find(new ModAliasKey(ModAliasKind.Item, itemKey)) is { } item)
            return new(item.Alias, ModAliasKind.Item);
        return MatchFields(originalName, fieldRules);
    }

    internal void Apply(Mod mod)
    {
        var match = Match(mod.name, ItemMappingEnabled ? ModAliasItems.ForMod(mod)?.Key : null);
        mod.SetAlias(match.Kind is null ? null : match.DisplayName);
    }

    internal string Preview(string name, ModAliasRule draft)
    {
        if (draft.Kind == ModAliasKind.Item) return ItemMappingEnabled ? draft.Alias : MatchFields(name, fieldRules).DisplayName;
        var fields = rules.Values.Where(rule => rule.Kind == ModAliasKind.Field && rule.Key != draft.Key).Append(draft);
        return MatchFields(name, IndexFields(fields)).DisplayName;
    }

    // Only accepted matches occupy the original title. Iterating left to right within each length
    // also covers overlapping occurrences of the same source and makes rule order irrelevant.
    private static ModAliasMatch MatchFields(string title, (int Length, Dictionary<string, string> Aliases)[] groups)
    {
        List<(int Start, int Length, string Alias)>? selected = null;
        bool[]? occupied = null;
        foreach (var (length, aliases) in groups)
        {
            var lookup = aliases.GetAlternateLookup<ReadOnlySpan<char>>();
            for (int start = 0; start <= title.Length - length; start++)
            {
                if (occupied?[start] == true || !lookup.TryGetValue(title.AsSpan(start, length), out var alias)) continue;
                occupied ??= new bool[title.Length];
                var range = occupied.AsSpan(start, length);
                if (range.Contains(true)) continue;
                range.Fill(true);
                (selected ??= []).Add((start, length, alias));
                start += length - 1;
            }
        }
        if (selected is null) return new(title, null);

        selected.Sort((left, right) => left.Start.CompareTo(right.Start));
        var result = new StringBuilder(title.Length);
        int end = 0;
        foreach (var (start, length, alias) in selected)
        {
            result.Append(title.AsSpan(end, start - end)).Append(alias);
            end = start + length;
        }
        result.Append(title.AsSpan(end));
        return new(result.ToString(), ModAliasKind.Field);
    }

    internal void Save(string originalName, string alias) => Save(new ModAliasRule(originalName, alias) { Kind = ModAliasKind.Field });

    internal void Save(ModAliasRule rule)
    {
        EnsureWritable();
        rule = rule with { Alias = rule.Alias.Trim(), ItemTitle = rule.ItemTitle ?? Find(rule.Key)?.ItemTitle };
        Validate(rule);
        var next = new Dictionary<ModAliasKey, ModAliasRule>(rules) { [rule.Key] = rule };
        Persist(next, ItemMappingEnabled);
    }

    internal void Delete(string originalName) => Delete(new ModAliasKey(ModAliasKind.Field, originalName));

    internal void Delete(ModAliasKey key)
    {
        EnsureWritable();
        if (!rules.ContainsKey(key)) return;
        var next = new Dictionary<ModAliasKey, ModAliasRule>(rules);
        next.Remove(key);
        Persist(next, ItemMappingEnabled);
    }

    internal void SetItemMapping(bool enabled)
    {
        EnsureWritable();
        if (enabled != ItemMappingEnabled) Persist(rules, enabled);
    }

    private void EnsureWritable()
    {
        if (LoadError is not null) throw new IOException(Text("MA_LoadError"), LoadError);
    }

    private void Persist(Dictionary<ModAliasKey, ModAliasRule> next, bool itemMappingEnabled)
    {
        if (next.Count > MaxRules) throw InvalidFile();
        var settings = !itemMappingEnabled && next.Keys.All(key => key.Kind == ModAliasKind.Field)
            ? new ModAliasSettings { Aliases = next.Values.ToDictionary(rule => rule.OriginalName, rule => rule.Alias, StringComparer.Ordinal) }
            : new ModAliasSettings { Version = 2, Aliases = null, Rules = next.Values.ToArray(), ItemMappingEnabled = itemMappingEnabled };
        string json = JsonSerializer.Serialize(settings, ModAliasJsonContext.Default.ModAliasSettings);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw InvalidFile();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileManager.WriteTextAtomically(path, json, overwrite: true);
        SetRules(next, itemMappingEnabled);
        Changed?.Invoke();
    }

    private void SetRules(Dictionary<ModAliasKey, ModAliasRule> next, bool itemMappingEnabled)
    {
        fieldRules = IndexFields(next.Values.Where(rule => rule.Kind == ModAliasKind.Field));
        rules = next;
        ItemMappingEnabled = itemMappingEnabled;
    }

    private static (int Length, Dictionary<string, string> Aliases)[] IndexFields(IEnumerable<ModAliasRule> fields) =>
        fields.GroupBy(rule => rule.OriginalName.Length).OrderByDescending(group => group.Key)
            .Select(group => (group.Key, group.ToDictionary(rule => rule.OriginalName, rule => rule.Alias, StringComparer.Ordinal))).ToArray();

    private static void Validate(ModAliasRule rule)
    {
        if (!ValidText(rule.OriginalName) || !ValidText(rule.Alias) || !Enum.IsDefined(rule.Kind) ||
            (rule.Kind == ModAliasKind.Item && !ModAliasItems.IsKey(rule.OriginalName)) ||
            (rule.ItemTitle is not null && (rule.Kind != ModAliasKind.Item || !ValidText(rule.ItemTitle))))
            throw new FormatException(Text("MA_InvalidRule"));
    }

    internal static bool ValidText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 1024 && !text.Any(char.IsControl);
    private static FormatException InvalidFile() => new(Text("MA_InvalidFile"));
    internal static bool IsError(Exception ex) => FileManager.IsFileError(ex) || ex is JsonException or FormatException;
    internal static string Text(string key) => LocalizedStrings.Instance.Get(key);
    internal static string KindLabel(ModAliasKind kind) => Text(kind == ModAliasKind.Item ? "MA_KindItem" : "MA_KindField");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ModAliasSettings))]
internal partial class ModAliasJsonContext : JsonSerializerContext { }
