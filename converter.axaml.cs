// Copyright 2026 Federation Studio
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GOHShaderModdingSupportLauncher.Properties;


namespace GOHShaderModdingSupportLauncher
{
    public partial class Converter : UserControl
    {
        private string targetPath = "";
        public Converter(MainWindow owner)
        {
            InitializeComponent();
        }

        private void convertPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            targetPath = convertPath.Text ?? "";
        }

        private async void viewPath_Click(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = GOHShaderModdingSupportLauncher.Properties.i18n.C_ViewPath
            });
            if (folders.Count == 0) return;
            targetPath = folders[0].Path.LocalPath;
            convertPath.Text = targetPath;
        }

        internal static bool EnableEnvInMTLFile(ref string mtl)
        {
            mtl = mtl.Replace("{material simple", "{material bump", StringComparison.Ordinal);

            if(mtl.Contains("{material bump") == true)
            {
                //a special vanilla goh material,or have enabled environment map
                if(mtl.Contains("{height")|| mtl.Contains("{lightmap"))
                {
                    return false;
                }

                int lastBracket = mtl.LastIndexOf('}');
                if (lastBracket < 0) return false;
                mtl=mtl.Insert(lastBracket, "\t{height \"$/envmap/env\"}\r\n\t{lightmap \"$/dummyTex/white\"}\r\n\t{parallax_scale 1000}\r\n");
#if DEBUG
                Trace.WriteLine("modified mtl=" + mtl);
#endif


                return true;
            }
            else
            {
                return false;
            }
        }

        private void enableEnvMaps_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(targetPath) == true)
            {
                console.Text = "";
                //no need to check because mod with shader must have resource folder
                DirectoryInfo target = new DirectoryInfo(targetPath);


                //check if mtl is in paks
                foreach (var obj in target.GetFiles("*.pak", SearchOption.AllDirectories))
                {
                    ZipArchive curPak = ZipFile.Open(obj.FullName, ZipArchiveMode.Update);

                    //path,content
                    Dictionary<string, string> mtls = new Dictionary<string, string>();
                    //read and modify
                    foreach (var file in curPak.Entries)
                    {
                        if (file.Name.EndsWith(".mtl") == true)
                        {
                            
                            using (var reader= new StreamReader(file.Open()))
                            {
                                string mtl= reader.ReadToEnd();

                                bool needModify=EnableEnvInMTLFile(ref mtl);

                                if (needModify == true)
                                {
                                    console.Text += "[" + DateTime.Now + $"] {i18n.C_PakMtl0} " + obj.Name + $" {i18n.C_PakMtl1} " + file.FullName + $" {i18n.C_MtlUpdated}\n";
                                    mtls.Add(file.FullName, mtl);
                                }
                                reader.Close();
                            }
                        }
                    }
                    
                    //write
                    foreach(var mtl in mtls)
                    {
                        var ori = curPak.GetEntry(mtl.Key);
                        ori?.Delete();

                        using (var writer = new StreamWriter(curPak.CreateEntry(mtl.Key).Open()))
                        {
                            writer.Write(mtl.Value);
                            writer.Close();
                        }
                    }

                    curPak.Dispose();

                }

                //normal mtl
                foreach (FileInfo file in target.GetFiles("*.mtl", SearchOption.AllDirectories))
                {
                    string mtl = File.ReadAllText(file.FullName);
                    bool needModify = EnableEnvInMTLFile(ref mtl);

                    if (needModify == true)
                    {
                        console.Text += "["+DateTime.Now+$"] {i18n.C_NormalMtl} " + file.FullName + $" {i18n.C_MtlUpdated}\n";
                        File.WriteAllText(file.FullName, mtl);
                    }
                    
                }


                MessageBox.Show(i18n.C_MtlComplete, i18n.Universal_Notice);

            }
            else
            {
                //path not found
                MessageBox.Show(i18n.C_PathNotFound, i18n.Universal_Notice);
            }
        }
    }
}