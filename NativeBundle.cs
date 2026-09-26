using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GOHShaderModdingSupportLauncher;

/// <summary>
/// Loads the native libraries appended by Publish-Aot.ps1 before Avalonia starts.
/// </summary>
internal static partial class NativeBundle
{
    private static readonly byte[] Magic = "GOHSMSN1"u8.ToArray();
    private const int FooterLength = 48;
    private const long MaximumArchiveLength = 256L * 1024 * 1024;
    private const long MaximumLibraryLength = 128L * 1024 * 1024;
    private static readonly List<nint> LoadedLibraries = new();
    private static nint dllDirectoryCookie;

    internal static bool IsEmbedded { get; private set; }
    internal static string? CacheDirectory { get; private set; }

    public static void Initialize()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < FooterLength) return;

        file.Seek(-FooterLength, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[FooterLength];
        file.ReadExactly(footer);
        if (!footer[..8].SequenceEqual(Magic)) return;
        IsEmbedded = true;

        long archiveLength = BinaryPrimitives.ReadInt64LittleEndian(footer.Slice(8, 8));
        if (archiveLength <= 0 || archiveLength > MaximumArchiveLength || archiveLength > file.Length - FooterLength)
            throw new InvalidDataException("Native bundle footer contains an invalid archive length.");

        var archiveBytes = new byte[checked((int)archiveLength)];
        file.Seek(-FooterLength - archiveLength, SeekOrigin.End);
        file.ReadExactly(archiveBytes);
        var archiveHash = SHA256.HashData(archiveBytes);
        if (!archiveHash.AsSpan().SequenceEqual(footer[16..]))
            throw new InvalidDataException("Native bundle archive checksum mismatch.");

        using var archiveStream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        if (entries.Count != archive.Entries.Count || !entries.TryGetValue("manifest.txt", out var manifest))
            throw new InvalidDataException("Native bundle has duplicate entries or no manifest.");
        if (manifest.Length > 16 * 1024)
            throw new InvalidDataException("Native bundle manifest is too large.");

        string manifestText;
        using (var reader = new StreamReader(manifest.Open(), new UTF8Encoding(false, true)))
            manifestText = reader.ReadToEnd();
        var libraries = new List<(string Name, byte[] Hash, ZipArchiveEntry Entry)>();
        long totalLength = 0;
        foreach (var line in manifestText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = line.TrimEnd('\r');
            var separator = item.IndexOf(' ');
            if (separator != 64)
                throw new InvalidDataException("Native bundle manifest has an invalid checksum.");
            byte[] expectedHash;
            try { expectedHash = Convert.FromHexString(item[..separator]); }
            catch (FormatException ex) { throw new InvalidDataException("Native bundle manifest has an invalid checksum.", ex); }
            var name = item[(separator + 1)..];
            if (name.Length == 0 || name != Path.GetFileName(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Equals("manifest.txt", StringComparison.OrdinalIgnoreCase) ||
                !entries.TryGetValue(name, out var entry) || entry.FullName != name ||
                libraries.Any(library => library.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Native bundle manifest contains an invalid or duplicate library name.");
            if (entry.Length <= 0 || entry.Length > MaximumLibraryLength)
                throw new InvalidDataException($"Native library has an invalid size: {name}");
            totalLength += entry.Length;
            if (totalLength > MaximumArchiveLength)
                throw new InvalidDataException("Native bundle expands beyond the allowed size.");
            libraries.Add((name, expectedHash, entry));
        }
        if (libraries.Count == 0 || libraries.Count + 1 != entries.Count)
            throw new InvalidDataException("Native bundle contains unlisted entries.");

        var hashName = Convert.ToHexStringLower(archiveHash);
        var cacheDirectory = CreateCacheDirectory(hashName);
        foreach (var library in libraries)
        {
            var target = Path.Combine(cacheDirectory, library.Name);
            if (File.Exists(target) && FileHashMatches(target, library.Hash)) continue;
            AppDiagnostics.Log($"Restoring native library: {library.Name}");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = library.Entry.Open())
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(destination);
                if (!FileHashMatches(temporary, library.Hash))
                    throw new InvalidDataException($"Native library checksum mismatch: {library.Name}");
                try { File.Move(temporary, target, overwrite: true); }
                catch (IOException) when (File.Exists(target) && FileHashMatches(target, library.Hash))
                {
                    // Another launcher instance installed the same verified DLL first.
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        CacheDirectory = cacheDirectory;
        if (!SetDefaultDllDirectories(0x00001000))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Unable to configure native DLL search directories.");
        dllDirectoryCookie = AddDllDirectory(cacheDirectory);
        if (dllDirectoryCookie == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Unable to add native library cache to DLL search path.");
        foreach (var library in libraries)
            LoadedLibraries.Add(NativeLibrary.Load(Path.Combine(cacheDirectory, library.Name)));
        AppDiagnostics.Log($"Native bundle loaded from {cacheDirectory}; {libraries.Count} libraries.");
    }

    private static string CreateCacheDirectory(string hashName)
    {
        Exception? lastFailure = null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath() })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var directory = Path.Combine(root, "GOHSMSLauncher", "Native", hashName);
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                AppDiagnostics.Log($"Cannot create native library cache under {root}", ex);
            }
        }
        throw new IOException("Unable to create a native library cache directory.", lastFailure);
    }

    private static bool FileHashMatches(string path, byte[] hash)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return SHA256.HashData(stream).AsSpan().SequenceEqual(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "AddDllDirectory", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint AddDllDirectory(string newDirectory);
}
