using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModHttpOptions
{
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan HostInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal int MaxRetries { get; init; } = 2;
    internal int MaxTextBytes { get; init; } = 4 * 1024 * 1024;
}

// Serializes requests (including response bodies) per host across all production clients.
internal sealed class ModHostLimiter
{
    internal static ModHostLimiter Shared { get; } = new();
    private readonly ConcurrentDictionary<string, Host> hosts = new(StringComparer.OrdinalIgnoreCase);
    internal sealed class Host
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextRequest { get; set; }
    }
    internal Host For(Uri uri) => hosts.GetOrAdd(uri.IdnHost, _ => new());
}

internal sealed class AnonymousModHttpClient : IDisposable
{
    private readonly HttpClient client;
    private readonly ModHttpOptions options;
    private readonly ModHostLimiter limiter;

    internal AnonymousModHttpClient(ModHttpOptions? options = null)
        : this(new SocketsHttpHandler
        {
            UseCookies = false, Credentials = null, PreAuthenticate = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15)
        }, options, ModHostLimiter.Shared) { }

    // Injection is for deterministic local tests; production always uses the credential-free handler above.
    internal AnonymousModHttpClient(HttpMessageHandler handler, ModHttpOptions? options, ModHostLimiter limiter)
    {
        this.options = options ?? new();
        if (this.options.Timeout <= TimeSpan.Zero || this.options.HostInterval < TimeSpan.Zero ||
            this.options.MaxRetries is < 0 or > 4 || this.options.MaxTextBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.limiter = limiter;
        client = new(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        string version = typeof(AnonymousModHttpClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GOHSMSLauncher/{version} (+https://github.com/FNSOIDATHQ/GOHShaderModdingSupportLauncher)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/rss+xml, application/xml;q=0.9, */*;q=0.5");
    }

    internal Task<string> GetTextAsync(Uri uri, Func<Uri, bool> allow, CancellationToken token) =>
        SendAsync(uri, allow, false, token, async (response, ct) =>
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
                throw Error(ModSourceError.ParseFailed, "The source returned an incomplete content page.");
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && type is not ("text/html" or "application/xhtml+xml" or "application/rss+xml" or "application/xml" or "text/xml"))
                throw Error(ModSourceError.ParseFailed, "The source returned an unexpected content type.");
            if (response.Content.Headers.ContentLength > options.MaxTextBytes)
                throw Error(ModSourceError.ParseFailed, "The source page exceeds the response size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + read > options.MaxTextBytes)
                    throw Error(ModSourceError.ParseFailed, "The source page exceeds the response size limit.");
                bytes.Write(buffer, 0, read);
            }
            // ModDB publishes UTF-8. A response encoding never selects an arbitrary codepage provider.
            return System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        });

    internal Task<Uri> ResolveFileAsync(Uri uri, Func<Uri, bool> allow, CancellationToken token) =>
        SendAsync(uri, allow, true, token, async (response, ct) =>
        {
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type is null || type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("xml", StringComparison.OrdinalIgnoreCase))
                throw Error(ModSourceError.AccessDenied, "The mirror returned a page instead of an anonymous file download.");
            // The range is only a probe. Even if ignored, never buffer a large Mod here.
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] header = new byte[16];
            int count = await stream.ReadAtLeastAsync(header, 1, throwOnEndOfStream: false, cancellationToken: ct).ConfigureAwait(false);
            if (count == 0) throw Error(ModSourceError.RequestFailed, "The mirror returned an empty file.");
            string prefix = System.Text.Encoding.ASCII.GetString(header, 0, count).TrimStart();
            if (prefix.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                throw Error(ModSourceError.AccessDenied, "The mirror returned an access page instead of a file.");
            return response.RequestMessage!.RequestUri!;
        });

    private async Task<T> SendAsync<T>(Uri uri, Func<Uri, bool> allow, bool probe, CancellationToken token,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Uri current = uri;
                for (int redirect = 0; redirect <= 8; redirect++)
                {
                    if (!IsPublicHttps(current) || !allow(current))
                        throw Error(ModSourceError.AccessDenied, "The source redirected outside its supported anonymous hosts.");
                    var host = limiter.For(current);
                    await host.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        TimeSpan wait = host.NextRequest - DateTimeOffset.UtcNow;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(options.Timeout);
                        using var request = new HttpRequestMessage(HttpMethod.Get, current);
                        if (probe) request.Headers.Range = new RangeHeaderValue(0, 15);
                        if (current != uri) request.Headers.Referrer = uri;
                        host.NextRequest = DateTimeOffset.UtcNow + options.HostInterval;
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirect == 8 || response.Headers.Location is null)
                                throw Error(ModSourceError.RequestFailed, "The source returned an invalid or looping redirect.");
                            current = new Uri(current, response.Headers.Location);
                            continue;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            var retry = response.Headers.RetryAfter;
                            DateTimeOffset? after = retry?.Date ?? (retry?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
                            if ((int)response.StatusCode is 429 or 503)
                            {
                                DateTimeOffset next = after ?? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Pow(2, attempt));
                                if (next > host.NextRequest) host.NextRequest = next;
                            }
                            var kind = response.StatusCode switch
                            {
                                HttpStatusCode.TooManyRequests => ModSourceError.RateLimited,
                                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModSourceError.AccessDenied,
                                HttpStatusCode.NotFound or HttpStatusCode.Gone => ModSourceError.NotFound,
                                _ => ModSourceError.RequestFailed
                            };
                            throw new ModSourceException(new(kind, $"The source returned HTTP {(int)response.StatusCode}.", response.StatusCode, after));
                        }
                        return await read(response, timeout.Token).ConfigureAwait(false);
                    }
                    finally { host.Gate.Release(); }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt >= options.MaxRetries) throw Error(ModSourceError.Timeout, "The anonymous source request timed out.");
            }
            catch (HttpRequestException)
            {
                if (attempt >= options.MaxRetries) throw Error(ModSourceError.RequestFailed, "The anonymous source could not be reached.");
            }
            catch (IOException ex) when (ex is not ModSourceException)
            {
                if (attempt >= options.MaxRetries) throw Error(ModSourceError.RequestFailed, "The source response was interrupted.");
            }
            catch (ModSourceException ex) when (IsRetryable(ex.Failure))
            {
                // Long Retry-After values remain on the host limiter; do not tie up a caller for minutes.
                if (attempt >= options.MaxRetries || ex.Failure.RetryAfter > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)) throw;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(ModSourceFailure failure) => failure.Kind == ModSourceError.RateLimited ||
        failure.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    internal static bool IsPublicHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0;
    internal static ModSourceException Error(ModSourceError kind, string message) => new(new(kind, message));
    public void Dispose() => client.Dispose();
}
