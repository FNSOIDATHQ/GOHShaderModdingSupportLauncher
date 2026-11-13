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
        current.FileNavigationPosition = fileNavigationScroll.Offset;
        if (fileImageExpanded || !fileContent.IsVisible || renderedFileKey is null) return;
        current.FilePosition = fileScroll.Offset;
        current.FilePositions[renderedFileKey] = fileScroll.Offset;
    }

    private void RestoreFilePosition(Vector position)
    {
        var expected = session;
        int generation = fileGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed || session != expected || generation != fileGeneration || !fileContent.IsVisible || fileImageExpanded) return;
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
        var navigationPosition = renderedFileSession == current ? fileNavigationScroll.Offset : current.FileNavigationPosition;
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
        { current.File = entries.FirstOrDefault()?.Key; fileImageExpanded = false; }
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
            string caption = file?.Name ?? entry.Group!.Title;
            string metadata = file is null ? T("ReleaseWithoutFiles") : FileMetadata(file);
            if (file is not null && notices.FirstOrDefault(item => item.File.Id == file.Id && item.File.Revision == file.Revision) is { } notice)
                metadata += " · " + T(notice.Kind == FileUpdateKind.NewFile ? "NewFile" : "ChangedFile");
            var label = Column(); label.Spacing = 2; label.Children.Add(Text(caption)); label.Children.Add(Text(metadata, 12));
            var button = Action(caption, () => { SelectFile(entry.Key); return Task.CompletedTask; }, independent: true);
            button.Tag = entry.Key;
            button.Content = label; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (entry.Key == current.File) button.FontWeight = FontWeight.Bold;
            if (file is not null)
            {
                bool available = snapshot?.Files.Any(item => item.Id == file.Id && item.Revision == file.Revision && item.CanDownload) == true;
                var choice = new CheckBox { Name = "subscriptionFileChoice", VerticalAlignment = VerticalAlignment.Top,
                    IsEnabled = available && subscription?.State is not (SubscriptionState.RemovalPending or SubscriptionState.RemovalFailed),
                    IsChecked = available && chosen.Contains((file.Id, file.Revision)) };
                ToolTip.SetTip(choice, T("SelectForDownload") + " " + file.Name);
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
        UpdateFileSelection(); RenderSelectedFile();
        RestorePosition(fileNavigationScroll, navigationPosition);
    }

    private static string FileMetadata(RemoteModFile file) => string.Join(" · ", new[]
        { (file.PublishedAt ?? file.UpdatedAt)?.ToLocalTime().ToString("d"), FormatBytes(file.SizeBytes) }.Where(value => !string.IsNullOrEmpty(value)));

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
        CancelRefresh(); CancelTranslation();
        SaveFileReadingPosition(); fileImageExpanded = false; current.File = key; readerGeneration++;
        RenderFiles(current.Mod, DetailToken);
    }

    private void RenderSelectedFile()
    {
        if (session is not { } current) return;
        if (fileImageExpanded && renderedFileSession == current && renderedFileKey == current.File) return;
        fileGeneration++; renderedFileSession = current; renderedFileKey = current.File; fileImageExpanded = false;
        fileReader.Children.Clear();
        if (fileEntries.FirstOrDefault(entry => entry.Key == current.File) is not { } entry) { RefreshTasks(); return; }
        var file = entry.File;
        var group = entry.Group;
        var fileDocument = file is null ? new ModReadingDocument() : ReadingDocument(file.Document, file.Description, file.BrowserUrl ?? file.PageUrl);
        var groupDocument = group is null ? new ModReadingDocument() : ReadingDocument(group.Document, group.Description, group.PageUrl);
        if (details.SelectedIndex == 1) PrepareTranslationSource(fileDocument, groupDocument);
        fileReader.Children.Add(Text(file?.Name ?? group!.Title, 20));
        if (file is not null)
        {
            fileReader.Children.Add(Text(FileMetadata(file), 12));
            if (file.IsSourceSnapshot) fileReader.Children.Add(Text(T("SourceSnapshot")));
            if (file.DownloadOnly) fileReader.Children.Add(Text(T("GitHubDownloadOnly")));
            if (string.IsNullOrWhiteSpace(file.Description) && (!ModDocumentParser.HasDocument(file.Document) || file.Document!.Blocks.Length == 0))
                fileReader.Children.Add(Text(T("NoFileDescription")));
            else AddDocument(fileReader, fileDocument);
            bool present = snapshot?.Files.Any(item => item.Id == file.Id && item.Revision == file.Revision) == true;
            if (!present) fileReader.Children.Add(Text(T("FileUnavailable")));
            else if (!file.CanDownload)
            {
                fileReader.Children.Add(Text(T("DownloadUnavailable")));
                if (file.DownloadUnavailableReason.Length > 0) fileReader.Children.Add(Text(file.DownloadUnavailableReason));
            }
            AddFileAcknowledgements(current.Mod, file);
        }
        if (group is not null)
        {
            fileReader.Children.Add(Text(T("SharedVersionNotes") + " · " + group.Title, 16));
            if (group.PublishedAt is { } date) fileReader.Children.Add(Text(date.ToLocalTime().ToString("g"), 12));
            if (file is null) fileReader.Children.Add(Text(T("ReleaseWithoutFiles")));
            AddDocument(fileReader, groupDocument);
            if (group.InstallationInstructions.Length > 0) fileReader.Children.Add(Text(T("InstallationInOverview")));
        }
        var original = file?.BrowserUrl ?? group?.PageUrl ?? file?.PageUrl;
        if (original is not null) fileReader.Children.Add(Action(T("Original"), () => { OpenUrl(original); return Task.CompletedTask; }, independent: true));
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
