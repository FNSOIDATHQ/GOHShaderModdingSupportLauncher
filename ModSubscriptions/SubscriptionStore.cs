using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal enum SubscriptionLoadState { NewLibrary, Loaded, RecoveredFromBackup }

internal sealed class UnsupportedSubscriptionSchemaException(int version)
    : IOException($"Subscription schema {version} is not supported; the library was left unchanged.")
{
    internal int SchemaVersion { get; } = version;
}

internal sealed class SubscriptionStoreConflictException()
    : IOException("The subscription library changed in another store. Reload before retrying.");

// Metadata only. Install/delete services must coordinate filesystem changes separately.
internal sealed class SubscriptionStore
{
    private const int MaxLibraryBytes = 32 * 1024 * 1024;
    private readonly object sync = new();
    private SubscriptionLibrary library = SubscriptionLibrary.Empty();
    private byte[]? expectedPrimary;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GOHSMSLauncher", "subscriptions", "library.json");

    internal string LibraryPath { get; }
    internal string BackupPath => LibraryPath + ".bak";
    internal SubscriptionLoadState LoadState { get; private set; }
    internal string? RecoveredCorruptPath { get; private set; }

    internal SubscriptionStore(string? path = null)
    {
        LibraryPath = SubscriptionPaths.Normalize(path ?? DefaultPath);
        Reload();
    }

    // The lock file stays in place: deleting it could split concurrent writers' locks.
    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
        return new FileStream(LibraryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal void Reload()
    {
        lock (sync)
        {
            using var lease = AcquireLock();
            ReloadUnderLease();
        }
    }

    // Both normal loads and metadata-only cancellation use the same recovery rules.
    // The caller holds sync and the cross-process metadata writer lease.
    private void ReloadUnderLease()
    {
        byte[]? primary = null;
        SubscriptionLibrary? loaded = null;
        bool corrupt = false;
        try
        {
            primary = ReadBytes(LibraryPath);
            if (primary is not null) loaded = Deserialize(primary);
        }
        catch (InvalidDataException) { corrupt = true; }

        var state = SubscriptionLoadState.Loaded;
        string? quarantine = null;
        if (loaded is null)
        {
            var backup = ReadBytes(BackupPath);
            if (backup is not null)
            {
                loaded = Deserialize(backup); // Unknown schemas are never downgraded to empty/older data.
                quarantine = corrupt ? LibraryPath + ".corrupt." + Guid.NewGuid().ToString("N") : null;
                WriteAtomically(backup, corrupt || primary is not null, quarantine);
                primary = backup;
                state = SubscriptionLoadState.RecoveredFromBackup;
            }
            else if (corrupt)
                throw new InvalidDataException("The subscription library is corrupt and no valid backup is available.");
            else
            {
                loaded = SubscriptionLibrary.Empty();
                state = SubscriptionLoadState.NewLibrary;
            }
        }
        // Interrupted .tmp files are deliberately ignored, never promoted as committed state.
        library = loaded;
        expectedPrimary = primary;
        LoadState = state;
        RecoveredCorruptPath = quarantine;
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxLibraryBytes) throw new InvalidDataException("The subscription library is too large.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    private static SubscriptionLibrary Deserialize(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
                throw new InvalidDataException("The subscription schema version is missing or invalid.");
            if (version is not (1 or 2 or 3 or 4 or 5 or SubscriptionLibrary.CurrentSchemaVersion)) throw new UnsupportedSubscriptionSchemaException(version);
            var parsed = JsonSerializer.Deserialize(bytes, SubscriptionJsonContext.Default.SubscriptionLibrary)
                ?? throw new InvalidDataException("The subscription library is empty.");
            // Schema 6 adds an optional payload filename. Older downloads keep their paths,
            // checksums and ownership; they are rechecked, never silently adopted or installed.
            // Shared file notes are additive to schema 5. Generated init setters assign null
            // when older libraries omit this optional array; retain all existing tracking.
            if (parsed.Subscriptions is not null)
                parsed = parsed with { Subscriptions = parsed.Subscriptions.Select(subscription => subscription is null ? null! : subscription with
                { Files = subscription.Files is null ? null! : subscription.Files with { Groups = subscription.Files.Groups ?? [] } }).ToArray() };
            // Schema 1 predates content fingerprints, and schemas 1/2 predate persisted notice kinds.
            // Missing old fingerprints cannot authorize destructive cleanup. Upgrade on the next save;
            // Schema 4 adds GitHub project/file policy metadata. Source-generated init setters
            // assign null for an absent string even when the model has an initializer.
            // Restore that new optional field only for the older library formats;
            // older launchers must explicitly reject the new format instead of trying backup recovery.
            if (version <= 3 && parsed.Mods is not null)
                parsed = parsed with
                {
                    Mods = parsed.Mods.Select(mod => mod is null ? null! : mod with
                    { InstallationHint = mod.InstallationHint ?? "" }).ToArray()
                };
            if (version is 1 or 2 && parsed.Subscriptions is not null)
                parsed = parsed with
                {
                    Subscriptions = parsed.Subscriptions.Select(subscription => subscription is null ? null! : subscription with
                    {
                        Files = subscription.Files is null ? null! : subscription.Files with { Notices = subscription.Files.Notices ?? [] },
                        Installations = subscription.Installations?.Select(installation => installation is null ? null! : installation with
                        { OwnedFileDetails = version == 1 ? installation.OwnedFileDetails ?? [] : installation.OwnedFileDetails }).ToArray()!
                    }).ToArray()
                };
            // Schema 5 adds anonymous download availability and independent page evidence.
            // All older providers supported downloading their file candidates; restore that
            // policy explicitly because generated init setters do not retain absent defaults.
            if (version <= 4 && parsed.Subscriptions is not null)
                parsed = parsed with
                {
                    Subscriptions = parsed.Subscriptions.Select(subscription => subscription is null ? null! : subscription with
                    {
                        Files = subscription.Files is null ? null! : subscription.Files with
                        {
                            PageRevision = "", AcknowledgedPageRevision = "", PageNotice = null,
                            KnownFiles = subscription.Files.KnownFiles?.Select(LegacyFile).ToArray()!,
                            Notices = subscription.Files.Notices?.Select(notice => notice is null ? null! :
                                notice with { File = LegacyFile(notice.File) }).ToArray()!
                        },
                        Downloads = subscription.Downloads?.Select(download => download is null ? null! :
                            download with { File = LegacyFile(download.File) }).ToArray()!
                    }).ToArray()
                };
            parsed = parsed with { SchemaVersion = SubscriptionLibrary.CurrentSchemaVersion };
            SubscriptionValidation.Library(parsed);
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The subscription library is invalid.", ex);
        }
    }

