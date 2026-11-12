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
