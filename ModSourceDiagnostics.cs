using System.Runtime.InteropServices;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// This command exercises the published executable's own transport, parser and cache.
// It never creates subscriptions, downloads archives or touches a game directory.
internal static class ModSourceDiagnostics
{
    internal static async Task<int> RunTranslationAsync()
    {
        AppDiagnostics.Log("Google translation check: anonymous production WinHTTP, public sample text only; no game files or subscription changes.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var translator = new GoogleModTextTranslator();
        try
        {
            string shortText = await translator.TranslateAsync("Mod installation instructions.", "zh-CN", timeout.Token).ConfigureAwait(false);
            if (!shortText.Any(c => c is >= '\u4e00' and <= '\u9fff')) throw new ModTranslationException(ModTranslationError.InvalidResponse);
            string text = string.Concat(Enumerable.Repeat("Read the installation instructions before downloading this public modification. Extract the complete archive into the game mods folder and keep a backup. ", 20)) +
                "Download mod.zip v1.2.3 from https://example.org/guide.";
            var code = new ModContentBlock { Kind = ModBlockKind.Code, Runs = [new() { Text = "mod.zip v1.2.3", Code = true }] };
            var document = new ModReadingDocument { Blocks = [new() { Runs = [new() { Text = text }] }, code] };
            var engine = new ModDocumentTranslator(translator);
            var result = await engine.TranslateAsync(document, "zh-CN", cancellationToken: timeout.Token).ConfigureAwait(false);
            if (result.FailedParts > 0) throw new ModTranslationException(result.Error ?? ModTranslationError.Unavailable, result.RetryAfter);
            string translated = result.Document.Blocks[0].Runs[0].Text;
            if (translated.Count(c => c is >= '\u4e00' and <= '\u9fff') < 100 || !translated.Contains("mod.zip v1.2.3", StringComparison.Ordinal) ||
                !translated.Contains("https://example.org/guide.", StringComparison.Ordinal) || result.Document.Blocks[1] != code || document.Blocks[0].Runs[0].Text != text ||
                !engine.TryGet(engine.Key(document, "zh-CN"), out var cached) || !ReferenceEquals(cached, result))
                throw new ModTranslationException(ModTranslationError.InvalidResponse);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await translator.TranslateAsync("Cancelled public sample", "en", cancelled.Token).ConfigureAwait(false); return 1; }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            AppDiagnostics.Log($"Google translation check passed: short text; {text.Length} public characters in {ModDocumentTranslator.Chunks(text).Count()} chunks; protected values, code, originals, cache and cancellation; HTTP requests={translator.RequestCount}, cached pieces={translator.CacheHits}.");
            return 0;
        }
        catch (ModTranslationException ex)
        { AppDiagnostics.Log($"Google translation check failed: kind={ex.Error}, retryAfter={ex.RetryAfter:O}."); return 1; }
        catch (OperationCanceledException) { AppDiagnostics.Log("Google translation check failed: total time limit reached."); return 1; }
        catch (Exception ex) { AppDiagnostics.Log($"Google translation check failed: {ex.GetType().Name}, HResult=0x{ex.HResult:X8}."); return 1; }
    }

    internal static async Task<int> RunAsync()
    {
        AppDiagnostics.Log("ModDB connectivity check: " + RuntimeInformation.FrameworkDescription +
            "; anonymous requests, no accounts or cookies.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var source = new ModDbSource(diagnostic: entry => AppDiagnostics.Log("ModDB HTTP: " + entry));
        try
        {
            var browse = await source.BrowseAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!Report("browse", browse)) return 1;
            AppDiagnostics.Log($"ModDB connectivity check passed: {browse.Content!.Items.Length} game-scoped Mods.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Log("ModDB connectivity check failed: total time limit reached.");
            return 1;
        }
        catch (Exception ex)
        {
            // No raw exception message: it may contain a proxy address or a signed URI.
            AppDiagnostics.Log($"ModDB connectivity check failed: {ex.GetType().Name}, HResult=0x{ex.HResult:X8}.");
            return 1;
        }
    }

    internal static async Task<int> RunImagesAsync()
    {
        AppDiagnostics.Log("ModDB image check: live galleries, complete thumbnail/original downloads and raster decoding; no game files.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        void Log(ModHttpDiagnostic entry) => AppDiagnostics.Log(entry.ToString());
        using var source = new ModDbSource(diagnostic: Log);
        using var client = new HttpClient(ModAnonymousTransport.Create()) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GOHSMSLauncher/{AppVersion.Current} (+https://github.com/FNSOIDATHQ/GOHShaderModdingSupportLauncher)");
        var media = new ModImageClient(client, _ => { }, (uri, status) => throw ModChallengeDetector.Required(uri, status), Log);
        try
        {
            foreach (string slug in new[] { "replays-unlocked", "hotmod" })
            {
                var gallery = await source.GetImagesAsync(new("https://www.moddb.com/mods/" + slug), cancellationToken: timeout.Token).ConfigureAwait(false);
                if (!Report("image gallery", gallery) || gallery.Content!.Items.Length == 0) return 1;
                AppDiagnostics.Log($"ModDB image gallery: sample={slug}, items={gallery.Content.Items.Length}, next={gallery.Content.NextPage is not null}.");
                var image = await source.GetImageAsync(gallery.Content.Items[0].PageUrl, cancellationToken: timeout.Token).ConfigureAwait(false);
                if (!Report("image page", image)) return 1;
                foreach (bool original in new[] { false, true })
                {
                    var url = original ? image.Content!.ImageUrl : image.Content!.ThumbnailUrl ?? image.Content.ImageUrl;
                    var operation = original ? ModMediaOperation.Original : ModMediaOperation.Thumbnail;
                    var payload = await media.GetAsync(url, "moddb", operation, timeout.Token).ConfigureAwait(false);
                    using var bitmap = ModImageDecoder.Decode(payload, original ? 1600 : 360, operation, timeout.Token, Log);
                    AppDiagnostics.Log($"ModDB decoded image: sample={slug}, media={operation}, pixels={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}.");
                }
                if (gallery.Content.NextPage is { } next)
                {
                    var more = await source.GetImagesAsync(next, cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (!Report("next image gallery", more)) return 1;
                }
            }
            AppDiagnostics.Log("ModDB image check passed: both galleries, thumbnails and originals fully decoded.");
            return 0;
        }
        catch (ModSourceException ex)
        {
            AppDiagnostics.Log($"ModDB image check failed: kind={ex.Failure.Kind}, HTTP={(int?)ex.Failure.StatusCode}, diagnostic={ex.Failure.DiagnosticKind}.");
            return 1;
        }
        catch (OperationCanceledException) { AppDiagnostics.Log("ModDB image check failed: total time limit reached."); return 1; }
        catch (Exception ex) { AppDiagnostics.Log($"ModDB image check failed: {ex.GetType().Name}, HResult=0x{ex.HResult:X8}."); return 1; }
    }

    private static bool Report<T>(string section, ModSourceResult<T> result)
    {
        if (result.IsSuccess && !result.IsStale)
        {
            AppDiagnostics.Log($"ModDB {section}: success.");
            return true;
        }
        var failure = result.Failure;
        AppDiagnostics.Log($"ModDB {section}: failed; kind={failure?.Kind}, diagnostic={failure?.DiagnosticKind}, " +
            $"HTTP={(int?)failure?.StatusCode}, cached={result.Content is not null}.");
        return false;
    }
}
