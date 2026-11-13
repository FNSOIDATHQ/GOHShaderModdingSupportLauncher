using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private sealed record ReadingContext(string Kind, Uri Url, string? GalleryKey = null);
    private sealed record ExpandedImage(ModImage Image, ReadingContext? ReturnContext);
    private Button original = null!, linkBack = null!;
    private WrapPanel detailActions = null!, verificationActions = null!;
    private Uri? navigationOriginal;
    private Action<Uri> openExternalPage = OpenUrl;
    private ReadingContext? documentContext;
    private readonly Dictionary<Control, (string[] Anchors, ReadingContext? Context)> documentAnchors = [];

    // An explicit browser action is deliberately separate from ordinary link navigation.
    private void OpenOriginal()
    {
        if (OriginalAddress is { } url) openExternalPage(url);
    }

    private Uri? OriginalAddress => navigationOriginal ?? session?.Context?.Url ?? (verificationBanner.IsVisible ? CurrentVerificationOrigin : null);

    private void UpdateOriginal()
    {
        if (original is null) return;
        Uri? address = OriginalAddress;
        original.IsEnabled = address is not null && ModDocumentParser.IsLink(address);
        ToolTip.SetTip(original, original.IsEnabled ? address!.AbsoluteUri : T("OriginalUnavailable"));
        // Keep the one control reachable when verification started before a Mod was opened.
        var host = verificationBanner.IsVisible && !detail.IsVisible ? verificationActions : detailActions;
        if (original.Parent != host)
        {
            (original.Parent as Panel)?.Children.Remove(original);
            host.Children.Add(original);
        }
        linkBack.IsEnabled = linkHistory.Count > 0;
    }

    private void SetReadingContext(ReadingContext? context)
    {
        navigationOriginal = null;
        if (session is not null) session.Context = context;
        UpdateOriginal();
    }

    private ReadingContext? DescriptionContext() => session is { } current ? new(current.Article is null ? "overview" : "article", current.Article ?? current.Mod.PageUrl) : null;
    private ReadingContext? FileContext() => fileEntries.FirstOrDefault(entry => entry.Key == session?.File) is { } entry &&
        (entry.File?.BrowserUrl ?? entry.File?.PageUrl ?? entry.Group?.PageUrl) is { } url ? new("file", url) : null;

    private void SelectVisibleContext()
    {
        if (operation.IsVisible) return;
        var expanded = details.SelectedIndex == 0 ? session?.DescriptionImage : session?.FileImage;
        SetReadingContext(expanded is not null ? new("image", expanded.Image.PageUrl ?? expanded.Image.ImageUrl) :
            details.SelectedIndex == 0 ? DescriptionContext() : FileContext());
    }

    private void BindReadingContext(Control control, ReadingContext? context)
    {
        if (context is null) return;
        var owner = session;
        void Select() { if (session == owner && !operation.IsVisible) { CancelLinkNavigation(); SetReadingContext(context); } }
        control.AddHandler(PointerPressedEvent, (_, _) => Select(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        control.GotFocus += (_, args) => { if (args.Source == control) Select(); };
    }

    private async Task FollowLinkAsync(Uri address)
    {
        if (!ModDocumentParser.IsLink(address) || disposed) return;
        if (operationBusy) { ReportNotice(T("RefreshAfterOperation")); return; }
        CancelLinkNavigation();
        if (TryDocumentAnchor(address)) return;
        if (FindMappedImage(address) is { } image)
        {
            RememberLinkLocation();
            await ShowImageAsync(image, DetailToken);
            UpdateOriginal(); return;
        }
        var targetAddress = session?.ImagePages.Values.SelectMany(page => page.Result?.Content?.Items ?? [])
            .FirstOrDefault(image => image.ThumbnailUrl == address)?.PageUrl ?? address;
        if (!ModPageRouter.TryResolveSupportedPage(targetAddress, out var route)) { openExternalPage(address); return; }
        if (route!.Kind == ModPageKind.Asset && !IsKnownAsset(address)) { openExternalPage(address); return; }
        if (runtime is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        linkCancellation = cancellation;
        int generation = linkGeneration;
        string? game = main.SubscriptionGameDirectory;
        bool Current() => !disposed && !cancellation.IsCancellationRequested && generation == linkGeneration && game == main.SubscriptionGameDirectory;
        var progress = BeginStatus("link", "Fetch", T("OpeningLink"));
        try
        {
            // Prepare without touching the current reader. Even sources that ignore cancellation
            // must pass the ownership check before their response can become visible.
            var prepared = await PrepareLinkAsync(route!, cancellation.Token);
            if (!Current()) return;
            RememberLinkLocation();
            linkCancellation = null;
            ActivateLink(prepared);
            var work = new List<Task>();
            if (prepared.Image is { } media) work.Add(ShowImageAsync(media, DetailToken));
            else if (address.Fragment.Length > 0) QueueDocumentAnchor(address);
            if (session is { } current) work.Add(EnsureDescriptionNavigationAsync(current, DetailToken));
            await Task.WhenAll(work);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { EndStatus(progress, ModPageOperationState.Cancelled); }
        catch (Exception ex)
        {
            if (!Current()) return;
            navigationOriginal = targetAddress; UpdateOriginal();
            string message = ex is ModSourceException sourceError ? ReadStatus(new ModSourceResult<RemoteMod>(null, null, sourceError.Failure), () => FollowLinkAsync(address)) : T("Failed") + ": " + ex.Message;
            EndStatus(progress, ModPageOperationState.Failed, T("LinkFailed") + " · " + message, retry: () => FollowLinkAsync(address));
        }
        finally
        {
            if (ReferenceEquals(linkCancellation, cancellation)) linkCancellation = null;
            if (!Current()) EndStatus(progress, ModPageOperationState.Cancelled);
            else if (navigationOriginal is null) EndStatus(progress, ModPageOperationState.Succeeded);
        }
    }

    private ModImage? FindMappedImage(Uri url)
    {
        if (session is not { } current) return null;
        IEnumerable<ModImage> known = details.SelectedIndex == 0
            ? (renderedDocument is null ? [] : ModDocumentParser.Images(renderedDocument)).Concat(current.Article is null ? current.Mod.Images : current.Bodies.GetValueOrDefault(current.Article)?.Result?.Content?.Images ?? [])
            : fileEntries.Where(entry => entry.Key == current.File).SelectMany(entry =>
                (entry.File?.Document is { } file ? ModDocumentParser.Images(file) : []).Concat(entry.Group?.Document is { } group ? ModDocumentParser.Images(group) : []));
        // Thumbnail-only references are not original images and require their detail reader.
        return known.FirstOrDefault(image => image.ImageUrl == url || image.ThumbnailUrl == url);
    }

    private bool TryDocumentAnchor(Uri address, bool remember = true)
    {
        if (address.Fragment.Length == 0) return false;
        string anchor = Uri.UnescapeDataString(address.Fragment[1..]);
        var root = details.SelectedIndex == 0 ? reading : fileReader;
        var match = documentAnchors.FirstOrDefault(pair =>
            (pair.Key == root || pair.Key.GetLogicalAncestors().Contains(root)) &&
            pair.Value.Context is { } context && SamePage(context.Url, address) &&
            pair.Value.Anchors.Any(id => id == anchor || "user-content-" + id == anchor));
        if (match.Key is null) return false;
        if (remember) RememberLinkLocation();
        SetReadingContext(match.Value.Context); match.Key.BringIntoView(); return true;
    }

    private void QueueDocumentAnchor(Uri address)
    {
        var expected = session;
        int generation = readerGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (expected != session || generation != readerGeneration || disposed) return;
            // The navigation already owns a history entry.
            TryDocumentAnchor(address, remember: false);
            UpdateOriginal();
        }, DispatcherPriority.Loaded);
    }

    private static bool SamePage(Uri left, Uri right) => new UriBuilder(left) { Fragment = "" }.Uri == new UriBuilder(right) { Fragment = "" }.Uri;
}
