using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Experimental anonymous endpoint, independent of ModDB HTTP/clearance and all account state.
// No redirects, cookies, credentials, request URLs or public text are logged.
internal abstract class ModBatchTextTranslator : IModBatchTextTranslator, IModTranslationCache, IDisposable
{
    private const long CacheLimit = 4 * 1024 * 1024;
    private sealed record CachedText(string Value, int Bytes, DateTimeOffset Expires);
    private readonly Dictionary<string, CachedText> cache = [];
    private readonly LinkedList<string> lru = [];
    private readonly object cacheGate = new();
    private long cacheGeneration;
    private long cacheBytes, requestCount, cacheHits;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest, limitedUntil;
    public abstract string Provider { get; }
    public abstract ModTranslationCapabilities Capabilities { get; }
    protected abstract HttpRequestMessage CreateRequest(IReadOnlyList<string> texts, string language);
    protected abstract string[] ParseResponse(JsonElement root, IReadOnlyList<string> texts, string language);
    protected virtual bool FitsRequest(IReadOnlyList<string> texts, string language) => true;
    internal long RequestCount => Interlocked.Read(ref requestCount);
    internal long CacheHits => Interlocked.Read(ref cacheHits);

    protected ModBatchTextTranslator(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? ModAnonymousTransport.Create()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GOHSMSLauncher/" + AppVersionValue);
    }

    private static string AppVersionValue => typeof(ModBatchTextTranslator).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    public async Task<string> TranslateAsync(string text, string language, CancellationToken cancellationToken)
    {
        var result = await TranslateBatchAsync([text], language, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Texts[0] ?? throw result.Failure ?? new ModTranslationException(ModTranslationError.InvalidResponse);
    }

    public async Task<ModTextBatchTranslation> TranslateBatchAsync(IReadOnlyList<string> texts, string language,
        IProgress<ModTranslationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (cacheGate) generation = cacheGeneration;
        if (!Capabilities.SupportsLanguage(language)) throw new ModTranslationException(ModTranslationError.UnsupportedLanguage);
        if (texts.Count > 4096 || texts.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > Capabilities.ChunkLength))
            throw new ModTranslationException(ModTranslationError.TooLarge);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new string?[texts.Count];
            var missing = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < texts.Count; i++)
            {
                string key = CacheKey(texts[i], language);
                if (TryCached(key, out string? value)) { results[i] = value; Interlocked.Increment(ref cacheHits); }
                else
                {
                    if (!missing.TryGetValue(key, out var indexes)) missing.Add(key, indexes = []);
                    indexes.Add(i);
                }
            }
            progress?.Report(new(results.Count(value => value is not null), texts.Count));
            var pending = missing.ToArray();
            int offset = 0;
            try
            {
                while (offset < pending.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = new List<string>();
                    int characters = 0;
                    while (offset + batch.Count < pending.Length && batch.Count < Capabilities.BatchItems)
                    {
                        string text = texts[pending[offset + batch.Count].Value[0]];
                        if (batch.Count > 0 && characters + text.Length > Capabilities.BatchCharacters) break;
                        batch.Add(text);
                        if (!FitsRequest(batch, language))
                        {
                            batch.RemoveAt(batch.Count - 1);
                            if (batch.Count == 0) throw new ModTranslationException(ModTranslationError.TooLarge);
                            break;
                        }
                        characters += text.Length;
                    }
                    int count = batch.Count;
                    var translated = await RequestAsync(batch, language, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int i = 0; i < count; i++)
                    {
                        var item = pending[offset + i];
                        lock (cacheGate) { if (generation == cacheGeneration) Cache(item.Key, translated[i]); }
                        foreach (int index in item.Value) results[index] = translated[i];
                    }
                    offset += count;
                    progress?.Report(new(results.Count(value => value is not null), texts.Count));
                }
                return new(results);
            }
            catch (ModTranslationException ex) { return new(results, ex); }
        }
        finally { gate.Release(); }
    }

    private async Task<string[]> RequestAsync(IReadOnlyList<string> texts, string language, CancellationToken token)
    {
        if (limitedUntil > DateTimeOffset.UtcNow) throw new ModTranslationException(ModTranslationError.RateLimited, limitedUntil);
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var request = CreateRequest(texts, language);
                Interlocked.Increment(ref requestCount);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1000);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null)
                {
                    limitedUntil = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                    if (limitedUntil < DateTimeOffset.UtcNow.AddSeconds(1)) limitedUntil = DateTimeOffset.UtcNow.AddSeconds(1);
                    // A brief server-specified pause can recover in this operation. Longer
                    // cooldowns return to the reader without more traffic or endpoint switching.
                    if (attempt < 2 && limitedUntil - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(5))
                    { nextRequest = limitedUntil; continue; }
                    throw new ModTranslationException(ModTranslationError.RateLimited, limitedUntil);
                }
                if (attempt < 2 && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                { nextRequest = DateTimeOffset.UtcNow.AddSeconds(attempt == 0 ? 1 : 3); continue; }
                if (!response.IsSuccessStatusCode) throw new ModTranslationException(ModTranslationError.Unavailable);
                if (response.Content.Headers.ContentLength > 128 * 1024 || response.Content.Headers.ContentType?.MediaType != "application/json")
                    throw new ModTranslationException(ModTranslationError.InvalidResponse);
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > 128 * 1024) throw new ModTranslationException(ModTranslationError.InvalidResponse);
                    bytes.Write(buffer, 0, read);
                }
                token.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                return ParseResponse(json.RootElement, texts, language);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { if (attempt >= 2) throw new ModTranslationException(ModTranslationError.Timeout); nextRequest = DateTimeOffset.UtcNow.AddSeconds(attempt == 0 ? 1 : 3); }
            catch (HttpRequestException)
            { if (attempt >= 2) throw new ModTranslationException(ModTranslationError.Offline); nextRequest = DateTimeOffset.UtcNow.AddSeconds(attempt == 0 ? 1 : 3); }
            catch (IOException)
            { if (attempt >= 2) throw new ModTranslationException(ModTranslationError.Offline); nextRequest = DateTimeOffset.UtcNow.AddSeconds(attempt == 0 ? 1 : 3); }
            catch (JsonException) { throw new ModTranslationException(ModTranslationError.InvalidResponse); }
        }
    }

    private string CacheKey(string text, string language) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Provider + "\n" + language + "\n" + text)));
    private bool TryCached(string key, out string? value)
    {
        lock (cacheGate)
        {
            value = null;
            if (!cache.TryGetValue(key, out var item)) return false;
            if (item.Expires <= DateTimeOffset.UtcNow) { cache.Remove(key); lru.Remove(key); cacheBytes -= item.Bytes; return false; }
            lru.Remove(key); lru.AddLast(key); value = item.Value; return true;
        }
    }

    private void Cache(string key, string value)
    {
        int bytes = value.Length * 2 + 256;
        if (cache.Remove(key, out var previous)) { cacheBytes -= previous.Bytes; lru.Remove(key); }
        while (cache.Count > 0 && (cache.Count >= 2048 || cacheBytes + bytes > CacheLimit))
        { string oldest = lru.First!.Value; cacheBytes -= cache[oldest].Bytes; cache.Remove(oldest); lru.RemoveFirst(); }
        if (bytes > CacheLimit) return;
        cache[key] = new(value, bytes, DateTimeOffset.UtcNow.AddHours(6)); lru.AddLast(key); cacheBytes += bytes;
    }

    public void Dispose() => client.Dispose();

    public void ClearCache()
    {
        lock (cacheGate) { cacheGeneration++; cache.Clear(); lru.Clear(); cacheBytes = 0; }
    }
}
