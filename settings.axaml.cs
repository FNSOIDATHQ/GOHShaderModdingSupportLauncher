using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GOHShaderModdingSupportLauncher.Properties;


namespace GOHShaderModdingSupportLauncher
{
    public partial class Settings : UserControl
    {
        private MainWindow main;

        private MainWindow.SettingsVars vars;

        public Settings(MainWindow owner)
        {
            main = owner;

            vars = main.settingsVars;

            InitializeComponent();

            if (main.universalVars.gameDir.FullName != null)
            {
                gamePath.Text = main.universalVars.gameDir.FullName;
            }

            if (main.universalVars.profileLoc != null)
            {
                gameConfigPath.Text = main.universalVars.profileLoc;
            }

            pathConfirm.IsChecked = main.universalVars.AlwaysConfirm;
            clearCache.IsChecked = main.universalVars.NeedClearCache;
            restore.IsChecked = main.universalVars.NeedRestore;
            gameExit.IsChecked = main.universalVars.NeedRedisplay;
            compileWarning.IsChecked= main.universalVars.NeedCompileWarning;
            lockModList.IsChecked = main.universalVars.NeedLockModList;
            autoLoadCache.IsChecked = main.universalVars.NeedAutoLoad;
            refreshCacheWhenModified.IsChecked = main.universalVars.NeedCheckShaderModify;
        }

        private void gamePath_LostFocus(object sender, RoutedEventArgs e)
        {
            SetGamePath(gamePath.Text);
        }

        private void SetGamePath(string? path)
        {
            if (string.Equals(path, main.universalVars.gameDir?.FullName, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && main.TrySetManualGameDirectory(path))
                {
                    gamePath.Text = main.universalVars.gameDir!.FullName;
                    return;
                }
            }
            catch (Exception ex) when (FileManager.IsFileError(ex))
            {
                AppDiagnostics.Log("Unable to use selected game directory.", ex);
            }

            gamePath.Text = main.universalVars.gameDir?.FullName ?? "";
            MessageBox.Show(i18n.S_InvalidGamePath, i18n.Universal_Warning,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void gameConfigPath_LostFocus(object sender, RoutedEventArgs e)
        {
            SetGameConfigPath(gameConfigPath.Text);
        }

        private void SetGameConfigPath(string? path)
        {
            if (string.Equals(path, main.universalVars.profileLoc, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && main.TrySetManualProfileDirectory(path))
                {
                    gameConfigPath.Text = main.universalVars.profileLoc;
                    return;
                }
            }
            catch (Exception ex) when (FileManager.IsFileError(ex))
            {
                AppDiagnostics.Log("Unable to use selected game profile directory.", ex);
            }

            gameConfigPath.Text = main.universalVars.profileLoc;
            MessageBox.Show(i18n.S_InvalidProfilePath, i18n.Universal_Warning,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void browseGamePath_Click(object sender, RoutedEventArgs e)
        {
            string? path = await SelectFolderAsync(i18n.S_GamePath);
            if (path == null) return;

            gamePath.Text = path;
            SetGamePath(path);
        }

        private async void browseGameConfigPath_Click(object sender, RoutedEventArgs e)
        {
            string? path = await SelectFolderAsync(i18n.S_GameConfigPath);
            if (path == null) return;

            gameConfigPath.Text = path;
            SetGameConfigPath(path);
        }

        private async Task<string?> SelectFolderAsync(string title)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return null;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = title
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        private void pathConfirm_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.AlwaysConfirm = pathConfirm.IsChecked.Value;
        }

        private void autoLoadCache_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedAutoLoad = autoLoadCache.IsChecked.Value;
        }

        private void refreshCacheWhenModified_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedCheckShaderModify = refreshCacheWhenModified.IsChecked.Value;
        }

        private void restore_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedRestore = restore.IsChecked.Value;
#if DEBUG
            Trace.WriteLine(restore.IsChecked.Value + "  " + main.universalVars.NeedRestore);
#endif
        }

        private void clearCache_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedClearCache = clearCache.IsChecked.Value;
#if DEBUG
            Trace.WriteLine(clearCache.IsChecked.Value + "  " + main.universalVars.NeedClearCache);
#endif
        }

        private void gameExit_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedRedisplay = gameExit.IsChecked.Value;
        }

        private void compileWarning_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedCompileWarning = compileWarning.IsChecked.Value;
        }

        private void lockModList_Click(object sender, RoutedEventArgs e)
        {
            main.universalVars.NeedLockModList = lockModList.IsChecked.Value;
        }
    }
}