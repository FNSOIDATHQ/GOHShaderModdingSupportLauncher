using Avalonia.Controls;

namespace GOHShaderModdingSupportLauncher;

public partial class ModManager
{
    private bool activationInProgress;
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;

    internal async Task ActivateModsAsync(IReadOnlyList<Mod> selected)
    {
        ModActivationGuard.RequireRegular(selected);
        if (activationInProgress) return;
        activationInProgress = true;
        long revision = main.ModListRevision;
        var context = main.ModDirectoryContext;
        try
        {
            string? options = main.ReadModOptions();
            await main.VerifyModActivationAsync(selected);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != main.ModListRevision || context != main.ModDirectoryContext || main.ReadModOptions() != options)
                    throw new OperationCanceledException();
                CommitLoadedMods(main.universalVars.modLoaded.Concat(selected).DistinctBy(mod => mod.DiscoveryId).ToList(),
                    expectedOptions: options);
            });
        }
        finally { activationInProgress = false; }
    }

    private void InspectionUpdated(bool structureChanged)
    {
        if (!hasInit) return;
        if (structureChanged) { RefreshView(); return; }
        RefreshModViews();
    }

    private void RestoreSelection(string name, HashSet<string> selected, bool clearIfMissing = false)
    {
        var grid = name == "loadedMods" ? loadedMods : unloadedMods;
        var matches = selected.Count == 0 ? [] :
            (grid.ItemsSource?.Cast<Mod>() ?? []).Where(mod => selected.Contains(mod.DiscoveryId)).ToList();
        if (matches.Count == 0 && !clearIfMissing) return;
        isSyncingSelection = true;
        try
        {
            loadedMods.SelectedItems.Clear(); unloadedMods.SelectedItems.Clear();
            foreach (var mod in matches) grid.SelectedItems.Add(mod);
            selectedRow = new SelectedRow(name, matches);
        }
        finally { isSyncingSelection = false; }
        UpdateSelectedModActions();
    }
}
