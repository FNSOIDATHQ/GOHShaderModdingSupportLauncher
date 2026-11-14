using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModHttpOptions
{
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan HostInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal int MaxRetries { get; init; } = 2;
    internal int MaxTextBytes { get; init; } = 4 * 1024 * 1024;
    internal Action<ModHttpDiagnostic>? Diagnostic { get; init; }
    internal ModVerificationState? Verification { get; init; }
    internal ModBrowserClearance? Clearance { get; init; }
}

internal sealed record ModJsonResponse(HttpStatusCode Status, string? Body, string? ETag);

// Serializes requests (including response bodies) per host across all production clients.
internal sealed class ModHostLimiter
{
    internal static ModHostLimiter Shared { get; } = new();
    private readonly ConcurrentDictionary<string, Host> hosts = new(StringComparer.OrdinalIgnoreCase);
    internal ConcurrentDictionary<string, DateTimeOffset> GitHubCooldowns { get; } = new();
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
        : this(ModAnonymousTransport.Create(), options, ModHostLimiter.Shared) { }

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
        GetPayloadAsync(uri, allow, token, json: false);

    internal Task<string> GetJsonAsync(Uri uri, Func<Uri, bool> allow, CancellationToken token) =>
        GetPayloadAsync(uri, allow, token, json: true);

    // Conditional public GitHub reads share the same anonymous transport and core quota.
    internal Task<ModJsonResponse> GetGitHubJsonAsync(Uri uri, string? etag, CancellationToken token) =>
        SendAsync(uri, candidate => IsPublicHttps(candidate) && candidate.IdnHost == "api.github.com", false, token,
            async (response, ct) => new ModJsonResponse(response.StatusCode,
                response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound ? null :
                    await ReadPayloadAsync(response, ct, json: true).ConfigureAwait(false), response.Headers.ETag?.ToString()),
            json: true, ifNoneMatch: etag, allowNotModifiedOrMissing: true);

    private Task<string> GetPayloadAsync(Uri uri, Func<Uri, bool> allow, CancellationToken token, bool json) =>
        SendAsync(uri, allow, false, token, (response, ct) => ReadPayloadAsync(response, ct, json), json);

