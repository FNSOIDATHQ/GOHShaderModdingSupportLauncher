using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModArchiveCandidate(string RootPath, string DirectoryName, string[] Files, long SizeBytes,
    bool PreserveAsFile = false);
internal sealed record ModArchiveInspection(ModArchiveCandidate[] Candidates, string AuthorInstructions,
    string? ManualInstallReason, bool RequiresRootSelection);
internal sealed record ModArchiveExtraction(string DirectoryPath, string[] OwnedFiles, long SizeBytes);

internal sealed record ModArchiveOptions
{
    internal int MaxEntries { get; init; } = 200_000;
    internal long MaxExpandedBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    internal int MaxCompressionRatio { get; init; } = 1000;
}

// Placement only: metadata helps choose a directory, never authorizes execution or activation.
internal sealed class ModArchiveService
{
    private readonly ModArchiveOptions options;
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
        try
        {
            using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!IsSupportedArchive(source, file.Name))
            {
                if (source.Length > options.MaxExpandedBytes) throw new InvalidDataException("The file exceeds the placement size limit.");
                string name = SubscriptionPaths.SafeFileName(file.Name, "download.bin", 240);
                return new([new("", SuggestedDirectory(name), [name], source.Length, true)], file.Description, null, false);
            }
            using var archive = Open(source);
            var entries = ReadEntries(archive, source.Length, cancellationToken);
            return Describe(entries, file.Description, file.Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new([], file.Description, "The file could not be prepared for placement: " + ex.Message, false);
        }
    }

    // The caller reviews the placement target; no author program is executed.
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
        if (candidate.PreserveAsFile) return CopyFile(source, candidate, staging, cancellationToken);
        using var archive = Open(source);
        var entries = ReadEntries(archive, source.Length, cancellationToken);
        var inspection = Describe(entries, "", candidate.DirectoryName);
        if (inspection.ManualInstallReason is not null) throw new InvalidDataException(inspection.ManualInstallReason);
        var selected = inspection.Candidates.SingleOrDefault(item => item.RootPath.Equals(candidate.RootPath, StringComparison.Ordinal));
        if (selected is null || selected.SizeBytes != candidate.SizeBytes ||
            !selected.Files.SequenceEqual(candidate.Files, StringComparer.Ordinal))
            throw new InvalidDataException("The selected archive layout changed. Inspect and select it again.");
        ManagedModFiles.EnsureSpace(staging, selected.SizeBytes);
        string prefix = selected.RootPath.Length == 0 ? "" : selected.RootPath + Path.DirectorySeparatorChar;
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
        { archive.Dispose(); throw new InvalidDataException("This archive format cannot be unpacked."); }
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

    private static ModArchiveInspection Describe(List<ArchiveItem> entries, string instructions, string packageName)
    {
        var payload = entries.Where(item => !item.Entry.IsDirectory).ToArray();
        if (payload.Length == 0) throw new InvalidDataException("The archive contains no files.");
        var roots = entries.Where(item => !item.Entry.IsDirectory && Path.GetFileName(item.Path).Equals("mod.info", StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.GetDirectoryName(item.Path) ?? "").ToArray();
        if (roots.Length > 64) throw new InvalidDataException("The package has too many separate Mod roots to inspect safely.");
        bool nested = roots.Any(root => roots.Any(other => root != other &&
            (other.Length == 0 || root.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))));
        bool outside = payload.Any(item => !roots.Any(root => root.Length == 0 || item.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
            !Documents.Contains(Path.GetExtension(item.Path)));
        // Preserve mixed/nested packages as a whole instead of dropping tools or guessing patch targets.
        if (roots.Length == 0 || nested || outside)
        {
            string first = payload[0].Path.Split(Path.DirectorySeparatorChar)[0];
            roots = payload.All(item => item.Path.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ? [first] : [""];
        }
        var candidates = roots.Select(root =>
        {
            string prefix = root.Length == 0 ? "" : root + Path.DirectorySeparatorChar;
            var files = entries.Where(item => !item.Entry.IsDirectory && item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new ModArchiveCandidate(root, root.Length == 0 ? SuggestedDirectory(packageName) : SubscriptionPaths.SafeFileName(Path.GetFileName(root), SuggestedDirectory(packageName)),
                files.Select(item => item.Path[prefix.Length..]).Order(StringComparer.Ordinal).ToArray(), files.Sum(item => item.Entry.Size));
        }).ToArray();
        return new(candidates, instructions, null, candidates.Length > 1);
    }

    private static string SuggestedDirectory(string name) => SubscriptionPaths.SafeFileName(Path.GetFileNameWithoutExtension(SubscriptionPaths.SafeFileName(name, "download")), "download");

    private static bool IsSupportedArchive(FileStream source, string name)
    {
        Span<byte> signature = stackalloc byte[8];
        int length = source.Read(signature); source.Position = 0;
        bool known = length >= 4 && (signature[..4].SequenceEqual("PK\u0003\u0004"u8) ||
            signature[..4].SequenceEqual("PK\u0005\u0006"u8) || signature[..4].SequenceEqual("PK\u0007\u0008"u8)) ||
            length >= 6 && signature[..6].SequenceEqual("Rar!\u001a\u0007"u8) ||
            length >= 6 && signature[..6].SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c });
        if (!known && new[] { ".zip", ".rar", ".7z" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The advertised archive header is damaged or missing.");
        return known;
    }

    private ModArchiveExtraction CopyFile(FileStream source, ModArchiveCandidate candidate, string staging, CancellationToken token)
    {
        if (candidate.RootPath.Length != 0 || candidate.Files.Length != 1 ||
            candidate.Files[0] != SubscriptionPaths.SafeFileName(candidate.Files[0], "", 240) || candidate.Files[0].Length == 0 ||
            source.Length != candidate.SizeBytes || source.Length > options.MaxExpandedBytes || IsSupportedArchive(source, candidate.Files[0]))
            throw new InvalidDataException("The selected file changed. Check it again.");
        ManagedModFiles.EnsureSpace(staging, source.Length);
        Directory.CreateDirectory(staging);
        string destination = Path.Combine(staging, candidate.Files[0]);
        bool created = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                byte[] buffer = new byte[128 * 1024];
                int count; long total = 0;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); total = checked(total + count); }
                if (total != candidate.SizeBytes) throw new InvalidDataException("The file length changed during placement.");
                output.Flush(true);
            }
            return new(staging, candidate.Files, candidate.SizeBytes);
        }
        catch
        {
            if (created) { ManagedModFiles.EnsureSafe(destination); File.Delete(destination); }
            ManagedModFiles.EnsureSafe(staging); ManagedModFiles.RemoveEmptyDirectories(staging);
            throw;
        }
    }

    private static string EntryPath(IEntry entry)
    {
        string key = entry.Key ?? throw new InvalidDataException("The archive contains an unnamed entry.");
        key = key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (entry.IsDirectory) key = key.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            string normalized = SubscriptionPaths.RelativeFile(key);
            if (normalized.Length > 2000 || normalized.Split(Path.DirectorySeparatorChar).Any(part => part.Length > 255 || SubscriptionPaths.IsReservedFileName(part)))
                throw new InvalidDataException("The archive contains a reserved or oversized Windows name.");
            return normalized;
        }
        catch (ArgumentException ex) { throw new InvalidDataException("The archive contains an unsafe path.", ex); }
    }

    private static void CreateDirectory(string path, string root, HashSet<string> created)
    {
        ManagedModFiles.EnsureSafe(path);
        if (Directory.Exists(path)) return;
        if (!path.Equals(root, SubscriptionPaths.Comparison)) CreateDirectory(Path.GetDirectoryName(path)!, root, created);
        Directory.CreateDirectory(path);
        created.Add(path);
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
