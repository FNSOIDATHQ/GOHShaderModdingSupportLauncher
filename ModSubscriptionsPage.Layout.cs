using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.LogicalTree;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private Grid body = null!, detail = null!, searchBar = null!, listPane = null!;
    private Border descriptionSurface = null!, fileReadingSurface = null!;
    private TabControl details = null!;
    private ScrollViewer readingScroll = null!, articleScroll = null!, fileScroll = null!, fileNavigationScroll = null!, operationScroll = null!;
    private StackPanel reading = null!, recovery = null!, fileReader = null!, fileNotice = null!;
    private Grid fileContent = null!;
    private TextBlock selectedFileCount = null!;
    private Button downloadSelected = null!;
    private ContentControl operation = null!;
    private TextBlock pageNumber = null!, listNotice = null!, activity = null!;
    private Button unsubscribe = null!, backToList = null!, refresh = null!, modListToggle = null!;
    private bool listOnly = true;

    private void BuildView()
    {
        rebuilding = true;
        int sourceIndex = source?.SelectedIndex ?? 0;
        int viewIndex = view?.SelectedIndex ?? 0;
        int sortIndex = Math.Max(0, sort?.SelectedIndex ?? 0);
        string query = keyword?.Text ?? "";
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*,Auto") };
        var header = subscriptionHeader = new Grid { ColumnDefinitions = new("*,Auto,*"), RowDefinitions = new("Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(Text(LocalizedStrings.Instance.Tab_ModSubscriptions, 22));
        var translation = BuildTranslationToolbar(); Grid.SetColumn(translation, 1); header.Children.Add(translation);
        var check = Action(T("Check"), CheckAsync);
        check.Name = "subscriptionCheck";
        ToolTip.SetTip(check, T("CheckScope")); Grid.SetColumn(check, 2); check.HorizontalAlignment = HorizontalAlignment.Right; header.Children.Add(check);
        root.Children.Add(header);

        source = new TabStrip { Name = "subscriptionSource", ItemsSource = new[] { "ModDB", "GitHub", "Mow Portal" }, SelectedIndex = sourceIndex };
        Grid.SetRow(source, 1); root.Children.Add(source);
        searchBar = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), RowDefinitions = new("Auto,Auto"), Margin = new Thickness(0, 8) };
        view = new ComboBox { Name = "subscriptionView", ItemsSource = new[] { T("Browse"), T("Subscribed"), T("Favorites"), T("Updates") }, SelectedIndex = viewIndex, MinWidth = 130 };
        keyword = new TextBox { Name = "subscriptionSearch", PlaceholderText = T("SearchPlaceholder"), MaxLength = 50, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0) };
        UpdateSearchScope();
        sort = new ComboBox { Name = "subscriptionSort", MinWidth = 130, SelectedIndex = sortIndex };
        UpdateSortOptions();
        refresh = Action(T("Refresh"), ApplySearchAsync, independent: true); refresh.Name = "subscriptionRefresh";
        refresh.Margin = new Thickness(8, 0, 0, 0);
        searchBar.Children.Add(view); searchBar.Children.Add(keyword); searchBar.Children.Add(sort); searchBar.Children.Add(refresh);
        Grid.SetColumn(keyword, 1); Grid.SetColumn(sort, 2); Grid.SetColumn(refresh, 3);
        Grid.SetRow(searchBar, 2); root.Children.Add(searchBar);
        var options = Column();
        var github = new Expander { Name = "subscriptionGitHubConfiguration", Header = T("GitHubConfiguration"), Content = new ScrollViewer { Content = BuildGitHubOptions(), MaxHeight = 160 },
            IsVisible = sourceIndex == 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        options.Children.Add(github);
        var mow = Text(T("MowPortalHint")); mow.Name = "subscriptionMowPortalHint"; mow.IsVisible = sourceIndex == 2; options.Children.Add(mow);
        Grid.SetRow(options, 3); root.Children.Add(options);

        var verification = Column();
        verification.Children.Add(Text(T("VerificationTitle"), 16));
        verificationMessage = Text(T("VerificationRequired")); verification.Children.Add(verificationMessage);
        verificationPrivacy = Text(T("VerificationPrivacy")); verification.Children.Add(verificationPrivacy);
        var verificationActions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        verificationOpen = Action(T("VerificationOpen"), OpenVerificationAsync, independent: true); verificationOpen.Name = "subscriptionVerificationOpen";
        verificationRetry = Action(T("VerificationRetry"), CompleteVerificationAsync, independent: true); verificationRetry.Name = "subscriptionVerificationRetry";
        verificationCancel = Action(T("VerificationCancel"), CloseVerificationAsync, independent: true); verificationCancel.Name = "subscriptionVerificationCancel";
        verificationActions.Children.Add(verificationOpen); verificationActions.Children.Add(verificationRetry); verificationActions.Children.Add(verificationCancel);
        verificationActions.Children.Add(Action(T("VerificationOriginal"), () => { OpenUrl(CurrentVerificationOrigin ?? new Uri("https://www.moddb.com/games/gates-of-hell/mods")); return Task.CompletedTask; }, independent: true));
        verification.Children.Add(verificationActions);
        verificationBanner = new Border { Name = "subscriptionVerification", IsVisible = false, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.DarkOrange, Child = new ScrollViewer { Content = verification, MaxHeight = 160 } };
        Grid.SetRow(verificationBanner, 4); root.Children.Add(verificationBanner);

        body = new Grid { ColumnDefinitions = new("260,12,*") };
        var list = listPane = new Grid { Name = "subscriptionListPane", RowDefinitions = new("Auto,*") };
        listNotice = Text(""); list.Children.Add(listNotice);
        mods = new ListBox { Name = "subscriptionMods", Focusable = true }; Grid.SetRow(mods, 1); list.Children.Add(mods);
        mods.SelectionChanged += async (_, _) => { if (!rebuilding && mods.SelectedItem is ModRow item) await OpenModAsync(item.Mod); };
        // Returning to the narrow list retains selection, so selecting the same row must work too.
        mods.Tapped += async (_, args) =>
        {
            if (listOnly && args.Source is Control control &&
                (control is ListBoxItem || control.FindLogicalAncestorOfType<ListBoxItem>() is not null) && mods.SelectedItem is ModRow item)
                await OpenModAsync(item.Mod);
        };
        mods.AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.Enter && listOnly && mods.SelectedItem is ModRow item)
            { args.Handled = true; await OpenModAsync(item.Mod); }
        }, RoutingStrategies.Tunnel);
        body.Children.Add(list);
        detail = new Grid { Name = "subscriptionDetailPane", RowDefinitions = new("Auto,*") }; Grid.SetColumn(detail, 2);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto") };
        backToList = Action(T("BackToList"), () => { SaveReadingPosition(); listOnly = true; CancelTranslation(); UpdateResponsiveLayout(); mods.Focus(NavigationMethod.Tab); return Task.CompletedTask; }, independent: true);
        backToList.Name = "subscriptionBackToList"; heading.Children.Add(backToList);
        Avalonia.Automation.AutomationProperties.SetName(backToList, T("BackToList"));
        title = Text(T("ChooseMod"), 20); title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(title, 1); heading.Children.Add(title);
        var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        favorite = Action(T("Favorite"), ToggleFavoriteAsync); favorite.IsEnabled = false; actions.Children.Add(favorite);
        actions.Children.Add(Action(T("Original"), () => { if (selected is { } mod) OpenUrl(mod.PageUrl); return Task.CompletedTask; }, independent: true));
        unsubscribe = Action(T("Unsubscribe"), UnsubscribeAsync); unsubscribe.Name = "subscriptionUnsubscribe"; unsubscribe.IsVisible = false;
        actions.Children.Add(unsubscribe); actions.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); heading.Children.Add(actions); detail.Children.Add(heading);

        summary = Column(); articles = Column(); images = Column(); files = Column(); tasks = Column(); recovery = Column();
        var description = new Grid { ColumnDefinitions = new("175,12,*") };
        articleScroll = Scroll(articles); articleScroll.Name = "subscriptionDescriptionNavigation"; description.Children.Add(articleScroll);
        reading = Column(); readingScroll = Scroll(reading); readingScroll.Name = "subscriptionReader";
        descriptionSurface = ReadingSurface(readingScroll); Grid.SetColumn(descriptionSurface, 2); description.Children.Add(descriptionSurface);
        fileContent = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), ColumnDefinitions = new("420,12,*") };
        fileNotice = Column(); Grid.SetColumnSpan(fileNotice, 3); fileContent.Children.Add(fileNotice);
        fileNavigationScroll = Scroll(files); fileNavigationScroll.Name = "subscriptionFileNavigation";
        Grid.SetRow(fileNavigationScroll, 1); fileContent.Children.Add(fileNavigationScroll);
        fileReader = Column(); fileReader.Name = "subscriptionFileReader";
        var fileBody = Column(); fileBody.Children.Add(fileReader); fileBody.Children.Add(tasks);
        fileScroll = Scroll(fileBody); fileScroll.Name = "subscriptionFileReading";
        fileReadingSurface = ReadingSurface(fileScroll); Grid.SetColumn(fileReadingSurface, 2); Grid.SetRow(fileReadingSurface, 1); fileContent.Children.Add(fileReadingSurface);
        var fileActions = new WrapPanel { Name = "subscriptionFileActions", ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        selectedFileCount = Text(""); selectedFileCount.VerticalAlignment = VerticalAlignment.Center;
        downloadSelected = Action(T("SubscribeDownload"), () => selected is { } mod ? SubscribeAsync(mod, DetailToken) : Task.CompletedTask);
        downloadSelected.Name = "subscriptionDownloadSelected";
        fileActions.Children.Add(selectedFileCount); fileActions.Children.Add(downloadSelected);
        BuildFileNavigationLayout(fileActions);
        Grid.SetRow(fileActions, 2); Grid.SetColumnSpan(fileActions, 3); fileContent.Children.Add(fileActions);
        var recoveryScroll = new ScrollViewer { Content = recovery, MaxHeight = 120 };
        Grid.SetRow(recoveryScroll, 3); Grid.SetColumnSpan(recoveryScroll, 3); fileContent.Children.Add(recoveryScroll);
        operation = new ContentControl { Name = "subscriptionOperation", IsVisible = false };
        operationScroll = Scroll(operation); operationScroll.IsVisible = false;
        var fileRoot = new Grid(); fileRoot.Children.Add(operationScroll); fileRoot.Children.Add(fileContent);
        details = new TabControl { Name = "subscriptionDetails", ItemsSource = new[] {
            new TabItem { Header = T("Description"), Content = description }, new TabItem { Header = T("Files"), Content = fileRoot } }, SelectedIndex = 0 };
        details.SelectionChanged += async (sender, args) =>
        {
            if (args.Source != details || rebuilding || session is null) return;
            SaveReadingPosition();
            session.Tab = details.SelectedIndex;
            CancelRefresh(); CancelTranslation();
            if (details.SelectedIndex == 1) await ExecuteAsync(() => EnsureFilesAsync());
            else RerenderTranslation();
        };
        Grid.SetRow(details, 1); detail.Children.Add(details); body.Children.Add(detail);
        Grid.SetRow(body, 5); root.Children.Add(body);

        var footer = new Grid { Name = "subscriptionFooter", ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var listActions = Row();
        modListToggle = new Button { Name = "subscriptionModListToggle", VerticalAlignment = VerticalAlignment.Center };
        modListToggle.Click += (_, _) => ToggleModList(); listActions.Children.Add(modListToggle);
        var paging = Row(); paging.Name = "subscriptionPagination";
        previous = Action(T("Previous"), async () => { CancelRefresh(); browsePage = Math.Max(1, browsePage - 1); await LoadListAsync(); }, independent: true);
        next = Action(T("Next"), async () => { CancelRefresh(); browsePage++; await LoadListAsync(); }, independent: true);
        pageNumber = Text(""); pageNumber.VerticalAlignment = VerticalAlignment.Center;
        paging.Children.Add(previous); paging.Children.Add(pageNumber); paging.Children.Add(next); listActions.Children.Add(paging); footer.Children.Add(listActions);
        activity = Text(""); activity.VerticalAlignment = VerticalAlignment.Center; activity.Margin = new Thickness(12, 0); Grid.SetColumn(activity, 1); footer.Children.Add(activity);
        status = Text(T("Ready")); status.Name = "subscriptionStatus"; status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis; status.MaxWidth = 220; status.VerticalAlignment = VerticalAlignment.Center;
        status.HorizontalAlignment = HorizontalAlignment.Right;
        BuildStatusDetails(root);
        statusToggle = new Button { Name = "subscriptionStatusToggle", Content = status, Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right };
        statusToggle.Click += (_, _) => { statusDetails.IsVisible = !statusDetails.IsVisible; if (statusDetails.IsVisible) RenderStatusDetails(); };
        ToolTip.SetTip(statusToggle, T("StatusDetails"));
        Avalonia.Automation.AutomationProperties.SetName(statusToggle, T("StatusDetails"));
        Grid.SetColumn(statusToggle, 2); footer.Children.Add(statusToggle); Grid.SetRow(footer, 6); root.Children.Add(footer);
        Content = root;

        keyword.Text = query;
        keyword.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ExecuteAsync(ApplySearchAsync); } };
        view.SelectionChanged += (_, _) => { if (!rebuilding && active) { CancelRefresh(); browsePage = 1; _ = ExecuteAsync(ApplySearchAsync); } };
        sort.SelectionChanged += (_, _) => { if (!rebuilding && active) { CancelRefresh(); browsePage = 1; _ = ExecuteAsync(ApplySearchAsync); } };
        source.SelectionChanged += (_, _) =>
        {
            if (rebuilding) return;
            rebuilding = true; UpdateSortOptions(); rebuilding = false;
            UpdateSearchScope();
            github.IsVisible = source.SelectedIndex == 1; mow.IsVisible = source.SelectedIndex == 2;
            browsePage = 1; browseItems = []; hasNext = false;
            OnGameChanged();
        };
        rebuilding = false;
        UpdatePaging(); UpdateResponsiveLayout();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (body is null) return;
        bool narrow = Bounds.Width is > 0 and < 900;
        bool wrapTranslation = Bounds.Width is > 0 and < 1120;
        Grid.SetColumn(translationToolbar, wrapTranslation ? 0 : 1); Grid.SetColumnSpan(translationToolbar, wrapTranslation ? 3 : 1);
        Grid.SetRow(translationToolbar, wrapTranslation ? 1 : 0);
        subscriptionHeader.ColumnDefinitions[1].Width = wrapTranslation ? new GridLength(0) : GridLength.Auto;
        translationToolbar.Margin = new Thickness(0, wrapTranslation ? 6 : 0, 0, 0);
        bool showList = selected is null || (narrow ? listOnly : !main.InterfacePreferences.ModListCollapsed);
        bool showDetail = !narrow || selected is not null && !listOnly;
        bool focusList = !showDetail && detail.IsKeyboardFocusWithin;
        bool focusDetail = !showList && listPane.IsKeyboardFocusWithin;
        body.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(showList ? 260 : 0);
        body.ColumnDefinitions[1].Width = new GridLength(!narrow && showList ? 12 : 0);
        body.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        listPane.IsVisible = showList;
        detail.IsVisible = showDetail;
        Grid.SetColumn(detail, narrow ? 0 : 2);
        backToList.IsVisible = narrow && showDetail;
        modListToggle.IsVisible = !narrow && selected is not null;
        string toggleText = T(showList ? "CollapseModList" : "ExpandModList");
        modListToggle.Content = toggleText; ToolTip.SetTip(modListToggle, toggleText);
        Avalonia.Automation.AutomationProperties.SetName(modListToggle, toggleText);
        if (focusList) mods.Focus(NavigationMethod.Tab);
        if (focusDetail) (narrow ? backToList : modListToggle).Focus(NavigationMethod.Tab);
        title.Margin = new Thickness(narrow ? 8 : 0, 0, 0, 0); title.MaxLines = narrow ? 1 : 2;
        bool compact = Bounds.Width is > 0 and < 720;
        // Short windows need room for the file body as well as the fixed download actions.
        var readingPadding = new Thickness(16, Bounds.Height is > 0 and < 600 ? 8 : 16);
        descriptionSurface.Padding = readingPadding; fileReadingSurface.Padding = readingPadding;
        Grid.SetRow(keyword, compact ? 1 : 0); Grid.SetColumn(keyword, compact ? 0 : 1); Grid.SetColumnSpan(keyword, compact ? 3 : 1);
        keyword.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0);
        Grid.SetColumn(sort, compact ? 1 : 2); Grid.SetColumn(refresh, 3); Grid.SetRow(refresh, compact ? 1 : 0);
        activity.IsVisible = !compact;
        if (statusDetails is not null) statusDetails.MaxHeight = Bounds.Height is > 0 and < 600 ? 80 : 182;
        UpdateFileNavigationLayout();
    }

    private void ToggleModList()
    {
        if (selected is null) return;
        SaveReadingPosition();
        var readingPosition = readingScroll.Offset;
        var filePosition = fileScroll.Offset;
        main.SetModListCollapsed(!main.InterfacePreferences.ModListCollapsed);
        UpdateResponsiveLayout();
        RestorePosition(readingScroll, readingPosition);
        RestoreFilePosition(filePosition);
        modListToggle.Focus(NavigationMethod.Tab);
    }

    private void UpdatePaging()
    {
        previous.IsEnabled = !listLoading && view.SelectedIndex == 0 && browsePage > 1;
        next.IsEnabled = !listLoading && view.SelectedIndex == 0 && hasNext;
        pageNumber.Text = view.SelectedIndex == 0 ? string.Format(T("PageNumber"), browsePage) : "";
    }
}
