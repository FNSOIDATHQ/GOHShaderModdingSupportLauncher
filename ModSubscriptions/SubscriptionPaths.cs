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
}
