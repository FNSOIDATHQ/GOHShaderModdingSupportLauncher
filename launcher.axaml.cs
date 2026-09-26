using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GOHShaderModdingSupportLauncher.Properties;


namespace GOHShaderModdingSupportLauncher
{
    public partial class Launcher : UserControl
    {
        private MainWindow main;

        private MainWindow.LauncherVars vars;

        private bool isLaunching;
        public Launcher(MainWindow owner)
        {
            main = owner;

            vars = main.launcherVars;

            InitializeComponent();



            launchMethod.SelectedIndex = (int)vars.lm;

            addModInfo.IsChecked = vars.showAddModInfo;
            runAsAdmin.IsChecked = vars.runAsAdmin;

        }

        public static void ReplaceFile(DirectoryInfo resourceDir)
        {
            FileInfo targetPak = new FileInfo(resourceDir + @"\shader.pak");
            if (targetPak.Length > 7 * 1048576)
            {
                //extract pak from exe
                //file is 326kb, so we use 350kb as a batch to extract everything once
                MainWindow.ExtractFile("GOHShaderModdingSupportLauncher.pak.noCache.shader.pak", resourceDir + @"\shader.pak", 358400);

            }
            //if shader.pak <7mb, means the patch has been added
            else
            {
                //do nothing
            }
        }

        //open bump options, a fix for my own shader mod
        public static void ForceChangeSettings(string optionLoc)
        {
            string opt;
            using (StreamReader sr = File.OpenText(optionLoc))
            {
                opt = sr.ReadToEnd();

                sr.Close();
            }

            int presetLoc = opt.IndexOf("{preset");
            int presetEndLoc = opt.IndexOf("}\r\n\t\t{hdr");
            int bumpLoc = opt.IndexOf("{bumpType");
            int bumpEndLoc = opt.IndexOf("}\r\n\t\t{specular");


            opt = opt.Remove(bumpLoc, bumpEndLoc - bumpLoc + 1);
            opt = opt.Insert(bumpLoc, "{bumpType parallax}");
            opt = opt.Remove(presetLoc, presetEndLoc - presetLoc + 1);
            opt = opt.Insert(presetLoc, "{preset custom}");

#if DEBUG
            Trace.WriteLine(presetEndLoc);
            //Trace.Write(opt);
#endif

            using (StreamWriter sw = File.CreateText(optionLoc))
            {

                sw.Write(opt);
                sw.Close();
            }
        }

        private void RestoreFile(bool force = false)
        {

            if (main.universalVars.NeedRestore == true || force == true)
            {
                //retore
                MainWindow.ExtractFile("GOHShaderModdingSupportLauncher.pak.Ori.shader.lzma", main.universalVars.resourceDir + @"\shader.lzma", 358400);
                main.DecompressFileLZMA(main.universalVars.resourceDir + @"\shader.lzma", main.universalVars.resourceDir + @"\shader.pak");
                File.Delete(main.universalVars.resourceDir + @"\shader.lzma");
            }
            else
            {
                //do nothing
            }

        }

        private void ClearCache(bool force = false)
        {
            if (main.universalVars.NeedClearCache == true || force == true)
            {
                main.ClearCacheWork();
            }
            else
            {

            }
        }

