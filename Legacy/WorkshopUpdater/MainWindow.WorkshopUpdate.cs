using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GOHShaderModdingSupportLauncher.Properties;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private bool checkingLauncherUpdate;
    private readonly HashSet<string> checkedUpdatePaths = new(StringComparer.OrdinalIgnoreCase);

        internal async Task CheckLauncherUpdateAsync(bool manual = false)
        {
            var workshop = universalVars.workshopDir?.FullName;
            var executable = Environment.ProcessPath;
            string Text(string key) => i18n.ResourceManager.GetString(key, i18n.Culture) ?? key;
            void Notice(string key) => MessageBox.Show(Text(key), Text("Update_Title"));
            if (checkingLauncherUpdate)
            {
                if (manual) Notice("Update_Checking");
                return;
            }
            if (workshop == null || !Directory.Exists(workshop) || executable == null)
            {
                if (manual) Notice("Update_PathUnavailable");
                return;
            }
            if (!manual && !checkedUpdatePaths.Add(workshop)) return;
            checkingLauncherUpdate = true;
            try
            {
                var candidate = await Task.Run(() => LauncherUpdate.Find(workshop, executable));
                if (!IsVisible) return;
                if (workshop != universalVars.workshopDir?.FullName)
                {
                    if (manual) Notice("Update_PathChanged");
                    return;
                }
                if (candidate == null)
                {
                    if (manual) Notice("Update_NotAvailable");
                    return;
                }
                if (await MessageBox.ConfirmAsync(this,
                    Text("Update_Available") + "\n\n" + candidate.Source, Text("Update_Title")) != MessageBoxResult.Yes) return;
                // Keep controls disabled while handing off to the update helper.
                IsEnabled = false;
                var prepared = LauncherUpdate.Prepare(candidate);
                SaveSettings();
                LauncherUpdate.Start(prepared);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else Close();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Launcher update failed.", ex);
                if (IsVisible) MessageBox.Show(Text("Update_Failed") + "\n\n" + ex.Message,
                    Text("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsEnabled = true;
                checkingLauncherUpdate = false;
            }
        }

        internal bool TrySetManualProfileDirectory(string path)
        {
            if (!TrySetProfileDirectory(path)) return false;
            HasGetProfileLoc = true;
            OnManualPathChanged();
            SaveSettings();
            return true;
        }

}