    private static byte[] Serialize(SubscriptionLibrary value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, SubscriptionJsonContext.Default.SubscriptionLibrary);

    internal static RemoteModFile LegacyFile(RemoteModFile file) => file is null ? null! : file with
    { CanDownload = true, DownloadUnavailableReason = "" };

    internal SubscriptionLibrary Snapshot()
    {
        lock (sync) return Deserialize(Serialize(library));
    }

    private void Change(Func<SubscriptionLibrary, SubscriptionLibrary> change)
    {
        lock (sync)
        {
            using var lease = AcquireLock();
            var current = ReadBytes(LibraryPath);
            if (current is null ? expectedPrimary is not null : expectedPrimary is null || !current.AsSpan().SequenceEqual(expectedPrimary))
                throw new SubscriptionStoreConflictException();

            var next = change(Deserialize(Serialize(library))) with { Revision = checked(library.Revision + 1) };
            SubscriptionValidation.Library(next);
            var bytes = Serialize(next);
            if (bytes.Length > MaxLibraryBytes) throw new InvalidDataException("The subscription library is too large.");
            var committed = Deserialize(bytes); // Detach caller-owned arrays before committing.
            WriteAtomically(bytes, current is not null, current is null ? null : BackupPath);
            // A failed save cannot change the in-memory state or the last committed file.
            library = committed;
            expectedPrimary = bytes;
        }
    }

