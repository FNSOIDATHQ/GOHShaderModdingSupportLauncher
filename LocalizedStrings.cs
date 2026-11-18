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
        nameof(Tab_ModSubscriptionsBeta),
        nameof(L_DonationBanner),
        nameof(D_ViewSupport),
        nameof(D_Title),
        nameof(D_Message),
        nameof(D_MessageMore),
        nameof(D_AlipayExpand),
        nameof(S_HideDonationBanner),
        nameof(D_Thanks),
        nameof(D_KoFi),
        nameof(D_MianBaoDuo),
        nameof(D_Alipay),
        nameof(D_AlipayHint),
        nameof(D_OpenSidebar),
        nameof(D_CloseSidebar),
        nameof(MI_Features),
        nameof(MI_FeatureHint),
        nameof(MI_Source),
        nameof(M_ViewSubscription),
        nameof(MA_Title),
        nameof(MA_EditAlias),
        nameof(MA_OriginalName),
        nameof(MA_Alias),
        nameof(MA_Search),
        nameof(MA_Save),
        nameof(MA_Delete),
        nameof(MA_Clear),
        nameof(MA_Cancel),
        nameof(MA_Hint),
        nameof(MA_Empty),
        nameof(MA_InvalidRule),
        nameof(MA_InvalidFile),
        nameof(MA_LoadError),
        nameof(MA_SaveError),
        nameof(MA_EnableItemMapping),
        nameof(MA_RuleType),
        nameof(MA_Target),
        nameof(MA_KindField),
        nameof(MA_KindItem),
        nameof(MA_WorkshopSearch),
        nameof(MA_WorkshopSelect),
        nameof(MA_ItemUrl),
        nameof(MA_ResolveItem),
        nameof(MA_SampleName),
        nameof(P_Presets), 
        nameof(P_Save), 
        nameof(P_Overwrite), 
        nameof(P_Load), 
        nameof(P_Rename), 
        nameof(P_Delete),
        nameof(A_BugReport),
        nameof(A_CheckUpdate),
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
        nameof(M_Available),
        nameof(M_Loaded),
        nameof(M_GridName),
        nameof(M_GridShader),
        nameof(M_GridType),
        nameof(M_Load),
        nameof(M_LoadCache),
        nameof(M_OpenFolder),
        nameof(M_Refresh),
        nameof(M_Search),
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
        nameof(S_RememberWindowPlacement),
        nameof(S_Restore),
        nameof(Tab_About),
        nameof(Tab_Converter),
        nameof(Tab_Launcher),
        nameof(Tab_ModManager),
        nameof(Tab_ModSubscriptions),
        nameof(Tab_Settings),
        nameof(Tab_Tools),
        nameof(Title),
        nameof(U_ClearAllCache),
        nameof(U_ClearModSubscriptionCache),
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
    internal string Get(string key) => this[key];

    public string Tab_ModSubscriptionsBeta => this[nameof(Tab_ModSubscriptionsBeta)];
    public string L_DonationBanner => this[nameof(L_DonationBanner)];
    public string D_ViewSupport => this[nameof(D_ViewSupport)];
    public string D_Title => this[nameof(D_Title)];
    public string D_Message => this[nameof(D_Message)];
    public string D_MessageMore => this[nameof(D_MessageMore)];
    public string D_AlipayExpand => this[nameof(D_AlipayExpand)];
    public string S_HideDonationBanner => this[nameof(S_HideDonationBanner)];
    public string D_Thanks => this[nameof(D_Thanks)];
    public string D_KoFi => this[nameof(D_KoFi)];
    public string D_MianBaoDuo => this[nameof(D_MianBaoDuo)];
    public string D_Alipay => this[nameof(D_Alipay)];
    public string D_AlipayHint => this[nameof(D_AlipayHint)];
    public string D_OpenSidebar => this[nameof(D_OpenSidebar)];
    public string D_CloseSidebar => this[nameof(D_CloseSidebar)];

    public string P_Presets => this[nameof(P_Presets)];
    public string MI_Features => this[nameof(MI_Features)];
    public string MI_FeatureHint => string.Join("\n", ModFeatureHintView.FeatureKeys.Select(key =>
        this["MI_" + key] + this["MI_FeatureHint" + key]));
    public string MI_Source => this[nameof(MI_Source)];
    public string M_ViewSubscription => this[nameof(M_ViewSubscription)];
    public string MA_Title => this[nameof(MA_Title)];
    public string MA_EditAlias => this[nameof(MA_EditAlias)];
    public string MA_OriginalName => this[nameof(MA_OriginalName)];
    public string MA_Alias => this[nameof(MA_Alias)];
    public string MA_Search => this[nameof(MA_Search)];
    public string MA_Save => this[nameof(MA_Save)];
    public string MA_Delete => this[nameof(MA_Delete)];
    public string MA_Clear => this[nameof(MA_Clear)];
    public string MA_Cancel => this[nameof(MA_Cancel)];
    public string MA_Hint => this[nameof(MA_Hint)];
    public string MA_Empty => this[nameof(MA_Empty)];
    public string MA_InvalidRule => this[nameof(MA_InvalidRule)];
    public string MA_InvalidFile => this[nameof(MA_InvalidFile)];
    public string MA_LoadError => this[nameof(MA_LoadError)];
    public string MA_SaveError => this[nameof(MA_SaveError)];
    public string MA_EnableItemMapping => this[nameof(MA_EnableItemMapping)];
    public string MA_RuleType => this[nameof(MA_RuleType)];
    public string MA_Target => this[nameof(MA_Target)];
    public string MA_KindField => this[nameof(MA_KindField)];
    public string MA_KindItem => this[nameof(MA_KindItem)];
    public string MA_WorkshopSearch => this[nameof(MA_WorkshopSearch)];
    public string MA_WorkshopSelect => this[nameof(MA_WorkshopSelect)];
    public string MA_ItemUrl => this[nameof(MA_ItemUrl)];
    public string MA_ResolveItem => this[nameof(MA_ResolveItem)];
    public string MA_SampleName => this[nameof(MA_SampleName)];
    public string P_Save => this[nameof(P_Save)];
    public string P_Overwrite => this[nameof(P_Overwrite)];
    public string P_Load => this[nameof(P_Load)];
    public string P_Rename => this[nameof(P_Rename)];
    public string P_Delete => this[nameof(P_Delete)];
    public string A_BugReport => this[nameof(A_BugReport)];
    public string A_CheckUpdate => this[nameof(A_CheckUpdate)];
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
    public string M_Available => this[nameof(M_Available)];
    public string M_Loaded => this[nameof(M_Loaded)];
    public string M_GridName => this[nameof(M_GridName)];
    public string M_GridShader => this[nameof(M_GridShader)];
    public string M_GridType => this[nameof(M_GridType)];
    public string M_Load => this[nameof(M_Load)];
    public string M_LoadCache => this[nameof(M_LoadCache)];
    public string M_OpenFolder => this[nameof(M_OpenFolder)];
    public string M_Refresh => this[nameof(M_Refresh)];
    public string M_Search => this[nameof(M_Search)];
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
    public string S_RememberWindowPlacement => this[nameof(S_RememberWindowPlacement)];
    public string S_Restore => this[nameof(S_Restore)];
    public string Tab_About => this[nameof(Tab_About)];
    public string Tab_Converter => this[nameof(Tab_Converter)];
    public string Tab_Launcher => this[nameof(Tab_Launcher)];
    public string Tab_ModManager => this[nameof(Tab_ModManager)];
    public string Tab_ModSubscriptions => this[nameof(Tab_ModSubscriptions)];
    public string Tab_Settings => this[nameof(Tab_Settings)];
    public string Tab_Tools => this[nameof(Tab_Tools)];
    public string Title => this[nameof(Title)];
    public string U_ClearAllCache => this[nameof(U_ClearAllCache)];
    public string U_ClearModSubscriptionCache => this[nameof(U_ClearModSubscriptionCache)];
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