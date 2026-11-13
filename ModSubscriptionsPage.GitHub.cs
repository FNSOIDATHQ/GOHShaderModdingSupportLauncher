using Avalonia;
using Avalonia.Controls;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private TextBox githubTopics = null!, githubRepository = null!;
    private ComboBox githubMatch = null!;
    private TextBlock githubSettingsStatus = null!;
    private string SelectedProvider => source.SelectedIndex switch { 1 => "github", 2 => "mowportal", _ => "moddb" };

    private IModSource SourceForProvider(string provider) => runtime!.ResolveSource(provider)
        ?? throw new InvalidOperationException(T("UnsupportedSource"));

    private void UpdateSortOptions()
    {
        int selection = Math.Max(0, sort.SelectedIndex);
        sort.ItemsSource = new object[] { T("Updated"),
            new ComboBoxItem { Content = T("Newest"), IsEnabled = source.SelectedIndex == 0 },
            new ComboBoxItem { Content = T("Popular"), IsEnabled = source.SelectedIndex != 2 },
            new ComboBoxItem { Content = T("Name"), IsEnabled = source.SelectedIndex != 2 } };
        sort.SelectedIndex = source.SelectedIndex == 2 || source.SelectedIndex == 1 && selection == 1 ? 0 : selection;
    }

    private Control BuildGitHubOptions()
    {
        var panel = Column(); panel.Margin = new Thickness(0, 8, 0, 0);
        panel.Name = "subscriptionGitHubOptions";
        var topics = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        topics.Children.Add(Text(T("GitHubTopics")));
        githubTopics = new TextBox { Name = "subscriptionGitHubTopics", Width = 255, MaxLength = 1024,
            PlaceholderText = "gates-of-hell" };
        topics.Children.Add(githubTopics);
        githubMatch = new ComboBox { Name = "subscriptionGitHubMatch", ItemsSource = new[] { T("MatchAny"), T("MatchAll") }, SelectedIndex = 0 };
        topics.Children.Add(githubMatch);
        topics.Children.Add(Action(T("SaveTopics"), SaveGitHubOptionsAsync));
        panel.Children.Add(topics);
        panel.Children.Add(Text(T("TopicsBoundary")));
        githubSettingsStatus = Text(""); githubSettingsStatus.IsVisible = false;
        panel.Children.Add(githubSettingsStatus);
        var repository = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        githubRepository = new TextBox { Name = "subscriptionGitHubRepository", Width = 380, MaxLength = 512,
            PlaceholderText = "https://github.com/owner/repository" };
        repository.Children.Add(githubRepository);
        repository.Children.Add(Action(T("AddRepository"), AddGitHubRepositoryAsync));
        panel.Children.Add(repository);
        return panel;
    }

    private void LoadGitHubOptions()
    {
        try
        {
            var settings = runtime!.GitHubSettings.Snapshot();
            githubTopics.Text = string.Join(", ", settings.Topics);
            githubMatch.SelectedIndex = settings.MatchAll ? 1 : 0;
            githubSettingsStatus.IsVisible = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or ArgumentException)
        {
            githubTopics.Text = ""; githubMatch.SelectedIndex = 0;
            githubSettingsStatus.Text = T("GitHubSettingsInvalid");
            githubSettingsStatus.IsVisible = true;
        }
    }

    private async Task SaveGitHubOptionsAsync()
    {
        string[] topics = (githubTopics.Text ?? "").Split([',', ';', ' ', '\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool matchAll = githubMatch.SelectedIndex == 1;
        await Task.Run(() => runtime!.GitHubSettings.Save(topics, matchAll), lifetime.Token);
        LoadGitHubOptions(); browsePage = 1;
        CancelRefresh(); listCancellation?.Cancel();
        await ApplySearchAsync();
    }

    private async Task AddGitHubRepositoryAsync()
    {
        if (!Uri.TryCreate(githubRepository.Text?.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme != "https" || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            url.UserInfo.Length != 0 || !url.IsDefaultPort || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath.Trim('/').Split('/').Length != 2)
        { ReportNotice(T("RepositoryUrlRequired")); return; }
        CancelRefresh();
        listCancellation?.Cancel(); listCancellation?.Dispose();
        listCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = listCancellation.Token;
        int generation = ++listGeneration;
        listLoading = false; UpdatePaging();
        var progress = BeginStatus("repository", "Fetch", T("AddRepository"));
        try
        {
            var response = await Task.Run(() => SourceForProvider("github").GetModAsync(url, cancellationToken: token), token);
            if (generation != listGeneration || disposed) return;
            ReportResult(progress, response, AddGitHubRepositoryAsync);
            if (response.Content is not { } mod) return;
            await Task.Run(() => runtime!.Store.SaveMod(mod), lifetime.Token);
            await runtime!.RefreshLibraryAsync(lifetime.Token);
            if (generation != listGeneration || disposed) return;
            if (view.SelectedIndex == 0)
            {
                browseItems = [mod, .. browseItems.Where(item => item.Id != mod.Id)];
                RenderModList(browseItems);
            }
            var content = ReadingState(mod);
            content.ManualRepository = true;
            content.Mod = mod; content.Overview.Result = response; content.Overview.Attempted = true;
            await OpenModCoreAsync(mod, refresh: false);
            if (generation != listGeneration || disposed || selected?.Id != mod.Id) return;
            var completed = BeginStatus("repository", "Completed", T("AddRepository")); ReportResult(completed, response, AddGitHubRepositoryAsync);
            runtime.NotifyChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { EndStatus(progress, ModPageOperationState.Cancelled); }
        catch (Exception ex) { EndStatus(progress, ModPageOperationState.Failed, T("Failed") + ": " + ex.Message, retry: AddGitHubRepositoryAsync); }
    }

    private void ShowSummary(RemoteMod mod)
    {
        if (mod.Id.Provider == "github")
        {
            summary.Children.Add(Text(T("ProjectType") + ": " + T("Kind_" + mod.Kind)));
            summary.Children.Add(Text(T("GitHubInstallHint")));
        }
        AddDocument(summary, renderedDocument ?? ReadingDocument(mod.Document, mod.Summary, mod.PageUrl));
    }
}
