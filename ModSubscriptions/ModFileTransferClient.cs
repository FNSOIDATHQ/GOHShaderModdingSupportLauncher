using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModResumeMetadata(string FileId, string Revision, long? TotalBytes, string? ETag, DateTimeOffset? LastModified);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ModResumeMetadata))]
internal partial class ModResumeJsonContext : JsonSerializerContext { }

internal sealed record ModTransferOptions
{
    internal int MaxRetries { get; init; } = 2;
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan HostInterval { get; init; } = TimeSpan.FromSeconds(1);
}

// Streams only public ModDB mirrors. Temporary mirror addresses never enter durable state.
internal sealed class ModFileTransferClient : IDisposable
{
    private readonly HttpClient client;
    private readonly ModTransferOptions options;
    private readonly ModHostLimiter limiter;

    internal ModFileTransferClient(ModTransferOptions? options = null)
        : this(new SocketsHttpHandler
        {
            UseCookies = false, Credentials = null, PreAuthenticate = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(15)
        }, options, ModHostLimiter.Shared) { }

    internal ModFileTransferClient(HttpMessageHandler handler, ModTransferOptions? options, ModHostLimiter limiter)
    {
        this.options = options ?? new();
        if (this.options.MaxRetries is < 0 or > 4 || this.options.IdleTimeout <= TimeSpan.Zero ||
            this.options.RetryDelay < TimeSpan.Zero || this.options.HostInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.limiter = limiter;
        client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GOHSMSLauncher/" +
            (typeof(ModFileTransferClient).Assembly.GetName().Version?.ToString(3) ?? "1.0") +
            " (+https://github.com/FNSOIDATHQ/GOHShaderModdingSupportLauncher)");
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    internal int MaxRetries => options.MaxRetries;
    internal TimeSpan RetryDelay => options.RetryDelay;

    internal async Task TransferAsync(ModDownloadLink link, string partialPath, string resumePath,
        Action<long, long?> progress, CancellationToken token)
    {
        ManagedModFiles.EnsureSafe(partialPath);
        ManagedModFiles.EnsureSafe(resumePath);
        var resume = ReadResume(resumePath);
        long offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (resume is null || resume.FileId != link.File.Id || resume.Revision != link.File.Revision ||
            (resume.ETag is null && resume.LastModified is null) || resume.TotalBytes is null ||
            offset >= resume.TotalBytes || link.File.SizeBytes is { } expected && resume.TotalBytes != expected)
            offset = 0;

        Uri current = link.DownloadUrl;
        for (int redirect = 0; redirect <= 8; redirect++)
        {
            token.ThrowIfCancellationRequested();
            if (!AnonymousModHttpClient.IsPublicHttps(current) || !ModDbParser.IsMirrorHost(current))
                throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "The download left its supported anonymous mirror hosts.");
            var host = limiter.For(current);
            await host.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                TimeSpan wait = host.NextRequest - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.FromSeconds(30))
                    throw new ModSourceException(new(ModSourceError.RateLimited, "The mirror requested a longer cooldown. Retry later.", RetryAfter: host.NextRequest));
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.IdleTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = resume!.ETag is { } etag
                        ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag))
                        : new RangeConditionHeaderValue(resume.LastModified!.Value);
                }
                host.NextRequest = DateTimeOffset.UtcNow + options.HostInterval;
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirect == 8 || response.Headers.Location is null)
                        throw AnonymousModHttpClient.Error(ModSourceError.RequestFailed, "The mirror returned an invalid redirect.");
                    current = new Uri(current, response.Headers.Location);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    DateTimeOffset? after = response.Headers.RetryAfter?.Date ??
                        (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
                    if ((int)response.StatusCode is 429 or 503)
                        host.NextRequest = after ?? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
                    var kind = response.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => ModSourceError.RateLimited,
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModSourceError.AccessDenied,
                        HttpStatusCode.NotFound or HttpStatusCode.Gone => ModSourceError.NotFound,
                        _ => ModSourceError.RequestFailed
                    };
                    throw new ModSourceException(new(kind, $"The mirror returned HTTP {(int)response.StatusCode}.", response.StatusCode, after));
                }
                string? type = response.Content.Headers.ContentType?.MediaType;
                if (type is not null && (type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("xml", StringComparison.OrdinalIgnoreCase)) ||
                    response.Content.Headers.ContentEncoding.Count != 0)
                    throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "The mirror returned a page or an encoded response instead of the selected file.");

                long? total;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (offset == 0 || range?.From != offset || range.Unit != "bytes" || range.Length is null ||
                        range.To != range.Length - 1 || range.Length != resume!.TotalBytes ||
                        response.Content.Headers.ContentLength != range.Length - offset ||
                        (resume.ETag is { } knownTag && response.Headers.ETag?.ToString() != knownTag) ||
                        (resume.ETag is null && response.Content.Headers.LastModified != resume.LastModified))
                        throw new InvalidDataException("The mirror returned an inconsistent resume response.");
                    total = range.Length;
                }
                else
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException("The mirror returned an unsupported success response.");
                    offset = 0; // A server ignoring Range, or a changed validator, requires a complete restart.
                    total = response.Content.Headers.ContentLength ?? link.File.SizeBytes;
                }
                if (link.File.SizeBytes is { } size && total is { } length && size != length)
                    throw new InvalidDataException("The selected file size no longer matches its download.");
                ManagedModFiles.EnsureSpace(partialPath, checked((total ?? 0) - offset));
                string? strongTag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                bool canResume = response.Headers.AcceptRanges.Contains("bytes") &&
                    (strongTag is not null || response.Content.Headers.LastModified is not null) && total is not null;
                ManagedModFiles.EnsureSafe(partialPath);
                await using var destination = new FileStream(partialPath, offset > 0 ? FileMode.Open : FileMode.Create,
                    FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                destination.Position = offset;
                SaveResume(resumePath, canResume ? new(link.File.Id, link.File.Revision, total, strongTag, response.Content.Headers.LastModified) : null);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                byte[] buffer = new byte[128 * 1024];
                long received = offset;
                progress(received, total);
                while (true)
                {
                    timeout.CancelAfter(options.IdleTimeout);
                    int count;
                    try { count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                    catch (IOException) { throw new HttpRequestException("The mirror interrupted the response stream."); }
                    if (count == 0) break;
                    if (received == 0)
                    {
                        string prefix = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Min(count, 32)).TrimStart();
                        if (prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
                            throw AnonymousModHttpClient.Error(ModSourceError.AccessDenied, "The mirror returned an access page instead of a file.");
                    }
                    if (total is { } expectedTotal && count > expectedTotal - received)
                        throw new InvalidDataException("The download exceeds the declared file size.");
                    ManagedModFiles.EnsureSpace(partialPath, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    received = checked(received + count);
                    progress(received, total);
                }
                await destination.FlushAsync(token).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                if (received == 0 || total is { } finalTotal && received != finalTotal)
                    throw new EndOfStreamException("The download ended before the declared file size.");
                return;
            }
            finally { host.Gate.Release(); }
        }
        throw new InvalidDataException("The mirror redirect limit was exceeded.");
    }

    private static ModResumeMetadata? ReadResume(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4096) return null;
            var data = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModResumeJsonContext.Default.ModResumeMetadata);
            if (data?.ETag is { } etag && (!EntityTagHeaderValue.TryParse(etag, out var tag) || tag.IsWeak)) return null;
            return data;
        }
        catch (JsonException) { return null; }
    }

    private static void SaveResume(string path, ModResumeMetadata? value)
    {
        ManagedModFiles.EnsureSafe(path);
        if (value is null) { File.Delete(path); return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ModResumeJsonContext.Default.ModResumeMetadata);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() => client.Dispose();
}
