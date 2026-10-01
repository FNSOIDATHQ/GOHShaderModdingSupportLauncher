using GOHShaderModdingSupportLauncher.Properties;

namespace GOHShaderModdingSupportLauncher;

internal static class ShaderGraphicsSettings
{
    internal static string Apply(string text)
    {
        foreach (var (name, value) in new[] { ("preset", "custom"), ("bumpType", "parallax") })
        {
            var (start, end) = FindField(text, name);
            if (start < 0 || end < 0)
                throw new FormatException(string.Format(i18n.L_MissingShaderSetting, name));

            // Search the updated text each time so field order and replacement length do not matter.
            text = text.Remove(start, end - start + 1).Insert(start, "{" + name + " " + value + "}");
        }
        return text;
    }

    private static (int Start, int End) FindField(string text, string name)
    {
        for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            int nameStart = start + 1;
            while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart])) nameStart++;
            int nameEnd = nameStart + name.Length;
            if (nameEnd >= text.Length ||
                !text.AsSpan(nameStart, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            // Require the complete field name, so presetExtra cannot match preset.
            if (!char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != '}') continue;
            return (start, text.IndexOf('}', nameEnd));
        }
        return (-1, -1);
    }
}
