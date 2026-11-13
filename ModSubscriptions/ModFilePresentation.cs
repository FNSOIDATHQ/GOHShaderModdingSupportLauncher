namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Display data only: never rewrite the cached name, file identity or revision.
internal sealed record ModFilePresentation(string Title, string? Host)
{
    internal static ModFilePresentation From(RemoteModFile file, string provider)
    {
        if (provider != "mowportal" || file.BrowserUrl is not { } url ||
            !AnonymousModHttpClient.IsPublicHttps(url) || MowPortalParser.IsSite(url))
            return new(file.Name, null);

        string host = url.IdnHost;
        string suffix = " — " + host;
        string title = file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && file.Name.Length > suffix.Length
            ? file.Name[..^suffix.Length] : file.Name;
        string label = host switch
        {
            "disk.yandex.ru" or "disk.yandex.com" or "yadi.sk" => "Yandex",
            "modsfire.com" or "www.modsfire.com" => "ModsFire",
            "drive.google.com" => "Google Drive",
            "mediafire.com" or "www.mediafire.com" => "MediaFire",
            "mega.nz" => "MEGA",
            _ => host
        };
        return new(title, label);
    }
}
