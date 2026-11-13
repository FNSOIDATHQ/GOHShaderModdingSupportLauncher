using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private void UpdateSearchScope()
    {
        keyword.PlaceholderText = T(source.SelectedIndex == 2 ? "MowSearchPlaceholder" : "SearchPlaceholder");
        Avalonia.Controls.ToolTip.SetTip(keyword, source.SelectedIndex == 2 ? T("MowSearchScope") : null);
    }

    private void AddPageNotice(RemoteMod mod, CancellationToken token)
    {
        if (Subscription(mod) is not { } subscription || ModUpdateCheckService.GetPageNotice(subscription) is not { } notice) return;
        var panel = Column();
        panel.Children.Add(Text(T("PageOrLinkChanged")));
        var actions = Row();
        actions.Children.Add(Action(T("Original"), () => { OpenUrl(mod.PageUrl); return Task.CompletedTask; }));
        foreach (var state in new[] { FileNoticeState.Read, FileNoticeState.Ignored })
            actions.Children.Add(Action(T(state == FileNoticeState.Read ? "MarkRead" : "Ignore"), async () =>
            {
                await Task.Run(() => runtime!.Updates.AcknowledgePage(subscription.Id, notice.Revision, state), token);
                await runtime!.RefreshLibraryAsync(token);
                token.ThrowIfCancellationRequested();
                runtime.NotifyChanged(); RefreshTracking(); RenderFiles(mod, token);
            }));
        panel.Children.Add(actions);
        fileNotice.Children.Add(panel);
    }
}
