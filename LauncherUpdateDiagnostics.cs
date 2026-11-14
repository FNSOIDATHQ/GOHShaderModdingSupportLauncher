namespace GOHShaderModdingSupportLauncher;

internal static class LauncherUpdateDiagnostics
{
    // Public metadata only: never downloads, replaces or starts a release attachment.
    internal static async Task<int> RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GOHSMSLauncher", "UpdateDiagnostics", Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "check.json");
        try
        {
            using var client = new GitHubLauncherUpdateClient(cache);
            var result = await client.CheckAsync(AppVersion.Current, force: true).ConfigureAwait(false);
            AppDiagnostics.Log($"Launcher release metadata read successfully. Current={AppVersion.Current}; tag={result.Release.Tag}; " +
                $"state={result.Release.State}; issue={result.Release.IssueKey ?? "none"}. No attachment downloaded or applied.");
            return 0;
        }
        catch (Exception ex) { AppDiagnostics.Log("Launcher release metadata check failed.", ex); return 1; }
        finally
        {
            try { File.Delete(cache); if (Directory.Exists(directory)) Directory.Delete(directory); }
            catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Unable to clean launcher release diagnostic.", ex); }
        }
    }
}
