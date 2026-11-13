using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GOHShaderModdingSupportLauncher;

public partial class MainWindow
{
    private readonly UiPreferencesStore uiPreferencesStore;
    internal UiPreferences InterfacePreferences { get; private set; } = new();
    private bool navigationNarrow, expandNavigationInNarrowWindow;

    internal void SetModListCollapsed(bool collapsed) => SaveInterfacePreferences(InterfacePreferences with { ModListCollapsed = collapsed });

    private void SaveInterfacePreferences(UiPreferences value)
    {
        InterfacePreferences = value;
        try { uiPreferencesStore.Save(value); }
        catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Unable to save interface preferences.", ex); }
    }

    private void NavigationToggle_Click(object? sender, RoutedEventArgs e)
    {
        bool collapse = mainView.IsPaneOpen;
        expandNavigationInNarrowWindow = navigationNarrow && !collapse;
        SaveInterfacePreferences(InterfacePreferences with { NavigationCollapsed = collapse });
        UpdateNavigationLayout();
        navigationToggle.Focus(NavigationMethod.Tab);
    }

    private void LanguageMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string code }) languageSelector.SelectedIndex = code == "zh-CN" ? 1 : 0;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        if (mainView is null) return;
        bool narrow = Bounds.Width is > 0 and < 1120;
        if (navigationNarrow != narrow) expandNavigationInNarrowWindow = false;
        navigationNarrow = narrow;
        bool collapsed = InterfacePreferences.NavigationCollapsed || narrow && !expandNavigationInNarrowWindow;
        bool focusLanguage = collapsed && languagePanel.IsKeyboardFocusWithin;
        bool focusSelector = !collapsed && languageButton.IsKeyboardFocusWithin;
        // Resizing only changes presentation; it must never overwrite the manual preference.
        mainView.IsPaneOpen = !collapsed;
        mainView.Classes.Set("compactNavigation", collapsed);
        string label = LocalizedStrings.Instance.Get(collapsed ? "Navigation_Expand" : "Navigation_Collapse");
        ToolTip.SetTip(navigationToggle, label);
        AutomationProperties.SetName(navigationToggle, label);
        languageButton.IsVisible = collapsed;
        languagePanel.IsVisible = !collapsed;
        subscriptionCompactBadge.IsVisible = collapsed && subscriptionNoticeCount > 0;
        if (focusLanguage || focusSelector) (collapsed ? (Control)languageButton : languageSelector).Focus(NavigationMethod.Tab);
    }
}