    private async Task<string> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct, bool json)
    {
            if (response.StatusCode == HttpStatusCode.PartialContent)
                throw Error(ModSourceError.ParseFailed, "The source returned an incomplete content page.");
            string? type = response.Content.Headers.ContentType?.MediaType;
            bool validType = json ? type is "application/json" or "application/vnd.github+json" :
                type is "text/html" or "application/xhtml+xml" or "application/rss+xml" or "application/xml" or "text/xml";
            if (type is not null && !validType)
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
            string html = System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            if (!json && ModChallengeDetector.IsChallengeHtml(html)) RequireVerification(response.RequestMessage!.RequestUri!, response.StatusCode);
            return html;
    }

    internal Task<Uri> ResolveFileAsync(Uri uri, Func<Uri, bool> allow, CancellationToken token) =>
        SendAsync(uri, allow, true, token, async (response, ct) =>
        {
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                string? html = await ReadChallengePageAsync(response, ct).ConfigureAwait(false);
                if (html is not null && ModChallengeDetector.IsChallengeHtml(html))
                    RequireVerification(response.RequestMessage!.RequestUri!, response.StatusCode, fileProbe: true);
            }
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
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, bool json = false,
        string? ifNoneMatch = null, bool allowNotModifiedOrMissing = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            Uri diagnosticUri = uri;
            long started = Stopwatch.GetTimestamp();
            ModHttpStage stage = ModHttpStage.Request;
            void Report(ModHttpDiagnostic diagnostic) => ModHttpDiagnostics.Report(options.Diagnostic, diagnostic);
            long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            try
            {
                Uri current = uri;
                for (int redirect = 0; redirect <= 8; redirect++)
                {
                    diagnosticUri = current;
                    if (!IsPublicHttps(current) || !allow(current))
                    {
                        Report(new(ModHttpDiagnostics.Host(current), ModHttpStage.Redirect, attempt + 1,
                            FailureKind: ModHttpFailureKind.RedirectRejected, ElapsedMilliseconds: Elapsed()));
                        throw new ModSourceException(new(ModSourceError.AccessDenied,
                            "The source redirected outside its supported anonymous hosts.", DiagnosticKind: ModHttpFailureKind.RedirectRejected));
                    }
                    CheckPaused(current, probe);
                    var host = limiter.For(current);
                    await host.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        ThrowIfGitHubLimited(current);
                        TimeSpan wait = host.NextRequest - DateTimeOffset.UtcNow;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                        ThrowIfGitHubLimited(current);
                        CheckPaused(current, probe);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(options.Timeout);
                        using var request = new HttpRequestMessage(HttpMethod.Get, current);
                        if (json)
                        {
                            request.Headers.Accept.Clear();
                            request.Headers.Accept.ParseAdd("application/json");
                        }
                        if (json && current.IdnHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Headers.Accept.Clear();
                            request.Headers.Accept.ParseAdd("application/vnd.github+json");
                            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                            if (ifNoneMatch is not null && EntityTagHeaderValue.TryParse(ifNoneMatch, out var entityTag))
                                request.Headers.IfNoneMatch.Add(entityTag);
                        }
                        options.Clearance?.Apply(request);
                        if (probe) request.Headers.Range = new RangeHeaderValue(0, 15);
                        if (current != uri) request.Headers.Referrer = uri;
                        host.NextRequest = DateTimeOffset.UtcNow + options.HostInterval;
                        stage = ModHttpStage.Request;
                        Report(new(ModHttpDiagnostics.Host(current), stage, attempt + 1, ElapsedMilliseconds: Elapsed()));
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        DateTimeOffset? githubAfter = ObserveGitHubQuota(current, response);
                        stage = ModHttpStage.Headers;
                        Report(new(ModHttpDiagnostics.Host(current), stage, attempt + 1, (int)response.StatusCode, ElapsedMilliseconds: Elapsed()));
                        if (ModChallengeDetector.IsChallenge(response)) RequireVerification(current, response.StatusCode, probe);
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirect == 8 || response.Headers.Location is null)
                                throw Error(ModSourceError.RequestFailed, "The source returned an invalid or looping redirect.");
                            current = new Uri(current, response.Headers.Location);
                            continue;
                        }
                        if (!response.IsSuccessStatusCode && !(allowNotModifiedOrMissing &&
                            response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound))
                        {
                            if (current.Host == "cloud-api.yandex.net" &&
                                response.Content.Headers.ContentType?.MediaType == "application/json")
                            {
                                string? body = await ReadChallengePageAsync(response, timeout.Token).ConfigureAwait(false);
                                if (ReadYandexFailure(body, response.StatusCode) is { } yandexFailure)
                                    throw new ModSourceException(yandexFailure);
                            }
                            if (current.IdnHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
                                response.StatusCode == HttpStatusCode.Forbidden && githubAfter is null &&
                                response.Content.Headers.ContentType?.MediaType == "application/json")
                            {
                                string? body = await ReadChallengePageAsync(response, timeout.Token).ConfigureAwait(false);
                                if (IsGitHubSecondaryLimit(body)) githubAfter = ObserveGitHubQuota(current, response, secondary: true);
                            }
                            if ((int)response.StatusCode is 403 or 503 &&
                                response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
                            {
                                string? html = await ReadChallengePageAsync(response, timeout.Token).ConfigureAwait(false);
                                if (html is not null && ModChallengeDetector.IsChallengeHtml(html)) RequireVerification(current, response.StatusCode, probe);
                            }
                            var retry = response.Headers.RetryAfter;
                            DateTimeOffset? after = githubAfter ?? retry?.Date ?? (retry?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
                            if ((int)response.StatusCode is 429 or 503)
                            {
                                DateTimeOffset next = after ?? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Pow(2, attempt));
                                if (next > host.NextRequest) host.NextRequest = next;
                            }
                            var kind = githubAfter is not null && (int)response.StatusCode is 403 or 429 ? ModSourceError.RateLimited : response.StatusCode switch
                            {
                                HttpStatusCode.TooManyRequests => ModSourceError.RateLimited,
                                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModSourceError.AccessDenied,
                                HttpStatusCode.NotFound or HttpStatusCode.Gone => ModSourceError.NotFound,
                                _ => ModSourceError.RequestFailed
                            };
                            throw new ModSourceException(new(kind, $"The source returned HTTP {(int)response.StatusCode}.", response.StatusCode, after,
                                response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ? ModHttpFailureKind.Proxy : ModHttpFailureKind.Response));
                        }
                        stage = ModHttpStage.Body;
                        T result = await read(response, timeout.Token).ConfigureAwait(false);
                        Report(new(ModHttpDiagnostics.Host(current), ModHttpStage.Completed, attempt + 1,
                            (int)response.StatusCode, ElapsedMilliseconds: Elapsed()));
                        return result;
                    }
                    finally { host.Gate.Release(); }
                }
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                var diagnostic = ModHttpDiagnostics.FromException(diagnosticUri, stage, attempt + 1, ex, Elapsed());
                Report(diagnostic);
                if (attempt >= options.MaxRetries) throw new ModSourceException(new(ModSourceError.Timeout,
                    ModHttpDiagnostics.FailureMessage(diagnostic), DiagnosticKind: diagnostic.FailureKind));
            }
            catch (HttpRequestException ex)
            {
                var diagnostic = ModHttpDiagnostics.FromException(diagnosticUri, stage, attempt + 1, ex, Elapsed());
                Report(diagnostic);
                if (attempt >= options.MaxRetries) throw new ModSourceException(new(
                    diagnostic.FailureKind == ModHttpFailureKind.Timeout ? ModSourceError.Timeout : ModSourceError.RequestFailed,
                    ModHttpDiagnostics.FailureMessage(diagnostic), ex.StatusCode, DiagnosticKind: diagnostic.FailureKind));
            }
            catch (IOException ex) when (ex is not ModSourceException)
            {
                var diagnostic = ModHttpDiagnostics.FromException(diagnosticUri, stage, attempt + 1, ex, Elapsed());
                Report(diagnostic);
                if (attempt >= options.MaxRetries) throw new ModSourceException(new(
                    diagnostic.FailureKind == ModHttpFailureKind.Timeout ? ModSourceError.Timeout : ModSourceError.RequestFailed,
                    ModHttpDiagnostics.FailureMessage(diagnostic), DiagnosticKind: diagnostic.FailureKind));
            }
            catch (OperationCanceledException)
            {
                Report(new(ModHttpDiagnostics.Host(diagnosticUri), ModHttpStage.Cancelled, attempt + 1, ElapsedMilliseconds: Elapsed()));
                throw;
            }
            catch (ModSourceException ex)
            {
                Report(new(ModHttpDiagnostics.Host(diagnosticUri), ModHttpStage.Failed, attempt + 1,
                    ex.Failure.StatusCode is { } status ? (int)status : null,
                    ex.Failure.DiagnosticKind ?? ModHttpFailureKind.Response,
                    ElapsedMilliseconds: Elapsed()));
                // Long Retry-After values remain on the host limiter; do not tie up a caller for minutes.
                if (!IsRetryable(ex.Failure) || attempt >= options.MaxRetries || ex.Failure.RetryAfter > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)) throw;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token).ConfigureAwait(false);
        }
    }

    private static string GitHubResource(Uri uri) => uri.AbsolutePath.StartsWith("/search/", StringComparison.Ordinal) ? "search" : "core";

    private static ModSourceFailure? ReadYandexFailure(string? body, HttpStatusCode status)
    {
        if (body is null) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            // Never forward the service's message or description: they may contain public keys or URLs.
            return error.GetString() switch
            {
                "DiskResourceDownloadLimitExceededError" or "DiskDownloadLimitExceededError" or "TooManyRequestsError" =>
                    new(ModSourceError.RateLimited, "The public Yandex file has reached its download quota. Retry later; account access is not supported.",
                        status, DateTimeOffset.UtcNow.AddHours(1)),
                "DiskNotFoundError" or "DiskPublicResourceDeactivatedError" =>
                    new(ModSourceError.NotFound, "The public Yandex share is unavailable or has been closed.", status),
                "DiskForbiddenError" or "ForbiddenError" or "DiskPublicResourceDownloadForbiddenError" =>
                    new(ModSourceError.AccessDenied, "The owner has disabled public Yandex downloads. Account access is not supported.", status),
                _ => null
            };
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private void ThrowIfGitHubLimited(Uri uri)
    {
        if (!uri.IdnHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return;
        DateTimeOffset next = DateTimeOffset.MinValue;
        foreach (string key in new[] { "*", GitHubResource(uri) })
            if (limiter.GitHubCooldowns.TryGetValue(key, out var until) && until > next) next = until;
        if (next > DateTimeOffset.UtcNow)
            throw new ModSourceException(new(ModSourceError.RateLimited, "GitHub's anonymous request quota is cooling down. Retry later.", RetryAfter: next));
    }

    private static bool IsGitHubSecondaryLimit(string? body)
    {
        if (body is null) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String &&
                message.GetString() is { } text && (text.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private DateTimeOffset? ObserveGitHubQuota(Uri uri, HttpResponseMessage response, bool secondary = false)
    {
        if (!uri.IdnHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        bool exhausted = Header("X-RateLimit-Remaining") == "0";
        DateTimeOffset? reset = null;
        if (long.TryParse(Header("X-RateLimit-Reset"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long seconds) && seconds is >= 0 and <= 253402300798)
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(1);
        DateTimeOffset? retry = response.Headers.RetryAfter?.Date ??
            (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
        bool rejected = response.StatusCode == HttpStatusCode.TooManyRequests || secondary;
        if (!exhausted && !rejected && retry is null) return null;
        DateTimeOffset after = new[] { exhausted ? reset : null, retry, rejected ? DateTimeOffset.UtcNow.AddMinutes(1) : null }
            .Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty(DateTimeOffset.UtcNow.AddMinutes(1)).Max();
        string resource = Header("X-RateLimit-Resource") ?? GitHubResource(uri);
        string key = retry is not null || rejected && !exhausted ? "*" : resource;
        limiter.GitHubCooldowns.AddOrUpdate(key, after, (_, previous) => previous > after ? previous : after);
        return after;
    }

    private static bool IsRetryable(ModSourceFailure failure) => failure.Kind != ModSourceError.VerificationRequired &&
        (failure.Kind == ModSourceError.RateLimited ||
        failure.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);

    private async Task<string?> ReadChallengePageAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > options.MaxTextBytes) return null;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > options.MaxTextBytes) return null;
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
    }

    private void CheckPaused(Uri uri, bool fileProbe)
    {
        if (options.Verification?.IsPausedFor(uri) == true) options.Verification.RecordProbe(uri, fileProbe);
        options.Verification?.ThrowIfPaused(uri);
    }

    private void RequireVerification(Uri uri, HttpStatusCode? status, bool fileProbe = false)
    {
        options.Clearance?.Clear(uri);
        options.Verification?.RecordProbe(uri, fileProbe);
        options.Verification?.Pause(uri);
        throw ModChallengeDetector.Required(uri, status);
    }

    internal static bool IsPublicHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0;
    internal static ModSourceException Error(ModSourceError kind, string message) => new(new(kind, message));
    public void Dispose() => client.Dispose();
}
