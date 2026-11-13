using System.Runtime.InteropServices;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// This command exercises the published executable's own transport, parser and cache.
// It never creates subscriptions, downloads archives or touches a game directory.
internal static class ModSourceDiagnostics
{
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
