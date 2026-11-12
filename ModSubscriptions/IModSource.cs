using System.Net;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal enum ModSourceError { RequestFailed, Timeout, RateLimited, AccessDenied, NotFound, ParseFailed, Offline, VerificationRequired }
internal enum ModReadMode { Refresh, CacheOnly }
internal enum ModBrowseSort { Updated, Newest, Popular, Name }

internal sealed record ModSourceFailure(ModSourceError Kind, string Message,
    HttpStatusCode? StatusCode = null, DateTimeOffset? RetryAfter = null, ModHttpFailureKind? DiagnosticKind = null,
    Uri? VerificationUri = null);

// A failed refresh can carry old content. Callers must check IsSuccess before replacing a snapshot.
internal sealed record ModSourceResult<T>(T? Content, DateTimeOffset? FetchedAt,
    ModSourceFailure? Failure = null, bool IsStale = false, string? CacheWarning = null)
{
    internal bool IsSuccess => Failure is null && Content is not null;
}

internal sealed class ModSourceException(ModSourceFailure failure) : IOException(failure.Message)
{
    internal ModSourceFailure Failure { get; } = failure;
}

internal sealed record ModSourcePage<T>(T[] Items, Uri? NextPage);
internal sealed record ModFileReference(string Id, Uri PageUrl, string Title, string Description, DateTimeOffset? PublishedAt);
internal sealed record ModImageReference(string Id, Uri PageUrl, Uri ThumbnailUrl, string Caption);
internal sealed record ModFileHint(string Id, Uri PageUrl, string Title, DateTimeOffset? PublishedAt);
internal sealed record ModFileHints(ModFileHint[] Items)
{
    // RSS can be truncated and is never an initial baseline or evidence that an old file disappeared.
    public bool IsCompleteSnapshot => false;
}
internal sealed record ModFileSnapshot(RemoteModFile[] Files);

// Short lived, deliberately excluded from every persistence/JSON context.
internal sealed record ModDownloadLink(RemoteModFile File, Uri DownloadUrl, DateTimeOffset ResolvedAt);

internal interface IModSource
{
    string Provider { get; }
    ModSourceCapabilities Capabilities { get; }
    Task<ModSourceResult<ModSourcePage<RemoteMod>>> BrowseAsync(string keyword = "", int page = 1,
        ModBrowseSort sort = ModBrowseSort.Updated, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<RemoteMod>> GetModAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModSourcePage<ModArticle>>> GetArticlesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModArticle>> GetArticleAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModSourcePage<ModImageReference>>> GetImagesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModImage>> GetImageAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModSourcePage<ModFileReference>>> GetFilesAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<RemoteModFile>> GetFileAsync(Uri page, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModFileSnapshot>> GetFileSnapshotAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModFileHints>> ReadFileHintsAsync(Uri modPage, ModReadMode mode = ModReadMode.Refresh, CancellationToken cancellationToken = default);
    Task<ModSourceResult<ModDownloadLink>> ResolveDownloadAsync(Uri filePage, CancellationToken cancellationToken = default);
}
