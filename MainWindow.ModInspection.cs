using GOHShaderModdingSupportLauncher.Properties;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private readonly ModInspectionService modInspector;
    private CancellationTokenSource? modScanCancellation;
    private readonly HashSet<CancellationTokenSource> activationChecks = [];
    private long modScanGeneration;
    private (string? Workshop, string? Local, string Options)? listedModPaths;
    private (string? Workshop, string? Local, string Options)? lastDiscoveryPaths;
    private ModScanResult? lastDiscovery;
    private long lastDiscoveryRevision;
    internal ModScanResult? ModScan { get; private set; }
    internal ModScanProgress? ModScanProgress { get; private set; }
    internal (int Trees, int Classifications, int Archives, int Hits) ModInspectionStatistics => modInspector.Statistics;
    internal Task PendingModRefresh { get; private set; } = Task.CompletedTask;
    internal event Action<bool>? ModInspectionChanged;
    internal long ModListRevision { get; private set; }
    internal (string? Workshop, string? Local, string Options) ModDirectoryContext => CurrentModPaths;
    internal void ModListWritten() => ModListRevision++;

    internal Mod? FindInspectedLocalMod(string directory) => listedModPaths == CurrentModPaths
        ? universalVars.modDic.Values.FirstOrDefault(mod => string.Equals(mod.path, directory, StringComparison.OrdinalIgnoreCase)) : null;
    private sealed class ScanReport<T>(Action<T> report, CancellationToken token) : IProgress<T>
    { public void Report(T value) => Dispatcher.UIThread.Invoke(() => report(value), DispatcherPriority.Background, token); }
    private (string? Workshop, string? Local, string Options) CurrentModPaths =>
        (universalVars.workshopDir?.FullName, universalVars.localDir?.FullName, universalVars.optionLoc);

    private sealed record LockedModListSnapshot(Mod[] Mods, long Revision, long Generation,
        string OptionsText, CancellationToken CancellationToken);

    private LockedModListSnapshot? CaptureLockedModList(bool afterGaming, CancellationToken cancellationToken = default) =>
        afterGaming && universalVars.NeedLockModList
            ? new(universalVars.modLoaded.ToArray(), ModListRevision, modScanGeneration,
                File.ReadAllText(universalVars.optionLoc), cancellationToken) : null;

    private void CancelModScan()
    {
        modScanGeneration++;
        modScanCancellation?.Cancel();
        foreach (var check in activationChecks.ToArray()) check.Cancel();
    }

    // Internal synchronous diagnostics only. Every user-facing entry uses RefreshModsAsync.
    internal void RefreshMods(bool afterGaming = false)
    {
        CancelModScan();
        var paths = CurrentModPaths;
        TrackModDirectories(paths.Workshop, paths.Local);
        lastDiscovery = null;
        var locked = CaptureLockedModList(afterGaming);
        ApplyModScan(modInspector.Scan(paths.Workshop, paths.Local), paths, locked);
    }

    internal Task<bool> RefreshModsAsync(bool afterGaming = false, CancellationToken cancellationToken = default)
    {
        var task = RunModScanAsync(afterGaming, cancellationToken,
            afterGaming ? () => universalVars.modLoaded.ToArray() : null);
        PendingModRefresh = task;
        return task;
    }

    internal Task<bool> CheckModsAsync(Func<Mod[]> select, CancellationToken token = default, bool force = false,
        bool protectOptions = true)
    {
        var task = RunModScanAsync(false, token, select, force, protectOptions);
        PendingModRefresh = task;
        return task;
    }

    private async Task<bool> RunModScanAsync(bool afterGaming, CancellationToken cancellationToken,
        Func<Mod[]>? select = null, bool force = false, bool protectOptions = false, bool onlyPending = false)
    {
        CancelModScan();
        long generation = modScanGeneration;
        var paths = CurrentModPaths;
        TrackModDirectories(paths.Workshop, paths.Local);
        long directoryRevision = Volatile.Read(ref modDirectoryRevision);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var locked = CaptureLockedModList(afterGaming, cancellation.Token);
        modScanCancellation = cancellation;
        ModScanProgress = null;
        bool Current() => generation == modScanGeneration && paths == CurrentModPaths && !cancellation.IsCancellationRequested;
        try
        {
            var discovered = onlyPending && HasModDirectoryCoverage && lastDiscoveryPaths == paths &&
                lastDiscoveryRevision == directoryRevision && lastDiscovery is { Issues.Count: 0 } prior
                ? prior
                : await Task.Run(() => modInspector.Discover(paths.Workshop, paths.Local, cancellation.Token),
                    cancellation.Token).ConfigureAwait(false);
            var targets = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current()) throw new OperationCanceledException();
                string? options = ReadModOptions();
                bool structureChanged = ApplyDiscovery(discovered, paths, options);
                lastDiscovery = discovered;
                lastDiscoveryPaths = paths;
                lastDiscoveryRevision = directoryRevision;
                if (structureChanged || !onlyPending) ModInspectionChanged?.Invoke(true);
                var priority = universalVars.modLoaded.Select(mod => mod.DiscoveryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var focused = (pageHost.Content as ModManager ?? Content as ModManager)?.VisibleInspectionRows()
                    .Select(mod => mod.DiscoveryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var requested = locked is not null ? locked.Mods : select?.Invoke() ?? universalVars.modDic.Values.ToArray();
                var ids = requested.Where(mod => !onlyPending || mod.IsScanPending).Select(mod => mod.DiscoveryId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidates = discovered.Mods.Where(mod => ids.Contains(mod.DiscoveryId))
                    .OrderByDescending(mod => priority.Contains(mod.DiscoveryId))
                    .ThenByDescending(mod => focused?.Contains(mod.DiscoveryId) == true).ToArray();
                if ((protectOptions || locked is not null) && ids.Count > 0 && discovered.Issues.Count > 0)
                    throw new IOException("Unable to verify all Mod directories for this operation.");
                var rows = universalVars.modDic.Values.ToDictionary(mod => mod.DiscoveryId, StringComparer.OrdinalIgnoreCase);
                return (Candidates: candidates, Options: options, Rows: rows);
            });
            string scope = ModInspectionService.Scope(paths.Workshop, paths.Local);
            var inspected = new ScanReport<(ModInspectionResult Result, (int Epoch, long Revision) Revision)>(reply =>
            {
                if (!Current()) return;
                var result = reply.Result;
                if (modInspector.RevisionFor(result.DirectoryPath) != reply.Revision) throw new ModInspectionChangedException();
                if (!targets.Rows.TryGetValue(result.DiscoveryId, out var mod)) return;
                bool changed = mod.IsScanPending || !ReferenceEquals(mod.Inspection, result);
                UpdateInspectedMod(mod, result, pending: false);
                ModScanProgress = new((ModScanProgress?.InspectedDirectories ?? 0) + 1, result.DirectoryPath);
                if (changed) ModInspectionChanged?.Invoke(false);
            }, cancellation.Token);
            var checkedMods = await Task.Run(() =>
            {
                var verified = new List<(string Path, (int Epoch, long Revision) Revision)>();
                foreach (var candidate in targets.Candidates)
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        var reply = modInspector.InspectVerifiedCandidate(candidate, cancellation.Token, scope, force);
                        try { inspected.Report(reply); }
                        catch (ModInspectionChangedException) when (attempt < 2) { continue; }
                        verified.Add((candidate.DirectoryPath, reply.Revision));
                        break;
                    }
                }
                return verified;
            }, cancellation.Token).ConfigureAwait(false);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current()) return false;
                bool Stable() => directoryRevision == Volatile.Read(ref modDirectoryRevision) &&
                    checkedMods.All(item => modInspector.RevisionFor(item.Path) == item.Revision);
                if ((protectOptions || locked is not null) && !Stable()) return false;
                if (ReadModOptions() != targets.Options)
                {
                    ReadEnabledSnapshot();
                    ModInspectionChanged?.Invoke(true);
                    if (protectOptions) return false;
                }
                ModScan = new(universalVars.modDic.Values.Select(mod => mod.Inspection!).ToArray(), discovered.Issues);
                try
                {
                    if (locked is not null)
                    {
                        ModInspectionChanged?.Invoke(true);
                        if (Current() && locked.Revision == ModListRevision && Stable())
                            RestoreLockedModList(locked.Mods, locked.OptionsText);
                    }
                    return true;
                }
                catch (ModActivationRejectedException ex) { ShowModActivationFailure(ex); return false; }
            });
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(modScanCancellation, cancellation)) return;
                modScanCancellation = null;
                QueuePendingModRefresh();
            });
        }
    }

    private void BeginModRefresh()
    {
        if (listedModPaths != CurrentModPaths)
        {
            universalVars.modDic = new(StringComparer.OrdinalIgnoreCase);
            universalVars.modLoaded.Clear();
            ModInspectionChanged?.Invoke(true);
        }
        PendingModRefresh = RefreshModsInBackgroundAsync();
    }

    private async Task RefreshModsInBackgroundAsync(bool all = false)
    {
        try { await RunModScanAsync(false, default, all || IsModManagerVisible ? null : () =>
            universalVars.modLoaded.Concat(demandedModPaths.Select(FindInspectedLocalMod).OfType<Mod>()).Distinct().ToArray(),
            onlyPending: true); }
        catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Mod scan failed.", ex); }
    }

    internal void EnsureModsScanned()
    {
        if (modScanCancellation is not null && listedModPaths == CurrentModPaths)
        {
            if (universalVars.modDic.Values.Any(mod => mod.IsScanPending)) modRefreshQueued = true;
            return;
        }
        if (listedModPaths != CurrentModPaths || universalVars.modDic.Values.Any(mod => mod.IsScanPending) ||
            !HasModDirectoryCoverage)
            PendingModRefresh = RefreshModsInBackgroundAsync(all: true);
    }

    internal Dictionary<string, Mod> ScanMods()
    {
        var paths = CurrentModPaths;
        return CreateScannedMods(modInspector.Scan(paths.Workshop, paths.Local), reuse: false);
    }

    private Dictionary<string, Mod> CreateScannedMods(ModScanResult scan, bool pending = false, bool reuse = true)
    {
        var old = reuse ? universalVars.modDic.Values.Distinct().GroupBy(mod => mod.DiscoveryId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase) : [];
        var mods = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in scan.Mods)
        {
            var mod = old.GetValueOrDefault(result.DiscoveryId) ?? new Mod(result.DisplayName, "", result.DirectoryPath, result.LoadId ?? "", false);
            UpdateInspectedMod(mod, result, pending);
            string key = result.LoadId is { } id && !mods.ContainsKey(id) ? id : result.DiscoveryId;
            mods.Add(key, mod);
        }
        return mods;
    }

    private void UpdateInspectedMod(Mod mod, ModInspectionResult result, bool pending)
    {
        if (ReferenceEquals(mod.Inspection, result) && mod.IsScanPending == pending) return;
        if (!pending || mod.Inspection is null) mod.name = result.DisplayName;
        mod.type = result.Source == ModSourceKind.Workshop ? i18n.Main_ModWorkshop : i18n.Main_ModLocal;
        mod.folderName = result.LoadId ?? "";
        mod.Inspection = result;
        CurrentModOrigins().Apply(mod);
        mod.hasShader = result.Shaders.Detected;
        mod.IsInspectionPending = pending;
        ModAliases.Apply(mod);
    }

    private void ReadEnabledSnapshot()
    {
        foreach (var mod in universalVars.modDic.Values) mod.hasLoad = false;
        universalVars.modLoaded.Clear();
        if (File.Exists(universalVars.optionLoc)) ReadLoadedMods();
    }

    internal string? ReadModOptions() => File.Exists(universalVars.optionLoc) ? File.ReadAllText(universalVars.optionLoc) : null;

    private bool ApplyDiscovery(ModScanResult discovered, (string? Workshop, string? Local, string Options) paths, string? options)
    {
        bool sameContext = listedModPaths == paths;
        var oldRows = universalVars.modDic;
        var enabled = universalVars.modLoaded.Select(mod => mod.DiscoveryId).ToArray();
        var previous = sameContext ? oldRows.Values.ToDictionary(mod => mod.DiscoveryId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        var listed = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in discovered.Mods)
        {
            bool exists = previous.Remove(candidate.DiscoveryId, out var mod);
            mod ??= new Mod(candidate.DisplayName, "", candidate.DirectoryPath, candidate.LoadId ?? "", false);
            var result = exists && mod.Inspection is { } current
                ? ModInspectionService.WithDiscoveryIssues(current, candidate) : candidate;
            UpdateInspectedMod(mod, result, pending: !exists || mod.IsScanPending);
            string key = candidate.LoadId is { } id && !listed.ContainsKey(id) ? id : candidate.DiscoveryId;
            listed.Add(key, mod);
        }
        if (discovered.Issues.Count > 0)
            foreach (var mod in previous.Values)
            {
                mod.IsInspectionPending = true;
                listed.TryAdd(mod.LoadId is { } id && !listed.ContainsKey(id) ? id : mod.DiscoveryId, mod);
            }
        universalVars.modDic = listed;
        listedModPaths = paths;
        foreach (var mod in universalVars.modDic.Values) mod.hasLoad = false;
        universalVars.modLoaded.Clear();
        if (options is not null)
        {
            using var reader = new StringReader(options);
            ReadLoadedMods(reader);
        }
        return !sameContext || oldRows.Count != listed.Count ||
            listed.Any(item => !ReferenceEquals(item.Value, oldRows.GetValueOrDefault(item.Key))) ||
            !enabled.SequenceEqual(universalVars.modLoaded.Select(mod => mod.DiscoveryId), StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyModScan(ModScanResult scan, (string? Workshop, string? Local, string Options) paths, LockedModListSnapshot? locked)
    {
        universalVars.modDic = CreateScannedMods(scan);
        ModScan = scan;
        listedModPaths = paths;
        ReadEnabledSnapshot();
        ModInspectionChanged?.Invoke(true);
        // Discovery and completion notifications can run user commands. Never restore their older list.
        if (locked is not null && locked.Revision == ModListRevision && locked.Generation == modScanGeneration &&
            paths == CurrentModPaths && !locked.CancellationToken.IsCancellationRequested)
            RestoreLockedModList(locked.Mods, locked.OptionsText);
    }

    internal async Task VerifyModActivationAsync(IReadOnlyList<Mod> mods)
    {
        ModActivationGuard.RequireRegular(mods);
        var paths = CurrentModPaths;
        long generation = modScanGeneration;
        TrackModDirectories(paths.Workshop, paths.Local);
        long directoryRevision = Volatile.Read(ref modDirectoryRevision);
        string? expectedOptions = ReadModOptions();
        using var cancellation = new CancellationTokenSource();
        activationChecks.Add(cancellation);
        foreach (var mod in mods) mod.SetActivationCheckPending(true);
        try
        {
            var results = await Task.Run(() =>
            {
                var discovered = modInspector.Discover(paths.Workshop, paths.Local, cancellation.Token);
                if (discovered.Issues.Count > 0) throw new IOException("Unable to verify all Mod directories for activation.");
                var candidates = discovered.Mods.ToDictionary(mod => mod.DiscoveryId, StringComparer.OrdinalIgnoreCase);
                string scope = ModInspectionService.Scope(paths.Workshop, paths.Local);
                return mods.Select(mod => candidates.TryGetValue(mod.DiscoveryId, out var candidate)
                    ? modInspector.InspectVerifiedCandidate(candidate, cancellation.Token, scope)
                    : (Result: (ModInspectionResult?)null, Revision: modInspector.RevisionFor(mod.path))).ToArray();
            }, cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                bool Stable() => directoryRevision == Volatile.Read(ref modDirectoryRevision) &&
                    mods.Select((mod, index) => modInspector.RevisionFor(mod.path) == results[index].Revision).All(current => current);
                if (generation != modScanGeneration || paths != CurrentModPaths || ReadModOptions() != expectedOptions || !Stable() ||
                    mods.Any(mod => !ReferenceEquals(mod, FindInspectedLocalMod(mod.path)))) throw new OperationCanceledException();
                for (int i = 0; i < mods.Count; i++)
                {
                    if (results[i].Result is { } result) UpdateInspectedMod(mods[i], result, pending: false);
                    else mods[i].Inspection = null;
                    mods[i].SetActivationCheckPending(false);
                }
                ModInspectionChanged?.Invoke(false);
                if (!Stable()) throw new OperationCanceledException();
                ModActivationGuard.RequireRegular(mods);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                activationChecks.Remove(cancellation);
                foreach (var mod in mods) mod.SetActivationCheckPending(false);
                ModInspectionChanged?.Invoke(false);
                QueuePendingModRefresh();
            });
        }
    }

    internal async Task<bool> CheckEnabledModsForLaunchAsync()
    {
        var paths = CurrentModPaths;
        var revision = ModListRevision;
        string? options = ReadModOptions();
        if (!await CheckModsAsync(() => universalVars.modLoaded.ToArray())) return false;
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (paths != CurrentModPaths || revision != ModListRevision || ReadModOptions() != options) return false;
            ModActivationGuard.RequireRegular(universalVars.modLoaded);
            return true;
        });
    }

    internal void RestoreLockedModList(IReadOnlyList<Mod> locked, string? expectedOptions = null)
    {
        // Compare against the file at exit-refresh start, not the pre-game list we intend to restore.
        if (expectedOptions is not null && File.ReadAllText(universalVars.optionLoc) != expectedOptions)
        {
            ReadEnabledSnapshot();
            ModInspectionChanged?.Invoke(true);
            return;
        }
        var restored = locked.Select(previous => universalVars.modDic.Values.FirstOrDefault(mod =>
            mod.DiscoveryId.Equals(previous.DiscoveryId, StringComparison.OrdinalIgnoreCase)) ??
            new Mod(previous.name, previous.type, previous.path, previous.folderName, false)).ToList();
        ModActivationGuard.RequireRegular(restored);
        if (!FileManager.TryWriteLoadedMods(universalVars.optionLoc, restored, expectedOptions))
        {
            ReadEnabledSnapshot();
            ModInspectionChanged?.Invoke(true);
            return;
        }
        ModListWritten();
        foreach (var mod in universalVars.modDic.Values) mod.hasLoad = false;
        foreach (var mod in restored) mod.hasLoad = true;
        universalVars.modLoaded = restored;
        ModInspectionChanged?.Invoke(true);
    }

    internal void ShowModActivationFailure(ModActivationRejectedException error)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowModActivationFailure(error)); return; }
        MessageBox.Show(this, error.Message, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal string GetModInspectionDebugDetails(Mod mod) => ModInspectionPresentation.Details(mod);
#if DEBUG
    internal Task ShowModInspectionDebugAsync(Mod mod) => new ModInspectionDialog(this, mod).ShowDialog(this);
#endif
}
