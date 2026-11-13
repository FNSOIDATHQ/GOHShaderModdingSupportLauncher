using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Provider names are stable, lowercase keys (for example, "moddb"), never UI labels.
internal sealed record ModSourceId(string Provider, string RemoteId);

internal enum UpdateEvidence { None, FileIdentityAndRevision, PageOrLinkChange }

internal sealed record ModSourceCapabilities
{
    public bool CanBrowse { get; init; }
    public bool CanReadArticles { get; init; }
    public bool CanReadImages { get; init; }
    public bool CanListFiles { get; init; }
    public bool CanDownloadAnonymously { get; init; }
    public UpdateEvidence UpdateEvidence { get; init; }
}

internal sealed record ModArticle
{
    public required string Id { get; init; }
    public required Uri PageUrl { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public ModImage[] Images { get; init; } = [];
    public ModReadingDocument? Document { get; init; }
}

internal sealed record ModImage
{
    public required string Id { get; init; }
    public required Uri ImageUrl { get; init; }
    public Uri? PageUrl { get; init; }
    public Uri? ThumbnailUrl { get; init; }
    public string Caption { get; init; } = "";
}

internal enum RemoteProjectKind { Unknown, Mod, Tool, SourceOnly }

internal sealed record RemoteMod
{
    public required ModSourceId Id { get; init; }
    public required Uri PageUrl { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public ModReadingDocument? Document { get; init; }
    public ModSourceCapabilities Capabilities { get; init; } = new();
    public ModArticle[] Articles { get; init; } = [];
    public ModImage[] Images { get; init; } = [];
    public RemoteProjectKind Kind { get; init; }
    public string InstallationHint { get; init; } = "";
}

internal sealed record RemoteModFile
{
    public required string Id { get; init; }
    // An adapter supplied revision/fingerprint; it is separate from the stable file ID.
    public required string Revision { get; init; }
    public required Uri PageUrl { get; init; }
    public required string Name { get; init; }
    public Uri? BrowserUrl { get; init; }
    public string Description { get; init; } = "";
    public ModReadingDocument? Document { get; init; }
    public string? GroupId { get; init; }
    public long? SizeBytes { get; init; }
    public string? Md5 { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool IsSourceSnapshot { get; init; }
    public bool DownloadOnly { get; init; }
    public bool CanDownload { get; init; } = true;
    public string DownloadUnavailableReason { get; init; } = "";
    // Temporary mirror URLs and account credentials have no place in this model.
}

// Shared author notes belong to a release/version, independently of downloadable attachments.
internal sealed record ModFileGroup
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Uri PageUrl { get; init; }
    public string Description { get; init; } = "";
    public ModReadingDocument? Document { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string InstallationInstructions { get; init; } = "";
}

internal sealed record ModFavorite(ModSourceId Source, DateTimeOffset AddedAt);
internal enum FileNoticeState { Read, Ignored }
internal sealed record FileAcknowledgement(string FileId, string Revision, FileNoticeState State);
internal enum FileUpdateKind { NewFile, ChangedFile }
internal sealed record FileUpdateNotice(RemoteModFile File, FileUpdateKind Kind);
internal sealed record PageChangeNotice(string Revision, DateTimeOffset DetectedAt);

internal sealed record FileTrackingState
{
    [JsonRequired]
    public bool HasBaseline { get; init; }
    [JsonRequired]
    public RemoteModFile[] KnownFiles { get; init; } = [];
    public ModFileGroup[] Groups { get; init; } = [];
    [JsonRequired]
    public FileAcknowledgement[] AcknowledgedFiles { get; init; } = [];
    // Persist the reason for an unread notice, independently of downloads and installed versions.
    // Optional for libraries written before update tracking was implemented.
    public FileUpdateNotice[] Notices { get; init; } = [];
    // Page/link evidence cannot establish that a new remote file was uploaded.
    public string PageRevision { get; init; } = "";
    public string AcknowledgedPageRevision { get; init; } = "";
    public PageChangeNotice? PageNotice { get; init; }
    [JsonRequired]
    public DateTimeOffset? LastSuccessfulCheck { get; init; }
}

internal enum SubscriptionState { Active, RemovalPending, RemovalFailed }
internal enum DownloadState { Pending, Downloading, Downloaded, NeedsManualInstall, Failed, Cancelled, LocalFileMissing }
internal enum InstallationState { Pending, Installed, ReplacementPending, RemovalPending, Failed, LocalFilesMissing }

internal sealed record ModDownload
{
    public required Guid Id { get; init; }
    public required RemoteModFile File { get; init; }
    [JsonRequired]
    public DownloadState State { get; init; }
    [JsonRequired]
    public string? LocalPath { get; init; }
    [JsonRequired]
    public long ReceivedBytes { get; init; }
    public string? ContentSha256 { get; init; }
    [JsonRequired]
    public DateTimeOffset UpdatedAt { get; init; }
    public string? Error { get; init; }
}

internal sealed record ModInstallation
{
    public required Guid Id { get; init; }
    public required Guid DownloadId { get; init; }
    public required string DirectoryPath { get; init; }
    // Relative to DirectoryPath. A manually added file is not implicitly owned.
    public required string[] OwnedFiles { get; init; }
    // Older records have no fingerprints and cannot authorize destructive cleanup.
    public OwnedModFile[] OwnedFileDetails { get; init; } = [];
    [JsonRequired]
    public InstallationState State { get; init; }
    [JsonRequired]
    public DateTimeOffset UpdatedAt { get; init; }
    public string? Error { get; init; }
}

internal sealed record GameModSubscription
{
    public required Guid Id { get; init; }
    public required ModSourceId Source { get; init; }
    // The installation root (parent of mods), not binaries/x64 or the working directory.
    public required string GameDirectory { get; init; }
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; init; }
    [JsonRequired]
    public SubscriptionState State { get; init; }
    [JsonRequired]
    public FileTrackingState Files { get; init; } = new();
    [JsonRequired]
    public ModDownload[] Downloads { get; init; } = [];
    [JsonRequired]
    public ModInstallation[] Installations { get; init; } = [];
}

internal sealed record LocalModOwnership(Guid SubscriptionId, ModSourceId Source, ModInstallation Installation);

internal sealed record OwnedModFile(string RelativePath, long SizeBytes, string Sha256);

internal sealed record SubscriptionLibrary
{
    internal const int CurrentSchemaVersion = 5;
    public required int SchemaVersion { get; init; }
    public required long Revision { get; init; }
    public required RemoteMod[] Mods { get; init; }
    public required ModFavorite[] Favorites { get; init; }
    public required GameModSubscription[] Subscriptions { get; init; }

    internal static SubscriptionLibrary Empty() => new()
    {
        SchemaVersion = CurrentSchemaVersion, Revision = 0, Mods = [], Favorites = [], Subscriptions = []
    };
}
