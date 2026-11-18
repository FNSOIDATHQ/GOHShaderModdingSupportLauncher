using System.Globalization;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

internal sealed record ModAliasItem(string Key, string? Title)
{
    public override string ToString() => Title is null ? Key : $"{Title} ({Key})";
}

internal static class ModAliasItems
{
    private const string WorkshopPrefix = "workshop:" + MainWindow.WorkshopAppId + ":";

    internal static ModAliasItem? ForMod(Mod mod)
    {
        if (mod.IsMissingReference || mod.Inspection is not { } inspection) return null;
        if (inspection.Source == ModSourceKind.Workshop)
        {
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(inspection.DirectoryPath));
            return FromNumber(WorkshopPrefix, folder, mod.name);
        }
        return mod.SubscriptionLink is { } link ? FromSource(link.Source, mod.name) : null;
    }

    internal static bool IsKey(string value)
    {
        int separator = value.LastIndexOf(':');
        if (separator < 0) return false;
        string prefix = value[..(separator + 1)];
        return (prefix is WorkshopPrefix or "moddb:" or "github:" or "mowportal:") &&
            FromNumber(prefix, value[(separator + 1)..], null)?.Key == value;
    }

    internal static ModAliasItem? DirectItem(Uri address)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(address)) return null;
        if (address.IdnHost == "steamcommunity.com" && address.AbsolutePath.TrimEnd('/') is
            "/sharedfiles/filedetails" or "/workshop/filedetails")
        {
            string? id = null;
            foreach (string pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = pair.Split('=', 2);
                if (!Uri.UnescapeDataString(fields[0]).Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
                if (id is not null || fields.Length != 2) return null;
                id = Uri.UnescapeDataString(fields[1]);
            }
            return id is null ? null : FromNumber(WorkshopPrefix, id, null);
        }
        if (ModPageRouter.TryResolveSupportedPage(address, out var route) &&
            route is { Provider: "mowportal", Kind: ModPageKind.Overview, ModPage: { } page })
            return FromSource(MowPortalParser.Identity(page), null);
        return null;
    }

    internal static async Task<ModAliasItem> ResolveProjectAsync(ModPageRoute route, ModSubscriptionRuntime runtime,
        CancellationToken token)
    {
        if (route is not { Kind: ModPageKind.Overview, ModPage: { } page }) throw InvalidAddress();
        await runtime.Ready.WaitAsync(token).ConfigureAwait(false);
        var saved = FindSavedProject(route.Provider, page, runtime.Library);
        if (saved is not null) return saved;

        var source = runtime.ResolveSource(route.Provider) ?? throw InvalidAddress();
        var result = await source.GetModAsync(page, ModReadMode.CacheOnly, token).ConfigureAwait(false);
        if (!result.IsSuccess) result = await source.GetModAsync(page, cancellationToken: token).ConfigureAwait(false);
        if (result.Failure is { } failure) throw new ModSourceException(failure);
        if (result.Content is not { } mod || mod.Id.Provider != route.Provider ||
            FromSource(mod.Id, mod.Title) is not { } item) throw InvalidAddress();
        return item;
    }

    private static ModAliasItem? FindSavedProject(string provider, Uri page, SubscriptionLibrary library)
    {
        var comparison = provider == "github" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var projects = library.Mods.Where(mod => mod.Id.Provider == provider && Matches(mod.PageUrl)).Take(2).ToArray();
        if (projects.Length == 1) return FromSource(projects[0].Id, projects[0].Title);
        if (projects.Length > 1) return null;

        var sources = library.Subscriptions.Where(subscription => subscription.Source.Provider == provider)
            .Where(subscription => subscription.Files.KnownFiles.Concat(subscription.Downloads.Select(download => download.File))
                .Any(file => Matches(file.PageUrl) || Matches(file.BrowserUrl)) ||
                subscription.Files.Groups.Any(group => Matches(group.PageUrl)))
            .Select(subscription => subscription.Source).Distinct().Take(2).ToArray();
        return sources.Length == 1 ? FromSource(sources[0], Uri.UnescapeDataString(page.AbsolutePath.Trim('/'))) : null;

        bool Matches(Uri? candidate) => candidate is not null && ModPageRouter.TryResolveSupportedPage(candidate, out var route) &&
            route?.Provider == provider && route.ModPage is { } candidatePage &&
            string.Equals(candidatePage.AbsoluteUri, page.AbsoluteUri, comparison);
    }

    private static ModAliasItem? FromSource(ModSourceId source, string? title) =>
        source.Provider is "moddb" or "github" or "mowportal" ? FromNumber(source.Provider + ":", source.RemoteId, title) : null;

    private static ModAliasItem? FromNumber(string prefix, string value, string? title) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0
            ? new(prefix + id.ToString(CultureInfo.InvariantCulture), ModAliasStore.ValidText(title) ? title : null)
            : null;

    internal static FormatException InvalidAddress() => new(ModAliasStore.Text("MA_InvalidAddress"));
}
