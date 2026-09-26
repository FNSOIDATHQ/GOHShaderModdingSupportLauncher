using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GOHShaderModdingSupportLauncher.Properties;



namespace GOHShaderModdingSupportLauncher
{
    public partial class Tools : UserControl
    {
        private MainWindow main;

        private MainWindow.ToolsVars vars;
        public Tools(MainWindow owner)
        {
            main = owner;

            vars = main.toolsVars;

            InitializeComponent();

            
        }
        //private void PreCompileAllShader()
        //{
        //    main.ClearCacheWork();

        //    Launcher.ReplaceFile(main.universalVars.resourceDir);
        //    Launcher.ForceChangeSettings(main.universalVars.optionLoc);

        //    //start game with shader cache build
        //    Process game = Process.Start(main.universalVars.gameDir.GetFiles("call_to_arms.exe")[0].ToString(), "-rebuildshadercache");

        //    main.Hide();
        //    game.WaitForExit();

        //    main.CheckCompileWarning();

        //    main.Show();
        //}
        private void clearShaderCache_Click(object sender, RoutedEventArgs e)
        {
            main.ClearCacheWork();
            main.universalVars.lastCacheHash = "-1";
            main.universalVars.lastShaderHash = "0";
            MessageBox.Show(i18n.U_ClearAllCacheSuccessful, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void restore_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.ExtractFile("GOHShaderModdingSupportLauncher.pak.Ori.shader.lzma", main.universalVars.resourceDir + @"\shader.lzma", 358400);
            main.DecompressFileLZMA(main.universalVars.resourceDir + @"\shader.lzma", main.universalVars.resourceDir + @"\shader.pak");
            File.Delete(main.universalVars.resourceDir + @"\shader.lzma");

            // main.CompressFileLZMA(main.universalVars.resourceDir + @"\shader.pak", main.universalVars.resourceDir + @"\shader.lzma");
            
            MessageBox.Show(i18n.U_RestoreSuccessful, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void openConfig_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", main.universalVars.configLoc);
        }

        //gate of hell will only recompile files existed in resource/shader.pak/shader_cache
        //which means this mehod is completely useless for now
        private void precompile_Click(object sender, RoutedEventArgs e)
        {
            //manual check
            //string message = "Compile all Shader Cache is EXTREMELY slow. If you do not know what you are doing please press No.";
            //string title = "Manual Check";
            //MessageBoxButton buttons = MessageBoxButton.YesNo;
            //MessageBoxResult result = MessageBox.Show(message, title, buttons);
            //if (result == MessageBoxResult.No)
            //{
                
            //}
            //else
            //{
            //    PreCompileAllShader();
            //}
        }

        private void openCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", main.universalVars.cacheLoc);
        }

        private void openLogFolder_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", main.universalVars.profileLoc + "\\log");
        }

        private void openProfileFolder_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", main.universalVars.profileLoc + "\\profiles");
        }

        private void openWorkshopModFolder_Click(object sender, RoutedEventArgs e)
        {
            if (main.universalVars.workshopDir!=null)
            {
                Process.Start("explorer.exe", main.universalVars.workshopDir.FullName);
            }
            else {
                MessageBox.Show(i18n.U_WorkshopNotExist, i18n.Universal_Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            
        }
    }
}