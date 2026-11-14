using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal interface IModTextTranslator
{
    string Provider { get; }
    Task<string> TranslateAsync(string text, string language, CancellationToken cancellationToken);
}

internal interface IModTranslationCache { void ClearCache(); }

internal sealed record ModTextBatchTranslation(string?[] Texts, ModTranslationException? Failure = null);

internal interface IModBatchTextTranslator : IModTextTranslator
{
    Task<ModTextBatchTranslation> TranslateBatchAsync(IReadOnlyList<string> texts, string language,
        IProgress<ModTranslationProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal enum ModTranslationError { Unavailable, Offline, Timeout, RateLimited, InvalidResponse, TooLarge }

internal sealed class ModTranslationException(ModTranslationError error, DateTimeOffset? retryAfter = null) : Exception(error.ToString())
{
    internal ModTranslationError Error { get; } = error;
    internal DateTimeOffset? RetryAfter { get; } = retryAfter;
}

// Experimental anonymous endpoint, independent of ModDB HTTP/clearance and all account state.
// No redirects, cookies, credentials, request URLs or public text are logged.
internal sealed class GoogleModTextTranslator : IModBatchTextTranslator, IModTranslationCache, IDisposable
{
    internal const int ChunkLength = 700;
    internal const int BatchItems = 32, BatchCharacters = 3600, MaxUrlLength = 8000;
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
    public string Provider => "google-anonymous-batch-v2";
    internal long RequestCount => Interlocked.Read(ref requestCount);
    internal long CacheHits => Interlocked.Read(ref cacheHits);

    internal GoogleModTextTranslator(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? ModAnonymousTransport.Create()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GOHSMSLauncher/" + AppVersionValue);
    }

    private static string AppVersionValue => typeof(GoogleModTextTranslator).Assembly.GetName().Version?.ToString(3) ?? "1.0";

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
        if (!ModTranslationSettings.IsLanguage(language)) throw new ArgumentException("Unsupported translation language.", nameof(language));
        if (texts.Count > 4096 || texts.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > ChunkLength))
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
                    var address = new StringBuilder("https://translate.googleapis.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=" + language);
                    int characters = 0, count = 0;
                    while (offset + count < pending.Length && count < BatchItems)
                    {
                        string text = texts[pending[offset + count].Value[0]];
                        string query = "&q=" + Uri.EscapeDataString(text);
                        if (count > 0 && (characters + text.Length > BatchCharacters || address.Length + query.Length > MaxUrlLength)) break;
                        if (address.Length + query.Length > MaxUrlLength) throw new ModTranslationException(ModTranslationError.TooLarge);
                        address.Append(query); characters += text.Length; count++;
                    }
                    var translated = await RequestAsync(new Uri(address.ToString()), count, cancellationToken).ConfigureAwait(false);
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

    private async Task<string[]> RequestAsync(Uri address, int count, CancellationToken token)
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
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
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
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != count) throw new ModTranslationException(ModTranslationError.InvalidResponse);
                var translated = new string[count];
                for (int i = 0; i < count; i++)
                {
                    var part = root[i];
                    if (part.ValueKind == JsonValueKind.Array)
                    {
                        if (part.GetArrayLength() < 2 || part[1].ValueKind != JsonValueKind.String) throw new ModTranslationException(ModTranslationError.InvalidResponse);
                        part = part[0];
                    }
                    if (part.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(part.GetString()) || part.GetString()!.Length > 24000)
                        throw new ModTranslationException(ModTranslationError.InvalidResponse);
                    translated[i] = part.GetString()!;
                }
                return translated;
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

    private static string CacheKey(string text, string language) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(language + "\n" + text)));
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