        private void AutoLoadCache()
        {
            if (main.universalVars.NeedAutoLoad == true)
            {
                int len = main.universalVars.modLoaded.Count;
                if (len > 0)
                {
                    for (int i = len - 1; i >= 0; i--)
                    {
                        if (main.universalVars.modLoaded[i].hasShader == true)
                        {
                            string cachePath = main.universalVars.modLoaded[i].path + "/resource/shader/shader_cache/dx11.0";
                            string hashFile = main.universalVars.modLoaded[i].path + "/resource/shader/shader_cache/dx11.0/hash";

                            if (Directory.Exists(cachePath) == true)
                            {
                                if (File.Exists(hashFile) == true)
                                {
                                    string cacheHash = File.ReadAllText(hashFile);
                                    if (cacheHash == main.universalVars.lastCacheHash)
                                    {
                                        //do nothing because cache has been loaded
                                    }
                                    else
                                    {
                                        main.universalVars.lastCacheHash = cacheHash;
                                        main.ClearCacheWork();
                                        Directory.CreateDirectory(main.universalVars.cacheLoc + "/dx11.0/");
                                        foreach (var file in new DirectoryInfo(cachePath).GetFiles())
                                        {
                                            if (file.Name != "hash")
                                            {
                                                file.CopyTo(main.universalVars.cacheLoc + "/dx11.0/" + file.Name);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    main.universalVars.lastCacheHash = "-1";
                                    main.ClearCacheWork();
                                    Directory.CreateDirectory(main.universalVars.cacheLoc + "/dx11.0/");
                                    foreach (var file in new DirectoryInfo(cachePath).GetFiles())
                                    {
                                        if (file.Name != "hash")
                                        {
                                            file.CopyTo(main.universalVars.cacheLoc + "/dx11.0/" + file.Name);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                //no cache
                            }

                            //only load cache of last shader mod
                            break;
                        }
                        else
                        {
                            //not a shader mod
                        }
                    }
                }
                else
                {
                    //no mod loaded
                }
            }
            else
            {

            }
        }

        private bool CheckShaderModify()
        {
            if (main.universalVars.NeedCheckShaderModify == true)
            {
                int len = main.universalVars.modLoaded.Count;
                if (len > 0)
                {
                    HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes("SahderCheck"));
                    for (int i = len - 1; i >= 0; i--)
                    {
                        if (main.universalVars.modLoaded[i].hasShader == true)
                        {
                            //no need to check because mod with shader must have resource folder
                            DirectoryInfo resouce = new DirectoryInfo(main.universalVars.modLoaded[i].path + "/resource");
                            

                            //check if shader is in paks
                            foreach (var obj in resouce.GetFiles("*.pak", SearchOption.TopDirectoryOnly))
                            {
                                ZipArchive curPak = ZipFile.Open(obj.FullName, ZipArchiveMode.Read);

                                foreach (var file in curPak.Entries)
                                {
                                    if (file.FullName.Contains("shader/dx10/") == true)
                                    {
                                        byte[] fn = Encoding.UTF8.GetBytes(file.FullName.ToString());
                                        byte[] fl = Encoding.UTF8.GetBytes(file.Length.ToString());
                                        byte[] fw = Encoding.UTF8.GetBytes(file.LastWriteTime.ToString());
                                        hmac.TransformBlock(fn, 0, fn.Length, null, 0);
                                        hmac.TransformBlock(fl, 0, fl.Length, null, 0);
                                        hmac.TransformBlock(fw, 0, fw.Length, null, 0);
                                    }
                                }

                            }

                            string shaderFolder = resouce.FullName + "/shader/dx10";
                            //check if shader is in dir
                            if (Directory.Exists(shaderFolder) == true)
                            {
                                DirectoryInfo shader = new DirectoryInfo(shaderFolder);
                                foreach (FileInfo file in shader.GetFiles("*", SearchOption.AllDirectories))
                                {

                                    byte[] fn = Encoding.UTF8.GetBytes(file.FullName.ToString());
                                    byte[] fl = Encoding.UTF8.GetBytes(file.Length.ToString());
                                    byte[] fw = Encoding.UTF8.GetBytes(file.LastWriteTime.ToString());
                                    hmac.TransformBlock(fn, 0, fn.Length, null, 0);
                                    hmac.TransformBlock(fl, 0, fl.Length, null, 0);
                                    hmac.TransformBlock(fw, 0, fw.Length, null, 0);
                                }
                            }

                            


                        }
                        else
                        {
                            //not a shader mod
                        }
                    }

                    //save hash
                    hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    byte[] hash = hmac.Hash;
                    string hashS = Convert.ToBase64String(hash);
                    //default shader -> shader mod
                    if (main.universalVars.lastShaderHash == "0")
                    {
#if DEBUG
                        Trace.WriteLine("vanilla -> shader mod");
#endif
                        main.ClearCacheWork();
                        main.universalVars.lastShaderHash = hashS;
                        return false;
                    }
                    //shader mod -> shader mod
                    else
                    {
                        //no changes
                        if (hashS == main.universalVars.lastShaderHash)
                        {
                            return false;
                        }
                        else
                        {
#if DEBUG
                            Trace.WriteLine("shader modified!");
#endif
                            main.ClearCacheWork();
                            main.universalVars.lastShaderHash = hashS;
                            return true;
                        }
                    }

                }
                else
                {
                    //no mod loaded
#if DEBUG
                    Trace.WriteLine("no mod loaded!");
#endif
                    //shader mod -> vanilla
                    if (main.universalVars.lastShaderHash != "0")
                    {
#if DEBUG
                        Trace.WriteLine("shader mod -> vanilla");
#endif
                        main.ClearCacheWork();
                        main.universalVars.lastShaderHash = "0";
                    }
                    
                    return false;
                }
            }
            else
            {
                //do not check
                return false;
            }
        }

        private void OnGameExit()
        {
            if (main.universalVars.NeedCompileWarning == true)
            {
                main.CheckCompileWarning();
            }
            if (main.universalVars.NeedRedisplay == true)
            {
                main.RefreshMods(true);
                main.RefreshModPage();
                main.Show();
                main.Topmost = true;
                main.Topmost = false;
            }
            else
            {
                main.SaveSettings();
                main.Close();
            }
        }

        private async Task RunGameAsync(string processName)
        {
            if (isLaunching) return;
            isLaunching = true;
            bool replaced = false;
            bool completed = false;
            try
            {
                if (!CheckShaderModify()) AutoLoadCache();
                if (vars.lm == MainWindow.LauncherVars.LaunchMethod.FileReplace)
                {
                    ReplaceFile(main.universalVars.resourceDir!);
                    replaced = true;
                }
                ForceChangeSettings(main.universalVars.optionLoc);
                string args = vars.lm == MainWindow.LauncherVars.LaunchMethod.DX101 ? "-dx 10.1" : "";
                if (vars.showAddModInfo) args += " -showmodinfo";
                if (vars.AdminUsed && !vars.runAsAdmin)
                {
                    MessageBox.Show(i18n.L_AdminDisabledNotice, i18n.Universal_Warning,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    vars.AdminUsed = false;
                }
                var psi = new ProcessStartInfo
                {
                    FileName = main.universalVars.gameDir!.GetFiles(processName)[0].FullName,
                    WorkingDirectory = main.universalVars.gameDir.FullName,
                    UseShellExecute = true,
                    Verb = vars.runAsAdmin ? "runas" : "open",
                    Arguments = args
                };
                using var game = Process.Start(psi) ?? throw new InvalidOperationException("Game process did not start.");
                main.Hide();
                await game.WaitForExitAsync();
                completed = true;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Unable to launch or wait for game.", ex);
                MessageBox.Show(ex.Message, i18n.Universal_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (replaced) RestoreFile();
                    if (completed) ClearCache();
                }
                catch (Exception ex)
                {
                    AppDiagnostics.Log("Post-game cleanup failed.", ex);
                    MessageBox.Show(ex.Message, i18n.Universal_Error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                if (completed) OnGameExit();
                else main.Show();
                isLaunching = false;
            }
        }

        private async void Game_Click(object sender, RoutedEventArgs e)
        {
            await RunGameAsync("call_to_arms.exe");
        }

        private async void Editor_Click(object sender, RoutedEventArgs e)
        {
            await RunGameAsync("call_to_arms_ed.exe");
        }

        private async void AutoFix_Click(object sender, RoutedEventArgs e)
        {
            //force fix
            ClearCache(true);
            RestoreFile(true);

            await RunGameAsync("call_to_arms.exe");
        }

        //this will make a auto fix
        private async void Safe_Click(object sender, RoutedEventArgs e)
        {
            if (isLaunching) return;
            isLaunching = true;
            try
            {
                ClearCache(true);
                RestoreFile(true);
                using var game = Process.Start(new ProcessStartInfo
                {
                    FileName = main.universalVars.gameDir!.GetFiles("call_to_arms.exe")[0].FullName,
                    WorkingDirectory = main.universalVars.gameDir.FullName,
                    Arguments = "-no_mods",
                    UseShellExecute = true
                }) ?? throw new InvalidOperationException("Game process did not start.");
                main.Hide();
                await game.WaitForExitAsync();
                OnGameExit();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Safe launch failed.", ex);
                main.Show();
                MessageBox.Show(ex.Message, i18n.Universal_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { isLaunching = false; }
        }

        private void LaunchMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            vars.lm = (MainWindow.LauncherVars.LaunchMethod)launchMethod.SelectedIndex;
        }

        private void AddModInfo_Click(object sender, RoutedEventArgs e)
        {
            vars.showAddModInfo = addModInfo.IsChecked.Value;
        }

        private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            vars.runAsAdmin = runAsAdmin.IsChecked.Value;
        }
    }
}