using Avalonia.Controls;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private sealed record InstallationPickerState(string Game, Guid SubscriptionId, Guid DownloadId, Guid? InstallationId);
    private InstallationPickerState? installationPicker;
    private string? renderedInstallationPicker;
    private sealed record InstalledOriginLocation(string Game, Guid SubscriptionId, Guid InstallationId, ModSourceId Source, string FileId);
    private InstalledOriginLocation? installedOriginLocation;

    private InstalledOriginLocation? CurrentInstalledOrigin() => installedOriginLocation is { } location &&
        SameGame(location.Game) && selected?.Id == location.Source && session is { } current && current.Mod.Id == location.Source &&
        current.File == "file:" + location.FileId ? location : null;

    // An installed Mod already has an identity and saved author content. Opening its
    // subscription must work without discovery requests or a successful site refresh.
    internal Task OpenInstalledSubscriptionAsync(Guid subscriptionId, Guid installationId) =>
        OpenInstalledSubscriptionCoreAsync(subscriptionId, installationId);

    private async Task<bool> OpenInstalledSubscriptionCoreAsync(Guid subscriptionId, Guid installationId)
    {
        if (disposed) return false;
        string? game = main.SubscriptionGameDirectory;
        if (game is null) return false;
        CancelLinkNavigation();
        int navigation = linkGeneration;
        CancelRefresh(); listCancellation?.Cancel(); listGeneration++; listLoading = false;
        runtime ??= main.SubscriptionRuntime;
        await runtime.Ready.WaitAsync(lifetime.Token);
        if (!SameGame(game) || navigation != linkGeneration) return false;
        if (!active)
        {
            active = true;
            runtime.Changed += RuntimeChanged;
            runtime.CachesClearing += CachesClearing;
            runtime.Verification.Changed += RuntimeChanged;
            timer.Start();
            await LoadTranslationOptionsAsync();
            if (!SameGame(game) || navigation != linkGeneration) return false;
            LoadGitHubOptions();
        }
        var library = runtime.Library;
        var subscription = library.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId &&
            item.GameDirectory.Equals(game, SubscriptionPaths.Comparison));
        var installation = subscription?.Installations.SingleOrDefault(item => item.Id == installationId &&
            item.State == InstallationState.Installed);
        var download = subscription?.Downloads.SingleOrDefault(item => item.Id == installation?.DownloadId);
        var mod = subscription is null ? null : library.Mods.SingleOrDefault(item => item.Id == subscription.Source);
        if (installation is null || download is null || mod is null ||
            mod.Id.Provider is not ("moddb" or "github" or "mowportal"))
        { ReportNotice(T("InstallationUnavailable")); return false; }

        // Prefer the installed download's saved revision when a later remote revision
        // shares its file ID. This navigation is not a fresh file availability check.
        var savedFiles = new ModFileSnapshot(new[] { download.File }.Concat(subscription!.Files.KnownFiles)
            .Concat(subscription.Downloads.Select(item => item.File)).DistinctBy(item => item.Id).ToArray())
        { Groups = subscription.Files.Groups, PageRevision = subscription.Files.PageRevision };
        var prepared = new PreparedLink(new(mod.Id.Provider, ModPageKind.File, download.File.PageUrl, mod.PageUrl))
        {
            Overview = new(mod, subscription.Files.LastSuccessfulCheck, IsStale: true),
            Files = new(savedFiles, subscription.Files.LastSuccessfulCheck, IsStale: true),
            FileKey = "file:" + download.File.Id,
            Context = new("file", download.File.BrowserUrl ?? download.File.PageUrl)
        };
        cacheReturn = null;
        CancelLinkNavigation();
        if (session is not null) RememberLinkLocation();
        SwitchLinkSource(mod.Id.Provider == "github" ? 1 : mod.Id.Provider == "mowportal" ? 2 : 0, 1, 0, "");
        appliedKeyword = ""; browsePage = 1; hasNext = false; listFresh = true;
        ActivateLink(prepared);
        installedOriginLocation = new(game, subscription.Id, installation.Id, mod.Id, download.File.Id);
        RefreshVerification();
        return true;
    }

    private Task ViewInstalledModsAsync(Guid subscriptionId, Guid downloadId)
    {
        var subscription = runtime?.Library.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId);
        if (subscription is null || !SameGame(subscription.GameDirectory)) return Task.CompletedTask;
        var installations = subscription.Installations.Where(item => item.DownloadId == downloadId &&
            item.State == InstallationState.Installed).ToArray();
        if (installations.Length == 0)
        { ReportNotice(T("InstallationUnavailable")); return Task.CompletedTask; }
        if (installations.Length == 1) return OpenInstallationInManagerAsync(subscriptionId, installations[0].Id);

        installationPicker = new(subscription.GameDirectory, subscriptionId, downloadId, installations[0].Id);
        renderedInstallationPicker = null;
        RefreshInstallationPicker(activate: true);
        return Task.CompletedTask;
    }

    private void RestoreInstallationPicker(InstallationPickerState picker)
    {
        if (!SameGame(picker.Game)) return;
        installationPicker = picker; renderedInstallationPicker = null;
        RefreshInstallationPicker(activate: true);
    }

    private void RefreshInstallationPicker(bool activate = false)
    {
        if (installationPicker is not { } picker || !SameGame(picker.Game) ||
            !activate && workflowView != WorkflowView.LocateInstallation) return;
        var subscription = runtime?.Library.Subscriptions.SingleOrDefault(item => item.Id == picker.SubscriptionId &&
            item.GameDirectory.Equals(picker.Game, SubscriptionPaths.Comparison));
        var installations = subscription?.Installations.Where(item => item.DownloadId == picker.DownloadId &&
            item.State == InstallationState.Installed).ToArray() ?? [];
        string signature = LocalizedStrings.Instance.Culture.Name + "|" + string.Join("|", installations.Select(item => item.Id + ":" + item.DirectoryPath));
        if (!activate && signature == renderedInstallationPicker) return;
        renderedInstallationPicker = signature;
        // A removed selection must not silently become a different installed directory.
        if (!installations.Any(item => item.Id == picker.InstallationId))
            installationPicker = picker = picker with { InstallationId = null };
        var panel = OperationPanel(T("ChooseInstallation"));
        var choices = new ComboBox
        {
            Name = "subscriptionManagerInstallationChoice",
            ItemsSource = installations.Select(item => item.DirectoryPath).ToArray(),
            SelectedIndex = Array.FindIndex(installations, item => item.Id == picker.InstallationId),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        panel.Children.Add(choices);
        if (installations.Length == 0) panel.Children.Add(Text(T("InstallationUnavailable")));
        int generation = 0;
        var locate = Action(T("ViewManager"), () => workflowView == WorkflowView.LocateInstallation &&
            CurrentOperation(generation, picker.Game) && choices.SelectedIndex is var index && index >= 0 && index < installations.Length
                ? OpenInstallationInManagerAsync(picker.SubscriptionId, installations[index].Id) : Task.CompletedTask, independent: true);
        locate.Name = "subscriptionLocateInstallation"; locate.IsEnabled = choices.SelectedIndex >= 0;
        choices.SelectionChanged += (_, _) =>
        {
            if (workflowView != WorkflowView.LocateInstallation || !SameWorkflow(generation, picker.Game)) return;
            int index = choices.SelectedIndex;
            installationPicker = picker with { InstallationId = index >= 0 && index < installations.Length ? installations[index].Id : null };
            locate.IsEnabled = index >= 0;
        };
        panel.Children.Add(locate);
        workflowView = WorkflowView.LocateInstallation; workflowSubscription = picker.SubscriptionId; workflowDownload = picker.DownloadId;
        _ = WorkflowToken;
        SetOperationContent(panel, activate); generation = operationGeneration;
    }

    private Task OpenInstallationInManagerAsync(Guid subscriptionId, Guid installationId)
    {
        var subscription = runtime?.Library.Subscriptions.SingleOrDefault(item => item.Id == subscriptionId);
        var installation = subscription?.Installations.SingleOrDefault(item => item.Id == installationId &&
            item.State == InstallationState.Installed);
        if (subscription is null || installation is null || !SameGame(subscription.GameDirectory))
        { ReportNotice(T("InstallationUnavailable")); return Task.CompletedTask; }
        main.OpenSubscriptionModManager(installation.DirectoryPath);
        return Task.CompletedTask;
    }
}
