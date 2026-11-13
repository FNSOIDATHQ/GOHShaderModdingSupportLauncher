using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private readonly ModPageStatus pageStatus = new();
    private Border statusDetails = null!;
    private StackPanel statusEntries = null!;
    private Button statusToggle = null!;
    private readonly Dictionary<string, Func<Task>> statusRetries = [];

    private ModPageScope StatusScope(string document = "") => new(main.SubscriptionGameDirectory, SelectedProvider, selected?.Id, document);

    private static string ContentLabel<TContent>(ContentState<TContent> state) where TContent : class =>
        T(state.Label) + (state.Address is { } address ? " · " + address.AbsolutePath : "");

    private void ResetStatus()
    {
        pageStatus.Reset(); statusRetries.Clear();
        if (statusDetails is not null) statusDetails.IsVisible = false;
        RenderStatus();
    }

    private ModPageOperation BeginStatus(string key, string phase, string document = "", string message = "")
    {
        statusRetries.Remove(key);
        var operation = pageStatus.Begin(key, StatusScope(document), phase, message);
        RenderStatus();
        return operation;
    }

    private void EndStatus(ModPageOperation operation, ModPageOperationState state, string message = "", string phase = "Completed", Func<Task>? retry = null)
    {
        if (!pageStatus.Update(operation, phase, message, state)) return;
        if (retry is null) statusRetries.Remove(operation.Key); else statusRetries[operation.Key] = retry;
        RenderStatus();
    }

    private void ReportNotice(string message, bool failure = false, string key = "action", Func<Task>? retry = null, bool attention = false)
    {
        var operation = BeginStatus(key, "Completed");
        EndStatus(operation, failure ? ModPageOperationState.Failed : attention ? ModPageOperationState.Warning : ModPageOperationState.Succeeded, message, retry: retry);
    }

    private void ReportResult<T>(ModPageOperation operation, ModSourceResult<T> result, Func<Task>? retry = null)
    {
        retry ??= ApplySearchAsync;
        string message = ReadStatus(result, retry);
        if (result.CacheWarning is { Length: > 0 } warning) message += " · " + warning;
        EndStatus(operation, result.IsSuccess && !result.IsStale
            ? result.CacheWarning is null ? ModPageOperationState.Succeeded : ModPageOperationState.Warning
            : result.Content is null ? ModPageOperationState.Failed : ModPageOperationState.Warning, message,
            retry: result.IsSuccess && !result.IsStale ? null : retry);
    }

    private void RenderStatus()
    {
        if (status is null || disposed) return;
        var entries = pageStatus.Operations.ToArray();
        int running = entries.Count(item => item.State == ModPageOperationState.Running);
        int issues = entries.Count(item => item.State is ModPageOperationState.Failed or ModPageOperationState.Warning);
        status.Text = running > 0 ? string.Format(T(issues > 0 ? "StatusRunningIssues" : "StatusRunning"), running, issues)
            : issues > 0 ? string.Format(T("StatusIssues"), issues)
            : entries.LastOrDefault(item => item.Key == "action" && item.State == ModPageOperationState.Succeeded)?.Message
                ?? (entries.Any(item => item.State == ModPageOperationState.Succeeded) ? T("StatusCompleted") : T("Ready"));
        ToolTip.SetTip(statusToggle, status.Text + " · " + T("StatusDetails"));
        if (statusDetails.IsVisible) RenderStatusDetails();
    }

    private void RenderStatusDetails()
    {
        statusEntries.Children.Clear();
        foreach (var item in pageStatus.Operations.OrderByDescending(item => item.State is ModPageOperationState.Failed or ModPageOperationState.Warning)
            .ThenByDescending(item => item.Id))
        {
            var row = Column(); row.Spacing = 3;
            row.Children.Add(Text(string.Join(" · ", new[] { item.Scope.Provider, item.Scope.Mod?.RemoteId,
                item.Scope.Document, T("StatusPhase_" + item.Phase) }.Where(value => !string.IsNullOrEmpty(value))), 12));
            row.Children.Add(Text(item.Message.Length > 0 ? item.Message : T("StatusState_" + item.State), 12));
            if (item.Scope.Game is { } game) row.Children.Add(Text(T("GameScope") + " " + game, 11));
            if (statusRetries.TryGetValue(item.Key, out var retry))
                row.Children.Add(Action(T("RetryContent"), retry, independent: true));
            statusEntries.Children.Add(row);
        }
        if (statusEntries.Children.Count == 0) statusEntries.Children.Add(Text(T("Ready")));
    }

    private void BuildStatusDetails(Grid root)
    {
        statusEntries = Column();
        statusDetails = new Border { Name = "subscriptionStatusDetails", IsVisible = false, Padding = new Thickness(10),
            BorderThickness = new Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#20242C")), ZIndex = 1,
            MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new ScrollViewer { Content = statusEntries, MaxHeight = 160,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        Grid.SetRow(statusDetails, 5); root.Children.Add(statusDetails);
    }
}
