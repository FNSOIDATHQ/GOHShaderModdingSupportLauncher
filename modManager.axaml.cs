using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GOHShaderModdingSupportLauncher.Properties;


namespace GOHShaderModdingSupportLauncher
{
    public partial class ModManager : UserControl
    {
        private MainWindow main;
        private MainWindow.ModManagerVars vars;
        private bool hasInit=false;
        private readonly ObservableCollection<Mod> loadedItems = new();
        private readonly ObservableCollection<Mod> unloadedItems = new();
        private readonly DataGridCollectionView unloadedView;

        private sealed class ModSortComparer(Func<Mod, Mod, int> compare) : IComparer
        {
            public int Compare(object? x, object? y) => compare((Mod)x!, (Mod)y!);
        }

        //user can not select one row in two data grids
        private struct SelectedRow
        {
            public string dataGrid;
            public List<Mod> mods;

            public SelectedRow(string dg, List<Mod> m){
                dataGrid = dg;
                mods = m;
            }
        }
        private SelectedRow selectedRow;

        public ModManager(MainWindow owner)
        {
            main = owner;

            vars = main.modManagerVars;

            InitializeComponent();
            unloadedView = new DataGridCollectionView(unloadedItems);
            unloadedMods.Columns[0].CustomSortComparer = new ModSortComparer(
                (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.name, right.name));
            unloadedMods.Columns[1].CustomSortComparer = new ModSortComparer(
                (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.type, right.type));
            unloadedMods.Columns[2].CustomSortComparer = new ModSortComparer(
                (left, right) => left.hasShader.CompareTo(right.hasShader));
            loadedMods.ItemsSource = loadedItems;
            unloadedMods.ItemsSource = unloadedView;
            loadedMods.AddHandler(InputElement.PointerPressedEvent, loadedMods_PointerPressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            Unloaded += Page_Unloaded;
            DragDrop.SetAllowDrop(loadedMods, true);
            DragDrop.AddDragOverHandler(loadedMods, loadedMods_DragOver);
            DragDrop.AddDropHandler(loadedMods, loadedMods_Drop);

            main.RefreshMods();
            UpdateUI();

            hasInit = true;
        }

        private void UpdateUI()
        {
            vars.hasMod = true;
            loadedItems.Clear();
            unloadedItems.Clear();
            foreach(var mod in main.universalVars.modDic.Values)
            {
                if (mod.hasLoad == true)
                {
                    //do nothing, because we must maintain order of loaded mods
                }
                else
                {
                    unloadedItems.Add(mod);
                }
            }

            foreach(var mod in main.universalVars.modLoaded)
            {
                loadedItems.Add(mod);
            }

            if (loadedItems.Count != 0)
            {
                loadedMods.SelectedIndex = 0;
                selectedRow = new SelectedRow("loadedMods",loadedMods.SelectedItems.Cast<Mod>().ToList());

            }
            else if (unloadedItems.Count!=0)
            {
                unloadedMods.SelectedIndex = 0;
                selectedRow = new SelectedRow("unloadedMods", unloadedMods.SelectedItems.Cast<Mod>().ToList());
            }
            else
            {
                //no mod!
                selectedRow = new SelectedRow("", new List<Mod>());
                vars.hasMod = false;
            }
            
        }

        private void UpdateOptionFile()
        {
            string modified = "";
            using (StreamReader opt = File.OpenText(main.universalVars.optionLoc))
            {
                //push to mod list start point
                while (opt.ReadLine() is var line)
                {
                    //no mod section
                    if (opt.EndOfStream == true)
                    {
                        modified += "\t{mods\r\n";
                        break;
                    }
                    modified += line+"\r\n";
                    if (line.Contains("{mods") == true)
                    {
                        break;
                    }

                }

                opt.Close();
            }

            foreach(Mod mod in loadedItems)
            {
                modified += "\t\t\""+mod.folderName+ ":0\"\r\n";
            }

            modified += "\t}\r\n}\r\n";

            File.WriteAllText(main.universalVars.optionLoc, modified);
        }

        private void DataGridAddRange(ObservableCollection<Mod> items,List<Mod> mods)
        {
            foreach(var mod in mods)
            {
                items.Add(mod);
            }
        }

        private void DataGridRemoveRange(ObservableCollection<Mod> items, List<Mod> mods)
        {
            foreach (var mod in mods)
            {
                items.Remove(mod);
            }
        }

        private void loadMod_Click(object sender, RoutedEventArgs e)
        {
            var selected = unloadedMods.SelectedItems.Cast<Mod>().ToList();
            if (selected.Count == 0)
            {
                if (loadedMods.SelectedItems.Count == 0)
                    MessageBox.Show(i18n.M_NoModSelected, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var mod in selected)
            {
                mod.hasLoad = true;
                main.universalVars.modLoaded.Add(mod);
            }
            DataGridAddRange(loadedItems, selected);
            DataGridRemoveRange(unloadedItems, selected);
            UpdateOptionFile();
            UpdateUI();
        }

        private void unloadMod_Click(object sender, RoutedEventArgs e)
        {
            var selected = loadedMods.SelectedItems.Cast<Mod>().ToList();
            if (selected.Count == 0)
            {
                if (unloadedMods.SelectedItems.Count == 0)
                    MessageBox.Show(i18n.M_NoModSelected, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var mod in selected)
            {
                mod.hasLoad = false;
                main.universalVars.modLoaded.Remove(mod);
            }
            DataGridAddRange(unloadedItems, selected);
            DataGridRemoveRange(loadedItems, selected);
            UpdateOptionFile();
            UpdateUI();
        }

        private void openModFolder_Click(object sender, RoutedEventArgs e)
        {
            if (selectedRow.mods.Count == 0)
            {
                MessageBox.Show(i18n.M_NoModSelected, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (vars.hasMod == true)
            {
                Process.Start("explorer.exe", selectedRow.mods[0].path);
            }
            
        }

        private void loadShaderCache_Click(object sender, RoutedEventArgs e)
        {
            if (selectedRow.mods.Count==0)
            {
                MessageBox.Show(i18n.M_NoModSelected, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string cachePath = selectedRow.mods[0].path + "/resource/shader/shader_cache/dx11.0";
            string hashFile = selectedRow.mods[0].path + "/resource/shader/shader_cache/dx11.0/hash";

            if (Directory.Exists(cachePath) == true)
            {
                if (File.Exists(hashFile) == true)
                {
                    string cacheHash = File.ReadAllText(hashFile);
                    if (cacheHash == main.universalVars.lastCacheHash)
                    {
                        //do nothing because cache has been loaded
                        MessageBox.Show(i18n.M_CacheHasBeenLoaded, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
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
                        MessageBox.Show(i18n.M_LoadCacheSuccessful, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(i18n.M_NoCacheToLoad, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void collectShaderCache_Click(object sender, RoutedEventArgs e)
        {
            if (selectedRow.mods.Count == 0)
            {
                MessageBox.Show(i18n.M_NoModSelected, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (selectedRow.mods[0].type == i18n.Main_ModLocal)
            {
                string modCachePath = selectedRow.mods[0].path + "/resource/shader/shader_cache/dx11.0";
                string gameCachePath = main.universalVars.cacheLoc + "/dx11.0";

                HMACSHA512 hmac = new HMACSHA512(Encoding.UTF8.GetBytes("CacheCheck"));

                
                if (Directory.Exists(gameCachePath)==true)
                {
                    List<FileInfo> files = new DirectoryInfo(gameCachePath).GetFiles("*", SearchOption.TopDirectoryOnly).OrderBy(p => p.FullName).ToList();
                    if (files.Count > 0)
                    {
                        if (Directory.Exists(modCachePath) == true)
                        {
                            Directory.Delete(modCachePath, true);
                        }
                        Directory.CreateDirectory(modCachePath);

                        for (int i = 0; i < files.Count && (files[i] is var file); i++)
                        {
                            byte[] curF = File.ReadAllBytes(file.FullName);
                            File.WriteAllBytes(modCachePath + "/" + file.Name, curF);
                            if (i == files.Count - 1)
                            {
                                hmac.TransformFinalBlock(curF, 0, curF.Length);
                            }
                            else
                            {
                                hmac.TransformBlock(curF, 0, curF.Length, curF, 0);
                            }
                        }
                        

                        byte[] hash = hmac.Hash;

                        File.WriteAllText(modCachePath + "/hash", Convert.ToBase64String(hash));
                        MessageBox.Show(i18n.M_CollectCacheSuccessful, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(i18n.M_NoCacheToCollect, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    MessageBox.Show(i18n.M_NoCacheToCollect, i18n.Universal_Notice, MessageBoxButton.OK, MessageBoxImage.Information);
                }

            }
            else
            {
                MessageBox.Show(i18n.M_TryCollectWorkshopMod, i18n.Universal_Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            
        }

        private bool isSyncingSelection = false;

        private void unloadedMods_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isSyncingSelection) return;
            try
            {
                isSyncingSelection = true;
                selectedRow = new SelectedRow("unloadedMods", unloadedMods.SelectedItems.Cast<Mod>().ToList());
                loadedMods.SelectedIndex = -1;
            }
            finally
            {
                isSyncingSelection = false;
            }
        }

        private void loadedMods_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isSyncingSelection) return;
            try
            {
                isSyncingSelection = true;
                selectedRow = new SelectedRow("loadedMods", loadedMods.SelectedItems.Cast<Mod>().ToList());
                unloadedMods.SelectedIndex = -1;
            }
            finally
            {
                isSyncingSelection = false;
            }
        }

        private void refresh_Click(object sender, RoutedEventArgs e)
        {
            main.RefreshMods();
            UpdateUI();
        }

        private static readonly DataFormat<string> LoadedModDragFormat =
            DataFormat.CreateStringApplicationFormat("gohshaderlauncher-loaded-mod");
        private Point startPoint;
        private PointerPressedEventArgs? dragStartEvent;
        private Mod? draggedItem;
        private string? dragToken;
        private bool dragInProgress;
        private TopLevel? dragRoot;

        private static Mod? RowItem(object? source)
        {
            if (source is DataGridRow row) return row.DataContext as Mod;
            return (source as Visual)?.GetVisualAncestors().OfType<DataGridRow>()
                .FirstOrDefault()?.DataContext as Mod;
        }

        private void loadedMods_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (dragInProgress || !e.GetCurrentPoint(loadedMods).Properties.IsLeftButtonPressed) return;
            draggedItem = RowItem(e.Source);
            dragStartEvent = draggedItem is null ? null : e;
            startPoint = e.GetPosition(loadedMods);
        }

        private async void loadedMods_PointerMoved(object? sender, PointerEventArgs e)
        {
            var trigger = dragStartEvent;
            if (dragInProgress || draggedItem is null || trigger is null) return;
            if (!e.GetCurrentPoint(loadedMods).Properties.IsLeftButtonPressed)
            {
                dragStartEvent = null;
                draggedItem = null;
                return;
            }
            var position = e.GetPosition(loadedMods);
            if (Math.Abs(position.X - startPoint.X) <= 5 &&
                Math.Abs(position.Y - startPoint.Y) <= 5) return;

            dragInProgress = true;
            dragToken = Guid.NewGuid().ToString("N");
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(LoadedModDragFormat, dragToken));
            e.Handled = true;
            try
            {
                await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Loaded mod drag failed.", ex);
            }
            finally
            {
                dragInProgress = false;
                dragStartEvent = null;
                draggedItem = null;
                dragToken = null;
            }
        }

        private void loadedMods_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (dragInProgress) return;
            dragStartEvent = null;
            draggedItem = null;
        }

        private bool IsCurrentDrag(DragEventArgs e) =>
            dragInProgress && draggedItem is not null && dragToken is not null &&
            e.DataTransfer.TryGetValue(LoadedModDragFormat) == dragToken;

        private Mod? LoadedModAt(Point position)
        {
            if (position.X < 0 || position.Y < 0 ||
                position.X >= loadedMods.Bounds.Width || position.Y >= loadedMods.Bounds.Height)
                return null;

            Mod? last = null;
            var lastBottom = double.NegativeInfinity;
            foreach (var row in loadedMods.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (!row.IsVisible || row.DataContext is not Mod mod) continue;
                var top = row.TranslatePoint(new Point(0, 0), loadedMods)?.Y;
                if (top is null) continue;
                var bottom = top.Value + row.Bounds.Height;
                if (position.Y >= top.Value && position.Y < bottom) return mod;
                if (bottom > lastBottom)
                {
                    lastBottom = bottom;
                    last = mod;
                }
            }
            // A drop in the unused area below the rows moves the mod to the end.
            return position.Y >= lastBottom ? last : null;
        }

        private void loadedMods_DragOver(object? sender, DragEventArgs e)
        {
            var target = IsCurrentDrag(e) ? LoadedModAt(e.GetPosition(loadedMods)) : null;
            e.DragEffects = target is not null && target != draggedItem
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void loadedMods_Drop(object? sender, DragEventArgs e)
        {
            var moved = draggedItem;
            var target = IsCurrentDrag(e) ? LoadedModAt(e.GetPosition(loadedMods)) : null;
            if (moved is null || target is null || !MoveLoadedMod(moved, target))
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            // Refresh after the native drop finishes so the visible rows follow the saved order.
            Dispatcher.UIThread.Post(() =>
            {
                if (!loadedItems.Contains(moved)) return;
                loadedMods.ItemsSource = null;
                loadedMods.ItemsSource = loadedItems;
                loadedMods.SelectedItem = moved;
            }, DispatcherPriority.Background);
        }

        internal bool MoveLoadedMod(Mod moved, Mod target)
        {
            int oldIndex = loadedItems.IndexOf(moved);
            int newIndex = loadedItems.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return false;
            loadedItems.Move(oldIndex, newIndex);
            main.universalVars.modLoaded.Remove(moved);
            main.universalVars.modLoaded.Insert(newIndex, moved);
            UpdateOptionFile();
            return true;
        }

        private void AttachDragRoot()
        {
            var root = TopLevel.GetTopLevel(this);
            if (root == dragRoot) return;
            DetachDragRoot();
            dragRoot = root;
            dragRoot?.AddHandler(InputElement.PointerMovedEvent, loadedMods_PointerMoved,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            dragRoot?.AddHandler(InputElement.PointerReleasedEvent, loadedMods_PointerReleased,
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        private void DetachDragRoot()
        {
            if (dragRoot is null) return;
            dragRoot.RemoveHandler(InputElement.PointerMovedEvent, loadedMods_PointerMoved);
            dragRoot.RemoveHandler(InputElement.PointerReleasedEvent, loadedMods_PointerReleased);
            dragRoot = null;
            dragStartEvent = null;
            draggedItem = null;
        }

        private void Page_Unloaded(object? sender, RoutedEventArgs e) => DetachDragRoot();

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            AttachDragRoot();
            if (hasInit == true)
            {
#if DEBUG
                Trace.WriteLine("mod manager page loaded!");
#endif

                //this should work when game exit and mod data instances have been refreshed
                UpdateUI();

            }
        }

        public void RefreshView()
        {
            if (hasInit) UpdateUI();
        }
    }
}