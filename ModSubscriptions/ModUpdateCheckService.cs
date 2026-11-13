namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModUpdateCheckResult(Guid SubscriptionId, bool Succeeded,
    FileUpdateNotice[] Notices, DateTimeOffset? LastSuccessfulCheck, ModSourceFailure? Failure = null);

// Only successful complete Files snapshots establish/advance tracking. RSS and cached
// responses remain browsing hints and can never silently dismiss an unread update.
internal sealed class ModUpdateCheckService(SubscriptionStore store, Func<string, IModSource?> resolveSource,
    ModVerificationState? verification = null)
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly Dictionary<Guid, DateTimeOffset> nextAttempts = [];

    internal static FileUpdateNotice[] GetNotices(GameModSubscription subscription) => subscription.Files.Notices
        .Where(notice => !subscription.Files.AcknowledgedFiles.Any(ack =>
            ack.FileId == notice.File.Id && ack.Revision == notice.File.Revision)).ToArray();

    internal static PageChangeNotice? GetPageNotice(GameModSubscription subscription) =>
        subscription.Files.PageNotice is { } notice && notice.Revision != subscription.Files.AcknowledgedPageRevision ? notice : null;

    internal void AcknowledgePage(Guid subscriptionId, string revision, FileNoticeState state)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        store.ChangeFileTracking(subscriptionId, tracking =>
        {
            if (tracking.PageNotice?.Revision != revision || tracking.PageRevision != revision)
                throw new InvalidOperationException("This page revision is no longer the discovered change.");
            return tracking with { AcknowledgedPageRevision = revision };
        });
    }

    internal void Acknowledge(Guid subscriptionId, string fileId, string revision, FileNoticeState state)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        store.ChangeFileTracking(subscriptionId, tracking =>
        {
            if (!tracking.KnownFiles.Any(file => file.Id == fileId && file.Revision == revision))
                throw new InvalidOperationException("This file revision is no longer in the discovered files.");
            return tracking with
            {
                AcknowledgedFiles = [.. tracking.AcknowledgedFiles.Where(ack => ack.FileId != fileId || ack.Revision != revision),
                    new(fileId, revision, state)]
            };
        });
    }

    internal async Task<ModUpdateCheckResult> CheckAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CheckCoreAsync(subscriptionId, cancellationToken).ConfigureAwait(false); }
        finally { checkGate.Release(); }
    }

    internal async Task<ModUpdateCheckResult[]> CheckGameAsync(string gameDirectory, bool onlyDue = false,
        CancellationToken cancellationToken = default)
    {
        await checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ModUpdateCheckResult>();
            foreach (var subscription in store.GetSubscriptions(gameDirectory).Where(item => item.State == SubscriptionState.Active))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A human verification is never retried by the scheduler. Leave all snapshots,
                // notices and successful check timestamps intact until the user resumes it.
                if (onlyDue && IsSourcePaused(subscription.Source.Provider)) continue;
                var now = DateTimeOffset.UtcNow;
                if (onlyDue && ((nextAttempts.TryGetValue(subscription.Id, out var next) && next > now) ||
                    (subscription.Files.LastSuccessfulCheck is { } last && last + CheckInterval > now))) continue;
                results.Add(await CheckCoreAsync(subscription.Id, cancellationToken).ConfigureAwait(false));
            }
            return results.ToArray();
        }
        finally { checkGate.Release(); }
    }

    private bool IsSourcePaused(string provider) => verification?.GetPending().Any(request => provider switch
    {
        "moddb" => ModDbParser.IsMirrorHost(request.Origin),
        "mowportal" => MowPortalParser.IsSite(request.Origin) || YandexPublicFileResolver.IsDownloadHost(request.Origin) ||
            request.Origin.Host == "cloud-api.yandex.net",
        _ => false
    }) == true;

    private async Task<ModUpdateCheckResult> CheckCoreAsync(Guid id, CancellationToken token)
    {
        var subscription = store.Snapshot().Subscriptions.SingleOrDefault(item => item.Id == id);
        if (subscription is null)
            return new(id, false, [], null, new(ModSourceError.RequestFailed, "The subscription no longer exists."));
        ModUpdateCheckResult Result(bool success, ModSourceFailure? failure = null)
        {
            var latest = store.Snapshot().Subscriptions.SingleOrDefault(item => item.Id == id) ?? subscription;
            nextAttempts[id] = success ? DateTimeOffset.UtcNow + CheckInterval :
                failure?.Kind == ModSourceError.VerificationRequired ? DateTimeOffset.MinValue :
                failure?.RetryAfter is { } retry && retry > DateTimeOffset.UtcNow ? retry : DateTimeOffset.UtcNow.AddMinutes(5);
            return new(id, success, GetNotices(latest), latest.Files.LastSuccessfulCheck, failure);
        }

        if (subscription.State != SubscriptionState.Active)
            return Result(false, new(ModSourceError.RequestFailed, "Resolve the pending unsubscribe before checking files."));
        var mod = store.FindMod(subscription.Source);
        var source = resolveSource(subscription.Source.Provider);
        if (mod is null || source is null || source.Provider != subscription.Source.Provider ||
            !source.Capabilities.CanListFiles || source.Capabilities.UpdateEvidence is not
                (UpdateEvidence.FileIdentityAndRevision or UpdateEvidence.PageOrLinkChange))
            return Result(false, new(ModSourceError.RequestFailed, "This source does not provide complete file update evidence."));
        try
        {
            var response = source is GitHubModSource github
                ? await github.GetFileSnapshotByIdAsync(mod.Id.RemoteId, ModReadMode.Refresh, token).ConfigureAwait(false)
                : await source.GetFileSnapshotAsync(mod.PageUrl, ModReadMode.Refresh, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!response.IsSuccess || response.IsStale)
            {
                if (response.Failure?.Kind == ModSourceError.VerificationRequired)
                    verification?.Pause(response.Failure.VerificationUri ?? mod.PageUrl);
                return Result(false, response.Failure ?? new(ModSourceError.Offline, "Cached files cannot advance update tracking."));
            }
            var files = response.Content!.Files;
            bool pageEvidence = source.Capabilities.UpdateEvidence == UpdateEvidence.PageOrLinkChange;
            // Validate before entering the store callback, including an explicitly empty snapshot.
            if (files is null || files.Any(file => file is null) ||
                files.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != files.Length ||
                pageEvidence && string.IsNullOrWhiteSpace(response.Content.PageRevision))
                return Result(false, new(ModSourceError.ParseFailed, "The complete file snapshot contains invalid or duplicate file identities."));
            var checkedAt = DateTimeOffset.UtcNow;
            SubscriptionValidation.FileGroups(response.Content.Groups);
            store.ChangeFileTracking(id, previous => (pageEvidence
                ? AdvancePage(previous, files, response.Content.PageRevision, checkedAt)
                : Advance(previous, files, checkedAt)) with
                { Groups = MergeGroups(previous.Groups, response.Content.Groups) });
            return Result(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ModSourceException ex)
        {
            if (ex.Failure.Kind == ModSourceError.VerificationRequired)
                verification?.Pause(ex.Failure.VerificationUri ?? mod.PageUrl);
            return Result(false, ex.Failure);
        }
        catch (OperationCanceledException) { return Result(false, new(ModSourceError.Timeout, "The file check timed out.")); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Result(false, new(ex is InvalidDataException ? ModSourceError.ParseFailed : ModSourceError.RequestFailed, ex.Message));
        }
    }

    internal static ModFileGroup[] MergeGroups(ModFileGroup[] previous, ModFileGroup[] current) =>
        current.Concat(previous).DistinctBy(group => group.Id).ToArray();

    private static FileTrackingState AdvancePage(FileTrackingState previous, RemoteModFile[] files,
        string revision, DateTimeOffset checkedAt)
    {
        var known = previous.KnownFiles.ToDictionary(file => file.Id, StringComparer.Ordinal);
        foreach (var file in files) known[file.Id] = file;
        // A first successful read establishes the page baseline, including a page
        // with no download links. Removals and body-only changes still produce one
        // uncertain page notice; they never claim a file was uploaded or revised.
        bool first = !previous.HasBaseline || previous.PageRevision.Length == 0;
        return previous with
        {
            HasBaseline = true, KnownFiles = known.Values.ToArray(), Notices = [],
            LastSuccessfulCheck = checkedAt, PageRevision = revision,
            AcknowledgedPageRevision = first ? revision : previous.AcknowledgedPageRevision,
            PageNotice = first ? null : revision != previous.PageRevision
                ? new(revision, checkedAt) : previous.PageNotice
        };
    }

    private static FileTrackingState Advance(FileTrackingState previous, RemoteModFile[] files, DateTimeOffset checkedAt)
    {
        if (!previous.HasBaseline)
            return previous with
            {
                HasBaseline = true, KnownFiles = files, Notices = [], LastSuccessfulCheck = checkedAt,
                AcknowledgedFiles = [.. previous.AcknowledgedFiles,
                    .. files.Where(file => !previous.AcknowledgedFiles.Any(ack => ack.FileId == file.Id && ack.Revision == file.Revision))
                        .Select(file => new FileAcknowledgement(file.Id, file.Revision, FileNoticeState.Read))]
            };

        var known = previous.KnownFiles.ToDictionary(file => file.Id, StringComparer.Ordinal);
        var notices = previous.Notices.ToDictionary(notice => notice.File.Id, StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!known.TryGetValue(file.Id, out var older)) notices[file.Id] = new(file, FileUpdateKind.NewFile);
            else if (older.Revision != file.Revision) notices[file.Id] = new(file, FileUpdateKind.ChangedFile);
            else if (notices.TryGetValue(file.Id, out var notice)) notices[file.Id] = notice with { File = file };
            known[file.Id] = file;
        }
        // Keep discovered identities and pending notices when a file disappears from the
        // remote listing. A successful empty listing must not erase already discovered work.
        return previous with { KnownFiles = known.Values.ToArray(), Notices = notices.Values.ToArray(), LastSuccessfulCheck = checkedAt };
    }
}
