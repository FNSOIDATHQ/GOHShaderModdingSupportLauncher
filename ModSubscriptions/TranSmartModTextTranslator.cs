using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Anonymous public web protocol. One generated client identity per provider instance;
// never import sessions, persist credentials, or rotate identities after a failure.
internal sealed class TranSmartModTextTranslator(HttpMessageHandler? handler = null) : ModBatchTextTranslator(handler)
{
    private readonly string clientKey = "desktop-GOHSMSLauncher-Windows-" + Guid.NewGuid().ToString("D") + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public override string Provider => "transmart-anonymous-batch-v1";
    public override ModTranslationCapabilities Capabilities { get; } = new(1000, 16, 4000,
        ["en", "zh-CN", "ja", "ko", "ru", "de", "fr", "es", "pt"]);

    private static string LanguageCode(string language) => language == "zh-CN" ? "zh" : language;

    protected override HttpRequestMessage CreateRequest(IReadOnlyList<string> texts, string language)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("header"); writer.WriteString("fn", "auto_translation"); writer.WriteString("client_key", clientKey); writer.WriteEndObject();
            writer.WriteString("type", "plain"); writer.WriteString("model_category", "normal"); writer.WriteString("text_domain", "general");
            writer.WriteStartObject("source"); writer.WriteString("lang", "auto"); writer.WriteStartArray("text_list");
            foreach (string text in texts) writer.WriteStringValue(text);
            writer.WriteEndArray(); writer.WriteEndObject();
            writer.WriteStartObject("target"); writer.WriteString("lang", LanguageCode(language)); writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt")
        { Content = new StringContent(Encoding.UTF8.GetString(bytes.ToArray()), Encoding.UTF8, "application/json") };
        request.Headers.Referrer = new("https://transmart.qq.com/");
        return request;
    }

    protected override string[] ParseResponse(JsonElement root, IReadOnlyList<string> texts, string language)
    {
        static string? String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("header", out var header))
            throw new ModTranslationException(ModTranslationError.InvalidResponse);
        if (String(header, "ret_code") == "Unsupported-Language") throw new ModTranslationException(ModTranslationError.UnsupportedLanguage);
        if (String(header, "ret_code") != "succ") throw new ModTranslationException(ModTranslationError.Unavailable);
        if (String(header, "type") != "auto_translation" || String(root, "tgt_lang") != LanguageCode(language) ||
            !root.TryGetProperty("auto_translation", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != texts.Count)
            throw new ModTranslationException(ModTranslationError.InvalidResponse);
        var translated = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            var part = parts[i];
            if (part.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(part.GetString()) || part.GetString()!.Length > 24000)
                throw new ModTranslationException(ModTranslationError.InvalidResponse);
            translated[i] = part.GetString()!;
        }
        // Identical output is valid when the detected source already is the target.
        if (texts.Count > 0 && String(root, "src_lang") != LanguageCode(language) && translated.SequenceEqual(texts, StringComparer.Ordinal))
            throw new ModTranslationException(ModTranslationError.InvalidResponse);
        return translated;
    }
}
