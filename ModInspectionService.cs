using System.Text;

namespace GOHShaderModdingSupportLauncher;

/// <summary>Read-only directory and archive-index inspection. Never loads code or extracts archives.</summary>
public sealed class ModInspectionService
{
    private const int MaxDepth = 64, MaxEntries = 200_000, MaxMetadataBytes = 1024 * 1024;
    private const int MaxCachedPakEntries = 200_000;
    private readonly object cacheGate = new();
    private readonly Dictionary<string, CachedPak> pakCache = new(StringComparer.OrdinalIgnoreCase);
    private int cachedEntries, cacheGeneration;
    private sealed record CachedPak(long Length, long Modified, FileManager.PakEntry[] Entries);
    private static readonly HashSet<string> ExternalExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".exe", ".asi", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".wsf" };
    private static readonly HashSet<string> NestedArchives = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".pat" };

    public void ClearCache()
    {
        lock (cacheGate) { pakCache.Clear(); cachedEntries = 0; cacheGeneration++; }
    }

    public Task<ModScanResult> ScanAsync(string? workshopDirectory, string? localDirectory,
        CancellationToken cancellationToken = default, IProgress<ModScanProgress>? progress = null, bool refresh = true) =>
        Task.Run(() => Scan(workshopDirectory, localDirectory, cancellationToken, progress, refresh), cancellationToken);

    public ModScanResult Scan(string? workshopDirectory, string? localDirectory,
        CancellationToken cancellationToken = default, IProgress<ModScanProgress>? progress = null, bool refresh = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (refresh) ClearCache();
        var results = new List<ModInspectionResult>();
        var issues = new List<ModInspectionIssue>();
        ReadRoot(workshopDirectory, ModSourceKind.Workshop);
        ReadRoot(localDirectory, ModSourceKind.Local);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var group in results.Where(result => result.LoadId is not null).GroupBy(result => result.LoadId!, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2) continue;
            foreach (var result in group.ToArray())
                results[results.IndexOf(result)] = result with
                {
                    Issues = Frozen(result.Issues.Append(new(ModInspectionReason.DuplicateLoadId, "",
                        string.Join("; ", group.Select(item => item.DirectoryPath)))))
                };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Frozen(results), Frozen(issues));

        void ReadRoot(string? path, ModSourceKind source)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var root = new DirectoryInfo(path);
                try { _ = File.GetAttributes(root.FullName); }
                catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return; }
                if (HasLinkAncestor(root)) { issues.Add(new(ModInspectionReason.ReparsePoint, path)); return; }
                foreach (var child in root.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child.Name.Equals(".gohsms", StringComparison.OrdinalIgnoreCase)) continue;
                    if (results.Count >= MaxEntries) { issues.Add(new(ModInspectionReason.ScanLimit, path)); break; }
                    results.Add(InspectDirectory(child.FullName, source, cancellationToken));
                    progress?.Report(new(results.Count, child.FullName));
                }
            }
            catch (Exception ex) when (IsReadError(ex)) { issues.Add(new(ModInspectionReason.UnreadablePath, path, ex.GetType().Name)); }
        }
    }

    public ModInspectionResult InspectDirectory(string directory, ModSourceKind source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        string discoveryId = source + ":" + root.FullName.ToUpperInvariant();
        string candidateId = source == ModSourceKind.Workshop ? "mod_" + root.Name : root.Name.ToLowerInvariant();
        string? loadId = IsValidLoadId(candidateId) ? candidateId : null;
        var issues = new List<ModInspectionIssue>();
        var content = new List<ModInspectionEvidence>();
        var programs = new List<ModInspectionEvidence>();
        var shaders = new List<ModInspectionEvidence>();
        bool treeComplete = true, programsComplete = true, shadersComplete = true;
        bool metadataSeen = false, metadataReadable = false, metadataValid = false, nestedRoot = false, unsupportedContent = false;
        string displayName = root.Name;
        int entries = 0, contentCount = 0, programCount = 0, shaderCount = 0;
        if (loadId is null) issues.Add(new(ModInspectionReason.InvalidLoadId, root.Name));

        try
        {
            if (HasLinkAncestor(root)) MarkIncomplete(ModInspectionReason.ReparsePoint, "");
            else Walk(root, 0);
        }
        catch (Exception ex) when (IsReadError(ex)) { MarkIncomplete(ModInspectionReason.UnreadablePath, "", ex.GetType().Name); }

        ModStructureState structure;
        bool structureComplete;
        if (!metadataSeen)
        {
            structure = treeComplete ? ModStructureState.Invalid : ModStructureState.Unconfirmed;
            structureComplete = treeComplete;
            issues.Add(new(nestedRoot ? ModInspectionReason.NestedModRoot : ModInspectionReason.MissingMetadata, "mod.info"));
        }
        else if (!metadataReadable) { structure = ModStructureState.Unconfirmed; structureComplete = false; }
        else if (!metadataValid) { structure = ModStructureState.Invalid; structureComplete = true; }
        else if (content.Count > 0) { structure = ModStructureState.Regular; structureComplete = true; }
        else if (!treeComplete) { structure = ModStructureState.Unconfirmed; structureComplete = false; }
        else
        {
            // Metadata-only / dependency-only layouts are not yet established as content rules.
            structure = programs.Count > 0 && !unsupportedContent ? ModStructureState.Invalid : ModStructureState.Unconfirmed;
            structureComplete = structure == ModStructureState.Invalid;
            issues.Add(new(structureComplete ? ModInspectionReason.NoContent : ModInspectionReason.UnrecognizedLayout, ""));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(root.FullName, source, discoveryId, loadId, displayName, structure, structureComplete,
            Frozen(content), new(programCount > 0, programsComplete, Frozen(programs), programCount),
            new(shaderCount > 0, shadersComplete, Frozen(shaders), shaderCount), Frozen(issues), contentCount);

        void MarkIncomplete(ModInspectionReason reason, string path, string detail = "")
        {
            treeComplete = programsComplete = shadersComplete = false;
            if (issues.Count < 256) issues.Add(new(reason, path, detail));
        }

        void Walk(DirectoryInfo current, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in current.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) { MarkIncomplete(ModInspectionReason.ScanLimit, Relative(current.FullName)); return; }
                string relative = Relative(item.FullName);
                try
                {
                    if (item.Name.Equals(".gohsms", StringComparison.OrdinalIgnoreCase) && item is DirectoryInfo) continue;
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    { MarkIncomplete(ModInspectionReason.ReparsePoint, relative); continue; }
                    if (item is DirectoryInfo child)
                    {
                        if (depth >= MaxDepth) MarkIncomplete(ModInspectionReason.ScanLimit, relative);
                        else Walk(child, depth + 1);
                        if (entries > MaxEntries) return;
                        continue;
                    }
                    var file = (FileInfo)item;
                    if (file.Name.Equals("mod.info", StringComparison.OrdinalIgnoreCase))
                    {
                        if (depth == 0) ReadMetadata(file);
                        else
                        {
                            nestedRoot = true;
                            if (issues.Count < 256) issues.Add(new(ModInspectionReason.NestedModRoot, relative));
                        }
                    }
                    ProcessFile(relative, file.Length, false);
                    if (file.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase)) ReadPak(file, relative);
                }
                catch (Exception ex) when (IsReadError(ex)) { MarkIncomplete(ModInspectionReason.UnreadablePath, relative, ex.GetType().Name); }
            }
        }

        void ReadMetadata(FileInfo file)
        {
            metadataSeen = true;
            try
            {
                using var stream = file.OpenRead();
                if (stream.Length > MaxMetadataBytes) { issues.Add(new(ModInspectionReason.ScanLimit, "mod.info")); return; }
                using var memory = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = stream.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (memory.Length + count > MaxMetadataBytes) { issues.Add(new(ModInspectionReason.ScanLimit, "mod.info")); return; }
                    memory.Write(buffer, 0, count);
                }
                var bytes = memory.ToArray();
                Encoding encoding = new UTF8Encoding(false, true);
                int skip = 0;
                if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
                    throw new DecoderFallbackException();
                if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
                string text = encoding.GetString(bytes, skip, bytes.Length - skip);
                if (text.Contains('\0')) throw new DecoderFallbackException();
                var metadata = ModMetadataReader.Parse(text);
                metadataReadable = true;
                metadataValid = metadata.Valid;
                if (!string.IsNullOrWhiteSpace(metadata.Name)) displayName = metadata.Name;
                if (!metadataValid) issues.Add(new(ModInspectionReason.InvalidMetadata, "mod.info"));
            }
            catch (DecoderFallbackException) { issues.Add(new(ModInspectionReason.UnsupportedEncoding, "mod.info")); }
            catch (Exception ex) when (IsReadError(ex)) { issues.Add(new(ModInspectionReason.UnreadablePath, "mod.info", ex.GetType().Name)); }
        }

        void ReadPak(FileInfo file, string relative)
        {
            var index = ReadPakIndex(file, cancellationToken, out bool limitExceeded);
            if (index is null) { MarkIncomplete(limitExceeded ? ModInspectionReason.ScanLimit : ModInspectionReason.UnreadablePak, relative); return; }
            bool resourcePak = relative.StartsWith("resource/", StringComparison.OrdinalIgnoreCase) && relative.Count(c => c == '/') == 1;
            bool localizationPak = relative.StartsWith("localizations/", StringComparison.OrdinalIgnoreCase) && relative.Count(c => c == '/') == 2;
            if (!resourcePak && !localizationPak)
            {
                unsupportedContent = true;
                if (issues.Count < 256) issues.Add(new(ModInspectionReason.UnrecognizedLayout, relative));
            }
            foreach (var entry in index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) { MarkIncomplete(ModInspectionReason.ScanLimit, relative); break; }
                string? entryPath = NormalizeEntry(entry.FullName);
                if (entryPath is null) { MarkIncomplete(ModInspectionReason.UnsafeArchivePath, relative, entry.FullName); continue; }
                string evidencePath = relative + "!/" + entryPath;
                ProcessFile(evidencePath, entry.Length, true,
                    resourcePak ? "resource/" + entryPath : localizationPak ? "localizations/language/" + entryPath : null);
            }
        }

        void ProcessFile(string relative, long length, bool inPak, string? virtualPath = null)
        {
            string extension = Path.GetExtension(relative);
            bool external = ExternalExtensions.Contains(extension);
            if (external) { programCount++; AddEvidence(programs, relative, extension); }
            if (NestedArchives.Contains(extension) || (inPak && extension.Equals(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                programsComplete = shadersComplete = false;
                unsupportedContent = true;
                if (issues.Count < 256) issues.Add(new(ModInspectionReason.UninspectedArchive, relative));
                return;
            }
            string? contentPath = inPak ? virtualPath : relative;
            if (contentPath is null || length <= 0) return;
            if (contentPath.StartsWith("resource/shader/dx10/", StringComparison.OrdinalIgnoreCase))
            { shaderCount++; AddEvidence(shaders, relative, inPak ? "pak-shader" : "loose-shader"); }
            if (external || extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) || IsDocumentation(contentPath)) return;
            // Resource paths are open ended: no extension whitelist for game assets or scripts.
            bool resource = contentPath.StartsWith("resource/", StringComparison.OrdinalIgnoreCase);
            bool localization = contentPath.StartsWith("localizations/", StringComparison.OrdinalIgnoreCase) && contentPath.Count(c => c == '/') >= 2;
            if (resource || localization) { contentCount++; AddEvidence(content, relative, inPak ? "pak-content" : localization ? "localization" : "resource"); }
            else if (!relative.Equals("mod.info", StringComparison.OrdinalIgnoreCase) && !IsDocumentation(relative) && !external)
                unsupportedContent = true;
        }

        string Relative(string path) => Path.GetRelativePath(root.FullName, path).Replace('\\', '/');
    }

    internal static bool IsValidLoadId(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or ':' or '\\' or '/' or '{' or '}');

    private FileManager.PakEntry[]? ReadPakIndex(FileInfo file, CancellationToken token, out bool limitExceeded)
    {
        limitExceeded = false;
        token.ThrowIfCancellationRequested();
        long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
        int generation;
        lock (cacheGate)
        {
            generation = cacheGeneration;
            if (pakCache.TryGetValue(file.FullName, out var cached) && cached.Length == length && cached.Modified == modified) return cached.Entries;
        }
        var entries = FileManager.TryReadPakIndex(file.FullName, token, MaxEntries, out limitExceeded);
        token.ThrowIfCancellationRequested();
        file.Refresh();
        if (file.Length != length || file.LastWriteTimeUtc.Ticks != modified) return null;
        if (entries is not null)
            lock (cacheGate)
            {
                if (generation != cacheGeneration) return entries;
                if (cachedEntries + entries.Length > MaxCachedPakEntries || pakCache.Count >= 128) { pakCache.Clear(); cachedEntries = 0; }
                if (pakCache.Remove(file.FullName, out var previous)) cachedEntries -= previous.Entries.Length;
                pakCache[file.FullName] = new(length, modified, entries);
                cachedEntries += entries.Length;
            }
        return entries;
    }

    private static bool HasLinkAncestor(DirectoryInfo root)
    {
        for (DirectoryInfo? item = root; item is not null; item = item.Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static string? NormalizeEntry(string path)
    {
        path = path.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.Length is 0 or > 4096 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)) return null;
        var parts = path.Split('/');
        return parts.Any(part => part is "" or "." or "..") ? null : string.Join('/', parts);
    }

    private static bool IsDocumentation(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        string stem = Path.GetFileNameWithoutExtension(name).TrimStart('!', '_').ToLowerInvariant();
        bool previewAtRoot = path.Count(c => c == '/') <= (path.StartsWith("localizations/", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
        return stem.StartsWith("readme", StringComparison.Ordinal) || stem is "license" or "licence" or "changelog" or "credits" ||
            (previewAtRoot && (stem is "preview" or "thumbnail"));
    }

    private static void AddEvidence(List<ModInspectionEvidence> list, string path, string kind)
    {
        // Bound retained evidence separately from the full scan; feature detection still visits every entry.
        if (list.Count < 128) list.Add(new(path, kind));
    }

    private static bool IsReadError(Exception ex) => FileManager.IsFileError(ex) || ex is NotSupportedException;
    private static IReadOnlyList<T> Frozen<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
