using System.ComponentModel;
using System.Globalization;
using GOHShaderModdingSupportLauncher.Properties;

namespace GOHShaderModdingSupportLauncher;

/// <summary>Provides live resource bindings for the AXAML controls.</summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    public static LocalizedStrings Instance { get; } = new();

    private static readonly string[] ResourcePropertyNames =
    [
        nameof(A_BugReport),
        nameof(A_LinkGithub),
        nameof(A_LinkWorkshop),
        nameof(A_Tip),
        nameof(C_Console),
        nameof(C_EnableEnvMaps),
        nameof(C_Path),
        nameof(C_Tab_MtlConvert),
        nameof(C_Tab_TextureExtInj),
        nameof(C_Tab_TextureRemap),
        nameof(C_ViewPath),
        nameof(L_AutoFix),
        nameof(L_DX101),
        nameof(L_Editor),
        nameof(L_FileReplace),
        nameof(L_Main),
        nameof(L_Method),
        nameof(L_ModInfo),
        nameof(L_RunAsAdmin),
        nameof(L_Safe),
        nameof(M_CollectCache),
        nameof(M_GridName),
        nameof(M_GridShader),
        nameof(M_GridType),
        nameof(M_Load),
        nameof(M_LoadCache),
        nameof(M_OpenFolder),
        nameof(M_Refresh),
        nameof(M_SelectedMod),
        nameof(M_Unload),
        nameof(S_AfterGame),
        nameof(S_AutoLoadCache),
        nameof(S_BeforeLaunch),
        nameof(S_Browse),
        nameof(S_Clear),
        nameof(S_CompileWarning),
        nameof(S_ProfileAccount),
        nameof(S_GameConfigPath),
        nameof(S_GameExit),
        nameof(S_GamePath),
        nameof(S_General),
        nameof(S_LockModList),
        nameof(S_PathConfirm),
        nameof(S_PathsRequired),
        nameof(S_RefreshCache),
        nameof(S_Restore),
        nameof(Tab_About),
        nameof(Tab_Converter),
        nameof(Tab_Launcher),
        nameof(Tab_ModManager),
        nameof(Tab_Settings),
        nameof(Tab_Tools),
        nameof(Title),
        nameof(U_ClearAllCache),
        nameof(U_OpenCacheFolder),
        nameof(U_OpenLauncherConfig),
        nameof(U_OpenLogFolder),
        nameof(U_OpenProfileFolder),
        nameof(U_OpenWorkshopModFolder),
        nameof(U_Restore)
    ];

    private LocalizedStrings() { }

    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    private string this[string key] => i18n.ResourceManager.GetString(key, i18n.Culture) ?? key;

    public string A_BugReport => this[nameof(A_BugReport)];
    public string A_LinkGithub => this[nameof(A_LinkGithub)];
    public string A_LinkWorkshop => this[nameof(A_LinkWorkshop)];
    public string A_Tip => this[nameof(A_Tip)];
    public string C_Console => this[nameof(C_Console)];
    public string C_EnableEnvMaps => this[nameof(C_EnableEnvMaps)];
    public string C_Path => this[nameof(C_Path)];
    public string C_Tab_MtlConvert => this[nameof(C_Tab_MtlConvert)];
    public string C_Tab_TextureExtInj => this[nameof(C_Tab_TextureExtInj)];
    public string C_Tab_TextureRemap => this[nameof(C_Tab_TextureRemap)];
    public string C_ViewPath => this[nameof(C_ViewPath)];
    public string L_AutoFix => this[nameof(L_AutoFix)];
    public string L_DX101 => this[nameof(L_DX101)];
    public string L_Editor => this[nameof(L_Editor)];
    public string L_FileReplace => this[nameof(L_FileReplace)];
    public string L_Main => this[nameof(L_Main)];
    public string L_Method => this[nameof(L_Method)];
    public string L_ModInfo => this[nameof(L_ModInfo)];
    public string L_RunAsAdmin => this[nameof(L_RunAsAdmin)];
    public string L_Safe => this[nameof(L_Safe)];
    public string M_CollectCache => this[nameof(M_CollectCache)];
    public string M_GridName => this[nameof(M_GridName)];
    public string M_GridShader => this[nameof(M_GridShader)];
    public string M_GridType => this[nameof(M_GridType)];
    public string M_Load => this[nameof(M_Load)];
    public string M_LoadCache => this[nameof(M_LoadCache)];
    public string M_OpenFolder => this[nameof(M_OpenFolder)];
    public string M_Refresh => this[nameof(M_Refresh)];
    public string M_SelectedMod => this[nameof(M_SelectedMod)];
    public string M_Unload => this[nameof(M_Unload)];
    public string S_AfterGame => this[nameof(S_AfterGame)];
    public string S_AutoLoadCache => this[nameof(S_AutoLoadCache)];
    public string S_BeforeLaunch => this[nameof(S_BeforeLaunch)];
    public string S_Browse => this[nameof(S_Browse)];
    public string S_Clear => this[nameof(S_Clear)];
    public string S_CompileWarning => this[nameof(S_CompileWarning)];
    public string S_ProfileAccount => this[nameof(S_ProfileAccount)];
    public string S_GameConfigPath => this[nameof(S_GameConfigPath)];
    public string S_GameExit => this[nameof(S_GameExit)];
    public string S_GamePath => this[nameof(S_GamePath)];
    public string S_General => this[nameof(S_General)];
    public string S_LockModList => this[nameof(S_LockModList)];
    public string S_PathConfirm => this[nameof(S_PathConfirm)];
    public string S_PathsRequired => this[nameof(S_PathsRequired)];
    public string S_RefreshCache => this[nameof(S_RefreshCache)];
    public string S_Restore => this[nameof(S_Restore)];
    public string Tab_About => this[nameof(Tab_About)];
    public string Tab_Converter => this[nameof(Tab_Converter)];
    public string Tab_Launcher => this[nameof(Tab_Launcher)];
    public string Tab_ModManager => this[nameof(Tab_ModManager)];
    public string Tab_Settings => this[nameof(Tab_Settings)];
    public string Tab_Tools => this[nameof(Tab_Tools)];
    public string Title => this[nameof(Title)];
    public string U_ClearAllCache => this[nameof(U_ClearAllCache)];
    public string U_OpenCacheFolder => this[nameof(U_OpenCacheFolder)];
    public string U_OpenLauncherConfig => this[nameof(U_OpenLauncherConfig)];
    public string U_OpenLogFolder => this[nameof(U_OpenLogFolder)];
    public string U_OpenProfileFolder => this[nameof(U_OpenProfileFolder)];
    public string U_OpenWorkshopModFolder => this[nameof(U_OpenWorkshopModFolder)];
    public string U_Restore => this[nameof(U_Restore)];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCulture(CultureInfo culture)
    {
        var selected = culture.TwoLetterISOLanguageName == "zh"
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en-US");
        if (Culture.Name == selected.Name && i18n.Culture?.Name == selected.Name) return;

        Culture = selected;
        i18n.Culture = selected;
        foreach (var name in ResourcePropertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}