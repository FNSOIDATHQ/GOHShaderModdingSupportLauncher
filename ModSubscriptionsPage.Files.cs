using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private sealed record FileEntry(string Key, RemoteModFile? File, ModFileGroup? Group);
    private FileEntry[] fileEntries = [];
    private ReadingSession? renderedFileSession;
    private string? renderedFileKey;
    private bool fileImageExpanded;
    private int fileGeneration;

    private void SaveFileReadingPosition()
    {
        if (session is not { } current || renderedFileSession != current) return;
        if (fileNavigationScroll.IsVisible) current.FileNavigationPosition = fileNavigationScroll.Offset;
        if (fileImageExpanded || !fileContent.IsVisible || !fileReadingSurface.IsVisible || renderedFileKey is null) return;
        current.FilePosition = fileScroll.Offset;
        current.FilePositions[renderedFileKey] = fileScroll.Offset;
    }

    private void RestoreFilePosition(Vector position)
    {
        var expected = session;
        int generation = fileGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed || session != expected || generation != fileGeneration || !fileContent.IsVisible || !fileReadingSurface.IsVisible || fileImageExpanded) return;
            fileScroll.Offset = position;
        }, DispatcherPriority.Loaded);
    }

    private void RenderFiles(RemoteMod mod, CancellationToken token)
    {
        if (session is not { } current || current.Mod.Id != mod.Id) return;
        SaveFileReadingPosition();
        var chosen = fileChoices.Count > 0 ? fileChoices.Where(pair => pair.Value.IsChecked == true)
            .Select(pair => (pair.Key.Id, pair.Key.Revision)).ToHashSet() : current.Chosen.ToHashSet();
        if (snapshotFresh && snapshot is not null)
        {
            int removed = chosen.RemoveWhere(key => !snapshot.Files.Any(file => (file.Id, file.Revision) == key && file.CanDownload));
            if (removed > 0) ReportNotice(string.Format(T("FileChoicesChanged"), removed), key: "fileChoices", attention: true);
        }
        current.Chosen.Clear(); current.Chosen.UnionWith(chosen);
        var navigationPosition = renderedFileSession == current && fileNavigationScroll.IsVisible ? fileNavigationScroll.Offset : current.FileNavigationPosition;
        files.Children.Clear(); fileChoices.Clear(); fileNotice.Children.Clear();
        AddPageNotice(mod, token);
        var subscription = Subscription(mod);
        var notices = subscription is not null ? ModUpdateCheckService.GetNotices(subscription) : [];
        var displayed = (snapshot?.Files ?? []).Concat(notices.Select(item => item.File))
            .Concat(subscription?.Downloads.Select(item => item.File) ?? []).DistinctBy(file => file.Id).ToArray();
        var groups = ModUpdateCheckService.MergeGroups(subscription?.Files.Groups ?? [], snapshot?.Groups ?? []);
        var entries = new List<FileEntry>();
        foreach (var group in groups)
        {
            var members = displayed.Where(file => file.GroupId == group.Id).ToArray();
            if (snapshot?.Groups.Any(item => item.Id == group.Id) == true && !snapshot.Files.Any(file => file.GroupId == group.Id))
                entries.Add(new("group:" + group.Id, null, group));
            foreach (var file in members) entries.Add(new("file:" + file.Id, file, group));
        }
        entries.AddRange(displayed.Where(file => !entries.Any(entry => entry.File?.Id == file.Id))
            .Select(file => new FileEntry("file:" + file.Id, file, null)));
        fileEntries = entries.ToArray();
        if (!entries.Any(entry => entry.Key == current.File))
        { current.File = entries.FirstOrDefault()?.Key; fileImageExpanded = false; current.FileImage = null; if (details.SelectedIndex == 1) SetReadingContext(FileContext()); }
        string? previousGroup = null;
        foreach (var entry in entries)
        {
            if (entry.File is not null && entry.Group is { } version && version.Id != previousGroup)
            {
                var heading = Text(version.Title, 13); heading.FontWeight = FontWeight.SemiBold;
                files.Children.Add(heading);
            }
            previousGroup = entry.Group?.Id;
            var row = new Grid { ColumnDefinitions = new("Auto,*") };
            var file = entry.File;
            var presentation = file is null ? null : ModFilePresentation.From(file, mod.Id.Provider);
            string caption = presentation?.Title ?? entry.Group!.Title;
            var label = Column(); label.Spacing = 2;
            var name = Text(caption); name.Name = "subscriptionFileName";
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxLines = 1;
            label.Children.Add(name);
            if ((file?.PublishedAt ?? file?.UpdatedAt ?? (file is null ? entry.Group?.PublishedAt : null)) is { } date)
            {
                var dateLabel = Text(date.ToLocalTime().ToString("d"), 12); dateLabel.Name = "subscriptionFileDate"; label.Children.Add(dateLabel);
            }
            if (file?.SizeBytes is { } size)
            {
                var sizeLabel = Text(FormatBytes(size), 12); sizeLabel.Name = "subscriptionFileSize"; label.Children.Add(sizeLabel);
            }
            if (presentation?.Host is { } host)
            {
                var hostLabel = Text(string.Format(T("FileDownloadHost"), host), 12); hostLabel.Name = "subscriptionFileHost"; label.Children.Add(hostLabel);
            }
            if (file is null) label.Children.Add(Text(T("ReleaseWithoutFiles"), 12));
            if (file is not null && notices.FirstOrDefault(item => item.File.Id == file.Id && item.File.Revision == file.Revision) is { } notice)
            {
                var badge = Text(T(notice.Kind == FileUpdateKind.NewFile ? "NewFile" : "ChangedFile"), 12);
                badge.Name = "subscriptionFileNotice"; badge.FontWeight = FontWeight.SemiBold; label.Children.Add(badge);
            }
            var button = new Button();
            button.Click += (_, _) => SelectFile(entry.Key);
            button.Tag = entry.Key;
            button.Content = label; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(button, caption);
            Avalonia.Automation.AutomationProperties.SetName(button, caption);
            if (entry.Key == current.File) button.FontWeight = FontWeight.Bold;
            if (file is not null)
            {
                bool available = snapshot?.Files.Any(item => item.Id == file.Id && item.Revision == file.Revision && item.CanDownload) == true;
                var choice = new CheckBox { Name = "subscriptionFileChoice", VerticalAlignment = VerticalAlignment.Top,
                    IsEnabled = available && subscription?.State is not (SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed),
                    IsChecked = available && chosen.Contains((file.Id, file.Revision)) };
                ToolTip.SetTip(choice, T("SelectForDownload") + " " + caption);
                choice.IsCheckedChanged += (_, _) =>
                {
                    if (choice.IsChecked == true) current.Chosen.Add((file.Id, file.Revision));
                    else current.Chosen.Remove((file.Id, file.Revision));
                    UpdateFileSelection();
                };
                fileChoices.Add(file, choice); row.Children.Add(choice);
            }
            Grid.SetColumn(button, 1); row.Children.Add(button); files.Children.Add(row);
        }
        if (entries.Count == 0 && snapshot is not null) files.Children.Add(Text(T("Empty")));
        UpdateFileSelection(); UpdateFileNavigationLayout(); RenderSelectedFile();
        RestorePosition(fileNavigationScroll, navigationPosition);
    }

    private void UpdateFileSelection()
    {
        int count = fileChoices.Count(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true);
        selectedFileCount.Text = string.Format(T("SelectedFileCount"), count);
        downloadSelected.IsEnabled = count > 0 && !operationBusy;
        downloadSelected.IsVisible = Subscription()?.State is not (SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed);
    }

    private void SelectFile(string key)
    {
        if (session is not { } current || !fileEntries.Any(entry => entry.Key == key)) return;
        CancelLinkNavigation();
        bool restoreFocus = fileNavigationToggle.IsVisible && fileNavigationScroll.IsKeyboardFocusWithin;
        CancelRefresh(); CancelTranslation();
        SaveFileReadingPosition(); fileImageExpanded = false; current.File = key; current.FileNavigationOnly = false; readerGeneration++;
        current.FileImage = null; SetReadingContext(FileContext());
        RenderFiles(current.Mod, DetailToken);
        if (restoreFocus) fileNavigationToggle.Focus(Avalonia.Input.NavigationMethod.Tab);
    }

    private void RenderSelectedFile()
    {
        if (session is not { } current) return;
        if (fileImageExpanded && renderedFileSession == current && renderedFileKey == current.File) return;
        fileGeneration++; renderedFileSession = current; renderedFileKey = current.File; fileImageExpanded = false;
        fileReader.Children.Clear();
        pageStatus.Remove("fileDownload"); RenderStatus();
        if (fileEntries.FirstOrDefault(entry => entry.Key == current.File) is not { } entry) { RefreshTasks(); return; }
        var file = entry.File;
        var group = entry.Group;
        var fileDocument = file is null ? new ModReadingDocument() : ReadingDocument(file.Document, file.Description, file.BrowserUrl ?? file.PageUrl);
        var groupDocument = group is null ? new ModReadingDocument() : ReadingDocument(group.Document, group.Description, group.PageUrl);
        if (details.SelectedIndex == 1) PrepareTranslationSource(fileDocument, groupDocument);
        if (file is not null)
        {
            if (file.IsSourceSnapshot) fileReader.Children.Add(Text(T("SourceSnapshot")));
            if (file.DownloadOnly) fileReader.Children.Add(Text(T("GitHubDownloadOnly")));
            if (string.IsNullOrWhiteSpace(file.Description) && (!ModDocumentParser.HasDocument(file.Document) || file.Document!.Blocks.Length == 0))
                fileReader.Children.Add(Text(T("NoFileDescription")));
            else AddContextDocument(fileReader, fileDocument, FileContext());
            bool present = snapshot?.Files.Any(item => item.Id == file.Id && item.Revision == file.Revision) == true;
            if (!present) fileReader.Children.Add(Text(T("FileUnavailable")));
            else if (!file.CanDownload)
            {
                fileReader.Children.Add(Text(T("DownloadUnavailable")));
                if (!string.IsNullOrWhiteSpace(file.DownloadUnavailableReason))
                {
                    var diagnostic = BeginStatus("fileDownload", "Completed", ModFilePresentation.From(file, current.Mod.Id.Provider).Title);
                    EndStatus(diagnostic, ModPageOperationState.Succeeded, file.DownloadUnavailableReason);
                }
            }
            AddFileAcknowledgements(current.Mod, file);
        }
        if (group is not null)
        {
            var context = new ReadingContext("release", group.PageUrl);
            var heading = Text(T("SharedVersionNotes"), 16); BindReadingContext(heading, context); fileReader.Children.Add(heading);
            if (file is null) fileReader.Children.Add(Text(T("ReleaseWithoutFiles")));
            AddContextDocument(fileReader, groupDocument, context);
            if (group.InstallationInstructions.Length > 0) fileReader.Children.Add(Text(T("InstallationInOverview")));
        }
        if (details.SelectedIndex == 1 && !operation.IsVisible)
        {
            if (current.Context is null || current.Context.Kind == "file") SetReadingContext(FileContext());
            else if (current.Context.Kind == "release") SetReadingContext(group is null ? FileContext() : new("release", group.PageUrl));
        }
        RefreshTasks();
        RestoreFilePosition(current.FilePositions.GetValueOrDefault(entry.Key));
    }

    private void AddFileAcknowledgements(RemoteMod mod, RemoteModFile file)
    {
        if (Subscription(mod) is not { } sub || !ModUpdateCheckService.GetNotices(sub).Any(item => item.File.Id == file.Id && item.File.Revision == file.Revision)) return;
        var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var state in new[] { FileNoticeState.Read, FileNoticeState.Ignored })
            actions.Children.Add(Action(T(state == FileNoticeState.Read ? "MarkRead" : "Ignore"), async () =>
            {
                var expected = session; var token = DetailToken;
                await Task.Run(() => runtime!.Updates.Acknowledge(sub.Id, file.Id, file.Revision, state), token);
                await runtime!.RefreshLibraryAsync(token);
                if (expected is null || !IsCurrent(expected, token)) return;
                runtime.NotifyChanged(); RefreshTracking(); RenderFiles(mod, token);
            }));
        fileReader.Children.Add(actions);
    }
}
