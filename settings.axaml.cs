using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;


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
            main.universalVars.gameDir = new DirectoryInfo(gamePath.Text);
            DirectoryInfo[] searchResult = main.universalVars.gameDir.GetDirectories("../../resource");
            if (searchResult.Length > 0)
            {
                main.universalVars.resourceDir = searchResult[0];
                //MessageBox.Show(resourceDir.FullName);
            }
        }

        private void gameConfigPath_LostFocus(object sender, RoutedEventArgs e)
        {
            main.universalVars.profileLoc = gameConfigPath.Text;
            main.universalVars.cacheLoc = main.universalVars.profileLoc + "\\shader_cache";
            main.universalVars.optionLoc = main.universalVars.profileLoc + "\\profiles";

            if (Directory.Exists(main.universalVars.optionLoc) == true)
            {
                string[] searchResult = Directory.GetDirectories(main.universalVars.optionLoc);
                if (searchResult.Length > 0)
                {
                    main.universalVars.optionLoc = searchResult[0] + @"\options.set";
                    //MessageBox.Show(optionLoc);
                }
            }
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