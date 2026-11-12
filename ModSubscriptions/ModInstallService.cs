using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModInstallRequest(Guid SubscriptionId, Guid DownloadId, string CandidateRoot,
    bool ConfirmCompletePackage, Guid? ReplaceInstallationId = null);
internal sealed record ModCleanupFile(string Path, OwnedModFile Fingerprint);
internal sealed record ModCleanupPlan(Guid SubscriptionId, string GameDirectory, ModCleanupFile[] Files,
    string[] PreservedPaths, string[] BlockingReasons);

internal enum ModTransactionKind { Install, Remove }
internal sealed record ModFileMove(string SourcePath, string BackupName, OwnedModFile Fingerprint);
internal sealed record ModFileTransaction
{
    public required int Version { get; init; }
    public required Guid Id { get; init; }
    public required string GameDirectory { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required ModTransactionKind Kind { get; init; }
    public ModInstallation? Installation { get; init; }
    public ModInstallation? PreviousInstallation { get; init; }
    public ModFileMove[] Moves { get; init; } = [];
    public string[] RemovedDirectories { get; init; } = [];
    public bool RemovalStarted { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModFileTransaction))]
internal partial class ModTransactionJsonContext : JsonSerializerContext { }

// These operations are synchronous disk work; UI callers run them on a worker thread.
// All writers cooperate through a per-game process lock, independently of the library lock.
internal sealed class ModInstallService(SubscriptionStore store, IModInstallationHost host)
{
    private readonly ModArchiveService archives = new();

    internal void MarkDownloadOnly(Guid subscriptionId, Guid downloadId)
    {
        var subscription = Subscription(subscriptionId);
        using var lease = GameOperationLock.Acquire(subscription.GameDirectory);
        var download = ReadyDownload(Subscription(subscriptionId), downloadId);
        store.SaveDownload(subscriptionId, download with
        {
            State = DownloadState.NeedsManualInstall, UpdatedAt = DateTimeOffset.UtcNow,
            Error = "Downloaded only. Follow the author's instructions; files outside mods are not managed by the launcher."
        });
    }
    internal ModArchiveInspection Inspect(Guid subscriptionId, Guid downloadId, CancellationToken token = default)
    {
        var subscription = Subscription(subscriptionId);
        using var lease = GameOperationLock.Acquire(subscription.GameDirectory);
        var download = ReadyDownload(subscription, downloadId);
        return archives.Inspect(download.LocalPath!, download.File, token);
    }

