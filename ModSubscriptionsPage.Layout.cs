using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private Grid body = null!, detail = null!, searchBar = null!;
    private TabControl details = null!;
    private ScrollViewer readingScroll = null!, articleScroll = null!, fileScroll = null!, fileNavigationScroll = null!, operationScroll = null!;
    private StackPanel reading = null!, recovery = null!, fileReader = null!, fileNotice = null!;
    private Grid fileContent = null!;
    private TextBlock selectedFileCount = null!;
    private Button downloadSelected = null!;
    private ContentControl operation = null!;
    private TextBlock pageNumber = null!, pageNotice = null!, listNotice = null!, activity = null!;
    private Button subscribe = null!, unsubscribe = null!, backToList = null!, refresh = null!;
    private bool listOnly = true;

    private void BuildView()
    {
        rebuilding = true;
        int sourceIndex = source?.SelectedIndex ?? 0;
        int viewIndex = view?.SelectedIndex ?? 0;
        int sortIndex = Math.Max(0, sort?.SelectedIndex ?? 0);
        string query = keyword?.Text ?? "";
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*,Auto") };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(Text(LocalizedStrings.Instance.Tab_ModSubscriptions, 22));
        var check = Action(T("Check"), CheckAsync);
        check.Name = "subscriptionCheck";
        ToolTip.SetTip(check, T("CheckScope")); Grid.SetColumn(check, 1); header.Children.Add(check);
        root.Children.Add(header);

        source = new TabStrip { Name = "subscriptionSource", ItemsSource = new[] { "ModDB", "GitHub", "Mow Portal" }, SelectedIndex = sourceIndex };
        Grid.SetRow(source, 1); root.Children.Add(source);
        searchBar = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), RowDefinitions = new("Auto,Auto"), Margin = new Thickness(0, 8) };
        view = new ComboBox { Name = "subscriptionView", ItemsSource = new[] { T("Browse"), T("Subscribed"), T("Favorites"), T("Updates") }, SelectedIndex = viewIndex, MinWidth = 130 };
        keyword = new TextBox { Name = "subscriptionSearch", PlaceholderText = T("SearchPlaceholder"), MaxLength = 50, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0) };
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
        pageNotice = Text(""); pageNotice.Name = "subscriptionNotice"; pageNotice.IsVisible = false;
        options.Children.Add(new ScrollViewer { Content = pageNotice, MaxHeight = 70 });
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
        var list = new Grid { Name = "subscriptionListPane", RowDefinitions = new("Auto,*") };
        listNotice = Text(""); list.Children.Add(listNotice);
        mods = new ListBox { Name = "subscriptionMods" }; Grid.SetRow(mods, 1); list.Children.Add(mods);
        mods.SelectionChanged += async (_, _) => { if (!rebuilding && mods.SelectedItem is ModRow item) await OpenModAsync(item.Mod); };
        body.Children.Add(list);
        detail = new Grid { Name = "subscriptionDetailPane", RowDefinitions = new("Auto,Auto,*") }; Grid.SetColumn(detail, 2);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto") };
        backToList = Action(T("BackToList"), () => { listOnly = true; UpdateResponsiveLayout(); return Task.CompletedTask; }, independent: true);
        backToList.Name = "subscriptionBackToList"; heading.Children.Add(backToList);
        title = Text(T("ChooseMod"), 20); title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(title, 1); heading.Children.Add(title);
        var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        favorite = Action(T("Favorite"), ToggleFavoriteAsync); favorite.IsEnabled = false; actions.Children.Add(favorite);
        actions.Children.Add(Action(T("Original"), () => { if (selected is { } mod) OpenUrl(mod.PageUrl); return Task.CompletedTask; }, independent: true));
        subscribe = Action(T("ChooseSubscribe"), OpenFilesAsync, independent: true); subscribe.Name = "subscriptionChooseFiles"; subscribe.IsVisible = false;
        unsubscribe = Action(T("Unsubscribe"), UnsubscribeAsync); unsubscribe.Name = "subscriptionUnsubscribe"; unsubscribe.IsVisible = false;
        actions.Children.Add(subscribe); actions.Children.Add(unsubscribe); actions.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); heading.Children.Add(actions); detail.Children.Add(heading);
        tracking = Text(""); Grid.SetRow(tracking, 1); detail.Children.Add(tracking);

        summary = Column(); articles = Column(); images = Column(); files = Column(); tasks = Column(); recovery = Column();
        var description = new Grid { ColumnDefinitions = new("175,12,*") };
        articleScroll = Scroll(articles); articleScroll.Name = "subscriptionDescriptionNavigation"; description.Children.Add(articleScroll);
        reading = Column(); readingScroll = Scroll(reading); readingScroll.Name = "subscriptionReader";
        Grid.SetColumn(readingScroll, 2); description.Children.Add(readingScroll);
        fileContent = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), ColumnDefinitions = new("175,12,*") };
        fileNotice = Column(); Grid.SetColumnSpan(fileNotice, 3); fileContent.Children.Add(fileNotice);
        fileNavigationScroll = Scroll(files); fileNavigationScroll.Name = "subscriptionFileNavigation";
        Grid.SetRow(fileNavigationScroll, 1); fileContent.Children.Add(fileNavigationScroll);
        fileReader = Column(); fileReader.Name = "subscriptionFileReader";
        var fileBody = Column(); fileBody.Children.Add(fileReader); fileBody.Children.Add(tasks);
        fileScroll = Scroll(fileBody); fileScroll.Name = "subscriptionFileReading";
        Grid.SetColumn(fileScroll, 2); Grid.SetRow(fileScroll, 1); fileContent.Children.Add(fileScroll);
        var fileActions = new WrapPanel { Name = "subscriptionFileActions", ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        selectedFileCount = Text(""); selectedFileCount.VerticalAlignment = VerticalAlignment.Center;
        downloadSelected = Action(T("SubscribeDownload"), () => selected is { } mod ? SubscribeAsync(mod, DetailToken) : Task.CompletedTask);
        downloadSelected.Name = "subscriptionDownloadSelected";
        fileActions.Children.Add(selectedFileCount); fileActions.Children.Add(downloadSelected);
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
            if (details.SelectedIndex == 1) await ExecuteAsync(() => EnsureFilesAsync());
        };
        Grid.SetRow(details, 2); detail.Children.Add(details); body.Children.Add(detail);
        Grid.SetRow(body, 5); root.Children.Add(body);

        var footer = new Grid { Name = "subscriptionFooter", ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var paging = Row(); paging.Name = "subscriptionPagination";
        previous = Action(T("Previous"), async () => { browsePage = Math.Max(1, browsePage - 1); await LoadListAsync(); }, independent: true);
        next = Action(T("Next"), async () => { browsePage++; await LoadListAsync(); }, independent: true);
        pageNumber = Text(""); pageNumber.VerticalAlignment = VerticalAlignment.Center;
        paging.Children.Add(previous); paging.Children.Add(pageNumber); paging.Children.Add(next); footer.Children.Add(paging);
        activity = Text(""); activity.VerticalAlignment = VerticalAlignment.Center; activity.Margin = new Thickness(12, 0); Grid.SetColumn(activity, 1); footer.Children.Add(activity);
        status = Text(T("Ready")); status.Name = "subscriptionStatus"; status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis; status.MaxWidth = 220; status.VerticalAlignment = VerticalAlignment.Center;
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBlock.TextProperty || status.Text is not { } value) return;
            int separator = value.IndexOf(" · ", StringComparison.Ordinal);
            if (separator < 0) separator = value.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0 && value.Length <= 80) return;
            pageNotice.Text = value; pageNotice.IsVisible = true;
            status.Text = separator > 0 ? value[..separator] : T("DetailsAbove");
        };
        Grid.SetColumn(status, 2); footer.Children.Add(status); Grid.SetRow(footer, 6); root.Children.Add(footer);
        Content = root;

        keyword.Text = query;
        keyword.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ExecuteAsync(ApplySearchAsync); } };
        view.SelectionChanged += (_, _) => { if (!rebuilding && active) { browsePage = 1; _ = RefreshAfterGameChangeAsync(); } };
        sort.SelectionChanged += (_, _) => { if (!rebuilding && active) { browsePage = 1; _ = RefreshAfterGameChangeAsync(); } };
        source.SelectionChanged += (_, _) =>
        {
            if (rebuilding) return;
            rebuilding = true; UpdateSortOptions(); rebuilding = false;
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
        body.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(260);
        body.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 12);
        body.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        body.Children[0].IsVisible = !narrow || listOnly;
        detail.IsVisible = !narrow || !listOnly;
        Grid.SetColumn(detail, narrow ? 0 : 2);
        backToList.IsVisible = narrow;
        title.Margin = new Thickness(narrow ? 8 : 0, 0, 0, 0); title.MaxLines = narrow ? 1 : 2;
        bool compact = Bounds.Width is > 0 and < 720;
        Grid.SetRow(keyword, compact ? 1 : 0); Grid.SetColumn(keyword, compact ? 0 : 1); Grid.SetColumnSpan(keyword, compact ? 3 : 1);
        keyword.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0);
        Grid.SetColumn(sort, compact ? 1 : 2); Grid.SetColumn(refresh, 3); Grid.SetRow(refresh, compact ? 1 : 0);
        activity.IsVisible = !compact;
    }

    private void UpdatePaging()
    {
        previous.IsEnabled = !listLoading && view.SelectedIndex == 0 && browsePage > 1;
        next.IsEnabled = !listLoading && view.SelectedIndex == 0 && hasNext;
        pageNumber.Text = view.SelectedIndex == 0 ? string.Format(T("PageNumber"), browsePage) : "";
    }
}
