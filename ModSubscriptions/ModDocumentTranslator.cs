using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModDocumentTranslation(ModReadingDocument Document, int FailedParts, ModTranslationError? Error = null, DateTimeOffset? RetryAfter = null);
internal sealed record ModTranslationProgress(int Completed, int Total);

// Immutable display copies retain every block/run, link, media address and code node.
// Work is bounded and sequential; completed documents alone enter the memory LRU.
internal sealed partial class ModDocumentTranslator(IModTextTranslator translator)
{
    private sealed record Cached(ModDocumentTranslation Value, long Bytes, DateTimeOffset Expires);
    private readonly Dictionary<string, Cached> cache = [];
    private readonly LinkedList<string> lru = [];
    private readonly object sync = new();
    private long cacheBytes;
    private long cacheGeneration;
    internal const long CacheLimit = 8 * 1024 * 1024;

    internal void ClearCache()
    { lock (sync) { cacheGeneration++; cache.Clear(); lru.Clear(); cacheBytes = 0; } }

    internal string Key(ModReadingDocument document, string language) => translator.Provider + ":" + language + ":" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, ModTranslationJsonContext.Default.ModReadingDocument)));

    internal bool TryGet(string key, out ModDocumentTranslation? value)
    {
        lock (sync)
        {
            value = null;
            if (!cache.TryGetValue(key, out var item)) return false;
            if (item.Expires <= DateTimeOffset.UtcNow) { cache.Remove(key); lru.Remove(key); cacheBytes -= item.Bytes; return false; }
            lru.Remove(key); lru.AddLast(key); value = item.Value; return true;
        }
    }

    internal async Task<ModDocumentTranslation> TranslateAsync(ModReadingDocument document, string language,
        IProgress<ModTranslationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!translator.Capabilities.SupportsLanguage(language)) throw new ModTranslationException(ModTranslationError.UnsupportedLanguage);
        long generation;
        lock (sync) generation = cacheGeneration;
        if (!ModDocumentParser.HasDocument(document)) throw new ModTranslationException(ModTranslationError.InvalidResponse);
        string key = Key(document, language);
        if (TryGet(key, out var cached)) return cached!;
        var blocks = ModDocumentParser.Walk(document.Blocks).ToArray();
        if (blocks.Sum(block => (long)block.Runs.Sum(run => run.Text.Length) + (block.Image?.Caption.Length ?? 0)) > 120000)
            throw new ModTranslationException(ModTranslationError.TooLarge);
        int total = blocks.Where(block => block.Kind != ModBlockKind.Code).Sum(block => block.Runs.Count(run => !run.Code && !string.IsNullOrWhiteSpace(run.Text)) + (string.IsNullOrWhiteSpace(block.Image?.Caption) ? 0 : 1));
        int completed = 0, failed = 0, requests = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var translatedParts = new Dictionary<string, string>(StringComparer.Ordinal);
        var batchTranslator = translator as IModBatchTextTranslator;
        ModTranslationException? lastError = null;
        bool stopped = false;
        if (batchTranslator is not null)
        {
            var pending = Texts(document.Blocks).SelectMany(UnprotectedParts).SelectMany(text => Chunks(text, translator.Capabilities.ChunkLength)).Select(text => text.Trim())
                .Where(text => text.Any(char.IsLetter)).Distinct(StringComparer.Ordinal).Take(4097).ToArray();
            if (pending.Length > 4096) throw new ModTranslationException(ModTranslationError.TooLarge);
            try
            {
                var batchResult = await batchTranslator.TranslateBatchAsync(pending, language, progress, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (batchResult.Texts.Length != pending.Length) throw new ModTranslationException(ModTranslationError.InvalidResponse);
                lastError = batchResult.Failure;
                for (int i = 0; i < pending.Length; i++)
                    if (batchResult.Texts[i] is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= 24000) translatedParts.Add(pending[i], text);
            }
            catch (ModTranslationException ex) { lastError = ex; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            { lastError = new(ModTranslationError.Timeout); }
        }
        async Task<string> Translate(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) return text;
            if (stopped && batchTranslator is null) { failed++; progress?.Report(new(++completed, total)); return text; }
            try
            {
                var result = new StringBuilder();
                // Stable source spans avoid relying on providers preserving synthetic markers.
                // Technical values are copied locally and never sent to the provider.
                int offset = 0;
                async Task AppendText(string source)
                {
                    foreach (string chunk in Chunks(source, translator.Capabilities.ChunkLength))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        string trimmed = chunk.Trim();
                        if (!trimmed.Any(char.IsLetter)) { result.Append(chunk); continue; }
                        if (!translatedParts.TryGetValue(trimmed, out string? translated))
                        {
                            if (batchTranslator is not null) throw lastError ?? new ModTranslationException(ModTranslationError.InvalidResponse);
                            if (++requests > 200) throw new ModTranslationException(ModTranslationError.TooLarge);
                            translated = await translator.TranslateAsync(trimmed, language, timeout.Token).ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                            translatedParts.Add(trimmed, translated);
                        }
                        if (string.IsNullOrWhiteSpace(translated) || translated.Length > 24000) throw new ModTranslationException(ModTranslationError.InvalidResponse);
                        int leading = chunk.Length - chunk.TrimStart().Length, trailing = chunk.Length - chunk.TrimEnd().Length;
                        result.Append(chunk.AsSpan(0, leading)); result.Append(translated.Trim());
                        if (trailing > 0) result.Append(chunk.AsSpan(chunk.Length - trailing));
                    }
                }
                foreach (Match match in ProtectedText().Matches(text))
                {
                    await AppendText(text[offset..match.Index]).ConfigureAwait(false);
                    result.Append(match.Value); offset = match.Index + match.Length;
                }
                await AppendText(text[offset..]).ConfigureAwait(false);
                return result.ToString();
            }
            catch (ModTranslationException ex)
            { failed++; lastError = ex; stopped = true; return text; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            { failed++; lastError = new(ModTranslationError.Timeout); stopped = true; return text; }
            finally { if (batchTranslator is null) progress?.Report(new(++completed, total)); }
        }
        async Task<ModContentBlock> Block(ModContentBlock block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Kind == ModBlockKind.Code) return block;
            var runs = new ModTextRun[block.Runs.Length];
            for (int i = 0; i < runs.Length; i++) runs[i] = block.Runs[i].Code ? block.Runs[i] : block.Runs[i] with { Text = await Translate(block.Runs[i].Text).ConfigureAwait(false) };
            var children = new ModContentBlock[block.Children.Length];
            for (int i = 0; i < children.Length; i++) children[i] = await Block(block.Children[i]).ConfigureAwait(false);
            var image = block.Image;
            if (image is not null) image = image with { Caption = await Translate(image.Caption).ConfigureAwait(false) };
            return block with { Runs = runs, Children = children, Image = image };
        }
        if (batchTranslator is null) progress?.Report(new(0, total));
        var translatedBlocks = new ModContentBlock[document.Blocks.Length];
        for (int i = 0; i < translatedBlocks.Length; i++) translatedBlocks[i] = await Block(document.Blocks[i]).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new ModDocumentTranslation(document with { Blocks = translatedBlocks }, failed, lastError?.Error, lastError?.RetryAfter);
        if (failed == 0)
        {
            long bytes = JsonSerializer.SerializeToUtf8Bytes(result.Document, ModTranslationJsonContext.Default.ModReadingDocument).LongLength * 2;
            lock (sync)
            {
                if (generation != cacheGeneration) return result;
                if (cache.Remove(key, out var previous)) { cacheBytes -= previous.Bytes; lru.Remove(key); }
                if (bytes <= CacheLimit)
                {
                    while (cache.Count >= 64 || cacheBytes + bytes > CacheLimit)
                    { string oldest = lru.First!.Value; cacheBytes -= cache[oldest].Bytes; cache.Remove(oldest); lru.RemoveFirst(); }
                    cache[key] = new(result, bytes, DateTimeOffset.UtcNow.AddMinutes(30)); lru.AddLast(key); cacheBytes += bytes;
                }
            }
        }
        return result;
    }

    // Prefer paragraph/sentence/word boundaries, leaving whitespace and UTF-16 pairs intact.
    internal static IEnumerable<string> Chunks(string text, int limit = 700)
    {
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit));
        int start = 0;
        while (start < text.Length)
        {
            if (text[start] is '\r' or '\n')
            {
                int length = text[start] == '\r' && start + 1 < text.Length && text[start + 1] == '\n' ? 2 : 1;
                yield return text.Substring(start, length); start += length; continue;
            }
            int end = Math.Min(text.Length, start + limit);
            int newline = text.AsSpan(start, end - start).IndexOfAny('\r', '\n');
            if (newline >= 0) end = start + newline;
            else if (end < text.Length)
            {
                int boundary = end;
                while (boundary > start + limit / 2 && !char.IsWhiteSpace(text[boundary - 1]) && text[boundary - 1] is not ('.' or '!' or '?' or '。' or '！' or '？')) boundary--;
                if (boundary > start + limit / 2) end = boundary;
                if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            yield return text[start..end]; start = end;
        }
    }

    private static IEnumerable<string> Texts(IEnumerable<ModContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.Kind == ModBlockKind.Code) continue;
            foreach (var run in block.Runs) if (!run.Code) yield return run.Text;
            if (block.Image is { } image) yield return image.Caption;
            foreach (string text in Texts(block.Children)) yield return text;
        }
    }

    private static IEnumerable<string> UnprotectedParts(string text)
    {
        int offset = 0;
        foreach (Match match in ProtectedText().Matches(text))
        { yield return text[offset..match.Index]; offset = match.Index + match.Length; }
        yield return text[offset..];
    }

    [GeneratedRegex("https?://[^\\s<>]+|[\"'][^\"'\\r\\n]{1,200}\\.[a-z0-9]{1,12}[\"']|(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\\s<>]+|(?:\\.{0,2}[\\\\/]|[\\w.-]+[\\\\/])[\\w./\\\\-]+|\\bv?\\d+(?:\\.\\d+)+(?:[-+][\\w.-]+)?\\b|\\b[\\w.-]+\\.[a-z0-9]{1,12}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedText();
}
