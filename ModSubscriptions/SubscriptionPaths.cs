namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class SubscriptionPaths
{
    internal static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.Any(char.IsControl))
            throw new ArgumentException("A standard absolute Windows path is required.", nameof(path));

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Reject alternate streams and names that Windows silently aliases.
        string tail = full[Path.GetPathRoot(full)!.Length..];
        if (tail.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("The path contains an ambiguous or invalid name.", nameof(path));
        return full;
    }

    internal static string ModsDirectory(string gameDirectory) => Path.Combine(Normalize(gameDirectory), "mods");

    internal static string ManagedPath(string gameDirectory, string path)
    {
        string full = Normalize(path);
        if (!full.StartsWith(ModsDirectory(gameDirectory) + Path.DirectorySeparatorChar, Comparison))
            throw new ArgumentException("Managed content must be inside this game's mods directory.", nameof(path));
        return full;
    }

    internal static string InstallationDirectory(string gameDirectory, string path)
    {
        string full = ManagedPath(gameDirectory, path);
        if (!string.Equals(Path.GetDirectoryName(full), ModsDirectory(gameDirectory), Comparison) ||
            Path.GetFileName(full).Equals(".gohsms", Comparison))
            throw new ArgumentException("An installation must be a direct mod directory, outside the private cache.", nameof(path));
        return full;
    }

    internal static string RelativeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("An owned file must use a relative path.", nameof(path));
        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
            part.Any(char.IsControl) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("The owned file path is invalid.", nameof(path));
        return normalized;
    }
    internal static string SafeFileName(string value, string fallback, int maxLength = 120)
    {
        string name = new(value.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        name = name.Trim().TrimEnd('.');
        if (name.Length > maxLength) name = name[..maxLength].TrimEnd(' ', '.');
        return name.Length == 0 || name is "." or ".." || name.Equals(".gohsms", StringComparison.OrdinalIgnoreCase) || IsReservedFileName(name) ? fallback : name;
    }

    internal static bool IsReservedFileName(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

}
