using System.ComponentModel;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private readonly List<FileSystemWatcher> modDirectoryWatchers = [];
    private readonly HashSet<string> demandedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object modChangesGate = new();
    private readonly HashSet<string> changedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unverifiedModRoots = new(StringComparer.OrdinalIgnoreCase);
    private (string? Workshop, string? Local)? watchedModRoots;
    private long modWatcherEpoch, modDirectoryRevision;
    private bool modChangePosted, refreshModDirectories, modRefreshQueued, modWatchClosed;
    private DispatcherTimer? modRefreshTimer;
    private bool HasModDirectoryCoverage { get; set; }
    private bool IsModManagerVisible => IsVisible && (pageHost.Content is ModManager || Content is ModManager);

    private void TrackModDirectories(string? workshop, string? local)
    {
        if (modWatchClosed || watchedModRoots == (workshop, local) && HasModDirectoryCoverage) return;
        if (watchedModRoots != (workshop, local))
            demandedModPaths.RemoveWhere(path => (local is null || !ModInspectionService.IsWithin(path, local)) &&
                (workshop is null || !ModInspectionService.IsWithin(path, workshop)));
        StopModDirectoryWatchers();
        watchedModRoots = (workshop, local);
        long epoch = Volatile.Read(ref modWatcherEpoch);
        HasModDirectoryCoverage = true;
        foreach (string root in new[] { workshop, local }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = new DirectoryInfo(root);
                if (directory.Parent is { } parent && ModInspectionService.CanReadDirectory(parent))
                {
                    var watcher = new FileSystemWatcher(parent.FullName, directory.Name)
                        { NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.Security };
                    modDirectoryWatchers.Add(watcher);
                    watcher.Created += (_, _) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: true);
                    watcher.Deleted += (_, _) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: true);
                    watcher.Renamed += (_, _) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: true);
                    watcher.Changed += (_, _) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: true);
                    watcher.Error += (_, _) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: true);
                    watcher.EnableRaisingEvents = true;
                }
                else HasModDirectoryCoverage = false;

                if (!ModInspectionService.CanReadDirectory(directory)) continue;
                var contents = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                        NotifyFilters.Size | NotifyFilters.Attributes };
                modDirectoryWatchers.Add(contents);
                contents.Created += (_, e) => ModPathChanged(root, e.FullPath, epoch, nameChanged: true);
                contents.Deleted += (_, e) => ModPathChanged(root, e.FullPath, epoch, nameChanged: true);
                contents.Changed += (_, e) =>
                {
                    // Enumerating a directory can produce a metadata notification on Windows.
                    // Its own timestamp does not affect classification; child names and writes have their own events.
                    if (!IsPlainDirectory(e.FullPath)) ModPathChanged(root, e.FullPath, epoch, nameChanged: false);
                };
                contents.Renamed += (_, e) =>
                {
                    ModPathChanged(root, e.OldFullPath, epoch, nameChanged: true);
                    ModPathChanged(root, e.FullPath, epoch, nameChanged: true);
                };
                contents.Error += (_, e) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: e.GetException() is not InternalBufferOverflowException);
                contents.EnableRaisingEvents = true;

                var security = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Security };
                modDirectoryWatchers.Add(security);
                security.Changed += (_, e) => ModPathChanged(root, e.FullPath, epoch, nameChanged: false);
                security.Error += (_, e) => LoseModDirectoryCoverage(root, epoch, replaceWatcher: e.GetException() is not InternalBufferOverflowException);
                security.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (FileManager.IsFileError(ex) || ex is Win32Exception)
            {
                HasModDirectoryCoverage = false;
                AppDiagnostics.Log("Unable to monitor a Mod directory.", ex);
            }
        }
        if (!HasModDirectoryCoverage)
            foreach (var mod in universalVars.modDic.Values) mod.IsInspectionPending = true;
    }

    private void StopModDirectoryWatchers()
    {
        lock (modChangesGate)
        {
            Interlocked.Increment(ref modWatcherEpoch);
            changedModPaths.Clear();
            unverifiedModRoots.Clear();
            refreshModDirectories = false;
            modChangePosted = false;
        }
        foreach (var watcher in modDirectoryWatchers) watcher.Dispose();
        modDirectoryWatchers.Clear();
    }

    private static bool IsPlainDirectory(string path)
    {
        try
        {
            var kind = File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint);
            return kind == FileAttributes.Directory;
        }
        catch (Exception ex) when (FileManager.IsFileError(ex)) { return false; }
    }

    private void ModPathChanged(string root, string path, long epoch, bool nameChanged)
    {
        var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        if (segments[0] is "." or ".." || segments[0].Equals(".gohsms", StringComparison.OrdinalIgnoreCase) ||
            segments.SkipLast(1).Any(part => part.Equals(".gohsms", StringComparison.OrdinalIgnoreCase))) return;
        string directory = Path.Combine(root, segments[0]);
        lock (modChangesGate)
        {
            if (epoch != modWatcherEpoch) return;
            if (nameChanged && segments.Length == 1) Interlocked.Increment(ref modDirectoryRevision);
            modInspector.Invalidate(directory, path, nameChanged || ModInspectionService.IsArchive(path));
            changedModPaths.Add(directory);
            PostModChanges(epoch);
        }
    }

    internal void LoseModDirectoryCoverage(string root, long? epoch = null, bool replaceWatcher = false)
    {
        lock (modChangesGate)
        {
            if (epoch.HasValue && epoch.Value != modWatcherEpoch) return;
            modInspector.InvalidateActiveInspection();
            Interlocked.Increment(ref modDirectoryRevision);
            unverifiedModRoots.Add(root);
            refreshModDirectories |= replaceWatcher;
            PostModChanges(modWatcherEpoch);
        }
    }

    private void PostModChanges(long epoch)
    {
        if (modChangePosted) return;
        modChangePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            string[] paths, roots;
            lock (modChangesGate)
            {
                if (epoch != modWatcherEpoch) return;
                paths = changedModPaths.ToArray();
                roots = unverifiedModRoots.ToArray();
                if (refreshModDirectories) HasModDirectoryCoverage = false;
                changedModPaths.Clear();
                unverifiedModRoots.Clear();
                refreshModDirectories = false;
                modChangePosted = false;
            }
            var changed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in universalVars.modDic.Values)
                if (changed.Contains(mod.path) || roots.Any(root => ModInspectionService.IsWithin(mod.path, root)))
                    mod.IsInspectionPending = true;
            ModInspectionChanged?.Invoke(false);
            modRefreshQueued = true;
            QueuePendingModRefresh();
        });
    }

    internal void EnsureModInspected(string path)
    {
        if (FindInspectedLocalMod(path)?.IsScanPending != true || !demandedModPaths.Add(path)) return;
        modRefreshQueued = true;
        QueuePendingModRefresh();
    }

    private void QueuePendingModRefresh()
    {
        if (modWatchClosed || !modRefreshQueued || modScanCancellation is not null || activationChecks.Count > 0) return;
        if (modRefreshTimer is null)
        {
            modRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(175) };
            modRefreshTimer.Tick += (_, _) =>
            {
                modRefreshTimer.Stop();
                if (modWatchClosed || modScanCancellation is not null || activationChecks.Count > 0) return;
                modRefreshQueued = false;
                PendingModRefresh = RefreshModsInBackgroundAsync();
            };
        }
        modRefreshTimer.Stop();
        modRefreshTimer.Start();
    }

    private void StopTrackingMods()
    {
        modWatchClosed = true;
        modRefreshTimer?.Stop();
        StopModDirectoryWatchers();
    }
}
