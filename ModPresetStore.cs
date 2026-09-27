using System.Collections;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace GOHShaderModdingSupportLauncher;

internal sealed record ModPresetEntry(string Id, string Name);
internal sealed record ModPresetMatch(List<Mod> Mods, List<ModPresetEntry> Missing);

internal sealed class ModPresetStore(string directory)
{
    internal static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "presets");

    internal string[] List()
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".toml", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    internal static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length > 240)
            throw new FormatException(PresetText.Get("P_InvalidName"));
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             "123456789¹²³".Contains(stem[3])))
            throw new FormatException(PresetText.Get("P_InvalidName"));
        return name;
    }

    // Names in the API are extensionless (including names obtained from List).
    internal string PathFor(string name) => Path.Combine(directory, ValidateName(name) + ".toml");
    internal bool Exists(string name) => File.Exists(PathFor(name));

    internal void Save(string name, IEnumerable<Mod> mods, bool overwrite = false)
    {
        var path = PathFor(name);
        var entries = mods.Select(mod => new ModPresetEntry(mod.folderName, mod.name)).ToArray();
        ValidateEntries(entries);
        var array = new TomlTableArray();
        foreach (var entry in entries)
            array.Add(new TomlTable { ["id"] = entry.Id, ["name"] = entry.Name });
        var table = new TomlTable { ["version"] = 1L };
        table["mods"] = entries.Length == 0 ? new TomlArray() : array;
        var text = TomlSerializer.Serialize(table, PresetTomlContext.Default.TomlTable);
        Directory.CreateDirectory(directory);
        FileManager.WriteTextAtomically(path, text, overwrite);
    }

    internal ModPresetEntry[] Read(string name)
    {
        TomlTable? table;
        try
        {
            table = TomlSerializer.Deserialize(File.ReadAllText(PathFor(name), new UTF8Encoding(false, true)), PresetTomlContext.Default.TomlTable);
        }
        catch (TomlException ex) { throw new FormatException(PresetText.Get("P_InvalidToml"), ex); }
        catch (DecoderFallbackException ex) { throw new FormatException(PresetText.Get("P_InvalidToml"), ex); }
        if (table is null || !table.TryGetValue("version", out var version) || version is not long number || number != 1)
            throw new FormatException(PresetText.Get("P_InvalidVersion"));
        if (!table.TryGetValue("mods", out var value) || value is not (TomlArray or TomlTableArray))
            throw new FormatException(PresetText.Get("P_InvalidEntries"));
        var entries = new List<ModPresetEntry>();
        foreach (var item in (IEnumerable)value)
        {
            if (item is not TomlTable entry || !entry.TryGetValue("id", out var id) || id is not string idText ||
                !entry.TryGetValue("name", out var display) || display is not string displayText)
                throw new FormatException(PresetText.Get("P_InvalidEntries"));
            entries.Add(new(idText, displayText));
        }
        ValidateEntries(entries);
        return entries.ToArray();
    }

    private static void ValidateEntries(IEnumerable<ModPresetEntry> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Name is null || !ids.Add(entry.Id))
                throw new FormatException(PresetText.Get("P_InvalidEntries"));
    }

    internal static ModPresetMatch Match(IEnumerable<ModPresetEntry> entries, IReadOnlyDictionary<string, Mod> available)
    {
        var result = new ModPresetMatch([], []);
        foreach (var entry in entries)
            if (available.TryGetValue(entry.Id, out var mod)) result.Mods.Add(mod);
            else result.Missing.Add(entry);
        return result;
    }

    internal void Rename(string name, string newName)
    {
        var source = PathFor(name);
        var destination = PathFor(newName);
        if (string.Equals(name, newName, StringComparison.Ordinal)) return;
        if (File.Exists(destination)) throw new IOException(PresetText.Get("P_NameExists"));
        File.Move(source, destination);
    }

    internal void Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) throw new FileNotFoundException(PresetText.Get("P_FileMissing"), path);
        File.Delete(path);
    }
}

internal static class PresetText
{
    internal static string Get(string key) => Properties.i18n.ResourceManager.GetString(key, Properties.i18n.Culture) ?? key;
}

[TomlSerializable(typeof(TomlTable))]
internal partial class PresetTomlContext : TomlSerializerContext { }
