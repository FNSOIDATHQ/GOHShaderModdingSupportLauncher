namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private bool checkingLauncherUpdate;
    private GitHubLauncherUpdateClient? launcherUpdates;
    private readonly CancellationTokenSource launcherUpdateLifetime = new();
    internal bool SuppressLauncherUpdates { get; set; }

    internal async Task CheckLauncherUpdateAsync(bool manual = false)
    {
        // Existing offline UI fixtures suppress all remote activation; the updater has an explicit switch too.
        if (SuppressLauncherUpdates || SuppressSubscriptionActivation || launcherUpdateLifetime.IsCancellationRequested) return;
        string Text(string key) => LocalizedStrings.Instance.Get(key);
        if (checkingLauncherUpdate)
        {
            if (manual) MessageBox.Show(this, Text("LU_Checking"), Text("Update_Title"));
            return;
        }
        checkingLauncherUpdate = true;
        LauncherUpdate.Prepared? prepared = null;
        bool handedOff = false, prompted = false;
        try
        {
            launcherUpdates ??= new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "launcher-update.json"));
            var result = await launcherUpdates.CheckAsync(AppVersion.Current, force: manual, launcherUpdateLifetime.Token);
            if (!IsVisible || launcherUpdateLifetime.IsCancellationRequested) return;
            if (!manual && result.Release.State != LauncherUpdateState.Available)
            {
                AppDiagnostics.Log($"Launcher update check: {result.Release.State}; {result.Release.Tag}.");
                return;
            }
            prompted = true;
            var dialog = new LauncherUpdateDialog(result, launcherUpdates, Environment.ProcessPath);
            prepared = await dialog.ShowDialog<LauncherUpdate.Prepared?>(this);
            if (prepared is null || !IsVisible || launcherUpdateLifetime.IsCancellationRequested) return;
            IsEnabled = false;
            SaveSettings();
            LauncherUpdate.Start(prepared);
            handedOff = true;
            // Normal close awaits the owned subscription downloads/browser cleanup before the helper can replace us.
            Close();
        }
        catch (OperationCanceledException) when (launcherUpdateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppDiagnostics.Log("Launcher update failed.", ex);
            if ((manual || prompted) && IsVisible)
            {
                string details = Text("LU_Failed") + "\n\n" + ex.Message;
                if (ex is ModSubscriptions.ModSourceException { Failure.RetryAfter: { } after })
                    details += "\n" + Text("LU_RetryAfter") + " " + after.ToLocalTime().ToString("g");
                MessageBox.Show(this, details, Text("Update_Title"), image: MessageBoxImage.Error);
            }
        }
        finally
        {
            if (prepared is not null && !handedOff)
            {
                try { LauncherUpdate.Discard(prepared); }
                catch (IOException ex) { AppDiagnostics.Log("Unable to discard staged launcher update.", ex); }
            }
            if (!handedOff) IsEnabled = true;
            checkingLauncherUpdate = false;
        }
    }

    private void StopLauncherUpdates()
    {
        launcherUpdateLifetime.Cancel();
        launcherUpdates?.Dispose();
    }
}
