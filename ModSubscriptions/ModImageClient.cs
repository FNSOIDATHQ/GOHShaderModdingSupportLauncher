using System.Diagnostics;
using System.Net;
using System.Text;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModImagePayload(byte[] Bytes, Uri FinalUri, string? ContentType);

// Borrows the page's anonymous client. Each hop has its own request, host lease and
// verification preparation, so an origin's temporary clearance cannot follow a redirect.
internal sealed class ModImageClient(HttpClient client, Action<HttpRequestMessage> prepare,
    Action<Uri, HttpStatusCode> requireVerification, Action<ModHttpDiagnostic>? diagnostic = null,
    ModHttpOptions? options = null, ModHostLimiter? limiter = null)
{
    internal const int MaxBytes = 20 * 1024 * 1024;
    private const int ChallengeBytes = 64 * 1024;
    private readonly ModHttpOptions settings = options ?? new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly ModHostLimiter hosts = limiter ?? ModHostLimiter.Shared;

    internal static bool IsAllowed(Uri initial, Uri current, string provider)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(current) || current.IsLoopback ||
            Uri.CheckHostName(current.IdnHost) != UriHostNameType.Dns || !current.IdnHost.Contains('.') ||
            current.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        return provider switch
        {
            "github" => GitHubParser.IsImageHost(current),
            "mowportal" => MowPortalParser.IsImage(current),
            _ => current.IdnHost.Equals(initial.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                 provider == "moddb" && current.IdnHost is "www.moddb.com" or "media.moddb.com"
        };
    }

    internal async Task<ModImagePayload> GetAsync(Uri uri, string provider, ModMediaOperation operation, CancellationToken token)
    {
        Uri current = uri;
        ModHttpStage stage = ModHttpStage.Request;
        int? status = null;
        string? contentType = null;
        long bytesRead = 0;
        long started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        void Report(ModHttpStage at, ModHttpFailureKind? failure = null) => ModHttpDiagnostics.Report(diagnostic,
            new(ModHttpDiagnostics.Host(current), at, 1, status, failure, ElapsedMilliseconds: Elapsed(),
                Media: operation, ContentType: contentType, Bytes: bytesRead));
        ModSourceException Error(ModHttpFailureKind kind, string message, ModSourceError error = ModSourceError.ParseFailed) =>
            new(new(error, message, status is { } code ? (HttpStatusCode)code : null, DiagnosticKind: kind));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(settings.Timeout);
            var ct = timeout.Token;
            var visited = new HashSet<Uri>();
            for (int redirects = 0; ; redirects++)
            {
                ct.ThrowIfCancellationRequested();
                stage = ModHttpStage.Redirect;
                if (!IsAllowed(uri, current, provider) || !visited.Add(current) || redirects > 8)
                    throw Error(ModHttpFailureKind.RedirectRejected, "The image address or redirect is not supported.", ModSourceError.AccessDenied);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                prepare(request);
                var host = hosts.For(current);
                await host.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var delay = host.NextRequest - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                    prepare(request);
                    host.NextRequest = DateTimeOffset.UtcNow + settings.HostInterval;
                    stage = ModHttpStage.Request; status = null; contentType = null; bytesRead = 0;
                    Report(stage);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    status = (int)response.StatusCode;
                    contentType = response.Content.Headers.ContentType?.MediaType;
                    stage = ModHttpStage.Headers; Report(stage);
                    if (ModChallengeDetector.IsChallenge(response)) requireVerification(current, response.StatusCode);
                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        stage = ModHttpStage.Redirect; Report(stage);
                        if (response.Headers.Location is null || !Uri.TryCreate(current, response.Headers.Location, out var target) ||
                            !IsAllowed(uri, target, provider))
                            throw Error(ModHttpFailureKind.RedirectRejected, "The image redirect has no valid address.", ModSourceError.RequestFailed);
                        current = new UriBuilder(target) { Fragment = "" }.Uri;
                        continue;
                    }
                    bool html = contentType is "text/html" or "application/xhtml+xml";
                    bool inspectPage = html || !response.IsSuccessStatusCode;
                    if (!inspectPage && response.Content.Headers.ContentLength > MaxBytes)
                        throw Error(ModHttpFailureKind.ImageTooLarge, "The image exceeds the display byte limit.");
                    stage = ModHttpStage.Body;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var bytes = new MemoryStream();
                    var buffer = new byte[16 * 1024];
                    int limit = inspectPage ? ChallengeBytes : MaxBytes;
                    while (true)
                    {
                        int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)bytes.Length)), ct).ConfigureAwait(false);
                        if (count == 0) break;
                        bytes.Write(buffer, 0, count); bytesRead = bytes.Length;
                        if (bytes.Length <= limit) continue;
                        if (inspectPage) break;
                        throw Error(ModHttpFailureKind.ImageTooLarge, "The image exceeds the display byte limit.");
                    }
                    string prefix = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, Math.Min((int)bytes.Length, ChallengeBytes));
                    if (ModChallengeDetector.IsChallengeHtml(prefix)) requireVerification(current, response.StatusCode);
                    if (!response.IsSuccessStatusCode)
                    {
                        var kind = status switch { 401 or 403 => ModSourceError.AccessDenied, 404 => ModSourceError.NotFound,
                            429 or 503 => ModSourceError.RateLimited, _ => ModSourceError.RequestFailed };
                        throw Error(status == 407 ? ModHttpFailureKind.Proxy : ModHttpFailureKind.Response, "The image request failed.", kind);
                    }
                    if (response.StatusCode == HttpStatusCode.PartialContent || bytes.Length == 0 || html ||
                        prefix.AsSpan().TrimStart('\uFEFF').TrimStart().StartsWith("<", StringComparison.Ordinal) ||
                        contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                        contentType != "application/octet-stream")
                        throw Error(ModHttpFailureKind.ImageContent, "The source did not return a complete raster image.");
                    if (response.Content.Headers.ContentLength is { } length && length != bytes.Length)
                        throw Error(ModHttpFailureKind.Response, "The image response was incomplete.", ModSourceError.RequestFailed);
                    ct.ThrowIfCancellationRequested(); Report(ModHttpStage.Completed);
                    return new(bytes.ToArray(), current, contentType);
                }
                finally { host.Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Report(ModHttpStage.Cancelled); throw; }
        catch (OperationCanceledException)
        { Report(stage, ModHttpFailureKind.Timeout); throw Error(ModHttpFailureKind.Timeout, "The image request timed out.", ModSourceError.Timeout); }
        catch (ModSourceException ex) { Report(stage, ex.Failure.DiagnosticKind ?? ModHttpFailureKind.Response); throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            var detail = ModHttpDiagnostics.FromException(current, stage, 1, ex, Elapsed()) with
                { Media = operation, ContentType = contentType, Bytes = bytesRead };
            ModHttpDiagnostics.Report(diagnostic, detail);
            throw new ModSourceException(new(detail.FailureKind == ModHttpFailureKind.Timeout ? ModSourceError.Timeout : ModSourceError.RequestFailed,
                ModHttpDiagnostics.FailureMessage(detail), detail.StatusCode is { } code ? (HttpStatusCode)code : null, DiagnosticKind: detail.FailureKind));
        }
    }
}
