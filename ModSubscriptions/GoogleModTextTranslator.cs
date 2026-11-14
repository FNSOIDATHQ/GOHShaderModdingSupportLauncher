using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

// Fixed anonymous Google endpoint; no account credentials or endpoint rotation.
internal sealed class GoogleModTextTranslator(HttpMessageHandler? handler = null) : ModBatchTextTranslator(handler)
{
    internal const int ChunkLength = 700;
    internal const int BatchItems = 32, BatchCharacters = 3600, MaxUrlLength = 8000;
    public override string Provider => "google-anonymous-batch-v2";
    public override ModTranslationCapabilities Capabilities { get; } = new(ChunkLength, BatchItems, BatchCharacters, ModTranslationSettings.Languages);

    private static Uri Address(IReadOnlyList<string> texts, string language)
    {
        var address = new StringBuilder("https://translate.googleapis.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=" + language);
        foreach (string text in texts) address.Append("&q=").Append(Uri.EscapeDataString(text));
        return new(address.ToString());
    }

    protected override bool FitsRequest(IReadOnlyList<string> texts, string language) => Address(texts, language).AbsoluteUri.Length <= MaxUrlLength;
    protected override HttpRequestMessage CreateRequest(IReadOnlyList<string> texts, string language) => new(HttpMethod.Get, Address(texts, language));

    protected override string[] ParseResponse(JsonElement root, IReadOnlyList<string> texts, string language)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != texts.Count) throw new ModTranslationException(ModTranslationError.InvalidResponse);
        var translated = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
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
}
