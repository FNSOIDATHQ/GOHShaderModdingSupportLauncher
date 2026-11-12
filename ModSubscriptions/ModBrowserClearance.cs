namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Human-approved, short-lived Cloudflare clearance from our own isolated browser only.
// Not a record: the default string representation must never contain a cookie value.
internal sealed class ModBrowserClearance
{
    private sealed class Entry(Uri origin, string userAgent, string value, string path, DateTimeOffset expires)
    {
        internal Uri Origin { get; } = origin;
        internal string UserAgent { get; } = userAgent;
        internal string Value { get; } = value;
        internal string Path { get; } = path;
        internal DateTimeOffset Expires { get; } = expires;
    }
    private readonly object sync = new();
    private Entry? entry;

    internal static bool IsSupportedOrigin(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) &&
        uri.Host.Equals("www.moddb.com", StringComparison.OrdinalIgnoreCase);

    internal bool Set(Uri origin, string userAgent, string value, string domain, string path, bool secure, DateTimeOffset? expires)
    {
        string cookieDomain = domain.TrimStart('.');
        if (!IsSupportedOrigin(origin) || !secure ||
            !(cookieDomain.Equals("moddb.com", StringComparison.OrdinalIgnoreCase) || cookieDomain.Equals("www.moddb.com", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(userAgent) || userAgent.Length > 1024 || userAgent.Any(c => c < 32 || c > 126) ||
            string.IsNullOrEmpty(value) || value.Length > 8192 || value.Any(c => !(c == 0x21 || c is >= (char)0x23 and <= (char)0x2b ||
                c is >= (char)0x2d and <= (char)0x3a || c is >= (char)0x3c and <= (char)0x5b || c is >= (char)0x5d and <= (char)0x7e)) ||
            path.Length > 1024 || !path.StartsWith('/') || path.Any(c => c < 32 || c > 126)) return false;
        DateTimeOffset expiry = expires ?? DateTimeOffset.UtcNow.AddMinutes(30);
        if (expiry <= DateTimeOffset.UtcNow) return false;
        expiry = expiry < DateTimeOffset.UtcNow.AddMinutes(30) ? expiry : DateTimeOffset.UtcNow.AddMinutes(30);
        lock (sync) entry = new(ModVerificationState.Origin(origin), userAgent, value, path, expiry);
        return true;
    }

    internal void Apply(HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri || !IsSupportedOrigin(uri)) return;
        lock (sync)
        {
            if (entry is null) return;
            if (entry.Expires <= DateTimeOffset.UtcNow) { entry = null; return; }
            if (ModVerificationState.Origin(uri) != entry.Origin ||
                !(uri.AbsolutePath == entry.Path || uri.AbsolutePath.StartsWith(entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/", StringComparison.Ordinal))) return;
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", "cf_clearance=" + entry.Value);
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", entry.UserAgent);
        }
    }

    internal void Clear(Uri? origin = null)
    {
        lock (sync)
            if (origin is null || entry?.Origin == ModVerificationState.Origin(origin)) entry = null;
    }
}
