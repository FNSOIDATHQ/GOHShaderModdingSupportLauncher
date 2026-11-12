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
            if (version != SubscriptionLibrary.CurrentSchemaVersion) throw new UnsupportedSubscriptionSchemaException(version);
            var parsed = JsonSerializer.Deserialize(bytes, SubscriptionJsonContext.Default.SubscriptionLibrary)
                ?? throw new InvalidDataException("The subscription library is empty.");
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

    internal void SaveDownload(Guid subscriptionId, ModDownload download) => ChangeSubscription(subscriptionId, value =>
    {
        if (!value.Downloads.Any(item => item.Id == download.Id)) throw new KeyNotFoundException("The selected download does not exist.");
        var previous = value.Downloads.Single(item => item.Id == download.Id);
        if (previous.File.Id != download.File.Id || previous.File.Revision != download.File.Revision)
            throw new InvalidOperationException("A download's source file and revision cannot be reassigned.");
        var normalized = download with { LocalPath = download.LocalPath is null ? null : SubscriptionPaths.ManagedPath(value.GameDirectory, download.LocalPath) };
        return value with { Downloads = value.Downloads.Select(item => item.Id == download.Id ? normalized : item).ToArray() };
    });

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

    // Removing metadata is only safe once the filesystem service has cleared its records.
    // Stage one intentionally provides no operation that deletes installed or downloaded files.
    internal void RemoveSubscription(Guid id) => Change(value =>
    {
        var subscription = value.Subscriptions.SingleOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("The subscription does not exist.");
        if (subscription.Installations.Length > 0 || subscription.Downloads.Any(item => item.LocalPath is not null || item.State == DownloadState.Downloading))
            throw new InvalidOperationException("Managed content or a running download still needs cleanup; keep the subscription for retry.");
        return value with { Subscriptions = value.Subscriptions.Where(item => item.Id != id).ToArray() };
    });
}
