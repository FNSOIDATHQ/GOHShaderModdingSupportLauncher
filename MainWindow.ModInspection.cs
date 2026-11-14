using GOHShaderModdingSupportLauncher.Properties;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private readonly ModInspectionService modInspector = new();
    private CancellationTokenSource? modScanCancellation;
    private readonly HashSet<CancellationTokenSource> activationChecks = [];
    private long modScanGeneration;
    private (string? Workshop, string? Local, string Options)? inspectedPaths, listedModPaths;
    internal ModScanResult? ModScan { get; private set; }
    internal ModScanProgress? ModScanProgress { get; private set; }
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
        var locked = afterGaming && universalVars.NeedLockModList ? universalVars.modLoaded.ToArray() : null;
        ApplyModScan(modInspector.Scan(paths.Workshop, paths.Local), paths, locked);
    }

    internal Task<bool> RefreshModsAsync(bool afterGaming = false, CancellationToken cancellationToken = default)
    {
        var task = RunModScanAsync(afterGaming, cancellationToken);
        PendingModRefresh = task;
        return task;
    }

    private async Task<bool> RunModScanAsync(bool afterGaming, CancellationToken cancellationToken)
    {
        CancelModScan();
        long generation = modScanGeneration;
        var paths = CurrentModPaths;
        var locked = afterGaming && universalVars.NeedLockModList ? universalVars.modLoaded.ToArray() : null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        modScanCancellation = cancellation;
        ModScanProgress = null;
        foreach (var mod in universalVars.modDic.Values) mod.IsInspectionPending = true;
        ModInspectionChanged?.Invoke(false);
        bool Current() => generation == modScanGeneration && paths == CurrentModPaths && !cancellation.IsCancellationRequested;
        var discovery = new ScanReport<ModScanResult>(scan =>
        {
            if (!Current()) return;
            universalVars.modDic = CreateScannedMods(scan, pending: true);
            listedModPaths = paths;
            ReadEnabledSnapshot();
            ModInspectionChanged?.Invoke(true);
        }, cancellation.Token);
        var inspected = new ScanReport<ModInspectionResult>(result =>
        {
            if (!Current()) return;
            if (FindInspectedLocalMod(result.DirectoryPath) is { } mod) UpdateInspectedMod(mod, result, pending: false);
            ModInspectionChanged?.Invoke(false);
        }, cancellation.Token);
        var progress = new ScanReport<ModScanProgress>(value => { if (Current()) ModScanProgress = value; }, cancellation.Token);
        try
        {
            var scan = await modInspector.ScanAsync(paths.Workshop, paths.Local, cancellation.Token, progress,
                discovery: discovery, inspected: inspected).ConfigureAwait(false);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current()) return false;
                try { ApplyModScan(scan, paths, locked); return true; }
                catch (ModActivationRejectedException ex) { ShowModActivationFailure(ex); return false; }
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(modScanCancellation, cancellation)) return;
                modScanCancellation = null;
                foreach (var mod in universalVars.modDic.Values.Where(mod => mod.IsScanPending))
                {
                    mod.Inspection = null;
                    mod.IsInspectionPending = false;
                }
                ModInspectionChanged?.Invoke(false);
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

    private async Task RefreshModsInBackgroundAsync()
    {
        try { await RefreshModsAsync(); }
        catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Mod scan failed.", ex); }
    }

    internal void EnsureModsScanned()
    {
        if (inspectedPaths != CurrentModPaths && modScanCancellation is null) BeginModRefresh();
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
        if (!pending || mod.Inspection is null) mod.name = result.DisplayName;
        mod.type = result.Source == ModSourceKind.Workshop ? i18n.Main_ModWorkshop : i18n.Main_ModLocal;
        mod.folderName = result.LoadId ?? "";
        mod.Inspection = result;
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

    private void ApplyModScan(ModScanResult scan, (string? Workshop, string? Local, string Options) paths, Mod[]? locked)
    {
        universalVars.modDic = CreateScannedMods(scan);
        ModScan = scan;
        inspectedPaths = listedModPaths = paths;
        ReadEnabledSnapshot();
        ModInspectionChanged?.Invoke(true);
        if (locked is not null) RestoreLockedModList(locked);
    }

    internal async Task VerifyModActivationAsync(IReadOnlyList<Mod> mods)
    {
        ModActivationGuard.RequireRegular(mods);
        var paths = CurrentModPaths;
        long generation = modScanGeneration;
        using var cancellation = new CancellationTokenSource();
        activationChecks.Add(cancellation);
        foreach (var mod in mods) mod.SetActivationCheckPending(true);
        try
        {
            var results = await Task.Run(() =>
            {
                var discovered = modInspector.Discover(paths.Workshop, paths.Local, cancellation.Token);
                modInspector.ClearCache();
                return mods.Select(mod => discovered.Mods.FirstOrDefault(candidate =>
                    candidate.DirectoryPath.Equals(mod.path, StringComparison.OrdinalIgnoreCase)) is { } candidate
                    ? modInspector.InspectCandidate(candidate, cancellation.Token) : null).ToArray();
            }, cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != modScanGeneration || paths != CurrentModPaths ||
                    mods.Any(mod => !ReferenceEquals(mod, FindInspectedLocalMod(mod.path)))) throw new OperationCanceledException();
                for (int i = 0; i < mods.Count; i++)
                {
                    if (results[i] is { } result) UpdateInspectedMod(mods[i], result, pending: false);
                    else mods[i].Inspection = null;
                    mods[i].SetActivationCheckPending(false);
                }
                ModInspectionChanged?.Invoke(false);
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
            });
        }
    }

    internal async Task<bool> CheckEnabledModsForLaunchAsync()
    {
        if (!await RefreshModsAsync()) return false;
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ModActivationGuard.RequireRegular(universalVars.modLoaded);
            return true;
        });
    }

    internal void RestoreLockedModList(IReadOnlyList<Mod> locked)
    {
        var restored = locked.Select(previous => universalVars.modDic.Values.FirstOrDefault(mod =>
            mod.DiscoveryId.Equals(previous.DiscoveryId, StringComparison.OrdinalIgnoreCase)) ??
            new Mod(previous.name, previous.type, previous.path, previous.folderName, false)).ToList();
        ModActivationGuard.RequireRegular(restored);
        FileManager.WriteLoadedMods(universalVars.optionLoc, restored);
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
