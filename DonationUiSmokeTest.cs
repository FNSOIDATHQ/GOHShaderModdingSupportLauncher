using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

// Shipped offline checks for the donation route, local assets and responsive navigation.
internal static class DonationUiSmokeTest
{
    // The fixture must not discover installations, start remote services or initialize real settings.
    private sealed class SupportWindow(string settings, bool showBanner = true) : MainWindow(settings, showBanner)
    { protected override void OnOpened(EventArgs e) { } }

    internal static void Run(string root)
    {
        var original = LocalizedStrings.Instance.Culture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN" })
            foreach (int width in new[] { 900, 1700 })
                Check(root, language, width);
            CheckBannerPreferences(root);
        }
        finally { LocalizedStrings.Instance.SetCulture(original); }
        AppDiagnostics.Log("Donation UI self-test passed: English/Chinese at 900/1700, default-collapsed support and Alipay sections, banner navigation and reopen, QR expansion, live language, beta notices, both session banner choices and permanent hide/restart preferences (offline).");
    }

    private static void Check(string root, string language, int width)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "donation-ui", language + "-" + width)).FullName;
        string settings = Path.Combine(directory, "settings.conf");
        FileManager.WriteSettings(settings,
            ["FileReplace", "True", "True", "False", "False", "True", "True",
                "False", "False", "False", "True", "-1", "0", language]);
        var main = new SupportWindow(settings)
            { SuppressSubscriptionActivation = true, SuppressLauncherUpdates = true, Width = width, Height = 600 };
        main.universalVars.configLoc = settings;
        try
        {
            var primary = Find<ListBox>(main, "primaryNavigation");
            var footer = Find<ListBox>(main, "footerNavigation");
            var host = Find<ContentControl>(main, "pageHost");
            primary.SelectedItem = primary.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "Launcher");
            main.Show();
            Layout(main, width);
            var launcher = host.Content as Launcher ?? throw new InvalidOperationException("Donation UI: launcher route");
            var banner = Find<Button>(launcher, "supportDevelopmentButton");
            Require(Find<Border>(launcher, "donationBanner").IsVisible && banner.IsEnabled && banner.Bounds.Width > 0,
                "selected session banner is reachable");
            footer.SelectedItem = footer.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "About");
            Layout(main, width);
            var defaultAbout = host.Content as About ?? throw new InvalidOperationException("Donation UI: About route");
            Require(!Find<SplitView>(defaultAbout, "aboutSupportView").IsPaneOpen &&
                Find<Button>(defaultAbout, "openDonationSidebarButton").IsVisible &&
                !Find<Expander>(defaultAbout, "alipayDonationExpander").IsExpanded,
                "ordinary About navigation starts with both support and Alipay collapsed");
            primary.SelectedItem = primary.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "Launcher");
            Layout(main, width);
            Click(banner);
            Layout(main, width);
            var about = host.Content as About ?? throw new InvalidOperationException("Donation UI: banner did not navigate to About");
            var support = Find<SplitView>(about, "aboutSupportView");
            Require(support.IsPaneOpen && support.PanePlacement == SplitViewPanePlacement.Right &&
                support.DisplayMode == SplitViewDisplayMode.Inline && footer.SelectedItem is ListBoxItem { Tag: "About" },
                "banner opens the right sidebar and selects About");
            var alipay = Find<Expander>(about, "alipayDonationExpander");
            Require(ReferenceEquals(defaultAbout, about) && !alipay.IsExpanded,
                "banner opens the cached support page without opening the payment QR");
            alipay.IsExpanded = true;
            var qr = Find<Image>(about, "alipayQrCode");
            Layout(main, width, () => qr.Bounds.Width > 0 && qr.Bounds.Height > 0);
            Require(qr.Source is { Size.Width: > 0, Size.Height: > 0 } &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(qr)), "embedded QR decodes without a network request");
            Require(Find<Button>(about, "koFiButton").IsEnabled && Find<Button>(about, "mianBaoDuoButton").IsEnabled,
                "both external donation options are available");
            Require(!about.GetLogicalDescendants().OfType<Button>().Any(button =>
                button.Content as string == LocalizedStrings.Instance.A_LinkWorkshop), "Workshop button is hidden");
            Require(Find<TextBlock>(about, "versionText").Text?.Contains("v" + AppVersion.Current, StringComparison.Ordinal) == true,
                "About displays the current project version");
            var readers = about.GetLogicalDescendants().OfType<ScrollViewer>().ToArray();
            Require(readers.Length == 2 && readers.All(reader => reader.Viewport.Width > 0),
                "About and donation content have independent usable scroll areas");
            var content = support.Content as Control ?? throw new InvalidOperationException("Donation UI: About content missing");
            bool SidebarIsLaidOut() => support.IsPaneOpen && content.Bounds.Width > 0 &&
                Math.Abs(content.Bounds.Width + support.OpenPaneLength - support.Bounds.Width) < 2;
            string SidebarState() => $"language={language}, requestedWidth={width}, window={main.Bounds.Width}, " +
                $"about={about.Bounds.Width}, split={support.Bounds.Width}, content={content.Bounds.Width}, " +
                $"paneLength={support.OpenPaneLength}, isOpen={support.IsPaneOpen}, " +
                $"reopenVisible={Find<Button>(about, "openDonationSidebarButton").IsVisible}";
            Layout(main, width, SidebarIsLaidOut, SidebarState);
            double openWidth = content.Bounds.Width;
            Click(Find<Button>(about, "closeDonationSidebarButton"));
            Layout(main, width, () => !support.IsPaneOpen && Find<Button>(about, "openDonationSidebarButton").IsVisible &&
                content.Bounds.Width > openWidth + 100, () => "closing sidebar, initialContent=" + openWidth + ", " + SidebarState());
            Click(Find<Button>(about, "openDonationSidebarButton"));
            Layout(main, width, SidebarIsLaidOut, SidebarState);
            Require(support.IsPaneOpen, "About reopens its sidebar");
            Click(Find<Button>(about, "closeDonationSidebarButton"));
            primary.SelectedItem = primary.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "Launcher");
            Layout(main, width);
            Require(ReferenceEquals(host.Content, launcher), "launcher remains cached");
            Click(banner);
            Layout(main, width, () => ReferenceEquals(host.Content, about) && SidebarIsLaidOut(), SidebarState);
            Require(ReferenceEquals(host.Content, about) && support.IsPaneOpen,
                "the banner reopens the existing About sidebar after it was closed");

            var navigation = Find<SplitView>(main, "mainView");
            var toggle = Find<Button>(main, "navigationToggle");
            if (!navigation.IsPaneOpen) Click(toggle);
            var beta = Find<Border>(main, "subscriptionBetaBadge");
            var betaText = beta.Child as TextBlock ?? throw new InvalidOperationException("Donation UI: beta label missing");
            Layout(main, width, () => navigation.IsPaneOpen && beta.IsVisible && beta.Bounds.Width > 0,
                () => $"expanded navigation: isOpen={navigation.IsPaneOpen}, betaVisible={beta.IsVisible}, betaWidth={beta.Bounds.Width}");
            Require(beta.IsVisible && betaText.Text == LocalizedStrings.Instance.Tab_ModSubscriptionsBeta && beta.Bounds.Width > 0,
                "expanded navigation displays the localized beta badge");
            main.UpdateSubscriptionBadge(2);
            var subscriptions = primary.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "ModSubscriptions");
            Require(Find<TextBlock>(main, "subscriptionNavigationLabel").Text == LocalizedStrings.Instance.Tab_ModSubscriptions + " (2)",
                "beta badge preserves update counts");
            Click(toggle);
            Layout(main, width, () => !navigation.IsPaneOpen && !beta.IsVisible && Find<TextBlock>(main, "subscriptionCompactBadge").IsVisible,
                () => $"compact navigation: isOpen={navigation.IsPaneOpen}, betaVisible={beta.IsVisible}, noticeVisible={Find<TextBlock>(main, "subscriptionCompactBadge").IsVisible}");
            Require(!navigation.IsPaneOpen && !beta.IsVisible && Find<TextBlock>(main, "subscriptionCompactBadge").IsVisible &&
                (ToolTip.GetTip(subscriptions) as string)?.Contains(betaText.Text!, StringComparison.Ordinal) == true &&
                AutomationProperties.GetName(subscriptions)?.Contains(betaText.Text!, StringComparison.Ordinal) == true,
                "compact navigation retains beta context and update notification");

            string prior = Find<TextBlock>(about, "collapsedDonationTitle").Text ?? "";
            var languages = Find<ComboBox>(main, "languageSelector");
            languages.SelectedIndex = language == "en-US" ? 1 : 0;
            Layout(main, width);
            Require(Find<TextBlock>(about, "collapsedDonationTitle").Text is { } current && current != prior &&
                current == LocalizedStrings.Instance.D_Title &&
                alipay.Header is TextBlock { Text: var alipayHeader } && alipayHeader == LocalizedStrings.Instance.Get("D_AlipayExpand") &&
                betaText.Text == LocalizedStrings.Instance.Tab_ModSubscriptionsBeta &&
                (ToolTip.GetTip(subscriptions) as string)?.Contains(betaText.Text!, StringComparison.Ordinal) == true &&
                Find<TextBlock>(main, "subscriptionNavigationLabel").Text == LocalizedStrings.Instance.Tab_ModSubscriptions + " (2)",
                "cached donation controls, beta context and counts follow live language changes");
            Require(!main.PathsReady, "UI checks did not configure or launch a real game");
        }
        finally { main.Close(); }
    }

    private static void CheckBannerPreferences(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "donation-banner-preferences")).FullName;
        string settings = Path.Combine(directory, "settings.conf");
        FileManager.WriteSettings(settings,
            ["FileReplace", "True", "True", "False", "False", "True", "True",
                "False", "False", "False", "True", "-1", "0", "en-US"]);
        string preferences = Path.Combine(directory, "ui-preferences.json");
        File.WriteAllText(preferences, "{\"version\":1,\"navigationCollapsed\":true,\"modListCollapsed\":true}");
        Require(new UiPreferencesStore(preferences).Load() is
                { HideDonationBanner: false, NavigationCollapsed: true, ModListCollapsed: true },
            "older interface preferences keep support banners allowed");

        static void Select(MainWindow window, string tag)
        {
            var navigation = Find<ListBox>(window, tag == "Launcher" ? "primaryNavigation" : "footerNavigation");
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == tag);
            Layout(window, 900);
        }
        static void SetHidden(MainWindow window, bool hidden)
        {
            Select(window, "Settings");
            var page = Find<ContentControl>(window, "pageHost").Content as Settings
                ?? throw new InvalidOperationException("Donation UI: settings route");
            var option = Find<CheckBox>(page, "hideDonationBanner");
            option.IsChecked = hidden;
            option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window, 900);
        }
        static Border Banner(MainWindow window) =>
            Find<Border>((Launcher)Find<ContentControl>(window, "pageHost").Content!, "donationBanner");

        var main = new SupportWindow(settings, showBanner: true)
            { SuppressSubscriptionActivation = true, SuppressLauncherUpdates = true, Width = 900, Height = 600 };
        main.universalVars.configLoc = settings;
        try
        {
            Select(main, "Launcher");
            main.Show();
            Layout(main, 900);
            var banner = Banner(main);
            Require(banner.IsVisible, "selected banner starts visible when allowed");
            SetHidden(main, true);
            Require(main.InterfacePreferences.HideDonationBanner && !banner.IsVisible &&
                new UiPreferencesStore(preferences).Load() is
                    { HideDonationBanner: true, NavigationCollapsed: true, ModListCollapsed: true },
                "settings immediately hides the cached banner and persists the preference");
            Select(main, "Launcher");
            Require(ReferenceEquals(banner, Banner(main)) && !banner.IsVisible,
                "page navigation keeps the selected banner hidden");

            var restarted = new SupportWindow(settings, showBanner: true)
                { SuppressSubscriptionActivation = true, SuppressLauncherUpdates = true, Width = 900, Height = 600 };
            restarted.universalVars.configLoc = settings;
            try
            {
                Select(restarted, "Launcher");
                restarted.Show();
                Layout(restarted, 900);
                Require(restarted.InterfacePreferences.HideDonationBanner && !Banner(restarted).IsVisible,
                    "permanent hide survives restart even when the session selects the banner");
                Select(restarted, "Settings");
                Require(Find<CheckBox>((Settings)Find<ContentControl>(restarted, "pageHost").Content!, "hideDonationBanner").IsChecked == true,
                    "settings restores the saved hide choice");
                SetHidden(restarted, false);
                Select(restarted, "Launcher");
                Require(Banner(restarted).IsVisible && new UiPreferencesStore(preferences).Load() is
                        { HideDonationBanner: false, NavigationCollapsed: true, ModListCollapsed: true },
                    "allowing banners again restores the original session choice");
            }
            finally { restarted.Close(); }
            SetHidden(main, false);
            Select(main, "Launcher");
            Require(banner.IsVisible, "reenabling also refreshes the original cached launcher");
        }
        finally { main.Close(); }

        var notSelected = new SupportWindow(settings, showBanner: false)
            { SuppressSubscriptionActivation = true, SuppressLauncherUpdates = true, Width = 900, Height = 600 };
        notSelected.universalVars.configLoc = settings;
        try
        {
            Select(notSelected, "Launcher");
            notSelected.Show();
            Layout(notSelected, 900);
            Require(!Banner(notSelected).IsVisible, "session can omit the banner while it is allowed in settings");
            SetHidden(notSelected, true);
            SetHidden(notSelected, false);
            Select(notSelected, "About");
            Select(notSelected, "Launcher");
            Require(!Banner(notSelected).IsVisible && !notSelected.InterfacePreferences.HideDonationBanner,
                "reenabling and navigation never redraw a session that did not select the banner");
        }
        finally { notSelected.Close(); }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Require(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException("Donation UI: " + reason); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(Window window, int width, Func<bool>? ready = null, Func<string>? state = null)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            window.Measure(new Size(width, 600));
            window.Arrange(new Rect(0, 0, width, 600));
            Dispatcher.UIThread.RunJobs();
            if (ready?.Invoke() ?? window.IsMeasureValid && window.IsArrangeValid) return;
            Thread.Sleep(1);
        } while (timer.Elapsed < TimeSpan.FromSeconds(2));
        throw new InvalidOperationException("Donation UI: layout did not reach the expected state within 2 seconds. " + state?.Invoke());
    }
}
