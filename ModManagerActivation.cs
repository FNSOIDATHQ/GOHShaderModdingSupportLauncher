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
        try
        {
            await main.VerifyModActivationAsync(selected);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != main.ModListRevision) throw new OperationCanceledException();
                CommitLoadedMods(main.universalVars.modLoaded.Concat(selected).DistinctBy(mod => mod.DiscoveryId).ToList());
            });
        }
        finally { activationInProgress = false; }
    }

    private void InspectionUpdated(bool structureChanged)
    {
        if (!hasInit) return;
        if (structureChanged) { RefreshView(); return; }
        var selected = selectedRow.mods?.Select(mod => mod.DiscoveryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string grid = selectedRow.dataGrid;
        isSyncingSelection = true;
        try { unloadedView.Refresh(); }
        finally { isSyncingSelection = false; }
        if (selected is not null && selected.Count > 0) RestoreSelection(grid, selected);
    }

    private void RestoreSelection(string name, HashSet<string> selected)
    {
        var grid = name == "loadedMods" ? loadedMods : unloadedMods;
        var matches = (grid.ItemsSource?.Cast<Mod>() ?? []).Where(mod => selected.Contains(mod.DiscoveryId)).ToArray();
        if (matches.Length == 0) return;
        isSyncingSelection = true;
        try
        {
            loadedMods.SelectedItems.Clear(); unloadedMods.SelectedItems.Clear();
            foreach (var mod in matches) grid.SelectedItems.Add(mod);
            selectedRow = new SelectedRow(name, matches.ToList());
        }
        finally { isSyncingSelection = false; }
        UpdateAliasAction();
    }
}
