using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// This resolver uses only Yandex's public-resource endpoints. Signed delivery URLs stay in memory.
internal sealed class YandexPublicFileResolver(AnonymousModHttpClient http)
{
    internal static bool IsShare(Uri uri)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(uri) || uri.Fragment.Length != 0 ||
            uri.Host is not ("disk.yandex.ru" or "disk.yandex.com" or "disk.yandex.com.tr" or "yadi.sk")) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && parts[0] is "d" or "i" && parts[1].Length is > 0 and <= 200 &&
            parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
            (uri.Query.Length == 0 || uri.Query is "?lang=en" or "?lang=ru");
    }

    // API metadata is never a delivery host. Boundary checks reject suffix lookalikes and credentials.
    internal static bool IsDownloadHost(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) &&
        (uri.Host == "downloader.disk.yandex.ru" || uri.Host.EndsWith(".storage.yandex.net", StringComparison.Ordinal));

    private static bool IsApi(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) &&
        uri.Host == "cloud-api.yandex.net" && uri.AbsolutePath is
            "/v1/disk/public/resources" or "/v1/disk/public/resources/download";

    internal async Task<RemoteModFile> ReadFileAsync(Uri share, CancellationToken token)
    {
        Uri canonical = Canonical(share);
        try
        {
            using var document = await JsonAsync(Api(canonical, download: false), token).ConfigureAwait(false);
            var root = document.RootElement;
            RequireObject(root);
            if (Text(root, "type") != "file")
                throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied,
                    "This Yandex share is a folder. Select a public file link; account access is not supported.");
            string name = Required(root, "name");
            if (!root.TryGetProperty("size", out var sizeProperty) || !sizeProperty.TryGetInt64(out long size) || size <= 0)
                throw new FormatException("Invalid public-file size.");
            string md5 = Required(root, "md5");
            if (md5.Length != 32 || !md5.All(Uri.IsHexDigit)) throw new FormatException("Invalid public-file checksum.");
            if (root.TryGetProperty("public_url", out var publicUrl) && publicUrl.ValueKind != JsonValueKind.Null &&
                (!Uri.TryCreate(publicUrl.GetString(), UriKind.Absolute, out var returned) || !IsShare(returned) ||
                 !returned.AbsolutePath.Equals(canonical.AbsolutePath, StringComparison.Ordinal)))
                throw new FormatException("The public-file identity does not match the share.");
            string revision = $"{size}:{md5.ToLowerInvariant()}";
            return new()
            {
                Id = "yandex:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.AbsoluteUri))),
                Revision = revision, PageUrl = canonical, BrowserUrl = canonical, Name = name,
                SizeBytes = size, Md5 = md5.ToLowerInvariant(),
                UpdatedAt = root.TryGetProperty("modified", out var modified) && modified.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(modified.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw AnonymousModHttpClient.Error(ModSourceError.ParseFailed, "The Yandex public-file metadata is invalid."); }
    }

    internal async Task<ModDownloadLink> ResolveDownloadAsync(RemoteModFile file, CancellationToken token)
    {
        if (!file.CanDownload)
            throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "This public link has no supported anonymous download resolver.");
        Uri share = Canonical(file.BrowserUrl ?? file.PageUrl);
        var metadata = await ReadFileAsync(share, token).ConfigureAwait(false);
        Uri delivery;
        try
        {
            using var document = await JsonAsync(Api(share, download: true), token).ConfigureAwait(false);
            var root = document.RootElement;
            RequireObject(root);
            if (Required(root, "method") != "GET" ||
                !Uri.TryCreate(Required(root, "href"), UriKind.Absolute, out var href) || !IsDownloadHost(href))
                throw new FormatException("Invalid public download response.");
            delivery = href;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw AnonymousModHttpClient.Error(ModSourceError.ParseFailed, "The Yandex anonymous download response is invalid."); }
        Uri final = await http.ResolveFileAsync(delivery, IsDownloadHost, token).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(file with
        {
            Name = metadata.Name, SizeBytes = metadata.SizeBytes, Md5 = metadata.Md5,
            UpdatedAt = metadata.UpdatedAt
        }, final, now);
    }

    private async Task<JsonDocument> JsonAsync(Uri uri, CancellationToken token) => JsonDocument.Parse(
        await http.GetJsonAsync(uri, IsApi, token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });

    private static Uri Canonical(Uri share)
    {
        if (!IsShare(share)) throw new ArgumentException("A supported public Yandex file URL is required.", nameof(share));
        return new Uri(share.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }
    private static Uri Api(Uri share, bool download) => new("https://cloud-api.yandex.net/v1/disk/public/resources" +
        (download ? "/download" : "") + "?public_key=" + Uri.EscapeDataString(share.AbsoluteUri));
    private static void RequireObject(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected public-resource metadata."); }
    private static string Required(JsonElement value, string property) => Text(value, property) is { Length: > 0 } text
        ? text : throw new FormatException("Required public-resource metadata is missing.");
    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var text) &&
        text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
}
