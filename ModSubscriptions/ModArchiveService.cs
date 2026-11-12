using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModArchiveCandidate(string RootPath, string? DirectoryName, string[] Files, long SizeBytes);
internal sealed record ModArchiveInspection(ModArchiveCandidate[] Candidates, string AuthorInstructions,
    string? ManualInstallReason, bool RequiresRootSelection);
internal sealed record ModArchiveExtraction(string DirectoryPath, string[] OwnedFiles, long SizeBytes);

internal sealed record ModArchiveOptions
{
    internal int MaxEntries { get; init; } = 200_000;
    internal long MaxExpandedBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    internal int MaxCompressionRatio { get; init; } = 1000;
}

// Discovering mod.info identifies a layout, not a complete package or permission to run author scripts.
internal sealed class ModArchiveService
{
    private const int MaxModInfoBytes = 1024 * 1024;
    private readonly ModArchiveOptions options;
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".com", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".scr", ".sh", ".py" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".pdf", ".url", ".html", ".htm", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal ModArchiveService(ModArchiveOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MaxEntries <= 0 || this.options.MaxExpandedBytes <= 0 || this.options.MaxCompressionRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    internal ModArchiveInspection Inspect(string archivePath, RemoteModFile file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManagedModFiles.EnsureSafe(archivePath);
        if (file.DownloadOnly || file.IsSourceSnapshot)
            return new([], file.Description,
                "This source snapshot or tool is download-only. Follow the author's instructions; the launcher does not run builds or generators.", false);
        try
        {
            using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = Open(source);
            var entries = ReadEntries(archive, source.Length, cancellationToken);
            return Describe(entries, file.Description, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new([], file.Description, "This file cannot be installed as a standard Mod: " + ex.Message, false);
        }
    }

    // The caller chooses a root and separately confirms it is a complete standard package.
    // No archive path is ever passed to an extraction helper that chooses filesystem paths itself.
    internal ModArchiveExtraction Extract(string archivePath, ModArchiveCandidate candidate, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManagedModFiles.EnsureSafe(archivePath);
        string staging = SubscriptionPaths.Normalize(stagingDirectory);
        ManagedModFiles.EnsureSafe(staging);
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("The staging directory already exists.");
        using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = Open(source);
        var entries = ReadEntries(archive, source.Length, cancellationToken);
        var inspection = Describe(entries, "", cancellationToken);
        if (inspection.ManualInstallReason is not null) throw new InvalidDataException(inspection.ManualInstallReason);
        var selected = inspection.Candidates.SingleOrDefault(item => item.RootPath.Equals(candidate.RootPath, StringComparison.Ordinal));
        if (selected is null || selected.DirectoryName is null || selected.SizeBytes != candidate.SizeBytes ||
            !selected.Files.SequenceEqual(candidate.Files, StringComparer.Ordinal))
            throw new InvalidDataException("The selected archive layout changed. Inspect and select it again.");
        ManagedModFiles.EnsureSpace(staging, selected.SizeBytes);
        string prefix = selected.RootPath + Path.DirectorySeparatorChar;
        var wanted = entries.Where(item => !item.Entry.IsDirectory && item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var createdFiles = new List<string>();
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            CreateDirectory(staging, staging, createdDirectories);
            // Sequential reads also handle solid RAR/7z archives without repeatedly decompressing the same blocks.
            byte[] buffer = new byte[128 * 1024];
            foreach (var payload in Payloads(archive, entries, wanted.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = EntryPath(payload.Entry);
                using var input = payload.Open();
                if (!wanted.TryGetValue(name, out var indexed))
                {
                    // Drain unselected solid entries with cancellation instead of allowing an uninterruptible skip.
                    long skipped = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        skipped = checked(skipped + read);
                        if (skipped > payload.Entry.Size || skipped > options.MaxExpandedBytes)
                            throw new InvalidDataException("An archive stream exceeds its declared size.");
                    }
                    if (skipped != payload.Entry.Size) throw new InvalidDataException("An archive stream is incomplete.");
                    continue;
                }
                if (payload.Entry.Size != indexed.Entry.Size || !extracted.Add(name))
                    throw new InvalidDataException("The archive directory and file stream do not agree.");
                string relative = name[prefix.Length..];
                string destination = SubscriptionPaths.Normalize(Path.Combine(staging, relative));
                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, SubscriptionPaths.Comparison))
                    throw new InvalidDataException("An archive entry escaped the staging directory.");
                CreateDirectory(Path.GetDirectoryName(destination)!, staging, createdDirectories);
                ManagedModFiles.EnsureSafe(destination);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                createdFiles.Add(destination);
                long bytes = 0;
                uint crc = uint.MaxValue;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bytes = checked(bytes + count);
                    total = checked(total + count);
                    if (bytes > indexed.Entry.Size || total > selected.SizeBytes || total > options.MaxExpandedBytes)
                        throw new InvalidDataException("The extracted data exceeds the advertised archive size.");
                    for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                    output.Write(buffer, 0, count);
                }
                if (bytes != indexed.Entry.Size ||
                    ((archive.Type == ArchiveType.Zip || indexed.Entry.Crc != 0) && (crc ^ uint.MaxValue) != unchecked((uint)indexed.Entry.Crc)))
                    throw new InvalidDataException("An archive entry failed its length or CRC check.");
                output.Flush(flushToDisk: true);
            }
            if (extracted.Count != wanted.Count || total != selected.SizeBytes)
                throw new InvalidDataException("The archive is incomplete.");
            ValidateModInfo(Path.Combine(staging, "mod.info"));
            return new(staging, selected.Files, total);
        }
        catch
        {
            // Remove only files this extraction created. Unexpected additions are deliberately retained.
            foreach (string path in createdFiles.AsEnumerable().Reverse())
                try { ManagedModFiles.EnsureSafe(path); File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (string path in createdDirectories.OrderByDescending(item => item.Length))
                try { ManagedModFiles.EnsureSafe(path); Directory.Delete(path, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static IArchive Open(Stream source)
    {
        var archive = ArchiveFactory.OpenArchive(source, ReaderOptions.ForExternalStream);
        if (archive.Type is not (ArchiveType.Zip or ArchiveType.Rar or ArchiveType.SevenZip))
        { archive.Dispose(); throw new InvalidDataException("Only ZIP, RAR and 7z standard packages are supported."); }
        return archive;
    }

    private sealed record ArchiveItem(string Path, IArchiveEntry Entry);

    private static IEnumerable<(IEntry Entry, Func<Stream> Open)> Payloads(IArchive archive,
        List<ArchiveItem> entries, HashSet<string> wanted)
    {
        if (archive.Type == ArchiveType.SevenZip || archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
                if (!reader.Entry.IsDirectory) yield return (reader.Entry, reader.OpenEntryStream);
        }
        else
            foreach (var item in entries)
                if (!item.Entry.IsDirectory && wanted.Contains(item.Path)) yield return (item.Entry, item.Entry.OpenEntryStream);
    }

    private List<ArchiveItem> ReadEntries(IArchive archive, long compressedBytes, CancellationToken token)
    {
        var entries = new List<ArchiveItem>();
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= options.MaxEntries) throw new InvalidDataException("The archive has too many entries.");
            string path = EntryPath(entry);
            if (!names.TryAdd(path, entry.IsDirectory)) throw new InvalidDataException("The archive has duplicate or case-conflicting names.");
            // SharpCompress reports streamless 7z entries as encrypted; they contain no encrypted payload.
            bool encrypted = entry.IsEncrypted && !(archive.Type == ArchiveType.SevenZip && entry.Size == 0);
            if (encrypted || entry.IsSplitAfter || !entry.IsComplete ||
                entry is SharpCompress.Archives.SevenZip.SevenZipArchiveEntry { IsAnti: true })
                throw new InvalidDataException("Encrypted or incomplete multipart packages require manual installation.");
            int attributes = entry.Attrib ?? 0;
            int unixType = (attributes >> 16) & 0xf000;
            int extendedUnixType = entry is SharpCompress.Common.SevenZip.SevenZipEntry sevenZip ? (sevenZip.ExtendedAttrib ?? 0) & 0xf000 : 0;
            if (entry.LinkTarget is not null || (attributes & (int)FileAttributes.ReparsePoint) != 0 ||
                unixType is not (0 or 0x8000 or 0x4000) || extendedUnixType is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("Archive links, reparse points and special filesystem entries are not allowed.");
            if (entry.Size < 0 || entry.CompressedSize < 0) throw new InvalidDataException("An archive entry has an invalid size.");
            if (!entry.IsDirectory)
            {
                total = checked(total + entry.Size);
                if (total > options.MaxExpandedBytes || total / Math.Max(1, compressedBytes) > options.MaxCompressionRatio)
                    throw new InvalidDataException("The archive exceeds the extraction size or compression ratio limit.");
                if (Path.GetFileName(path).Equals("mod.info", StringComparison.OrdinalIgnoreCase) && entry.Size is <= 0 or > MaxModInfoBytes)
                    throw new InvalidDataException("The Mod metadata file is empty or too large.");
            }
            entries.Add(new(path, entry));
        }
        foreach (string path in names.Keys)
        {
            string? parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (names.TryGetValue(parent, out bool directory) && !directory)
                    throw new InvalidDataException("An archive file conflicts with another entry's parent directory.");
                parent = Path.GetDirectoryName(parent);
            }
        }
        if (!archive.IsComplete) throw new InvalidDataException("The archive is incomplete.");
        return entries;
    }

    private static ModArchiveInspection Describe(List<ArchiveItem> entries, string instructions, CancellationToken token)
    {
        var roots = entries.Where(item => !item.Entry.IsDirectory && Path.GetFileName(item.Path).Equals("mod.info", StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.GetDirectoryName(item.Path) ?? "").ToArray();
        if (roots.Length > 64) throw new InvalidDataException("The package has too many separate Mod roots to inspect safely.");
        var candidates = roots.Select(root =>
        {
            string prefix = root.Length == 0 ? "" : root + Path.DirectorySeparatorChar;
            var files = entries.Where(item => !item.Entry.IsDirectory && item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new ModArchiveCandidate(root, root.Length == 0 ? null : Path.GetFileName(root),
                files.Select(item => item.Path[prefix.Length..]).Order(StringComparer.Ordinal).ToArray(), files.Sum(item => item.Entry.Size));
        }).ToArray();
        string? reason = null;
        if (roots.Length == 0) reason = "No standard Mod root was found. Keep the download and follow the author's instructions.";
        else if (roots.Any(root => root.Length == 0)) reason = "The package has a bare mod.info and no original Mod directory name. Choose manual installation.";
        else if (candidates.Any(item => item.DirectoryName!.Contains(' ') || item.DirectoryName.Equals(".gohsms", StringComparison.OrdinalIgnoreCase)))
            reason = "An original Mod directory name cannot be used by the local Mod scanner. Rename it manually after reviewing dependencies.";
        else if (roots.Any(root => roots.Any(other => root != other && root.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            reason = "Nested Mod roots have an ambiguous installation layout. Choose manual installation.";
        else if (entries.Any(item => !item.Entry.IsDirectory && Executables.Contains(Path.GetExtension(item.Path))))
            reason = "The package contains executable files or installation scripts. Keep it as a download and review the author's instructions.";
        else if (entries.Any(item => !item.Entry.IsDirectory && !roots.Any(root => item.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) && !Documents.Contains(Path.GetExtension(item.Path))))
            reason = "The package contains additional content outside its Mod directories. Review the author's installation instructions.";
        foreach (var info in entries.Where(item => !item.Entry.IsDirectory && Path.GetFileName(item.Path).Equals("mod.info", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            using var stream = info.Entry.OpenEntryStream();
            ValidateModInfo(stream, token);
        }
        return new(candidates, instructions, reason, candidates.Length > 1);
    }

    private static string EntryPath(IEntry entry)
    {
        string key = entry.Key ?? throw new InvalidDataException("The archive contains an unnamed entry.");
        key = key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (entry.IsDirectory) key = key.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            string normalized = SubscriptionPaths.RelativeFile(key);
            if (normalized.Length > 2000 || normalized.Split(Path.DirectorySeparatorChar).Any(part => part.Length > 255 || ReservedName(part)))
                throw new InvalidDataException("The archive contains a reserved or oversized Windows name.");
            return normalized;
        }
        catch (ArgumentException ex) { throw new InvalidDataException("The archive contains an unsafe path.", ex); }
    }

    private static bool ReservedName(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    private static void CreateDirectory(string path, string root, HashSet<string> created)
    {
        ManagedModFiles.EnsureSafe(path);
        if (Directory.Exists(path)) return;
        if (!path.Equals(root, SubscriptionPaths.Comparison)) CreateDirectory(Path.GetDirectoryName(path)!, root, created);
        Directory.CreateDirectory(path);
        created.Add(path);
    }

    private static void ValidateModInfo(string path)
    {
        using var source = File.OpenRead(path);
        ValidateModInfo(source, CancellationToken.None);
    }

    private static void ValidateModInfo(Stream source, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length + count > MaxModInfoBytes) throw new InvalidDataException("The Mod metadata file is too large.");
            bytes.Write(buffer, 0, count);
        }
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        if (text.Contains('\0') || !text.Split('\n').Any(line =>
        {
            string content = line.Split(';')[0];
            int start = content.IndexOf('"');
            return content.Contains("name", StringComparison.OrdinalIgnoreCase) && content.Contains('{') && start >= 0 && content.LastIndexOf('"') > start + 1;
        })) throw new InvalidDataException("mod.info is not readable Mod metadata with a display name.");
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320 ^ (value >> 1);
            table[i] = value;
        }
        return table;
    }
}
