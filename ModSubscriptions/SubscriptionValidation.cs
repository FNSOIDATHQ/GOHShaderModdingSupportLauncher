using System.Diagnostics.CodeAnalysis;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class SubscriptionValidation
{
    private static void Require([DoesNotReturnIf(false)] bool valid)
    {
        if (!valid) throw new InvalidDataException("The subscription library contains invalid or conflicting records.");
    }

    private static void Text(string? value) => Require(!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl));

    private static void Url(Uri? value) => Require(value is { IsAbsoluteUri: true } &&
        (value.Scheme == Uri.UriSchemeHttps || value.Scheme == Uri.UriSchemeHttp) && value.UserInfo.Length == 0);

    internal static void Source(ModSourceId source)
    {
        Require(source is not null);
        Text(source.Provider);
        Text(source.RemoteId);
        Require(source.Provider.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));
    }

    private static void Image(ModImage image)
    {
        Require(image is not null && image.Caption is not null);
        Text(image.Id);
        Url(image.ImageUrl);
        if (image.ThumbnailUrl is not null) Url(image.ThumbnailUrl);
    }

    private static void File(RemoteModFile file)
    {
        Require(file is not null && file.Description is not null && file.SizeBytes is not < 0);
        Text(file.Id);
        Text(file.Revision);
        Text(file.Name);
        Url(file.PageUrl);
        if (file.BrowserUrl is not null) Url(file.BrowserUrl);
        Require(file.Md5 is null || (file.Md5.Length == 32 && file.Md5.All(Uri.IsHexDigit)));
    }

    internal static void Library(SubscriptionLibrary library)
    {
        Require(library.SchemaVersion == SubscriptionLibrary.CurrentSchemaVersion && library.Revision >= 0 &&
            library.Mods is not null && library.Favorites is not null && library.Subscriptions is not null);
        var sources = new HashSet<ModSourceId>();
        foreach (var mod in library.Mods)
        {
            Require(mod is not null && mod.Summary is not null && mod.InstallationHint is not null &&
                Enum.IsDefined(mod.Kind) && mod.Capabilities is not null &&
                mod.Articles is not null && mod.Images is not null);
            Source(mod.Id);
            Require(sources.Add(mod.Id) && Enum.IsDefined(mod.Capabilities.UpdateEvidence));
            Text(mod.Title);
            Url(mod.PageUrl);
            var articleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var article in mod.Articles)
            {
                Require(article is not null && article.Body is not null && article.Images is not null);
                Text(article.Id);
                Text(article.Title);
                Url(article.PageUrl);
                Require(articleIds.Add(article.Id));
                foreach (var image in article.Images) Image(image);
            }
            var imageIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var image in mod.Images)
            {
                Image(image);
                Require(imageIds.Add(image.Id));
            }
        }

        var favorites = new HashSet<ModSourceId>();
        foreach (var favorite in library.Favorites)
        {
            Require(favorite is not null);
            Source(favorite.Source);
            Require(sources.Contains(favorite.Source) && favorites.Add(favorite.Source));
        }

        var subscriptionIds = new HashSet<Guid>();
        var subscriptions = new HashSet<(ModSourceId, string)>();
        var taskIds = new HashSet<Guid>();
        var installationDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subscription in library.Subscriptions)
        {
            Require(subscription is not null && subscription.Files is not null &&
                subscription.Downloads is not null && subscription.Installations is not null);
            Source(subscription.Source);
            Require(subscription.Id != Guid.Empty && subscriptionIds.Add(subscription.Id) &&
                sources.Contains(subscription.Source) && Enum.IsDefined(subscription.State));
            string game = SubscriptionPaths.Normalize(subscription.GameDirectory);
            Require(game == subscription.GameDirectory && subscriptions.Add((subscription.Source, game.ToUpperInvariant())));
            var tracking = subscription.Files;
            Require(tracking.KnownFiles is not null && tracking.AcknowledgedFiles is not null && tracking.Notices is not null);
            var fileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in tracking.KnownFiles)
            {
                File(file);
                Require(fileIds.Add(file.Id));
            }
            var acknowledged = new HashSet<(string, string)>();
            foreach (var file in tracking.AcknowledgedFiles)
            {
                Require(file is not null);
                Text(file.FileId);
                Text(file.Revision);
                Require(Enum.IsDefined(file.State) && acknowledged.Add((file.FileId, file.Revision)));
            }
            var notices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var notice in tracking.Notices)
            {
                Require(notice is not null && Enum.IsDefined(notice.Kind));
                File(notice.File);
                Require(tracking.HasBaseline && notices.Add(notice.File.Id) &&
                    tracking.KnownFiles.Any(file => file.Id == notice.File.Id && file.Revision == notice.File.Revision));
            }

            var downloads = new HashSet<Guid>();
            foreach (var download in subscription.Downloads)
            {
                Require(download is not null && download.Id != Guid.Empty && taskIds.Add(download.Id) &&
                    downloads.Add(download.Id) && Enum.IsDefined(download.State) && download.ReceivedBytes >= 0);
                File(download.File);
                Require(download.ContentSha256 is null || (download.ContentSha256.Length == 64 &&
                    download.ContentSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')));
                if (download.LocalPath is not null)
                    Require(SubscriptionPaths.ManagedPath(game, download.LocalPath) == download.LocalPath && downloadedPaths.Add(download.LocalPath));
                if (download.State is DownloadState.Downloaded or DownloadState.NeedsManualInstall)
                    Require(download.LocalPath is not null);
            }
            foreach (var installation in subscription.Installations)
            {
                Require(installation is not null && installation.Id != Guid.Empty && taskIds.Add(installation.Id) &&
                    downloads.Contains(installation.DownloadId) && Enum.IsDefined(installation.State) && installation.OwnedFiles is not null);
                Require(SubscriptionPaths.InstallationDirectory(game, installation.DirectoryPath) == installation.DirectoryPath &&
                    installationDirectories.Add(installation.DirectoryPath));
                var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in installation.OwnedFiles)
                    Require(SubscriptionPaths.RelativeFile(file) == file && owned.Add(file));
                Require(installation.OwnedFileDetails is not null);
                var detailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var detail in installation.OwnedFileDetails)
                    Require(detail is not null && owned.Contains(detail.RelativePath) && detailed.Add(detail.RelativePath) &&
                        detail.SizeBytes >= 0 && detail.Sha256 is { Length: 64 } && detail.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
                Require(detailed.Count == 0 || detailed.Count == owned.Count);
                if (installation.State == InstallationState.Installed) Require(owned.Count > 0);
            }
        }
        foreach (string file in downloadedPaths)
            Require(!installationDirectories.Any(directory => file.Equals(directory, SubscriptionPaths.Comparison) ||
                file.StartsWith(directory + Path.DirectorySeparatorChar, SubscriptionPaths.Comparison)));
    }
}
