using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace GOHShaderModdingSupportLauncher;

/// <summary>Read-only directory and archive-index inspection. Never loads code or extracts archives.</summary>
public sealed class ModInspectionService
{
    private const int MaxDepth = 64, MaxEntries = 200_000, MaxMetadataBytes = 1024 * 1024;
    private const int MaxCachedPakEntries = 200_000;
    private readonly object cacheGate = new();
    private readonly SemaphoreSlim inspectionWorker = new(1, 1);
    private readonly ModInspectionCache resultCache;
    private readonly Dictionary<string, DirectoryChange> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedPak> pakCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> supportsArchiveVersions;
    private int cachedEntries, cacheGeneration, treeReads, classifications, archiveReads, resultHits;
    private long changeRevision;
    private sealed record CachedPak(ModArchiveVersion Version, FileManager.PakEntry[] Entries);
    private sealed record DirectoryChange(long Revision, string[] ArchivePaths);
    private sealed record TreeEntry(string Path, string Relative, long Length = 0, FileAttributes Attributes = 0,
        ModArchiveVersion? Version = null, ModInspectionIssue? Issue = null, FileManager.PakEntry[]? WeakIndex = null,
        bool WeakIndexRead = false, bool WeakIndexLimited = false);

    public ModInspectionService() : this(null) { }

    internal ModInspectionService(string? cacheDirectory, Func<string, bool>? supportsArchiveVersions = null)
    {
        resultCache = new(cacheDirectory);
        this.supportsArchiveVersions = supportsArchiveVersions ?? ModArchiveVersion.HasReliableVersions;
    }

    internal (int Trees, int Classifications, int Archives, int Hits) Statistics =>
        (Volatile.Read(ref treeReads), Volatile.Read(ref classifications), Volatile.Read(ref archiveReads), Volatile.Read(ref resultHits));

    internal void Invalidate(string directory, string? affectedPath, bool archiveMayHaveChanged)
    {
        lock (cacheGate)
        {
            string[] paths = pendingChanges.GetValueOrDefault(directory)?.ArchivePaths ?? [];
            if (archiveMayHaveChanged)
            {
                string path = affectedPath ?? directory;
                if (paths.Length >= 128) paths = [directory];
                else if (!paths.Any(parent => IsWithin(path, parent))) paths = [.. paths, path];
                foreach (string cached in pakCache.Keys.Where(file => IsWithin(file, path)).ToArray())
                {
                    cachedEntries -= pakCache[cached].Entries.Length;
                    pakCache.Remove(cached);
                }
            }
            pendingChanges[directory] = new(++changeRevision, paths);
        }
    }

    internal (int Epoch, long Revision) RevisionFor(string directory)
    {
        lock (cacheGate) return (cacheGeneration, pendingChanges.GetValueOrDefault(directory)?.Revision ?? 0);
    }

    internal static bool IsWithin(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal void InvalidateActiveInspection()
    {
        lock (cacheGate) cacheGeneration++;
    }
    private static readonly HashSet<string> ExternalExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".exe", ".asi", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".wsf" };
    private static readonly HashSet<string> NestedArchives = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z" };

    public void ClearCache()
    {
        lock (cacheGate) { pakCache.Clear(); cachedEntries = 0; cacheGeneration++; }
    }

    public Task<ModScanResult> ScanAsync(string? workshopDirectory, string? localDirectory,
        CancellationToken cancellationToken = default, IProgress<ModScanProgress>? progress = null, bool refresh = false,
        IProgress<ModScanResult>? discovery = null, IProgress<ModInspectionResult>? inspected = null) =>
        Task.Run(() => Scan(workshopDirectory, localDirectory, cancellationToken, progress, refresh, discovery, inspected), cancellationToken);