    private void WriteAtomically(byte[] bytes, bool replace, string? backup)
    {
        string temporary = LibraryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (replace) System.IO.File.Replace(temporary, LibraryPath, backup);
            else System.IO.File.Move(temporary, LibraryPath);
        }
        finally
        {
            try { System.IO.File.Delete(temporary); }
            catch (IOException) { /* An orphan temp cannot supersede a committed library. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal RemoteMod? FindMod(ModSourceId source) => Snapshot().Mods.SingleOrDefault(mod => mod.Id == source);

    internal void SaveMod(RemoteMod mod) => Change(value => value with
    {
        Mods = [.. value.Mods.Where(item => item.Id != mod.Id), mod]
    });

    internal void SetFavorite(ModSourceId source, bool favorite) => Change(value => value with
    {
        Favorites = favorite
            ? value.Favorites.Any(item => item.Source == source) ? value.Favorites : [.. value.Favorites, new(source, DateTimeOffset.UtcNow)]
            : value.Favorites.Where(item => item.Source != source).ToArray()
    });

    internal GameModSubscription[] GetSubscriptions(string gameDirectory)
    {
        string game = SubscriptionPaths.Normalize(gameDirectory);
        return Snapshot().Subscriptions.Where(item => item.GameDirectory.Equals(game, SubscriptionPaths.Comparison)).ToArray();
    }

    internal GameModSubscription? FindSubscription(ModSourceId source, string gameDirectory) =>
        GetSubscriptions(gameDirectory).SingleOrDefault(item => item.Source == source);

    internal GameModSubscription Subscribe(ModSourceId source, string gameDirectory, IReadOnlyCollection<RemoteModFile> selectedFiles)
    {
        if (selectedFiles.Count == 0) throw new ArgumentException("Choose at least one file before subscribing.", nameof(selectedFiles));
        if (selectedFiles.Any(file => file is null || !file.CanDownload))
            throw new ArgumentException("Only supported anonymous downloads can be selected.", nameof(selectedFiles));
        if (selectedFiles.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != selectedFiles.Count)
            throw new ArgumentException("A file may only be selected once.", nameof(selectedFiles));
        string game = SubscriptionPaths.Normalize(gameDirectory);
        GameModSubscription result = null!;
        Change(value =>
        {
            var existing = value.Subscriptions.SingleOrDefault(item => item.Source == source && item.GameDirectory.Equals(game, SubscriptionPaths.Comparison));
            if (existing is not null && existing.State != SubscriptionState.Active)
                throw new InvalidOperationException("Resolve the pending unsubscribe before selecting more files.");
            result = existing ?? new() { Id = Guid.NewGuid(), Source = source, GameDirectory = game, CreatedAt = DateTimeOffset.UtcNow };
            var downloads = result.Downloads.ToList();
            foreach (var file in selectedFiles)
                if (!downloads.Any(item => item.File.Id == file.Id && item.File.Revision == file.Revision))
                    downloads.Add(new() { Id = Guid.NewGuid(), File = file, State = DownloadState.Pending, UpdatedAt = DateTimeOffset.UtcNow });
            result = result with { Downloads = downloads.ToArray() };
            return value with { Subscriptions = [.. value.Subscriptions.Where(item => item.Id != result.Id), result] };
        });
        return result;
    }

    private void ChangeSubscription(Guid id, Func<GameModSubscription, GameModSubscription> change) => Change(value =>
    {
        if (!value.Subscriptions.Any(item => item.Id == id)) throw new KeyNotFoundException("The subscription does not exist.");
        return value with { Subscriptions = value.Subscriptions.Select(item => item.Id == id ? change(item) : item).ToArray() };
    });

    internal void SetSubscriptionState(Guid id, SubscriptionState state) =>
        ChangeSubscription(id, value => value with { State = state });

    internal void SaveFileTracking(Guid id, FileTrackingState files) =>
        ChangeSubscription(id, value => value with { Files = files });

    // Re-read tracking inside the metadata write lock: a network check must not undo
    // an acknowledgement or a download committed while its request was in flight.
    internal void ChangeFileTracking(Guid id, Func<FileTrackingState, FileTrackingState> change) =>
        ChangeSubscription(id, value => value with { Files = change(value.Files) });

    internal void SaveDownload(Guid subscriptionId, ModDownload download) =>
        ChangeSubscription(subscriptionId, value => WithDownload(value, download));

    // A cancelled queued task may commit while another process transfers a different
    // file. Merge that unrelated metadata, but retain optimistic concurrency for this
    // exact download and the subscription's removal state.
    internal void SaveDownloadProgress(Guid subscriptionId, ModDownload download)
    {
        lock (sync)
        {
            using var lease = AcquireLock();
            var current = ReadBytes(LibraryPath) ?? throw new SubscriptionStoreConflictException();
            var latest = Deserialize(current);
            var expectedSubscription = library.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId);
            var subscription = latest.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId);
            var expected = expectedSubscription?.Downloads.SingleOrDefault(item => item.Id == download.Id);
            var existing = subscription?.Downloads.SingleOrDefault(item => item.Id == download.Id);
            if (subscription is null || expectedSubscription is null || expected is null || existing is null ||
                subscription.State != SubscriptionState.Active || expectedSubscription.State != SubscriptionState.Active ||
                subscription.Source != expectedSubscription.Source || subscription.GameDirectory != expectedSubscription.GameDirectory ||
                !JsonSerializer.SerializeToUtf8Bytes(existing, SubscriptionJsonContext.Default.ModDownload).AsSpan().SequenceEqual(
                    JsonSerializer.SerializeToUtf8Bytes(expected, SubscriptionJsonContext.Default.ModDownload)))
                throw new SubscriptionStoreConflictException();
            var next = latest with
            {
                Revision = checked(latest.Revision + 1),
                Subscriptions = latest.Subscriptions.Select(item => item.Id == subscriptionId ? WithDownload(item, download) : item).ToArray()
            };
            SubscriptionValidation.Library(next);
            var bytes = Serialize(next);
            if (bytes.Length > MaxLibraryBytes) throw new InvalidDataException("The subscription library is too large.");
            var committed = Deserialize(bytes);
            WriteAtomically(bytes, replace: true, BackupPath);
            library = committed;
            expectedPrimary = bytes;
        }
    }

    private static GameModSubscription WithDownload(GameModSubscription value, ModDownload download)
    {
        if (!value.Downloads.Any(item => item.Id == download.Id)) throw new KeyNotFoundException("The selected download does not exist.");
        var previous = value.Downloads.Single(item => item.Id == download.Id);
        if (previous.File.Id != download.File.Id || previous.File.Revision != download.File.Revision)
            throw new InvalidOperationException("A download's source file and revision cannot be reassigned.");
        var normalized = download with { LocalPath = download.LocalPath is null ? null : SubscriptionPaths.ManagedPath(value.GameDirectory, download.LocalPath) };
        return value with { Downloads = value.Downloads.Select(item => item.Id == download.Id ? normalized : item).ToArray() };
    }

    // Cancellation before taking the game lease is metadata only. Merge into the latest
    // committed library under its writer lease instead of saving a stale download snapshot.
    // A queued job cannot cancel a newer attempt owned by another launcher process.
    internal ModDownload? CancelPendingDownload(Guid subscriptionId, Guid downloadId, DateTimeOffset? expectedUpdatedAt)
    {
        lock (sync)
        {
            using var lease = AcquireLock();
            ReloadUnderLease();
            var current = expectedPrimary;
            var latest = library;
            var subscription = latest.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId);
            var download = subscription?.Downloads.SingleOrDefault(item => item.Id == downloadId);
            if (download is not null && download.State is DownloadState.Pending or DownloadState.Downloading &&
                (expectedUpdatedAt is null || download.UpdatedAt == expectedUpdatedAt))
            {
                download = download with { State = DownloadState.Cancelled, Error = null, UpdatedAt = DateTimeOffset.UtcNow };
                var cancelled = download;
                latest = latest with
                {
                    Revision = checked(latest.Revision + 1),
                    Subscriptions = latest.Subscriptions.Select(item => item.Id == subscriptionId ? item with
                    { Downloads = item.Downloads.Select(task => task.Id == downloadId ? cancelled : task).ToArray() } : item).ToArray()
                };
                SubscriptionValidation.Library(latest);
                var bytes = Serialize(latest);
                if (bytes.Length > MaxLibraryBytes) throw new InvalidDataException("The subscription library is too large.");
                var committed = Deserialize(bytes);
                WriteAtomically(bytes, current is not null, current is null ? null : BackupPath);
                latest = committed;
                current = bytes;
            }
            library = latest;
            expectedPrimary = current;
            return download;
        }
    }

    // Explicit installer output only; this method never scans or adopts a matching local folder.
    internal void SaveInstallation(Guid subscriptionId, ModInstallation installation) => ChangeSubscription(subscriptionId, value =>
    {
        var previous = value.Installations.SingleOrDefault(item => item.Id == installation.Id);
        if (previous is not null && (previous.DownloadId != installation.DownloadId ||
            !previous.DirectoryPath.Equals(installation.DirectoryPath, SubscriptionPaths.Comparison)))
            throw new InvalidOperationException("An installation's download and directory cannot be reassigned.");
        var normalized = installation with
        {
            DirectoryPath = SubscriptionPaths.InstallationDirectory(value.GameDirectory, installation.DirectoryPath),
            OwnedFiles = installation.OwnedFiles.Select(SubscriptionPaths.RelativeFile).ToArray()
        };
        return value with { Installations = [.. value.Installations.Where(item => item.Id != installation.Id), normalized] };
    });

    internal LocalModOwnership? FindLocalOwner(string gameDirectory, string localPath)
    {
        string full = SubscriptionPaths.Normalize(localPath);
        foreach (var subscription in GetSubscriptions(gameDirectory))
            foreach (var installation in subscription.Installations)
                if (installation.DirectoryPath.Equals(full, SubscriptionPaths.Comparison) || installation.OwnedFiles.Any(file =>
                    Path.Combine(installation.DirectoryPath, file).Equals(full, SubscriptionPaths.Comparison)))
                    return new(subscription.Id, subscription.Source, installation);
        return null;
    }

    // The filesystem service commits a fully prepared transaction in a single library write.
    internal void CommitInstallation(Guid subscriptionId, ModInstallation installation, Guid? replacementId) =>
        ChangeSubscription(subscriptionId, value =>
        {
            if (value.State != SubscriptionState.Active || installation.State != InstallationState.Installed ||
                !value.Downloads.Any(item => item.Id == installation.DownloadId && item.State is DownloadState.Downloaded or DownloadState.NeedsManualInstall))
                throw new InvalidOperationException("The selected file is not ready for installation.");
            if (replacementId is { } id && !value.Installations.Any(item => item.Id == id))
                throw new InvalidOperationException("The managed installation to replace no longer exists.");
            return value with
            {
                Installations = [.. value.Installations.Where(item => item.Id != replacementId), installation],
                Downloads = value.Downloads.Select(item => item.Id == installation.DownloadId ? item with
                { State = DownloadState.Downloaded, Error = null, UpdatedAt = DateTimeOffset.UtcNow } : item).ToArray()
            };
        });

    // Only the filesystem service calls this after removing this download's verified content.
    // Removing the last selected file also ends the Mod subscription in the same commit.
    internal void CommitDownloadRemoval(Guid subscriptionId, Guid downloadId) => Change(value =>
    {
        var subscription = value.Subscriptions.Single(item => item.Id == subscriptionId);
        var download = subscription.Downloads.Single(item => item.Id == downloadId);
        if (download.State is DownloadState.Pending or DownloadState.Downloading)
            throw new InvalidOperationException("The selected file is not ready for cleanup.");
        var remaining = subscription with
        {
            Downloads = subscription.Downloads.Where(item => item.Id != downloadId).ToArray(),
            Installations = subscription.Installations.Where(item => item.DownloadId != downloadId).ToArray()
        };
        return value with
        {
            Subscriptions = remaining.Downloads.Length == 0
                ? value.Subscriptions.Where(item => item.Id != subscriptionId).ToArray()
                : value.Subscriptions.Select(item => item.Id == subscriptionId ? remaining : item).ToArray()
        };
    });

    internal void CommitRemoval(Guid subscriptionId) => Change(value =>
    {
        var subscription = value.Subscriptions.Single(item => item.Id == subscriptionId);
        if (subscription.State == SubscriptionState.Active || subscription.Downloads.Any(item => item.State == DownloadState.Downloading))
            throw new InvalidOperationException("The subscription is not ready for cleanup.");
        return value with { Subscriptions = value.Subscriptions.Where(item => item.Id != subscriptionId).ToArray() };
    });

    // Removing metadata is only safe once the filesystem service has cleared its records.
    // Filesystem cleanup must go through ModInstallService before removing owned records.
    internal void RemoveSubscription(Guid id) => Change(value =>
    {
        var subscription = value.Subscriptions.SingleOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("The subscription does not exist.");
        if (subscription.Installations.Length > 0 || subscription.Downloads.Any(item => item.LocalPath is not null || item.State == DownloadState.Downloading))
            throw new InvalidOperationException("Managed content or a running download still needs cleanup; keep the subscription for retry.");
        return value with { Subscriptions = value.Subscriptions.Where(item => item.Id != id).ToArray() };
    });
}
