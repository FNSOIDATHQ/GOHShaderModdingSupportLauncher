using System.Net;
using System.Text;
using System.Text.Json;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// Shipped, offline checks for the same protocol and preferences used by the UI.
internal static class ModTranslationUiSmokeTest
{
    internal static void Run(string directory) => CheckAsync(directory).GetAwaiter().GetResult();

    private static async Task CheckAsync(string directory)
    {
        string path = Path.Combine(directory, "translation-smoke.json");
        File.WriteAllText(path, """{"version":1,"enabled":false,"language":"zh-CN","showOriginal":true}""");
        var store = new ModTranslationOptionsStore(path);
        var previous = store.Load();
        if (previous is not { Provider: ModTranslationProviders.Google, Enabled: false, ShowOriginal: true })
            throw new InvalidOperationException("Legacy translation preferences changed.");
        store.Save(previous with { Provider = ModTranslationProviders.TranSmart });
        if (store.Load() is not { Provider: ModTranslationProviders.TranSmart, Enabled: false, ShowOriginal: true })
            throw new InvalidOperationException("Translation provider did not persist.");

        using var handler = new SampleHandler();
        using var translator = new TranSmartModTextTranslator(handler);
        var first = await translator.TranslateBatchAsync(["First paragraph.", "Second paragraph.", "First paragraph."], "zh-CN").ConfigureAwait(false);
        if (first.Failure is not null || !first.Texts.SequenceEqual(new[] { "译 First paragraph.", "译 Second paragraph.", "译 First paragraph." }) ||
            translator.Capabilities.SupportsLanguage("pl") || !translator.Capabilities.SupportsLanguage("en"))
            throw new InvalidOperationException("Translation response order or language capabilities failed.");
        await translator.TranslateBatchAsync(["Second paragraph."], "zh-CN").ConfigureAwait(false);
        if (translator.RequestCount != 1 || translator.CacheHits != 1)
            throw new InvalidOperationException("Translation fragment cache failed.");
        var document = new ModReadingDocument { Blocks = [
            new() { Runs = [new() { Text = "Read instructions.\nKeep mod.zip v1.2.3 from https://example.org/guide." }] },
            new() { Kind = ModBlockKind.Code, Runs = [new() { Text = "unchanged code", Code = true }] }] };
        var engine = new ModDocumentTranslator(translator);
        using var google = new GoogleModTextTranslator();
        var result = await engine.TranslateAsync(document, "zh-CN").ConfigureAwait(false);
        string text = result.Document.Blocks[0].Runs[0].Text;
        if (result.FailedParts != 0 || !text.Contains("\n", StringComparison.Ordinal) || !text.Contains("mod.zip v1.2.3", StringComparison.Ordinal) ||
            !text.Contains("https://example.org/guide.", StringComparison.Ordinal) || result.Document.Blocks[1] != document.Blocks[1] ||
            new ModDocumentTranslator(google).Key(document, "zh-CN") == engine.Key(document, "zh-CN"))
            throw new InvalidOperationException("Translation display copy or provider cache identity failed.");
        engine.ClearCache(); translator.ClearCache();
        if (engine.TryGet(engine.Key(document, "zh-CN"), out _)) throw new InvalidOperationException("Translation cache cleanup failed.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await translator.TranslateAsync("Cancelled sample", "en", cancelled.Token).ConfigureAwait(false); throw new InvalidOperationException("Translation cancellation failed."); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        AppDiagnostics.Log("Translation provider self-test passed: legacy preferences, TranSmart anonymous protocol, ordered batches, capabilities, provider cache isolation, protected values, cleanup and cancellation (offline).");
    }

    private sealed class SampleHandler : HttpMessageHandler
    {
        private string? identity;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://transmart.qq.com/api/imt" ||
                request.Headers.Authorization is not null || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("Unexpected translation request.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
            var header = json.RootElement.GetProperty("header");
            string key = header.GetProperty("client_key").GetString()!;
            if (header.GetProperty("fn").GetString() != "auto_translation" || !key.StartsWith("desktop-GOHSMSLauncher-Windows-", StringComparison.Ordinal) ||
                identity is not null && identity != key)
                throw new InvalidOperationException("Anonymous translation identity changed.");
            identity = key;
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("header"); writer.WriteString("ret_code", "succ"); writer.WriteString("type", "auto_translation"); writer.WriteEndObject();
                writer.WriteString("src_lang", "en"); writer.WriteString("tgt_lang", json.RootElement.GetProperty("target").GetProperty("lang").GetString());
                writer.WriteStartArray("auto_translation");
                foreach (var text in json.RootElement.GetProperty("source").GetProperty("text_list").EnumerateArray())
                {
                    if (text.GetString()!.Contains("mod.zip", StringComparison.Ordinal) || text.GetString()!.Contains("https://", StringComparison.Ordinal))
                        throw new InvalidOperationException("Protected values were sent to the translator.");
                    writer.WriteStringValue("译 " + text.GetString());
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Encoding.UTF8.GetString(bytes.ToArray()), Encoding.UTF8, "application/json") };
        }
    }
}