    public ModScanResult Scan(string? workshopDirectory, string? localDirectory,
        CancellationToken cancellationToken = default, IProgress<ModScanProgress>? progress = null, bool refresh = false,
        IProgress<ModScanResult>? discovery = null, IProgress<ModInspectionResult>? inspected = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (refresh) ClearCache();
        var candidates = Discover(workshopDirectory, localDirectory, cancellationToken);
        discovery?.Report(candidates);
        var results = new List<ModInspectionResult>();
        foreach (var candidate in candidates.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = InspectVerifiedCandidate(candidate, cancellationToken, Scope(workshopDirectory, localDirectory), refresh).Result;
            results.Add(result);
            inspected?.Report(result);
            progress?.Report(new(results.Count, candidate.DirectoryPath));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Frozen(results), candidates.Issues);
    }

    // Enumerates only immediate directories. Metadata and PAK reads happen in the subsequent inspection pass.
    internal ModScanResult Discover(string? workshopDirectory, string? localDirectory, CancellationToken cancellationToken = default)
    {
        var results = new List<ModInspectionResult>();
        var issues = new List<ModInspectionIssue>();
        ReadRoot(workshopDirectory, ModSourceKind.Workshop);
        ReadRoot(localDirectory, ModSourceKind.Local);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var group in results.Select((mod, index) => (mod, index)).Where(item => item.mod.LoadId is not null)
            .GroupBy(item => item.mod.LoadId!, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2) continue;
            var duplicate = new ModInspectionIssue(ModInspectionReason.DuplicateLoadId, "",
                string.Join("; ", group.Select(item => item.mod.DirectoryPath)));
            foreach (var (mod, index) in group)
                results[index] = mod with
                {
                    Issues = Frozen(mod.Issues.Append(duplicate))
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
                    string id = source == ModSourceKind.Workshop ? "mod_" + child.Name : child.Name.ToLowerInvariant();
                    results.Add(new(child.FullName, source, source + ":" + child.FullName.ToUpperInvariant(),
                        IsValidLoadId(id) ? id : null, child.Name, ModStructureState.Unconfirmed, false, [],
                        new(false, false, [], 0), new(false, false, [], 0), [], 0));
                }
            }
            catch (Exception ex) when (IsReadError(ex)) { issues.Add(new(ModInspectionReason.UnreadablePath, path, ex.GetType().Name)); }
        }
    }

    internal static string Scope(string? workshop, string? local) => local ?? workshop ?? "";

    internal static ModInspectionResult WithDiscoveryIssues(ModInspectionResult result, ModInspectionResult candidate)
    {
        var duplicates = candidate.Issues.Where(issue => issue.Reason == ModInspectionReason.DuplicateLoadId);
        if (!result.Issues.Any(issue => issue.Reason == ModInspectionReason.DuplicateLoadId) && !duplicates.Any()) return result;
        return result with { Issues = Frozen(result.Issues.Where(issue => issue.Reason != ModInspectionReason.DuplicateLoadId).Concat(duplicates)) };
    }

    internal ModInspectionResult InspectCandidate(ModInspectionResult candidate, CancellationToken cancellationToken,
        string scope = "", bool force = false) => WithDiscoveryIssues(
        Inspect(candidate.DirectoryPath, candidate.Source, scope, force, cancellationToken), candidate);

    internal (ModInspectionResult Result, (int Epoch, long Revision) Revision) InspectVerifiedCandidate(
        ModInspectionResult candidate, CancellationToken token, string scope, bool force = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            var revision = RevisionFor(candidate.DirectoryPath);
            try
            {
                var result = InspectCandidate(candidate, token, scope, force);
                if (RevisionFor(candidate.DirectoryPath) != revision) throw new ModInspectionChangedException();
                return (result, revision);
            }
            catch (ModInspectionChangedException) when (attempt < 2) { token.ThrowIfCancellationRequested(); }
        }
    }

    public ModInspectionResult InspectDirectory(string directory, ModSourceKind source, CancellationToken cancellationToken = default) =>
        Inspect(directory, source, null, true, cancellationToken);

    private ModInspectionResult Inspect(string directory, ModSourceKind source, string? scope, bool force, CancellationToken token)
    {
        inspectionWorker.Wait(token);
        try { return InspectDirectoryCore(directory, source, scope, force, token); }
        finally { inspectionWorker.Release(); }
    }

    private ModInspectionResult InspectDirectoryCore(string directory, ModSourceKind source, string? scope, bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        int generation;
        DirectoryChange? change;
        lock (cacheGate)
        {
            generation = cacheGeneration;
            change = pendingChanges.GetValueOrDefault(root.FullName);
        }
        Interlocked.Increment(ref treeReads);
        bool archiveVersions = supportsArchiveVersions(root.FullName);
        string? cacheKey = scope is null ? null : ModInspectionCache.Key(scope, source, root.FullName);
        var files = new List<TreeEntry>();
        bool cacheable = true, archiveInvalidated = false;
        int enumerated = 0, weakEntries = 0;
        byte[]? metadataBytes = null;
        string discoveryId = source + ":" + root.FullName.ToUpperInvariant();
        string candidateId = source == ModSourceKind.Workshop ? "mod_" + root.Name : root.Name.ToLowerInvariant();
        string? loadId = IsValidLoadId(candidateId) ? candidateId : null;
        var issues = new List<ModInspectionIssue>();
        var content = new List<ModInspectionEvidence>();
        var programs = new List<ModInspectionEvidence>();
        var shaders = new List<ModInspectionEvidence>();
        var other = new List<ModInspectionEvidence>();
        bool treeComplete = true, programsComplete = true, shadersComplete = true;
        bool metadataSeen = false, metadataReadable = false, metadataValid = false, nestedRoot = false, unsupportedContent = false;
        string displayName = root.Name;
        int entries = 0, contentCount = 0, programCount = 0, shaderCount = 0, otherCount = 0;
        if (loadId is null) issues.Add(new(ModInspectionReason.InvalidLoadId, root.Name));

        try
        {
            if (HasLinkAncestor(root)) MarkUncacheableLink("");
            else
            {
                // Metadata is critical and must not lose its budget to large resource/source trees.
                var metadata = new FileInfo(Path.Combine(root.FullName, "mod.info"));
                try
                {
                    var attributes = File.GetAttributes(metadata.FullName);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) MarkUncacheableLink("mod.info");
                    else ReadMetadataBytes(metadata);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception ex) when (IsReadError(ex)) { MarkIncomplete(ModInspectionReason.UnreadablePath, "mod.info", ex.GetType().Name); }
                Walk(root, 0);
            }
        }
        catch (Exception ex) when (IsReadError(ex)) { MarkIncomplete(ModInspectionReason.UnreadablePath, "", ex.GetType().Name); }

        string? fingerprint = cacheKey is not null && cacheable ? Fingerprint(files, metadataBytes, cancellationToken) : null;
        RequireCurrent(complete: false);
        if (!force && !archiveInvalidated && fingerprint is not null &&
            resultCache.Read(cacheKey!, fingerprint, root.FullName, source) is { } cached)
        {
            RequireCurrent(complete: true);
            Interlocked.Increment(ref resultHits);
            return cached;
        }

        Interlocked.Increment(ref classifications);
        if (metadataBytes is not null) ParseMetadata(metadataBytes);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entries > MaxEntries) { MarkIncomplete(ModInspectionReason.ScanLimit, file.Relative); break; }
            if (file.Issue is { } issue) MarkIncomplete(issue.Reason, issue.RelativePath, issue.Detail);
            if (file.Issue is not null || (file.Attributes & FileAttributes.Directory) != 0) continue;
            try
            {
                string name = Path.GetFileName(file.Path);
                if (name.Equals("mod.info", StringComparison.OrdinalIgnoreCase))
                {
                    nestedRoot = true;
                    if (issues.Count < 256) issues.Add(new(ModInspectionReason.NestedModRoot, file.Relative));
                }
                string extension = Path.GetExtension(file.Path);
                if (extension.Equals(".pak", StringComparison.OrdinalIgnoreCase)) ReadGameArchive(file, file.Relative, false);
                else if (extension.Equals(".pat", StringComparison.OrdinalIgnoreCase)) ReadGameArchive(file, file.Relative, true);
                else if (NestedArchives.Contains(extension)) ReadAttachment(file, file.Relative);
                else ProcessFile(file.Relative, file.Length, false);
            }
            catch (Exception ex) when (IsReadError(ex)) { MarkIncomplete(ModInspectionReason.UnreadablePath, file.Relative, ex.GetType().Name); }
            if (entries > MaxEntries) break;
        }

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
            structure = ModStructureState.Unconfirmed;
            structureComplete = false;
            issues.Add(new(programs.Count > 0 && !unsupportedContent ? ModInspectionReason.NoContent : ModInspectionReason.UnrecognizedLayout, ""));
        }
        RequireCurrent(complete: false);
        var result = new ModInspectionResult(root.FullName, source, discoveryId, loadId, displayName, structure, structureComplete,
            Frozen(content), new(programCount > 0, programsComplete, Frozen(programs), programCount),
            new(shaderCount > 0, shadersComplete, Frozen(shaders), shaderCount), Frozen(issues), contentCount)
            { OtherEvidence = Frozen(other), OtherMatchCount = otherCount };
        if (cacheable && fingerprint is not null)
            resultCache.Write(cacheKey!, fingerprint, result, () => RequireCurrent(complete: false));
        RequireCurrent(complete: true);
        return result;

        void RequireCurrent(bool complete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (cacheGate)
            {
                if (generation != cacheGeneration || !ReferenceEquals(change, pendingChanges.GetValueOrDefault(root.FullName)))
                    throw new ModInspectionChangedException();
                if (complete && change?.ArchivePaths.Length > 0 && cacheable)
                    pendingChanges[root.FullName] = change with { ArchivePaths = [] };
            }
        }

        void MarkUncacheableLink(string path)
        {
            cacheable = false;
            MarkIncomplete(ModInspectionReason.ReparsePoint, path);
        }

        bool ForceArchive(string path) => force || change?.ArchivePaths.Any(parent => IsWithin(path, parent)) == true;

        void MarkIncomplete(ModInspectionReason reason, string path, string detail = "")
        {
            if (reason is ModInspectionReason.UnreadablePath or ModInspectionReason.UnreadablePak or ModInspectionReason.ScanLimit)
                cacheable = false;
            treeComplete = programsComplete = shadersComplete = false;
            if (issues.Count < 256) issues.Add(new(reason, path, detail));
        }

        void Walk(DirectoryInfo current, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = new List<FileSystemInfo>();
            foreach (var item in current.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth == 0 && item.Name.Equals("mod.info", StringComparison.OrdinalIgnoreCase)) continue;
                if (item is DirectoryInfo && item.Name.Equals(".gohsms", StringComparison.OrdinalIgnoreCase)) continue;
                if (enumerated == MaxEntries) { MarkIncomplete(ModInspectionReason.ScanLimit, Relative(current.FullName)); break; }
                enumerated++;
                children.Add(item);
            }
            foreach (var item in children.OrderBy(item => Priority(item, depth)).ThenBy(item => item.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Relative(item.FullName);
                try
                {
                    var attributes = item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        files.Add(new(item.FullName, relative, Attributes: attributes,
                            Issue: new(ModInspectionReason.ReparsePoint, relative)));
                        continue;
                    }
                    if (item is DirectoryInfo child)
                    {
                        files.Add(new(item.FullName, relative, Attributes: FileAttributes.Directory));
                        if (depth >= MaxDepth) MarkIncomplete(ModInspectionReason.ScanLimit, relative);
                        else if (enumerated < MaxEntries) Walk(child, depth + 1);
                        else MarkIncomplete(ModInspectionReason.ScanLimit, relative);
                        continue;
                    }
                    var file = (FileInfo)item;
                    ModArchiveVersion? version = null;
                    if (IsArchive(file.FullName))
                    {
                        if (archiveVersions)
                        {
                            try
                            {
                                using var stream = file.OpenRead();
                                version = ModArchiveVersion.Read(stream);
                            }
                            catch (Exception ex) when (IsReadError(ex)) { }
                        }
                        archiveInvalidated |= version is not null && change?.ArchivePaths.Any(path => IsWithin(file.FullName, path)) == true;
                    }
                    var entry = new TreeEntry(file.FullName, relative, version?.Length ?? file.Length, Version: version);
                    if (cacheKey is not null && IsArchive(entry.Path) && version is null && cacheable)
                    {
                        // On a volume with weak version information, compare what the classifier consumes.
                        var index = ReadPakIndex(entry, true, generation, cancellationToken, out bool limited);
                        weakEntries += index?.Length ?? 0;
                        cacheable = index is not null && weakEntries <= MaxEntries;
                        entry = entry with { WeakIndex = index, WeakIndexRead = true, WeakIndexLimited = limited };
                    }
                    files.Add(entry);
                }
                catch (Exception ex) when (IsReadError(ex))
                {
                    cacheable = false;
                    files.Add(new(item.FullName, relative, Issue: new(ModInspectionReason.UnreadablePath, relative, ex.GetType().Name)));
                }
            }
        }

        void ReadMetadataBytes(FileInfo file)
        {
            metadataSeen = true;
            try
            {
                using var stream = file.OpenRead();
                if (stream.Length > MaxMetadataBytes) { MetadataLimit(); return; }
                using var memory = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = stream.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (memory.Length + count > MaxMetadataBytes) { MetadataLimit(); return; }
                    memory.Write(buffer, 0, count);
                }
                metadataBytes = memory.ToArray();
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                cacheable = false;
                issues.Add(new(ModInspectionReason.UnreadablePath, "mod.info", ex.GetType().Name));
            }
        }

        void MetadataLimit()
        {
            cacheable = false;
            issues.Add(new(ModInspectionReason.ScanLimit, "mod.info"));
        }

        void ParseMetadata(byte[] bytes)
        {
            try
            {
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
        }

        int Priority(FileSystemInfo item, int depth)
        {
            if (item is DirectoryInfo)
                return depth > 0 || item.Name.Equals("resource", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Equals("localizations", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
            string extension = item.Extension;
            if (extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pat", StringComparison.OrdinalIgnoreCase)) return 1;
            return depth == 0 || NestedArchives.Contains(extension) ? 2 : 0;
        }

        void ReadGameArchive(TreeEntry file, string relative, bool patch)
        {
            var index = ReadPakIndex(file, ForceArchive(file.Path), generation, cancellationToken, out bool limitExceeded);
            if (index is null) { MarkIncomplete(limitExceeded ? ModInspectionReason.ScanLimit : ModInspectionReason.UnreadablePak, relative); return; }
            string parent = relative[..(relative.LastIndexOf('/') + 1)];
            bool mounted = !patch && IsContentPath(relative);
            // This identifies the documented root patch container, not a resource mount or
            // a promise that a patch-only package can be enabled by the game.
            bool knownPatch = patch && parent.Length == 0 && index.Any(entry =>
                entry.FullName.Equals(".patch", StringComparison.OrdinalIgnoreCase) && entry.Length > 0);
            if (!mounted && !knownPatch)
            {
                unsupportedContent = true;
                AddOther(relative, patch ? "unrecognized-patch" : "unsupported-pak");
                if (issues.Count < 256) issues.Add(new(ModInspectionReason.UnrecognizedLayout, relative));
            }
            foreach (var entry in index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) { MarkIncomplete(ModInspectionReason.ScanLimit, relative); break; }
                string? entryPath = NormalizeEntry(entry.FullName);
                if (entryPath is null) { MarkIncomplete(ModInspectionReason.UnsafeArchivePath, relative, entry.FullName); continue; }
                string evidencePath = relative + "!/" + entryPath;
                string? virtualPath = null;
                if (mounted)
                {
                    // Some packages already carry their full mount prefix; do not duplicate it or
                    // silently move a nested package's entries into a different resource subtree.
                    if (entryPath.StartsWith("resource/", StringComparison.OrdinalIgnoreCase) ||
                        entryPath.StartsWith("localizations/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entryPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) virtualPath = entryPath;
                        else MarkIncomplete(ModInspectionReason.UnrecognizedLayout, evidencePath);
                    }
                    else virtualPath = parent + entryPath;
                }
                // PAT mounting is not inferred from an index alone. Retain unknown shader coverage
                // for shader-shaped entries rather than inventing a deployed shader or its absence.
                if (!mounted && (entryPath.StartsWith("shader/dx10/", StringComparison.OrdinalIgnoreCase) ||
                    entryPath.StartsWith("resource/shader/dx10/", StringComparison.OrdinalIgnoreCase)))
                {
                    shadersComplete = false;
                    if (issues.Count < 256) issues.Add(new(ModInspectionReason.UnrecognizedLayout, evidencePath));
                }
                ProcessFile(evidencePath, entry.Length, true, virtualPath);
            }
        }

        void ReadAttachment(TreeEntry file, string relative)
        {
            var index = ReadPakIndex(file, ForceArchive(file.Path), generation, cancellationToken, out bool limitExceeded);
            if (index is null)
            {
                cacheable = false;
                AddOther(relative, "attachment");
                programsComplete = shadersComplete = false;
                unsupportedContent = true;
                if (issues.Count < 256) issues.Add(new(limitExceeded ? ModInspectionReason.ScanLimit : ModInspectionReason.UninspectedArchive, relative));
                return;
            }
            bool textBackup = true;
            void RecordAttachment()
            {
                if (!textBackup) return;
                textBackup = false;
                unsupportedContent = true;
                AddOther(relative, "attachment");
            }
            foreach (var entry in index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) { MarkIncomplete(ModInspectionReason.ScanLimit, relative); return; }
                string? entryPath = NormalizeEntry(entry.FullName);
                if (entryPath is null) { MarkIncomplete(ModInspectionReason.UnsafeArchivePath, relative, entry.FullName); RecordAttachment(); continue; }
                string extension = Path.GetExtension(entryPath);
                // Indexing an attachment describes its contents, not files mounted into the game.
                ProcessFile(relative + "!/" + entryPath, entry.Length, true);
                if (extension.ToLowerInvariant() is not (".po" or ".pot" or ".lng" or ".txt" or ".md") && !IsDocumentation(entryPath)) RecordAttachment();
            }
        }

        void AddOther(string relative, string kind)
        {
            otherCount++;
            AddEvidence(other, relative, kind);
        }

        void ProcessFile(string relative, long length, bool inPak, string? virtualPath = null)
        {
            string extension = Path.GetExtension(relative);
            bool external = ExternalExtensions.Contains(extension);
            if (external) { programCount++; AddEvidence(programs, relative, extension); }
            if (NestedArchives.Contains(extension) || (inPak && (extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pat", StringComparison.OrdinalIgnoreCase))))
            {
                programsComplete = shadersComplete = false;
                unsupportedContent = true;
                AddOther(relative, "nested-archive");
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
            else if (!relative.Equals("mod.info", StringComparison.OrdinalIgnoreCase) && !IsSupplementaryDocument(relative) && !external)
            {
                unsupportedContent = true;
                AddOther(relative, "additional-content");
            }
        }

        string Relative(string path) => Path.GetRelativePath(root.FullName, path).Replace('\\', '/');
    }

    internal static bool IsValidLoadId(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or ':' or '\\' or '/' or '{' or '}');

    internal static bool IsArchive(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pat", StringComparison.OrdinalIgnoreCase) || NestedArchives.Contains(extension);
    }

    private static string Fingerprint(List<TreeEntry> files, byte[]? metadata, CancellationToken token)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(metadata?.Length ?? -1);
        if (metadata is not null) writer.Write(metadata);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            writer.Write(file.Relative);
            writer.Write((int)file.Attributes);
            writer.Write(file.Length > 0);
            writer.Write(file.Version.HasValue);
            if (file.Version is { } version)
            {
                writer.Write(version.Length);
                writer.Write(version.Modified);
                writer.Write(version.Changed);
                writer.Write(version.Volume);
                writer.Write(version.IdLow);
                writer.Write(version.IdHigh);
            }
            writer.Write(file.WeakIndex?.Length ?? -1);
            if (file.WeakIndex is { } index)
                foreach (var entry in index)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Write(entry.FullName);
                    writer.Write(entry.Length);
                }
        }
        writer.Flush();
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private FileManager.PakEntry[]? ReadPakIndex(TreeEntry file, bool force, int generation, CancellationToken token, out bool limitExceeded)
    {
        limitExceeded = file.WeakIndexLimited;
        if (file.WeakIndexRead) return file.WeakIndex;
        token.ThrowIfCancellationRequested();
        if (!force && file.Version is { } expected)
        {
            lock (cacheGate)
                if (pakCache.TryGetValue(file.Path, out var cached) && cached.Version == expected) return cached.Entries;
        }
        FileManager.PakEntry[]? entries;
        try
        {
            if ((File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0 ||
                HasLinkAncestor(new DirectoryInfo(Path.GetDirectoryName(file.Path)!))) throw new ModInspectionChangedException();
            using var stream = File.OpenRead(file.Path);
            if (file.Version is { } version && ModArchiveVersion.Read(stream) != version) throw new ModInspectionChangedException();
            entries = Path.GetExtension(file.Path).Equals(".7z", StringComparison.OrdinalIgnoreCase)
                ? ReadAttachmentIndex(file.Path, stream, token, out limitExceeded)
                : FileManager.TryReadPakIndex(file.Path, token, MaxEntries, out limitExceeded, stream);
        }
        catch (Exception ex) when (IsReadError(ex)) { return null; }
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref archiveReads);
        if (entries is not null && file.Version is { } parsed)
            lock (cacheGate)
            {
                if (generation != cacheGeneration) return entries;
                if (cachedEntries + entries.Length > MaxCachedPakEntries || pakCache.Count >= 128) { pakCache.Clear(); cachedEntries = 0; }
                if (pakCache.Remove(file.Path, out var previous)) cachedEntries -= previous.Entries.Length;
                pakCache[file.Path] = new(parsed, entries);
                cachedEntries += entries.Length;
            }
        return entries;
    }

    private static FileManager.PakEntry[]? ReadAttachmentIndex(string path, Stream stream, CancellationToken token, out bool limitExceeded)
    {
        limitExceeded = false;
        try
        {
            using var archive = ArchiveFactory.OpenArchive(stream, ReaderOptions.ForExternalStream);
            var entries = new List<FileManager.PakEntry>();
            int count = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaxEntries) { limitExceeded = true; return null; }
                if (entry.IsEncrypted || !entry.IsComplete) return null;
                if (!entry.IsDirectory) entries.Add(new(entry.Key ?? "", entry.Size, DateTimeOffset.UnixEpoch));
            }
            return archive.IsComplete ? entries.ToArray() : null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        { AppDiagnostics.Log("Skipping unreadable attachment index: " + path, ex); return null; }
    }

    private static bool IsContentPath(string path) => path.StartsWith("resource/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("localizations/", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '/') >= 2;

    private static bool IsSupplementaryDocument(string path) => IsDocumentation(path) ||
        !IsContentPath(path) && Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".pdf" or ".rtf" or ".html" or ".url" or ".png" or ".jpg" or ".jpeg";

    private static bool HasLinkAncestor(DirectoryInfo root)
    {
        for (DirectoryInfo? item = root; item is not null; item = item.Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    internal static bool CanReadDirectory(DirectoryInfo directory) => directory.Exists && !HasLinkAncestor(directory);

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

internal sealed class ModInspectionChangedException : OperationCanceledException;
