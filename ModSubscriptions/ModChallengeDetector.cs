using System.Net;
using HtmlAgilityPack;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal static class ModChallengeDetector
{
    // Cloudflare documents this response header as the explicit challenge signal.
    // A generic 403, a Server: cloudflare header, or the author's text is not enough.
    internal static bool IsChallenge(HttpResponseMessage response) =>
        response.Headers.TryGetValues("cf-mitigated", out var values) &&
        values.Any(value => value.Trim().Equals("challenge", StringComparison.OrdinalIgnoreCase));

    internal static bool IsChallengeHtml(string html)
    {
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 256 };
        try { document.LoadHtml(html); }
        catch (Exception ex) when (ex is ArgumentException or ApplicationException) { return false; }
        return IsChallengeDocument(document.DocumentNode);
    }

    internal static bool IsChallengeDocument(HtmlNode root)
    {
        string title = HtmlEntity.DeEntitize(root.Descendants("title").FirstOrDefault()?.InnerText ?? "")
            .Trim().TrimEnd('.', '…', '!');
        bool challengeTitle = title.Equals("Just a moment", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase);
        bool challengeForm = root.Descendants("form").Any(node => node.Id == "challenge-form" ||
            node.GetAttributeValue("action", "").Contains("__cf_chl", StringComparison.OrdinalIgnoreCase));
        bool challengeRuntime = root.Descendants("script").Any(node =>
            node.GetAttributeValue("src", "").Contains("/cdn-cgi/challenge-platform/", StringComparison.OrdinalIgnoreCase) ||
            node.InnerText.Contains("_cf_chl_opt", StringComparison.Ordinal));
        // Titles alone can be legitimate Mod/article names. A form titled as a challenge,
        // or the Cloudflare runtime together with its challenge structure, is needed.
        return (challengeTitle && (challengeForm || challengeRuntime)) || (challengeForm && challengeRuntime);
    }

    internal static ModSourceException Required(Uri uri, HttpStatusCode? status = null) =>
        new(new(ModSourceError.VerificationRequired, "The source requires a browser verification before requests can continue.",
            status, VerificationUri: ModVerificationState.Origin(uri)));
}
