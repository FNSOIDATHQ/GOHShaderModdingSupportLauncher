using System.ComponentModel;

namespace GOHShaderModdingSupportLauncher;

public sealed class Mod : INotifyPropertyChanged
{
    private string originalName;
    private string? alias;
    public string name
    {
        get => originalName;
        set
        {
            if (originalName == value) return;
            originalName = value;
            // A different original name must be looked up again, without carrying an old alias.
            alias = null;
            PropertyChanged?.Invoke(this, new(nameof(name)));
            NotifyDisplay();
        }
    }
    public string DisplayName => alias ?? name;
    public bool HasAlias => alias is not null;
    public string type { get; set; }
    public bool hasShader { get; set; }
    private bool inspectionPending;
    private bool activationCheckPending;
    internal bool IsScanPending => inspectionPending;
    public bool IsInspectionPending
    {
        get => inspectionPending || activationCheckPending;
        internal set
        {
            if (inspectionPending == value) return;
            inspectionPending = value;
            PropertyChanged?.Invoke(this, new(nameof(IsInspectionPending)));
            PropertyChanged?.Invoke(this, new(nameof(FeatureSortKey)));
        }
    }
    internal void SetActivationCheckPending(bool value)
    {
        activationCheckPending = value;
        PropertyChanged?.Invoke(this, new(nameof(IsInspectionPending)));
        PropertyChanged?.Invoke(this, new(nameof(FeatureSortKey)));
    }
    public bool HasRegularFeature => !IsInspectionPending && Inspection?.HasRegularFeature == true;
    private ModInspectionResult? inspection;
    private string? inspectionDiscoveryId;
    public ModInspectionResult? Inspection
    {
        get => inspection;
        internal set
        {
            inspection = value;
            if (value is not null) inspectionDiscoveryId = value.DiscoveryId;
            PropertyChanged?.Invoke(this, new(nameof(Inspection)));
            PropertyChanged?.Invoke(this, new(nameof(FeatureSortKey)));
        }
    }
    // Sort by the combination of facts, independently of translated badge labels.
    public int FeatureSortKey => IsInspectionPending ? 16 : Inspection is { } result
        ? (ModInspectionPresentation.IsOther(result) ? 8 : 1) + (result.ExternalPrograms.Detected ? 2 : 0) + (result.Shaders.Detected ? 4 : 0)
        : hasShader ? 4 : 0;
    internal bool IsMissingReference { get; init; }
    public string DiscoveryId => inspectionDiscoveryId ?? (IsMissingReference ? "missing:" + folderName : path);
    public string? LoadId => Inspection is null ? folderName : Inspection.LoadId;
    public string path;
    public string folderName;
    public bool hasLoad;

    public Mod(string n, string t, string p, string fn, bool hS)
    {
        originalName = n;
        type = t;
        path = p;
        folderName = fn;
        hasShader = hS;
    }

    internal void SetAlias(string? value)
    {
        if (alias == value) return;
        alias = value;
        NotifyDisplay();
    }

    private void NotifyDisplay()
    {
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new(nameof(HasAlias)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