    internal ModInstallation Install(ModInstallRequest request, CancellationToken token = default)
    {
        if (!request.ConfirmCompletePackage)
            throw new InvalidOperationException("Confirm that the selected directory is a complete standard Mod; patches require manual installation. Read the author's instructions first.");
        var subscription = Subscription(request.SubscriptionId);
        string game = subscription.GameDirectory;
        using var lease = GameOperationLock.Acquire(game);
        RecoverCore(game);
        subscription = Subscription(request.SubscriptionId);
        if (subscription.State != SubscriptionState.Active) throw new InvalidOperationException("This subscription is being removed.");
        EnsureStopped(game);
        var download = ReadyDownload(subscription, request.DownloadId);
        var inspection = archives.Inspect(download.LocalPath!, download.File, token);
        if (inspection.ManualInstallReason is not null) throw new InvalidOperationException(inspection.ManualInstallReason);
        var candidate = inspection.Candidates.SingleOrDefault(item => item.RootPath == request.CandidateRoot)
            ?? throw new InvalidOperationException("Explicitly select one of the detected Mod directories.");
        if (candidate.DirectoryName is null) throw new InvalidOperationException("The archive has no original Mod directory name. Keep it as a manual download.");
        string destination = SubscriptionPaths.InstallationDirectory(game, Path.Combine(SubscriptionPaths.ModsDirectory(game), candidate.DirectoryName));
        ManagedModFiles.EnsureSafe(destination);
        var previous = request.ReplaceInstallationId is { } replacement
            ? subscription.Installations.SingleOrDefault(item => item.Id == replacement)
                ?? throw new InvalidOperationException("The selected managed installation no longer exists.")
            : null;
        if (previous is not null)
        {
            if (!previous.DirectoryPath.Equals(destination, SubscriptionPaths.Comparison))
                throw new InvalidOperationException("A replacement must retain the original Mod directory name.");
            RequireUnchangedDirectory(previous);
        }
        else if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("This Mod directory already exists. A manual Mod or another subscription cannot be overwritten.");

        string transactionDirectory = NewTransactionDirectory(game, out Guid transactionId);
        string staging = Path.Combine(transactionDirectory, "staged");
        // A preparation journal means a crash during extraction never leaves an unrecorded task.
        var transaction = new ModFileTransaction
        {
            Version = 1, Id = transactionId, GameDirectory = game, SubscriptionId = subscription.Id,
            Kind = ModTransactionKind.Install, PreviousInstallation = previous
        };
        WriteJournal(transactionDirectory, transaction);
        bool committed = false;
        try
        {
            var extracted = archives.Extract(download.LocalPath!, candidate, staging, token);
            var installation = new ModInstallation
            {
                Id = Guid.NewGuid(), DownloadId = download.Id, DirectoryPath = destination,
                OwnedFiles = extracted.OwnedFiles, State = InstallationState.Installed, UpdatedAt = DateTimeOffset.UtcNow,
                OwnedFileDetails = extracted.OwnedFiles.Select(file => ManagedModFiles.Fingerprint(staging, file)).ToArray()
            };
            transaction = transaction with { Installation = installation };
            WriteJournal(transactionDirectory, transaction);
            token.ThrowIfCancellationRequested();
            EnsureStopped(game);
            if (previous is not null)
            {
                RequireUnchangedDirectory(previous);
                Directory.Move(destination, Path.Combine(transactionDirectory, "previous"));
            }
            ManagedModFiles.EnsureSafe(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The installation destination became occupied.");
            Directory.Move(staging, destination);
            RequireUnchangedDirectory(installation);
            EnsureStopped(game);
            store.CommitInstallation(subscription.Id, installation, previous?.Id);
            committed = true;
            FinishCommitted(transactionDirectory, transaction);
            return installation;
        }
        catch
        {
            if (!committed) RollBack(transactionDirectory, transaction);
            // Once metadata committed, keep the journal until refresh/backup cleanup can be retried.
            throw;
        }
    }

    // A reviewable scope, including manual additions and anything that cannot safely be deleted.
    internal ModCleanupPlan PreviewUnsubscribe(Guid subscriptionId)
    {
        var subscription = Subscription(subscriptionId);
        using var lease = GameOperationLock.Acquire(subscription.GameDirectory);
        RecoverCore(subscription.GameDirectory);
        return BuildCleanupPlan(Subscription(subscriptionId));
    }

    internal void Unsubscribe(ModCleanupPlan reviewedPlan, CancellationToken token = default)
    {
        var subscription = Subscription(reviewedPlan.SubscriptionId);
        string game = subscription.GameDirectory;
        using var lease = GameOperationLock.Acquire(game);
        RecoverCore(game);
        subscription = Subscription(subscription.Id);
        if (!game.Equals(reviewedPlan.GameDirectory, SubscriptionPaths.Comparison)) throw new InvalidOperationException("The cleanup belongs to a different game.");
        EnsureStopped(game);
        var current = BuildCleanupPlan(subscription);
        if (current.BlockingReasons.Length > 0) throw new IOException(string.Join("\n", current.BlockingReasons));
        if (reviewedPlan.BlockingReasons.Length > 0 || !SamePlan(current, reviewedPlan))
            throw new IOException("The cleanup scope changed. Review the current file list before retrying.");

        // With the game lock held, Downloading can only be an interrupted process's stale state.
        foreach (var download in subscription.Downloads.Where(item => item.State == DownloadState.Downloading))
            store.SaveDownload(subscription.Id, download with { State = DownloadState.Cancelled, UpdatedAt = DateTimeOffset.UtcNow });
        store.SetSubscriptionState(subscription.Id, SubscriptionState.RemovalPending);
        string transactionDirectory = NewTransactionDirectory(game, out Guid transactionId);
        var transaction = new ModFileTransaction
        {
            Version = 1, Id = transactionId, GameDirectory = game, SubscriptionId = subscription.Id,
            Kind = ModTransactionKind.Remove,
            Moves = current.Files.Select((file, index) => new ModFileMove(file.Path, index.ToString("D8"), file.Fingerprint)).ToArray(),
            // This list is separate from file moves because directories can already be missing.
            RemovedDirectories = subscription.Installations.Select(item => item.DirectoryPath).ToArray()
        };
        WriteJournal(transactionDirectory, transaction);
        bool removalStarted = false;
        try
        {
            foreach (var move in transaction.Moves)
            {
                token.ThrowIfCancellationRequested();
                EnsureStopped(game);
                if (!ManagedModFiles.Matches(move.SourcePath, move.Fingerprint)) throw new IOException("A file changed after the cleanup preview.");
                File.Move(move.SourcePath, Path.Combine(transactionDirectory, move.BackupName));
            }
            EnsureStopped(game);
            token.ThrowIfCancellationRequested();
            // Deletion is irreversible: persist this boundary before removing the staged files.
            // Keep the subscription and its ownership until physical cleanup and enabled-list writes succeed.
            transaction = transaction with { RemovalStarted = true };
            WriteJournal(transactionDirectory, transaction);
            removalStarted = true;
            FinishRemoval(transactionDirectory, transaction);
        }
        catch
        {
            if (!removalStarted)
            {
                try { RollBack(transactionDirectory, transaction); }
                finally { store.SetSubscriptionState(subscription.Id, SubscriptionState.RemovalFailed); }
            }
            else if (store.Snapshot().Subscriptions.Any(item => item.Id == subscription.Id))
                store.SetSubscriptionState(subscription.Id, SubscriptionState.RemovalFailed);
            throw;
        }
    }

    internal void Recover(string gameDirectory)
    {
        string game = SubscriptionPaths.Normalize(gameDirectory);
        using var lease = GameOperationLock.Acquire(game);
        RecoverCore(game);
    }

    private void RecoverCore(string game)
    {
        store.Reload(); // Another process may have committed the library before leaving its journal.
        string root = Path.Combine(ManagedModFiles.PrivateRoot(game), "transactions");
        ManagedModFiles.EnsureSafe(root);
        if (!Directory.Exists(root)) return;
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            ManagedModFiles.EnsureSafe(directory);
            string journal = Path.Combine(directory, "transaction.json");
            if (!File.Exists(journal)) continue; // Never adopt an uncommitted temporary journal.
            EnsureStopped(game);
            ManagedModFiles.EnsureSafe(journal);
            if (new FileInfo(journal).Length > 32 * 1024 * 1024) throw new InvalidDataException("The installation recovery journal is too large.");
            var transaction = JsonSerializer.Deserialize(File.ReadAllBytes(journal), ModTransactionJsonContext.Default.ModFileTransaction)
                ?? throw new InvalidDataException("The installation recovery journal is invalid.");
            ValidateJournal(directory, game, transaction);
            var subscription = store.Snapshot().Subscriptions.SingleOrDefault(item => item.Id == transaction.SubscriptionId);
            bool committed = transaction.Kind == ModTransactionKind.Remove ? subscription is null :
                transaction.Installation is { } installation && subscription?.Installations.Any(item => item.Id == installation.Id) == true;
            if (transaction.Kind == ModTransactionKind.Remove && (transaction.RemovalStarted || committed))
            {
                try { FinishRemoval(directory, transaction); }
                catch
                {
                    if (store.Snapshot().Subscriptions.Any(item => item.Id == transaction.SubscriptionId))
                        store.SetSubscriptionState(transaction.SubscriptionId, SubscriptionState.RemovalFailed);
                    throw;
                }
            }
            else if (committed) FinishCommitted(directory, transaction);
            else RollBack(directory, transaction);
        }
    }

