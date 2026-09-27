using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Win32;
using GOHShaderModdingSupportLauncher.Properties;
using System.Linq;



namespace GOHShaderModdingSupportLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.axaml
    /// </summary>
    public partial class MainWindow : Window
    {
        internal sealed record ProfileAccount(string OptionsPath)
        {
            public string ProfileRoot => Directory.GetParent(OptionsPath)!.Parent!.Parent!.FullName;
            public override string ToString() => $"{Directory.GetParent(OptionsPath)!.Name} ({ProfileRoot})";
        }

        internal List<ProfileAccount> ProfileAccounts { get; } = new();
        private string selectedProfileOptions = "";

        private bool HasGetGameRoot, HasGetProfileLoc;
        private bool openSettingsOnStartup;
        private bool settingsOnly;
        internal bool PathsReady => HasGetGameRoot && HasGetProfileLoc;
        private readonly UserControl?[] pages = new UserControl?[6];
        private bool initialized;
        private readonly string settingsPath;

        //universal vars
        public class UniversalVars
        {
            public DirectoryInfo? gameDir, resourceDir;
            public string profileLoc, cacheLoc, optionLoc;
            public string configLoc;
            public bool NeedRestore, NeedClearCache, NeedRedisplay, NeedCompileWarning, NeedLockModList, NeedAutoLoad, NeedCheckShaderModify, AlwaysConfirm;

            public DirectoryInfo? workshopDir, localDir;
            //option.set name -> mod
            public Dictionary<string, Mod> modDic;
            public List<Mod> modLoaded;
            public bool hasMod;
            //default value of lastShaderHash is 0, means the game is using default shader
            //in this case shader modify check should return true to auto load caches
            public string lastCacheHash, lastShaderHash;
            public UniversalVars()
            {
                profileLoc = "";
                cacheLoc = "";
                optionLoc = "";
                configLoc = "";
                modLoaded = new List<Mod>();
                modDic = new Dictionary<string, Mod>();
                lastCacheHash = "";
                lastShaderHash = "";
            }
        }
        public UniversalVars universalVars;
        //launcher vars
        public class LauncherVars
        {
            public enum LaunchMethod
            {
                FileReplace,
                DX101
            }
            public LaunchMethod lm;
            public bool showAddModInfo;
            public bool runAsAdmin;

            public bool AdminUsed;
        }
        public LauncherVars launcherVars;

        //settings vars
        public class SettingsVars
        {

        }
        public SettingsVars settingsVars;

        //settings vars
        public class ToolsVars
        {

        }
        public ToolsVars toolsVars;

        //mod manager vars
        public class ModManagerVars
        {
            public bool hasMod;
        }
        public ModManagerVars modManagerVars;

        public MainWindow() : this(FileManager.SettingsPath) { }

        internal MainWindow(string settingsPath)
        {
            this.settingsPath = settingsPath;
            LocalizedStrings.Instance.SetCulture(LoadSavedLanguage(settingsPath));
            AppDiagnostics.Log("Loading main window XAML.");
            InitializeComponent();
            universalVars = new UniversalVars();
            launcherVars = new LauncherVars();
            settingsVars = new SettingsVars();
            toolsVars = new ToolsVars();
            modManagerVars = new ModManagerVars();
            languageSelector.SelectedIndex = LocalizedStrings.Instance.Culture.Name == "zh-CN" ? 1 : 0;
            languageSelector.IsEnabled = false;
            PropertyChanged += (_, change) =>
            {
                if (change.Property == WindowStateProperty) UpdateWindowChrome();
            };
            UpdateWindowChrome();
            Opened += OnOpened;
        }

        private static string SettingsSource(string path) =>
            File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, "settings.conf");

        private static CultureInfo LoadSavedLanguage(string path)
        {
            var fallback = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                ? CultureInfo.GetCultureInfo("zh-CN")
                : CultureInfo.GetCultureInfo("en-US");
            var source = SettingsSource(path);
            if (!File.Exists(source)) return fallback;

            try
            {
                var lines = FileManager.ReadSettings(source);
                return lines.Length is 14 or 16 or 17 ? CultureInfo.GetCultureInfo(lines[FileManager.LanguageSettingIndex]) : fallback;
            }
            catch (Exception ex) when (FileManager.IsFileError(ex) || ex is FormatException)
            {
                AppDiagnostics.Log($"Ignoring invalid/unreadable language in settings: {source}", ex);
                return fallback;
            }
        }

        private void UpdateWindowChrome()
        {
            maximizeButton.Content = WindowState is WindowState.Maximized or WindowState.FullScreen
                ? "\uE923" : "\uE922";
            resizeGrips.IsVisible = WindowState == WindowState.Normal;
        }

        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.Pointer.IsPrimary || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var point = e.GetPosition(captionButtons);
            if (point.X >= 0 && point.X < captionButtons.Bounds.Width &&
                point.Y >= 0 && point.Y < captionButtons.Bounds.Height) return;

            if (e.ClickCount == 2) ToggleMaximize();
            else BeginMoveDrag(e);
            e.Handled = true;
        }

        private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (WindowState != WindowState.Normal || !e.Pointer.IsPrimary ||
                !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                sender is not Control { Tag: string edgeName } ||
                !Enum.TryParse<WindowEdge>(edgeName, out var edge)) return;

            BeginResizeDrag(edge, e);
            e.Handled = true;
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState is WindowState.Maximized or WindowState.FullScreen
                ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void OnOpened(object? sender, EventArgs e)
        {
            if (initialized) return;
            initialized = true;
            try
            {
                await InitBasicDataAsync();
                languageSelector.IsEnabled = true;
                if (openSettingsOnStartup) footerNavigation.SelectedIndex = 0;
                else primaryNavigation.SelectedIndex = 0;
                AppDiagnostics.Log("Main window rendered.");
            }
            catch (Exception ex)
            {
                AppDiagnostics.ReportFatal("Main window initialization", ex);
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown(1);
            }
        }

        private async Task InitBasicDataAsync()
        {
            HasGetGameRoot = false;
            HasGetProfileLoc = false;
            settingsOnly = true;
            primaryNavigation.IsEnabled = false;
            ((ListBoxItem)footerNavigation.Items[1]!).IsEnabled = false;

            AppDiagnostics.Log("Loading settings.");
            LoadConfigFromFile();
            bool usingCachedGamePath = HasGetGameRoot;

            AppDiagnostics.Log("Finding game profiles.");
            GetProfileLoc();

            if (HasGetGameRoot == false)
            {
                AppDiagnostics.Log("Finding game installation.");
                await GetGameRootAsync();
            }

            if (usingCachedGamePath && universalVars.AlwaysConfirm &&
                await MessageBox.ConfirmAsync(this,
                    $"{i18n.S_ConfirmSavedGamePath}\n\n{universalVars.gameDir}",
                    i18n.Main_MaunalCheck) != MessageBoxResult.Yes)
            {
                openSettingsOnStartup = true;
            }

            openSettingsOnStartup |= !PathsReady;
            if (PathsReady) RefreshMods();
            UpdatePathAvailability();

            SaveSettings();
            AppDiagnostics.Log("Startup data initialized.");
        }

        internal void LoadConfigFromFile()
        {
            SetDefaultSettings();
            universalVars.configLoc = settingsPath;
            string source = SettingsSource(settingsPath);
            if (!File.Exists(source)) return;

            string[] lines;
            try
            {
                lines = FileManager.ReadSettings(source);
            }
            catch (Exception ex) when (FileManager.IsFileError(ex) || ex is FormatException)
            {
                AppDiagnostics.Log($"Ignoring invalid/unreadable settings: {source}", ex);
                MessageBox.Show(i18n.Main_ErrorReadConfig, i18n.Universal_Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            launcherVars.lm = Enum.Parse<LauncherVars.LaunchMethod>(lines[0]);
            launcherVars.showAddModInfo = bool.Parse(lines[1]);
            launcherVars.runAsAdmin = launcherVars.AdminUsed = bool.Parse(lines[2]);
            universalVars.NeedRestore = bool.Parse(lines[3]);
            universalVars.NeedClearCache = bool.Parse(lines[4]);
            universalVars.NeedRedisplay = bool.Parse(lines[5]);
            universalVars.NeedCompileWarning = bool.Parse(lines[6]);
            universalVars.NeedLockModList = bool.Parse(lines[7]);
            universalVars.AlwaysConfirm = bool.Parse(lines[8]);
            universalVars.NeedAutoLoad = bool.Parse(lines[9]);
            universalVars.NeedCheckShaderModify = bool.Parse(lines[10]);
            universalVars.lastCacheHash = lines[11];
            universalVars.lastShaderHash = lines[12];

            if (lines.Length == 17) selectedProfileOptions = lines[14];
            if (lines.Length is not (15 or 16 or 17)) return;
            int pathIndex = lines.Length - 2;

            try { HasGetGameRoot = TrySetGameDirectory(lines[pathIndex]); }
            catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Cached game path is unavailable.", ex); }

            try { HasGetProfileLoc = TrySetProfileDirectory(lines[pathIndex + 1]); }
            catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Cached profile path is unavailable.", ex); }
        }

        private void SetDefaultSettings()
        {
            launcherVars.lm = LauncherVars.LaunchMethod.FileReplace;
            launcherVars.showAddModInfo = true;
            launcherVars.runAsAdmin = true;
            launcherVars.AdminUsed = true;
            universalVars.NeedRedisplay = true;
            universalVars.NeedCompileWarning = true;
            universalVars.NeedCheckShaderModify = true;
            universalVars.lastCacheHash = "-1";
            universalVars.lastShaderHash = "0";
        }

        private bool TrySetGameDirectory(string path)
        {
            if (FileManager.IsGameDirectory(path) == false) return false;

            var game = new DirectoryInfo(path);
            var root = game.Parent!.Parent!;
            var local = new DirectoryInfo(Path.Combine(root.FullName, "mods"));
            var workshop = new DirectoryInfo(
                                    Path.GetFullPath(
                                                Path.Combine(root.FullName, "..", "..", "workshop", "content", "400750")
                                                )
                                            );

            Environment.CurrentDirectory = game.FullName;
            universalVars.gameDir = game;
            universalVars.resourceDir = new DirectoryInfo(Path.Combine(root.FullName, "resource"));
            universalVars.localDir = local;
            universalVars.workshopDir = workshop;

            return true;
        }

        private bool TrySetProfileDirectory(string path)
        {
            var accounts = FileManager.FindOptionsFiles(path);
            foreach (var optionsPath in accounts)
                if (!ProfileAccounts.Any(account => string.Equals(account.OptionsPath, optionsPath, StringComparison.OrdinalIgnoreCase)))
                    ProfileAccounts.Add(new ProfileAccount(optionsPath));
            var options = accounts.FirstOrDefault(option => string.Equals(option, selectedProfileOptions, StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault();
            if (options == null) return false;
            ApplyProfileAccount(new ProfileAccount(options));
            return true;
        }

        private void ApplyProfileAccount(ProfileAccount account)
        {
            universalVars.profileLoc = account.ProfileRoot;
            universalVars.cacheLoc = Path.Combine(account.ProfileRoot, "shader_cache");
            universalVars.optionLoc = account.OptionsPath;
            selectedProfileOptions = account.OptionsPath;
        }

        internal bool TrySelectProfileAccount(ProfileAccount account)
        {
            if (!ProfileAccounts.Contains(account) || !File.Exists(account.OptionsPath)) return false;
            ApplyProfileAccount(account);
            HasGetProfileLoc = true;
            OnManualPathChanged();
            SaveSettings();
            return true;
        }

        internal bool TrySetManualGameDirectory(string path)
        {
            if (!TrySetGameDirectory(path)) return false;
            HasGetGameRoot = true;
            OnManualPathChanged();
            SaveSettings();
            return true;
        }

        internal bool TrySetManualProfileDirectory(string path)
        {
            if (!TrySetProfileDirectory(path)) return false;
            HasGetProfileLoc = true;
            OnManualPathChanged();
            SaveSettings();
            return true;
        }

        private void OnManualPathChanged()
        {
            if (PathsReady) RefreshMods();
            UpdatePathAvailability();
            RefreshModPage();
        }

        internal void UpdatePathAvailability()
        {
            settingsOnly = !PathsReady;
            primaryNavigation.IsEnabled = !settingsOnly;
            ((ListBoxItem)footerNavigation.Items[1]!).IsEnabled = !settingsOnly;
            if (settingsOnly)
            {
                primaryNavigation.SelectedIndex = -1;
                footerNavigation.SelectedIndex = 0;
            }
            if (pages[4] is Settings settings) settings.UpdatePathStatus();
        }

        private void GetProfileLoc()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "digitalmindsoft", "gates of hell"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "my games", "gates of hell")
            };

            string preferred = selectedProfileOptions;
            foreach (string candidate in candidates)
            {
                try
                {
                    foreach (string options in FileManager.FindOptionsFiles(candidate))
                        if (!ProfileAccounts.Any(account => string.Equals(account.OptionsPath, options, StringComparison.OrdinalIgnoreCase)))
                            ProfileAccounts.Add(new ProfileAccount(options));
                }
                catch (Exception ex) when (FileManager.IsFileError(ex))
                {
                    AppDiagnostics.Log($"Unable to inspect profile: {candidate}", ex);
                }
            }
            var selected = ProfileAccounts.FirstOrDefault(account => string.Equals(account.OptionsPath, preferred, StringComparison.OrdinalIgnoreCase))
                ?? ProfileAccounts.FirstOrDefault();
            if (selected != null)
            {
                ApplyProfileAccount(selected);
                HasGetProfileLoc = true;
            }
            else AppDiagnostics.Log("Game profile was not found; manual selection is required.");
        }

        private async Task<bool> GetGameRootAsync()
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Shortcuts can start in an unrelated working directory
            foreach (var start in new[] {
                AppContext.BaseDirectory
                , Directory.GetCurrentDirectory()
                })
            {
                for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    if (dir.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    {
                        libraries.Add(dir.FullName);
                    }
                }
            }

            var steamPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] {
                RegistryHive.CurrentUser
                , RegistryHive.LocalMachine
            })
            {
                foreach (var view in new[] {
                    RegistryView.Registry64
                    , RegistryView.Registry32
                })
                {
                    try
                    {
                        using var registry = RegistryKey.OpenBaseKey(hive, view);
                        using var steam = registry.OpenSubKey(@"SOFTWARE\Valve\Steam");
                        var location = steam?.GetValue(hive == RegistryHive.CurrentUser ? "SteamPath" : "InstallPath") as string;

                        if (string.IsNullOrWhiteSpace(location) == false) steamPaths.Add(location);
                    }
                    catch (Exception ex) when (FileManager.IsFileError(ex))
                    {
                        AppDiagnostics.Log("Unable to read a Steam registry location.", ex);
                    }
                }
            }

            foreach (string steamPath in steamPaths)
            {
                string steamApps = Path.Combine(steamPath, "steamapps");
                libraries.Add(steamApps);
                try
                {
                    string vdf = Path.Combine(steamApps, "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (string library in FileManager.ReadLibraryPaths(vdf))
                        {
                            libraries.Add(Path.Combine(library, "steamapps"));
                        }
                    }
                }
                catch (Exception ex) when (FileManager.IsFileError(ex))
                {
                    AppDiagnostics.Log("Unable to read Steam libraries; trying the known locations.", ex);
                }
            }

            foreach (string library in libraries)
            {
                try
                {
                    if (TrySetGameDirectory(Path.Combine(library, "common", "Call to Arms - Gates of Hell", "binaries", "x64")))
                    {
                        HasGetGameRoot = true;
                        break;
                    }
                }
                catch (Exception ex) when (FileManager.IsFileError(ex))
                {
                    AppDiagnostics.Log($"Unable to inspect Steam library: {library}", ex);
                }
            }

            if (HasGetGameRoot == false)
            {
                AppDiagnostics.Log("Game installation was not found; manual selection is required.");
                return false;
            }

            string message = $"{i18n.Main_FoundGame0}\n{universalVars.gameDir}\n\n{i18n.Main_FoundGame1}";
            if (await MessageBox.ConfirmAsync(this, message, i18n.Main_MaunalCheck) != MessageBoxResult.Yes)
            {
                // Do not cache a path that the user rejected.
                HasGetGameRoot = false;
                universalVars.gameDir = null;
                universalVars.resourceDir = null;
                universalVars.localDir = null;
                universalVars.workshopDir = null;
                SaveSettings();
                AppDiagnostics.Log("User declined the detected game path.");
                return false;
            }
            
            if (HasGetProfileLoc) ClearCacheWork();
            return true;
        }

        public void ClearCacheWork()
        {

            if (Directory.Exists(universalVars.cacheLoc) == true)
            {
                Directory.Delete(universalVars.cacheLoc, true);
            }

        }

        public void CheckCompileWarning()
        {
            using (StreamReader log = File.OpenText(universalVars.profileLoc + @"\log\game.log"))
            {
                while (log.EndOfStream != true)
                {
                    string line;
                    line = log.ReadLine();

                    if (line.IndexOf("compile error:") != -1)
                    {
                        string errorMsg = line + "\n\n";
                        bool hasError = false;
                        while (!string.IsNullOrEmpty(line = log.ReadLine()))
                        {
                            errorMsg += line + "\n\n";
                            if (line.Contains("error") == false)
                            {
                                hasError = true;
                            }
                        }

                        string errorMessage = $"{i18n.Main_ShaderCompileErrorMessage1}\n\n\n\n";
                        errorMessage += errorMsg;

                        if (hasError == true)
                        {
                            MessageBox.Show($"{i18n.Main_ShaderCompileErrorMessage0}\n\n" + errorMessage, i18n.Main_ShaderCompileErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        else
                        {
                            MessageBox.Show($"{i18n.Main_ShaderCompileErrorMessage2}\n\n" + errorMessage, i18n.Main_ShaderCompileErrorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                        }

                        break;
                    }
                }

                log.Close();
            }
        }

        public void RefreshMods(bool afterGaming = false)
        {
            universalVars.modDic.Clear();


            ReadModsFromWorkshop();
            ReadModsFromLocal();

            if (universalVars.NeedLockModList == true && afterGaming == true)
            {
                verifyLoadedMods();
            }
            else
            {
                universalVars.modLoaded.Clear();
                ReadLoadedMods();
            }
        }

        private string getModShowName(FileInfo[] modInfo)
        {
            string name = "";
            try
            {
                using (StreamReader info = modInfo[0].OpenText())
                {
                    string nameLine;
                    //push to name line
                    while (info.EndOfStream == false)
                    {
                        while (((nameLine = info.ReadLine()).Contains("name", StringComparison.OrdinalIgnoreCase) == false || nameLine.Contains("{", StringComparison.Ordinal) == false) && info.EndOfStream == false) ;

                        int commentIndex = nameLine.IndexOf(";");
                        nameLine = nameLine.Substring(0, commentIndex == -1 ? nameLine.Length : commentIndex);
#if DEBUG
                        Trace.WriteLine("get nameline= " + nameLine);
#endif
                        if (nameLine.Length > 0)
                        {
                            int nameS = nameLine.IndexOf('"') + 1;
                            int nameL = nameLine.LastIndexOf('"') - nameS;
                            if (nameS == -1 || nameL <= 0)
                            {
                                continue;
                            }
                            name = nameLine.Substring(nameS, nameL);
#if DEBUG
                            Trace.WriteLine("mod show name= " + name);
#endif
                            break;
                        }

                    }


                    info.Close();
                }

                if (name == "")
                {
                    name = i18n.Main_ModErrorName;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show($"{i18n.Main_ErrorReadModInfo}\n" + e, i18n.Universal_Error, MessageBoxButton.OK, MessageBoxImage.Warning);
                name = i18n.Main_ModErrorName;
            }

            return name;
        }

        private bool checkShader(DirectoryInfo root)
        {
            DirectoryInfo[] searchResult = root.GetDirectories("resource", SearchOption.TopDirectoryOnly);
            if (searchResult.Length != 0)
            {
                DirectoryInfo resouce = searchResult[0];

                //check if shader is in paks
                foreach (var obj in resouce.GetFiles("*.pak", SearchOption.TopDirectoryOnly))
                {
#if DEBUG
                    Trace.WriteLine("get a pak= " + obj.Name);
#endif
                    using ZipArchive curPak = ZipFile.Open(obj.FullName, ZipArchiveMode.Read);

                    foreach (var file in curPak.Entries)
                    {
                        if (file.FullName.Contains("shader/dx10/") == true)
                        {
                            if (file.Length > 0)
                            {
#if DEBUG
                                Trace.WriteLine("get a shader in pak= " + file.FullName);
#endif
                                return true;
                            }
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
#if DEBUG
                        Trace.WriteLine("get a shader file= " + file.Name);
#endif
                        if (file.Length > 0)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void ReadModsFromWorkshop()
        {
            if (universalVars.workshopDir?.Exists != true) return;

            foreach (var dir in universalVars.workshopDir.GetDirectories())
            {
                FileInfo[] modInfo = dir.GetFiles("mod.info", SearchOption.TopDirectoryOnly);
                string folderName = "mod_" + dir.Name;

                if (modInfo.Length == 0)
                {
                    //not a mod
                    continue;
                }
                else if (folderName.Contains(' ') == true)
                {
                    //is a mod, but not valid
                    continue;
                }
#if DEBUG
                Trace.WriteLine("mod dir name= " + dir.Name);
#endif
                string name = getModShowName(modInfo);

                bool hasShader = checkShader(dir);


                Mod single = new Mod(name, i18n.Main_ModWorkshop, dir.FullName, folderName, hasShader);

                if (universalVars.modDic.TryAdd(folderName, single) == false)
                {
                    AppDiagnostics.Log($"Duplicate mod identifier ignored: {folderName} ({dir.FullName})");
                }
            }
        }

        private void ReadModsFromLocal()
        {
            if (universalVars.localDir?.Exists != true) return;

            foreach (var dir in universalVars.localDir.GetDirectories())
            {
                FileInfo[] modInfo = dir.GetFiles("mod.info", SearchOption.TopDirectoryOnly);
                string folderName = dir.Name.ToLowerInvariant();

                if (modInfo.Length == 0)
                {
                    //not a mod
                    continue;
                }
                else if (folderName.Contains(' ') == true)
                {
                    //is a mod, but not valid
                    continue;
                }
                string name = getModShowName(modInfo);

                bool hasShader = checkShader(dir);



                Mod single = new Mod(name, i18n.Main_ModLocal, dir.FullName, folderName, hasShader);

                if (universalVars.modDic.TryAdd(folderName, single) == false)
                {
                    AppDiagnostics.Log($"Duplicate mod identifier ignored: {folderName} ({dir.FullName})");
                }
            }
        }

        public void ReadLoadedMods()
        {
            using var options = File.OpenText(universalVars.optionLoc);
            foreach (string modName in FileManager.ReadLoadedModNames(options))
            {
                if (universalVars.modDic.TryGetValue(modName, out var mod) && mod.hasLoad == false)
                {
                    mod.hasLoad = true;
                    universalVars.modLoaded.Add(mod);
                }
            }
        }

        public void verifyLoadedMods()
        {
            string modified = "";
            using (StreamReader opt = File.OpenText(universalVars.optionLoc))
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
                    modified += line + "\r\n";
                    if (line.Contains("{mods") == true)
                    {
                        break;
                    }

                }

                opt.Close();
            }

            List<Mod> purned = new List<Mod>();
            List<Mod> newList = universalVars.modDic.Values.ToList();
            foreach (Mod mod in universalVars.modLoaded)
            {
                int newIndex = newList.FindIndex(m => m.folderName == mod.folderName);
                if (newIndex != -1)
                {
                    modified += "\t\t\"" + mod.folderName + ":0\"\r\n";
                    purned.Add(newList[newIndex]);
                    universalVars.modDic[mod.folderName].hasLoad = true;
                }
                else
                {

                }
            }
            universalVars.modLoaded = purned;

            modified += "\t}\r\n}\r\n";

            File.WriteAllText(universalVars.optionLoc, modified);
        }

        //reference https://juejin.cn/post/6989143365862293534
        public static void ExtractFile(String resource, String path, int batch)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using var source = assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException($"Embedded resource is missing: {resource}");
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
            source.CopyTo(output, batch);
        }

        //reference https://stackoverflow.com/questions/7646328/how-to-use-the-7z-sdk-to-compress-and-decompress-a-file
        public void CompressFileLZMA(string inFile, string outFile)
        {
            SevenZip.Compression.LZMA.Encoder coder = new SevenZip.Compression.LZMA.Encoder();
            using FileStream input = new FileStream(inFile, FileMode.Open);
            using FileStream output = new FileStream(outFile, FileMode.Create);

            // Write the encoder properties
            coder.WriteCoderProperties(output);

            // Write the decompressed file size.
            output.Write(BitConverter.GetBytes(input.Length), 0, 8);

            // Encode the file.
            coder.Code(input, output, input.Length, -1, null);
            output.Flush();
        }

        public void DecompressFileLZMA(string inFile, string outFile)
        {
            SevenZip.Compression.LZMA.Decoder coder = new SevenZip.Compression.LZMA.Decoder();
            using FileStream input = new FileStream(inFile, FileMode.Open);
            using FileStream output = new FileStream(outFile, FileMode.Create);

            // Read the decoder properties
            byte[] properties = new byte[5];
            input.ReadExactly(properties);

            // Read in the decompress file size.
            byte[] fileLengthBytes = new byte[8];
            input.ReadExactly(fileLengthBytes);
            long fileLength = BitConverter.ToInt64(fileLengthBytes, 0);

            coder.SetDecoderProperties(properties);
            coder.Code(input, output, input.Length, fileLength, null);
            output.Flush();
        }

        public void SaveSettings()
        {
            if (
                AppDiagnostics.IsFatal
                || string.IsNullOrWhiteSpace(universalVars.configLoc)
                ) return;

            try
            {
                var lines = new List<string>{
                    launcherVars.lm.ToString()
                    , launcherVars.showAddModInfo.ToString()
                    , launcherVars.runAsAdmin.ToString()
                    , universalVars.NeedRestore.ToString()
                    , universalVars.NeedClearCache.ToString()
                    , universalVars.NeedRedisplay.ToString()
                    , universalVars.NeedCompileWarning.ToString()
                    , universalVars.NeedLockModList.ToString()
                    , universalVars.AlwaysConfirm.ToString()
                    , universalVars.NeedAutoLoad.ToString()
                    , universalVars.NeedCheckShaderModify.ToString()
                    , FileManager.NormalizeCacheHash(universalVars.lastCacheHash)
                    , FileManager.NormalizeShaderHash(universalVars.lastShaderHash)
                    , LocalizedStrings.Instance.Culture.Name
                };
                lines.Add(selectedProfileOptions);
                lines.Add(HasGetGameRoot ? universalVars.gameDir!.FullName : "");
                lines.Add(HasGetProfileLoc ? universalVars.profileLoc : "");
                FileManager.WriteSettings(universalVars.configLoc, lines);
            }
            catch (Exception ex) when (FileManager.IsFileError(ex))
            {
                AppDiagnostics.Log("Unable to save launcher settings.", ex);
                MessageBox.Show($"{universalVars.configLoc}\n\n{ex.Message}", i18n.Universal_Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LanguageSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (languageSelector?.SelectedItem is not ComboBoxItem { Tag: string code }) return;

            var culture = CultureInfo.GetCultureInfo(code);
            if (LocalizedStrings.Instance.Culture.Name == culture.Name) return;

            var oldLocalType = i18n.Main_ModLocal;
            var oldWorkshopType = i18n.Main_ModWorkshop;
            LocalizedStrings.Instance.SetCulture(culture);
            foreach (var mod in universalVars.modDic.Values)
            {
                if (mod.type == oldLocalType) mod.type = i18n.Main_ModLocal;
                else if (mod.type == oldWorkshopType) mod.type = i18n.Main_ModWorkshop;
            }
            if (pageHost.Content is ModManager manager) manager.RefreshView();

            SaveSettings();
        }

        private void PrimaryNavigation_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (primaryNavigation.SelectedIndex < 0) return;
            footerNavigation.SelectedIndex = -1;
            Navigate(primaryNavigation.SelectedIndex);
        }

        private void FooterNavigation_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (footerNavigation.SelectedIndex < 0) return;
            primaryNavigation.SelectedIndex = -1;
            Navigate(footerNavigation.SelectedIndex + 4);
        }

        private void Navigate(int index)
        {
            if (settingsOnly && index != 4)
            {
                primaryNavigation.SelectedIndex = -1;
                footerNavigation.SelectedIndex = 0;
                return;
            }
            if (pages[index] == null)
                pages[index] = index switch
                {
                    0 => new Launcher(this),
                    1 => new ModManager(this),
                    2 => new Converter(this),
                    3 => new Tools(this),
                    4 => new Settings(this),
                    5 => new About(),
                    _ => throw new ArgumentOutOfRangeException(nameof(index))
                };
            pageHost.Content = pages[index];
            if (index == 1 && pages[index] is ModManager manager) manager.RefreshView();
        }

        public void RefreshModPage()
        {
            if (pages[1] is ModManager manager) manager.RefreshView();
        }

        //when manually close by user, only store the settings
        private void OnClosed(object sender, EventArgs e)
        {
            SaveSettings();
        }

    }
}