using GOHShaderModdingSupportLauncher.Properties;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private readonly ModInspectionService modInspector = new();
    private CancellationTokenSource? modScanCancellation;
    private long modScanGeneration;
    private (string? Workshop, string? Local, string Options)? inspectedPaths;
    internal ModScanResult? ModScan { get; private set; }
    internal ModScanProgress? ModScanProgress { get; private set; }
    internal Task PendingModRefresh { get; private set; } = Task.CompletedTask;
    private sealed class ScanProgress(Action<ModScanProgress> report) : IProgress<ModScanProgress>
    { public void Report(ModScanProgress value) => report(value); }

    private (string? Workshop, string? Local, string Options) CurrentModPaths =>
        (universalVars.workshopDir?.FullName, universalVars.localDir?.FullName, universalVars.optionLoc);

    private void CancelModScan()
    {
        modScanGeneration++;
        modScanCancellation?.Cancel();
    }

    // Synchronous entry retained for transactions and the shipped, offline self-test.
    public void RefreshMods(bool afterGaming = false)
    {
        CancelModScan();
        var paths = CurrentModPaths;
        ApplyModScan(modInspector.Scan(paths.Workshop, paths.Local), paths, afterGaming);
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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        modScanCancellation = cancellation;
        ModScanProgress = null;
        var progress = new ScanProgress(value => Dispatcher.UIThread.Post(() =>
        {
            if (generation == modScanGeneration && paths == CurrentModPaths &&
                ReferenceEquals(modScanCancellation, cancellation) && !cancellation.IsCancellationRequested) ModScanProgress = value;
        }));
        try
        {
            var scan = await modInspector.ScanAsync(paths.Workshop, paths.Local, cancellation.Token, progress).ConfigureAwait(false);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != modScanGeneration || paths != CurrentModPaths || cancellation.IsCancellationRequested) return false;
                ApplyModScan(scan, paths, afterGaming);
                RefreshModPage();
                return true;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(modScanCancellation, cancellation)) modScanCancellation = null;
            });
        }
    }

    private void BeginModRefresh()
    {
        if (inspectedPaths != CurrentModPaths)
        {
            universalVars.modDic = new(StringComparer.OrdinalIgnoreCase);
            universalVars.modLoaded.Clear();
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
        // Startup and path changes already scan in the background. Reopening the page reuses that snapshot.
        if (inspectedPaths != CurrentModPaths && modScanCancellation is null) RefreshMods();
    }

    internal Dictionary<string, Mod> ScanMods()
    {
        var paths = CurrentModPaths;
        return CreateScannedMods(modInspector.Scan(paths.Workshop, paths.Local));
    }

    private Dictionary<string, Mod> CreateScannedMods(ModScanResult scan)
    {
        var mods = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        foreach (var inspection in scan.Mods)
        {
            var mod = new Mod(inspection.DisplayName,
                inspection.Source == ModSourceKind.Workshop ? i18n.Main_ModWorkshop : i18n.Main_ModLocal,
                inspection.DirectoryPath, inspection.LoadId ?? "", inspection.Shaders.Detected) { Inspection = inspection };
            ModAliases.Apply(mod);
            // Keep all directories, including duplicate and unusable IDs. Never use their display keys in options.set.
            string key = inspection.LoadId is { } id && !mods.ContainsKey(id) ? id : inspection.DiscoveryId;
            mods.Add(key, mod);
        }
        return mods;
    }

    private void ApplyModScan(ModScanResult scan, (string? Workshop, string? Local, string Options) paths, bool afterGaming)
    {
        var mods = CreateScannedMods(scan);
        ModScan = scan;
        inspectedPaths = paths;
        universalVars.modDic = mods;
        if (universalVars.NeedLockModList && afterGaming) verifyLoadedMods();
        else
        {
            universalVars.modLoaded.Clear();
            ReadLoadedMods();
        }
    }
}
