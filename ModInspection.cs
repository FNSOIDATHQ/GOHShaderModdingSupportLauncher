namespace GOHShaderModdingSupportLauncher;

public enum ModSourceKind { Local, Workshop }
public enum ModStructureState { Regular, Invalid, Unconfirmed }
public enum ModInspectionReason
{
    MissingMetadata, InvalidMetadata, UnsupportedEncoding, NestedModRoot, NoContent,
    UnrecognizedLayout, UnreadablePath, UnreadablePak, UninspectedArchive,
    ReparsePoint, ScanLimit, UnsafeArchivePath, InvalidLoadId, DuplicateLoadId
}

public sealed record ModInspectionIssue(ModInspectionReason Reason, string RelativePath, string Detail = "");
public sealed record ModInspectionEvidence(string RelativePath, string Kind);

// Detected and Complete are independent: a positive hit survives an unreadable sibling.
public sealed record ModFeatureInspection(bool Detected, bool Complete, IReadOnlyList<ModInspectionEvidence> Evidence, int MatchCount)
{
    public bool EvidenceTruncated => MatchCount > Evidence.Count;
}

public sealed record ModInspectionResult(
    string DirectoryPath, ModSourceKind Source, string DiscoveryId, string? LoadId, string DisplayName,
    ModStructureState Structure, bool StructureComplete, IReadOnlyList<ModInspectionEvidence> ContentEvidence,
    ModFeatureInspection ExternalPrograms, ModFeatureInspection Shaders, IReadOnlyList<ModInspectionIssue> Issues, int ContentMatchCount)
{
    public bool IsRegular => Structure == ModStructureState.Regular;
    public bool ContentEvidenceTruncated => ContentMatchCount > ContentEvidence.Count;
    public bool CanActivate => IsRegular && LoadId is not null &&
        !Issues.Any(issue => issue.Reason == ModInspectionReason.DuplicateLoadId);
}

public sealed record ModScanProgress(int InspectedDirectories, string DirectoryPath);
public sealed record ModScanResult(IReadOnlyList<ModInspectionResult> Mods, IReadOnlyList<ModInspectionIssue> Issues);
