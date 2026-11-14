namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModTranslationCapabilities(int ChunkLength, int BatchItems, int BatchCharacters, IReadOnlyList<string> Languages)
{
    internal bool SupportsLanguage(string language) => Languages.Contains(language, StringComparer.Ordinal);
    internal static readonly ModTranslationCapabilities Default = new(700, 32, 3600, ModTranslationSettings.Languages);
}

internal interface IModTextTranslator
{
    string Provider { get; }
    ModTranslationCapabilities Capabilities => ModTranslationCapabilities.Default;
    Task<string> TranslateAsync(string text, string language, CancellationToken cancellationToken);
}

internal interface IModTranslationCache { void ClearCache(); }
internal sealed record ModTextBatchTranslation(string?[] Texts, ModTranslationException? Failure = null);

internal interface IModBatchTextTranslator : IModTextTranslator
{
    Task<ModTextBatchTranslation> TranslateBatchAsync(IReadOnlyList<string> texts, string language,
        IProgress<ModTranslationProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal enum ModTranslationError { Unavailable, Offline, Timeout, RateLimited, InvalidResponse, TooLarge, UnsupportedLanguage }

internal sealed class ModTranslationException(ModTranslationError error, DateTimeOffset? retryAfter = null) : Exception(error.ToString())
{
    internal ModTranslationError Error { get; } = error;
    internal DateTimeOffset? RetryAfter { get; } = retryAfter;
}

internal static class ModTranslationProviders
{
    internal const string Google = "google", TranSmart = "transmart";
    internal static bool IsProvider(string provider) => provider is Google or TranSmart;
    internal static IModTextTranslator Create(string provider) => provider switch
    {
        Google => new GoogleModTextTranslator(),
        TranSmart => new TranSmartModTextTranslator(),
        _ => throw new ArgumentException("Unsupported translation provider.", nameof(provider))
    };
}
