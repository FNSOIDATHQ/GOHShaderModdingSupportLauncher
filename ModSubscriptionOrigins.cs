using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// A display/navigation link to committed installer output. It never grants file ownership.
internal sealed record ModSubscriptionLink(string GameDirectory, string DirectoryPath, Guid SubscriptionId,
    Guid InstallationId, Guid DownloadId, ModSourceId Source, Uri? RemotePage);

// Built once for a game/library revision; resolving a row never clones the store or touches the filesystem.
internal sealed class ModSubscriptionOrigins
{
    private readonly Dictionary<string, ModSubscriptionLink?> installations = new(StringComparer.OrdinalIgnoreCase);
    internal string? GameDirectory { get; }
    internal long Revision { get; }

    internal ModSubscriptionOrigins(SubscriptionLibrary library, string? gameDirectory, Action<string>? diagnostic = null)
    {
        Revision = library.Revision;
        if (gameDirectory is null) return;
        try { GameDirectory = SubscriptionPaths.Normalize(gameDirectory); }
        catch (ArgumentException) { return; }

        var projectPages = library.Mods.ToDictionary(mod => mod.Id, mod => mod.PageUrl);
        foreach (var subscription in library.Subscriptions)
        {
            if (subscription.Source.Provider is not ("moddb" or "github" or "mowportal")) continue;
            string game;
            try { game = SubscriptionPaths.Normalize(subscription.GameDirectory); }
            catch (ArgumentException) { continue; }
            if (!game.Equals(GameDirectory, SubscriptionPaths.Comparison)) continue;
            var remotePage = projectPages.GetValueOrDefault(subscription.Source);
            var downloads = subscription.Downloads.Select(download => download.Id).ToHashSet();
            foreach (var installation in subscription.Installations)
            {
                if (installation.State != InstallationState.Installed || installation.OwnedFiles.Length == 0 ||
                    !downloads.Contains(installation.DownloadId)) continue;
                string directory;
                try { directory = SubscriptionPaths.InstallationDirectory(game, installation.DirectoryPath); }
                catch (ArgumentException) { continue; }
                var link = new ModSubscriptionLink(game, directory, subscription.Id, installation.Id,
                    installation.DownloadId, subscription.Source, remotePage);
                if (installations.TryAdd(directory, link)) continue;
                // Valid libraries have unique installation directories. Never choose an arbitrary owner if that invariant fails.
                installations[directory] = null;
                diagnostic?.Invoke("Ambiguous Mod subscription installation directory: " + directory);
            }
        }
    }

    internal ModSubscriptionLink? Resolve(Mod mod)
    {
        if (GameDirectory is null || mod.IsMissingReference || mod.Inspection is not { Source: ModSourceKind.Local } inspection)
            return null;
        try
        {
            string directory = SubscriptionPaths.InstallationDirectory(GameDirectory, mod.path);
            if (!SubscriptionPaths.Normalize(inspection.DirectoryPath).Equals(directory, SubscriptionPaths.Comparison)) return null;
            return installations.GetValueOrDefault(directory);
        }
        catch (ArgumentException) { return null; }
    }

    internal bool Apply(Mod mod) => mod.SetSubscriptionLink(Resolve(mod));
}
