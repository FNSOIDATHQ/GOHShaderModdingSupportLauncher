using System.Security.Cryptography;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class ManagedModFiles
{
    internal static string PrivateRoot(string game) => Path.Combine(SubscriptionPaths.ModsDirectory(game), ".gohsms");

    // Inspect each existing ancestor too: a safe-looking child can sit behind a junction.
    internal static void EnsureSafe(string path)
    {
        string full = SubscriptionPaths.Normalize(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Managed content cannot pass through a symbolic link or junction.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        foreach (string part in full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar))
        {
            string stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'))
                throw new IOException("Managed content contains a reserved Windows name.");
        }
    }

    internal static void EnsureSpace(string path, long bytes)
    {
        EnsureSafe(path);
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        // Leave room for manifests, atomic metadata writes and filesystem allocation.
        if (new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < checked(bytes + 16 * 1024 * 1024))
            throw new IOException("There is not enough free space for this operation.");
    }

    internal static string[] Files(string directory)
    {
        EnsureSafe(directory);
        if (!Directory.Exists(directory)) return [];
        var files = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureSafe(entry);
            if (Directory.Exists(entry)) files.AddRange(Files(entry));
            else files.Add(entry);
        }
        return files.ToArray();
    }

    internal static OwnedModFile Fingerprint(string directory, string relative)
    {
        relative = SubscriptionPaths.RelativeFile(relative);
        string path = Path.Combine(directory, relative);
        EnsureSafe(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(relative, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    internal static bool Matches(string path, OwnedModFile file)
    {
        EnsureSafe(path);
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length == file.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(stream)) == file.Sha256;
    }

    internal static void RemoveEmptyDirectories(string directory)
    {
        EnsureSafe(directory);
        if (!Directory.Exists(directory)) return;
        foreach (string child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}

internal static class GameOperationLock
{
    // The file stays in place so competing processes cannot lock different files at this path.
    internal static IDisposable Acquire(string game)
    {
        string root = ManagedModFiles.PrivateRoot(game);
        ManagedModFiles.EnsureSafe(root);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "operation.lock");
        ManagedModFiles.EnsureSafe(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
