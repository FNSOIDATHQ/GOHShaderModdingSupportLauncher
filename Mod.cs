using System.ComponentModel;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

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
    private string legacyType;
    // Kept for existing physical-source callers; platform display is independent.
    public string type
    {
        get => legacyType;
        set
        {
            if (legacyType == value) return;
            legacyType = value;
            PropertyChanged?.Invoke(this, new(nameof(type)));
            RefreshSourceDisplay();
        }
    }
    internal ModSubscriptionLink? SubscriptionLink { get; private set; }
    public string SourceLabel => Inspection?.Source == ModSourceKind.Workshop
        ? LocalizedStrings.Instance.Get("Main_ModWorkshop")
        : SubscriptionLink?.Source.Provider switch
        {
            "moddb" => "ModDB",
            "github" => "GitHub",
            "mowportal" => "Mow Portal",
            _ => Inspection?.Source == ModSourceKind.Local
                ? LocalizedStrings.Instance.Get("Main_ModLocal") : type
        };
    public int SourceSortKey => Inspection?.Source == ModSourceKind.Workshop ? 1 : SubscriptionLink?.Source.Provider switch
    {
        "moddb" => 2,
        "github" => 3,
        "mowportal" => 4,
        _ => 0
    };
    internal bool SetSubscriptionLink(ModSubscriptionLink? value)
    {
        if (SubscriptionLink == value) return false;
        SubscriptionLink = value;
        PropertyChanged?.Invoke(this, new(nameof(SubscriptionLink)));
        RefreshSourceDisplay();
        return true;
    }
    internal void RefreshSourceDisplay()
    {
        PropertyChanged?.Invoke(this, new(nameof(SourceLabel)));
        PropertyChanged?.Invoke(this, new(nameof(SourceSortKey)));
    }
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
            NotifyInspectionState();
        }
    }
    internal void SetActivationCheckPending(bool value)
    {
        if (activationCheckPending == value) return;
        activationCheckPending = value;
        NotifyInspectionState();
    }
    public bool HasRegularFeature => !IsInspectionPending && Inspection?.HasRegularFeature == true;
    public bool CanActivate => !IsInspectionPending && !IsMissingReference && Inspection?.CanActivate == true;
    private ModInspectionResult? inspection;
    private string? inspectionDiscoveryId;
    public ModInspectionResult? Inspection
    {
        get => inspection;
        internal set
        {
            inspection = value;
            if (value is not null) inspectionDiscoveryId = value.DiscoveryId;
            if (value?.Source != ModSourceKind.Local || SubscriptionLink is { } link &&
                !value.DirectoryPath.Equals(link.DirectoryPath, SubscriptionPaths.Comparison))
                SetSubscriptionLink(null);
            PropertyChanged?.Invoke(this, new(nameof(Inspection)));
            NotifyInspectionState();
            RefreshSourceDisplay();
        }
    }
    // Sort by the combination of facts, independently of translated badge labels.
    public int FeatureSortKey => IsInspectionPending ? 16 : IsMissingReference ? 8 : Inspection is { } result
        ? (result.HasRegularFeature ? 1 : 0) + (result.ExternalPrograms.Detected ? 2 : 0) +
            (result.Shaders.Detected ? 4 : 0) + (result.HasOtherFeature ? 8 : 0)
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
        legacyType = t;
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

    private void NotifyInspectionState()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsInspectionPending)));
        PropertyChanged?.Invoke(this, new(nameof(HasRegularFeature)));
        PropertyChanged?.Invoke(this, new(nameof(CanActivate)));
        PropertyChanged?.Invoke(this, new(nameof(FeatureSortKey)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
