namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModCacheClearResult(long BytesFreed, int FilesDeleted, int Failures);

// One generation covers all sources, including nested snapshot reads. Clearing never
// waits for network work, and old work cannot acquire a new generation halfway through.
internal sealed class ModCacheMaintenance(string root)
{
    internal readonly object Gate = new();
    private readonly AsyncLocal<long?> readingGeneration = new();
    private readonly SemaphoreSlim clearGate = new(1, 1);
    private long generation;
    private bool clearing;
    internal string Root { get; } = SubscriptionPaths.Normalize(root);
    internal long Generation { get { lock (Gate) return clearing ? -1 : generation; } }
    internal bool IsCurrent(long value) { lock (Gate) return !clearing && value == generation; }
    internal string DirectoryFor(string name) => Path.Combine(Root, name);

    internal IDisposable BeginRead()
    {
        long? previous = readingGeneration.Value;
        readingGeneration.Value = previous ?? Generation;
        return new ReadScope(() => readingGeneration.Value = previous);
    }

    internal bool CanWrite => IsCurrent(readingGeneration.Value ?? Generation);
    private sealed class ReadScope(Action release) : IDisposable { public void Dispose() => release(); }

    internal async Task<ModCacheClearResult> ClearAsync(Action invalidate, CancellationToken token = default)
    {
        await clearGate.WaitAsync(token).ConfigureAwait(true);
        try
        {
            lock (Gate) { clearing = true; generation++; }
            try
            {
                invalidate();
                // Only these disposable directories are owned by this operation. Never
                // recurse or follow junctions, and never delete the subscriptions root.
                return await Task.Run(() => ClearDirectories()).ConfigureAwait(true);
            }
            finally { lock (Gate) { generation++; clearing = false; } }
        }
        finally { clearGate.Release(); }
    }

    internal async Task WaitForClearAsync(CancellationToken token)
    {
        await clearGate.WaitAsync(token).ConfigureAwait(false);
        clearGate.Release();
    }

    private ModCacheClearResult ClearDirectories()
    {
        long bytes = 0;
        int deleted = 0, failures = 0;
        foreach (string name in new[] { "moddb-cache", "github-cache", "mowportal-cache", "media-cache" })
        {
            string directory = DirectoryFor(name);
            try
            {
                ManagedModFiles.EnsureSafe(directory);
                if (!Directory.Exists(directory)) continue;
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        ManagedModFiles.EnsureSafe(entry);
                        if (Directory.Exists(entry)) { failures++; continue; }
                        long length = new FileInfo(entry).Length;
                        File.Delete(entry);
                        bytes += length; deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; }
        }
        return new(bytes, deleted, failures);
    }
}
