namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class ModImagePreview
{
    // Use ModDB's existing uncropped page preview instead of stretching its 120x90 navigation crop.
    // Keep the source model unchanged so old caches, image identities and link mappings still work.
    internal static Uri For(Uri thumbnail, string provider)
    {
        if (provider != "moddb" || !AnonymousModHttpClient.IsPublicHttps(thumbnail) ||
            thumbnail.IdnHost != "media.moddb.com" || thumbnail.Query.Length != 0 ||
            !thumbnail.AbsolutePath.StartsWith("/cache/images/", StringComparison.Ordinal)) return thumbnail;
        const string crop = "/crop_120x90/";
        string path = thumbnail.AbsolutePath;
        int position = path.LastIndexOf(crop, StringComparison.Ordinal);
        if (position < "/cache/images/".Length || position + crop.Length == path.Length ||
            path.AsSpan(position + crop.Length).Contains('/')) return thumbnail;
        return new UriBuilder(thumbnail) { Path = path[..position] + "/thumb_620x2000/" + path[(position + crop.Length)..] }.Uri;
    }
}