    private GameModSubscription Subscription(Guid id) => store.Snapshot().Subscriptions.Single(item => item.Id == id);

    private static ModDownload ReadyDownload(GameModSubscription subscription, Guid id)
    {
        var download = subscription.Downloads.Single(item => item.Id == id);
        if (download.State is not (DownloadState.Downloaded or DownloadState.NeedsManualInstall) || download.LocalPath is null ||
            !download.LocalPath.Equals(ModDownloadService.ArtifactPath(subscription.GameDirectory, id), SubscriptionPaths.Comparison))
            throw new InvalidOperationException("The selected file has not completed downloading.");
        ManagedModFiles.EnsureSafe(download.LocalPath);
        if (!File.Exists(download.LocalPath)) throw new FileNotFoundException("The selected download is missing.");
        if (download.ContentSha256 is null && download.File.Md5 is null)
            throw new InvalidDataException("The existing download lacks a durable checksum. Download it again before installing.");
        // Revalidate the payload, including its original source checksum, before every inspection/install.
        using var stream = new FileStream(download.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (download.File.SizeBytes is { } size && stream.Length != size) throw new InvalidDataException("The downloaded file size changed.");
        if (download.File.Md5 is { } md5 && !Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(stream)).Equals(md5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded file checksum changed.");
        stream.Position = 0;
        if (download.ContentSha256 is { } sha && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != sha)
            throw new InvalidDataException("The downloaded file content changed.");
        return download;
    }

    private void EnsureStopped(string game)
    {
        ManagedModFiles.EnsureSafe(SubscriptionPaths.ModsDirectory(game));
        if (host.IsGameRunning(game)) throw new IOException("Stop the game and editor before installing, replacing or removing Mod files.");
    }

    private static void RequireUnchangedDirectory(ModInstallation installation)
    {
        string root = installation.DirectoryPath;
        ManagedModFiles.EnsureSafe(root);
        if (!Directory.Exists(root) || installation.OwnedFileDetails.Length == 0 ||
            installation.OwnedFileDetails.Length != installation.OwnedFiles.Length)
            throw new IOException("The managed directory is missing or lacks a verified ownership manifest.");
        var actual = ManagedModFiles.Files(root).Select(file => Path.GetRelativePath(root, file)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(installation.OwnedFiles) || installation.OwnedFileDetails.Any(file =>
            !ManagedModFiles.Matches(Path.Combine(root, file.RelativePath), file)))
            throw new IOException("The directory contains added, missing or modified files. Preserve those files before replacing this complete package.");
    }

    private static ModCleanupPlan BuildCleanupPlan(GameModSubscription subscription)
    {
        var files = new List<ModCleanupFile>();
        var preserved = new List<string>();
        var blocked = new List<string>();
        foreach (var installation in subscription.Installations)
        {
            ManagedModFiles.EnsureSafe(installation.DirectoryPath);
            var owned = installation.OwnedFileDetails.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (string file in ManagedModFiles.Files(installation.DirectoryPath))
            {
                string relative = Path.GetRelativePath(installation.DirectoryPath, file);
                if (owned.TryGetValue(relative, out var detail) && ManagedModFiles.Matches(file, detail)) files.Add(new(file, detail));
                else
                {
                    preserved.Add(file);
                    if (installation.OwnedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                        blocked.Add("Cannot verify ownership of the changed file: " + file);
                }
            }
        }
        foreach (var download in subscription.Downloads)
        {
            string directory = ModDownloadService.DownloadDirectory(subscription.GameDirectory, download.Id);
            ManagedModFiles.EnsureSafe(directory);
            foreach (string path in ManagedModFiles.Files(directory))
            {
                string relative = Path.GetRelativePath(directory, path);
                if (download.LocalPath is not null && relative is "download.bin" or "download.part" or "resume.json")
                {
                    var fingerprint = ManagedModFiles.Fingerprint(directory, relative);
                    if (relative == "download.bin" && (download.ContentSha256 is null || download.ContentSha256 != fingerprint.Sha256))
                    {
                        preserved.Add(path);
                        blocked.Add("Cannot verify the downloaded file's content: " + path);
                    }
                    else files.Add(new(path, fingerprint));
                }
                else preserved.Add(path);
            }
            if (download.LocalPath is not null && !download.LocalPath.Equals(Path.Combine(directory, "download.bin"), SubscriptionPaths.Comparison) &&
                !download.LocalPath.Equals(Path.Combine(directory, "download.part"), SubscriptionPaths.Comparison))
                blocked.Add("The download path is outside its recorded task directory: " + download.LocalPath);
        }
        return new(subscription.Id, subscription.GameDirectory, files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            preserved.Order(StringComparer.OrdinalIgnoreCase).ToArray(), blocked.ToArray());
    }

    private static bool SamePlan(ModCleanupPlan first, ModCleanupPlan second) =>
        first.Files.SequenceEqual(second.Files) && first.PreservedPaths.SequenceEqual(second.PreservedPaths, StringComparer.OrdinalIgnoreCase);

    private static string NewTransactionDirectory(string game, out Guid id)
    {
        id = Guid.NewGuid();
        string path = Path.Combine(ManagedModFiles.PrivateRoot(game), "transactions", id.ToString("N"));
        ManagedModFiles.EnsureSafe(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteJournal(string directory, ModFileTransaction transaction)
    {
        string path = Path.Combine(directory, "transaction.json");
        ManagedModFiles.EnsureSafe(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(transaction, ModTransactionJsonContext.Default.ModFileTransaction);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("The installation recovery journal is too large.");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private void FinishCommitted(string directory, ModFileTransaction transaction)
    {
        EnsureStopped(transaction.GameDirectory);
        if (transaction.Kind == ModTransactionKind.Remove)
        {
            FinishRemoval(directory, transaction);
            return;
        }
        else if (transaction.PreviousInstallation is { } previous)
            DeleteVerifiedTree(Path.Combine(directory, "previous"), previous.OwnedFileDetails);
        host.RefreshMods(transaction.GameDirectory);
        FinishJournal(directory);
    }

    private void FinishRemoval(string directory, ModFileTransaction transaction)
    {
        EnsureStopped(transaction.GameDirectory);
        foreach (var move in transaction.Moves)
        {
            EnsureStopped(transaction.GameDirectory);
            string backup = Path.Combine(directory, move.BackupName);
            DeleteVerifiedFile(backup, move.Fingerprint);
            RemoveEmptyParents(Path.GetDirectoryName(move.SourcePath)!, transaction.GameDirectory);
        }
        host.RemoveEnabledMods(transaction.GameDirectory, transaction.RemovedDirectories);
        if (store.Snapshot().Subscriptions.Any(item => item.Id == transaction.SubscriptionId))
            store.CommitRemoval(transaction.SubscriptionId);
        host.RefreshMods(transaction.GameDirectory);
        FinishJournal(directory);
    }

    private void RollBack(string directory, ModFileTransaction transaction)
    {
        EnsureStopped(transaction.GameDirectory);
        if (transaction.Kind == ModTransactionKind.Install)
        {
            string staged = Path.Combine(directory, "staged");
            string backup = Path.Combine(directory, "previous");
            // If extraction never completed, no final-directory mutation was permitted.
            if (transaction.Installation is { } installation)
            {
                bool oldStillAtTarget = transaction.PreviousInstallation is not null && !Directory.Exists(backup);
                if (!oldStillAtTarget && !Directory.Exists(staged) && Directory.Exists(installation.DirectoryPath))
                {
                    RequireUnchangedDirectory(installation);
                    Directory.Move(installation.DirectoryPath, staged);
                }
                if (Directory.Exists(backup) && transaction.PreviousInstallation is { } old)
                {
                    RequireUnchangedDirectory(old with { DirectoryPath = backup });
                    ManagedModFiles.EnsureSafe(old.DirectoryPath);
                    if (Directory.Exists(old.DirectoryPath) || File.Exists(old.DirectoryPath)) throw new IOException("The rollback destination is occupied; recovery was preserved.");
                    Directory.Move(backup, old.DirectoryPath);
                }
                DeleteVerifiedTree(staged, installation.OwnedFileDetails);
            }
            else if (ManagedModFiles.Files(directory).Any(path => !path.Equals(Path.Combine(directory, "transaction.json"), SubscriptionPaths.Comparison)))
            {
                // An interrupted extraction has no completed hashes. Preserve its files AND journal,
                // outside the active recovery queue; the original selected download can be retried.
                string abandoned = Path.Combine(ManagedModFiles.PrivateRoot(transaction.GameDirectory), "interrupted-preparations", transaction.Id.ToString("N"));
                ManagedModFiles.EnsureSafe(abandoned);
                Directory.CreateDirectory(Path.GetDirectoryName(abandoned)!);
                Directory.Move(directory, abandoned);
                return;
            }
        }
        else
            foreach (var move in transaction.Moves.Reverse())
            {
                string backup = Path.Combine(directory, move.BackupName);
                ManagedModFiles.EnsureSafe(backup);
                if (!File.Exists(backup)) continue;
                if (!ManagedModFiles.Matches(backup, move.Fingerprint)) throw new IOException("A rollback backup changed; recovery was preserved.");
                ManagedModFiles.EnsureSafe(move.SourcePath);
                if (File.Exists(move.SourcePath) || Directory.Exists(move.SourcePath)) throw new IOException("The rollback destination is occupied; recovery was preserved.");
                Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                File.Move(backup, move.SourcePath);
            }
        FinishJournal(directory);
    }

    private static void DeleteVerifiedFile(string path, OwnedModFile fingerprint)
    {
        ManagedModFiles.EnsureSafe(path);
        if (!File.Exists(path)) return;
        if (!ManagedModFiles.Matches(path, fingerprint)) throw new IOException("A transaction backup changed; recovery was preserved.");
        File.Delete(path);
    }

    private static void DeleteVerifiedTree(string directory, OwnedModFile[] files)
    {
        ManagedModFiles.EnsureSafe(directory);
        if (!Directory.Exists(directory)) return;
        var expected = files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (string path in ManagedModFiles.Files(directory))
        {
            if (!expected.TryGetValue(Path.GetRelativePath(directory, path), out var file))
                throw new IOException("A transaction directory contains an unrecognized file; recovery was preserved.");
            if (!ManagedModFiles.Matches(path, file)) throw new IOException("A transaction file changed; recovery was preserved.");
        }
        foreach (var file in files) DeleteVerifiedFile(Path.Combine(directory, file.RelativePath), file);
        ManagedModFiles.RemoveEmptyDirectories(directory);
    }

    private static void FinishJournal(string directory)
    {
        string path = Path.Combine(directory, "transaction.json");
        ManagedModFiles.EnsureSafe(path);
        File.Delete(path);
        ManagedModFiles.RemoveEmptyDirectories(directory);
    }

    private static void RemoveEmptyParents(string directory, string game)
    {
        string mods = SubscriptionPaths.ModsDirectory(game);
        string privateRoot = ManagedModFiles.PrivateRoot(game);
        for (string? current = directory; current is not null && current.StartsWith(mods + Path.DirectorySeparatorChar, SubscriptionPaths.Comparison) &&
            !current.Equals(privateRoot, SubscriptionPaths.Comparison); current = Path.GetDirectoryName(current))
        {
            ManagedModFiles.EnsureSafe(current);
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) break;
            Directory.Delete(current);
        }
    }

    private static void ValidateJournal(string directory, string game, ModFileTransaction transaction)
    {
        if (transaction.Version != 1 || transaction.Id == Guid.Empty || transaction.SubscriptionId == Guid.Empty ||
            !Enum.IsDefined(transaction.Kind) || !transaction.GameDirectory.Equals(game, SubscriptionPaths.Comparison) ||
            Path.GetFileName(directory) != transaction.Id.ToString("N") || transaction.Moves is null || transaction.RemovedDirectories is null)
            throw new InvalidDataException("The installation recovery journal is invalid.");
        foreach (var installation in new[] { transaction.Installation, transaction.PreviousInstallation }.OfType<ModInstallation>())
        {
            if (SubscriptionPaths.InstallationDirectory(game, installation.DirectoryPath) != installation.DirectoryPath ||
                installation.Id == Guid.Empty || installation.OwnedFiles is null || installation.OwnedFileDetails is null ||
                installation.OwnedFileDetails.Length != installation.OwnedFiles.Length || installation.OwnedFiles.Length == 0 ||
                !installation.OwnedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(installation.OwnedFileDetails.Select(file => file.RelativePath)))
                throw new InvalidDataException("The recovery ownership manifest is invalid.");
            foreach (var file in installation.OwnedFileDetails) ValidateFingerprint(file);
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in transaction.Moves)
        {
            if (SubscriptionPaths.ManagedPath(game, move.SourcePath) != move.SourcePath || !paths.Add(move.SourcePath) ||
                move.BackupName.Length != 8 || !move.BackupName.All(char.IsAsciiDigit) || !names.Add(move.BackupName))
                throw new InvalidDataException("The recovery file move is invalid.");
            ValidateFingerprint(move.Fingerprint);
        }
        foreach (string removed in transaction.RemovedDirectories) _ = SubscriptionPaths.InstallationDirectory(game, removed);
    }

    private static void ValidateFingerprint(OwnedModFile file)
    {
        if (file is null || SubscriptionPaths.RelativeFile(file.RelativePath) != file.RelativePath || file.SizeBytes < 0 ||
            file.Sha256 is not { Length: 64 } || !file.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("The recovery file fingerprint is invalid.");
    }
}
