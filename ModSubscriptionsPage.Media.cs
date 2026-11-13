using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private sealed record MediaCacheEntry(ModImagePayload Payload, DateTimeOffset AddedAt);
    private readonly Dictionary<Uri, MediaCacheEntry> mediaCache = [];
    private readonly SemaphoreSlim mediaDecodeGate = new(1, 1);
    private long mediaCacheBytes;
    private sealed class MediaTransfer(CancellationTokenSource cancellation)
    {
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal Task<ModImagePayload> Task = null!;
        internal int Readers;
    }
    private readonly Dictionary<(string Provider, Uri Url), MediaTransfer> mediaTransfers = [];
    private readonly HashSet<Func<Task>> visibleMediaRefresh = [];
    private Func<Task>? expandedMediaRefresh;
    private sealed record PendingImage(ReadingSession Owner, ModImageReference Reference, int Tab, Uri? Article, string? File);
    private PendingImage? pendingImage;

    private bool MediaFresh(Uri url) => mediaCache.TryGetValue(url, out var cached) && DateTimeOffset.UtcNow - cached.AddedAt < TimeSpan.FromMinutes(10);

    private async Task RefreshVisibleMediaAsync()
    {
        await Task.WhenAll(visibleMediaRefresh.ToArray().Select(refresh => refresh()));
        if (pendingImage is not null) await RetryPendingImageAsync();
        if ((imageExpanded || fileImageExpanded) && expandedMediaRefresh is { } refreshExpanded) await refreshExpanded();
    }

    private async Task RetryPendingImageAsync()
    {
        if (pendingImage is not { } pending || !IsCurrent(pending.Owner, DetailToken) || details.SelectedIndex != pending.Tab ||
            (pending.Tab == 0 ? session!.Article != pending.Article : session!.File != pending.File)) return;
        var token = DetailToken;
        int generation = readerGeneration;
        var progress = BeginStatus("imagePage:" + pending.Reference.PageUrl, "Fetch", pending.Reference.Caption);
        var retry = DocumentRetry(pending.Owner, pending.Tab, pending.Article, pending.File);
        try
        {
            var response = await Task.Run(() => SourceForProvider(pending.Owner.Mod.Id.Provider).GetImageAsync(pending.Reference.PageUrl, cancellationToken: token), token);
            if (!IsCurrent(pending.Owner, token) || generation != readerGeneration || pendingImage != pending)
            { EndStatus(progress, ModPageOperationState.Cancelled); return; }
            LogMediaParse(pending.Reference.PageUrl, ModMediaOperation.ImagePage, response);
            ReportResult(progress, response, retry);
            if (response.Failure?.Kind == ModSourceError.VerificationRequired) verificationRetryAction = RetryPendingImageAsync;
            if (response.Content is { } full) { pendingImage = null; await ShowImageAsync(full, token); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { EndStatus(progress, ModPageOperationState.Cancelled); }
        catch (Exception ex)
        {
            if (IsCurrent(pending.Owner, token) && generation == readerGeneration)
                EndStatus(progress, ModPageOperationState.Failed, ImageFailure(ex, retry), retry: retry);
        }
    }

    private async Task<ModImagePayload> ReadMediaPayloadAsync(Uri url, string provider, ModMediaOperation operation, CancellationToken token)
    {
        var key = (provider, url);
        if (!mediaTransfers.TryGetValue(key, out var transfer))
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
            transfer = new(cancellation);
            var client = new ModImageClient(imageClient, runtime!.PrepareVerificationRequest,
                (uri, code) => runtime!.RequireVerification(uri, code), LogMedia);
            transfer.Task = client.GetAsync(url, provider, operation, cancellation.Token);
            mediaTransfers.Add(key, transfer);
        }
        transfer.Readers++;
        try { return await transfer.Task.WaitAsync(token); }
        finally
        {
            if (--transfer.Readers == 0)
            {
                if (mediaTransfers.TryGetValue(key, out var active) && active == transfer) mediaTransfers.Remove(key);
                if (!transfer.Task.IsCompleted) transfer.Cancellation.Cancel();
                // Observe a cancelled last reader's transfer even if its transport ignores cancellation.
                _ = transfer.Task.ContinueWith(task => { _ = task.Exception; transfer.Cancellation.Dispose(); }, TaskScheduler.Default);
            }
        }
    }

    private StackPanel InlineImage(ModImage image, ModImageReference? reference = null, bool embedded = false)
    {
        var owner = session;
        bool attached = true;
        var panel = Column();
        var picture = new Image { Height = embedded ? double.NaN : 160, MinHeight = 160, MaxHeight = embedded ? 600 : 160,
            MaxWidth = embedded ? 1600 : 360, Stretch = Stretch.Uniform, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        // Image arranges to its drawn size, which can be zero while Source is absent.
        // Observe a stable frame so a released/offscreen image can become visible again.
        var frame = new Border { Child = picture, MinHeight = 160, Height = embedded ? double.NaN : 160,
            MaxWidth = embedded ? 1600 : 360, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var message = Text(image.Caption, 12);
        panel.Children.Add(frame); panel.Children.Add(message);
        var caption = image.Caption.Length == 0 ? T("ViewImage") : image.Caption;
        var open = Action(T("ViewImage") + " · " + caption, OpenImageAsync, independent: true);
        open.Content = Text(T("ViewImage")); ToolTip.SetTip(open, caption); panel.Children.Add(open);
        message.MaxLines = embedded ? 4 : 2; message.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(message, image.Caption);
        async Task OpenImageAsync()
        {
            var expected = owner;
            var token = DetailToken;
            if (expected is null || session != expected || !attached) return;
            if (reference is null) { await ShowImageAsync(image, token); return; }
            pendingImage = new(expected, reference, details.SelectedIndex, expected.Article, expected.File);
            await RetryPendingImageAsync();
        }
        bool loading = false, visible = false;
        CancellationTokenSource? tileCancellation = null;
        async Task LoadThumbnail()
        {
            if (loading || picture.Source is not null && MediaFresh(image.ThumbnailUrl ?? image.ImageUrl) || owner is null || session != owner ||
                !attached) return;
            var expected = owner;
            loading = true;
            var cancellation = tileCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
            var token = cancellation.Token;
            Bitmap? bitmap = null;
            var progress = BeginStatus("thumbnail:" + (image.ThumbnailUrl ?? image.ImageUrl), "Media", caption);
            var retry = MediaRetry();
            try
            {
                bitmap = await FetchBitmapAsync(image.ThumbnailUrl ?? image.ImageUrl, embedded ? 1200 : 360, token);
                if (!IsCurrent(expected, token) || !attached) return;
                var previous = picture.Source as Bitmap;
                picture.Source = bitmap; bitmap = null;
                previous?.Dispose();
                EndStatus(progress, ModPageOperationState.Succeeded);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { EndStatus(progress, ModPageOperationState.Cancelled); }
            catch (Exception ex)
            {
                if (IsCurrent(expected, token) && attached)
                    EndStatus(progress, ModPageOperationState.Failed, ImageFailure(ex, retry), "Media", retry);
            }
            finally
            {
                bitmap?.Dispose(); loading = false;
                if (ReferenceEquals(tileCancellation, cancellation)) tileCancellation = null;
                cancellation.Dispose();
                if (!IsCurrent(expected, token) || !attached) EndStatus(progress, ModPageOperationState.Cancelled);
                if (token.IsCancellationRequested && visible && attached && IsCurrent(expected, DetailToken)) await LoadThumbnail();
            }
        }
        Task RefreshTile() => visible && attached ? LoadThumbnail() : Task.CompletedTask;
        visibleMediaRefresh.Add(RefreshTile);
        frame.EffectiveViewportChanged += async (_, args) =>
        {
            visible = new Rect(frame.Bounds.Size).Intersects(args.EffectiveViewport);
            if (visible && !disposed) await LoadThumbnail();
            else
            {
                tileCancellation?.Cancel();
                if (picture.Source is Bitmap owned) { picture.Source = null; owned.Dispose(); }
            }
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            visible = false; attached = false; visibleMediaRefresh.Remove(RefreshTile);
            tileCancellation?.Cancel(); tileCancellation?.Dispose(); tileCancellation = null;
            if (picture.Source is Bitmap bitmap) { picture.Source = null; bitmap.Dispose(); }
        };
        return panel;
    }

    private async Task<Bitmap> FetchBitmapAsync(Uri url, int width, CancellationToken token)
    {
        var operation = width <= 360 ? ModMediaOperation.Thumbnail : ModMediaOperation.Original;
        string provider = session?.Mod.Id.Provider ?? "moddb";
        if (!ModImageClient.IsAllowed(url, url, provider))
            throw new ModSourceException(new(ModSourceError.AccessDenied, "The image address is not supported.", DiagnosticKind: ModHttpFailureKind.RedirectRejected));
        if (mediaCache.TryGetValue(url, out var cached))
        {
            // Revisit a URL after a bounded lifetime rather than keeping same-address images forever.
            if (DateTimeOffset.UtcNow - cached.AddedAt < TimeSpan.FromMinutes(10))
                return await DecodeBitmapAsync(cached.Payload, width, operation, token);
        }
        await runtime!.Ready.WaitAsync(token);
        var payload = await ReadMediaPayloadAsync(url, provider, operation, token);
        var decoded = await DecodeBitmapAsync(payload, width, operation, token);
        if (mediaCache.Remove(url, out var previous)) mediaCacheBytes -= previous.Payload.Bytes.Length;
        // Keep a bounded byte cache; visual controls own and release their decoded bitmaps.
        while (mediaCache.Count > 0 && mediaCacheBytes + payload.Bytes.Length > 32 * 1024 * 1024)
        {
            var first = mediaCache.First(); mediaCacheBytes -= first.Value.Payload.Bytes.Length; mediaCache.Remove(first.Key);
        }
        if (mediaCache.TryAdd(url, new(payload, DateTimeOffset.UtcNow))) mediaCacheBytes += payload.Bytes.Length;
        return decoded;
    }

    private static void LogMedia(ModHttpDiagnostic diagnostic) => AppDiagnostics.Log(diagnostic.ToString());

    private static void LogMediaParse<T>(Uri page, ModMediaOperation operation, ModSourceResult<T> response) =>
        LogMedia(new(ModHttpDiagnostics.Host(page), ModHttpStage.Parse, 1, (int?)response.Failure?.StatusCode,
            response.Failure?.DiagnosticKind ?? (response.Failure is null ? null : ModHttpFailureKind.Response), Media: operation));

    private string ImageFailure(Exception exception, Func<Task> retry)
    {
        var failure = exception is ModSourceException sourceError ? sourceError.Failure :
            new ModSourceFailure(ModSourceError.ParseFailed, "The image could not be decoded.", DiagnosticKind: ModHttpFailureKind.ImageDecode);
        return T("ImageFailed") + " · " + ReadStatus(new ModSourceResult<ModImage>(null, null, failure), retry);
    }

    private async Task<Bitmap> DecodeBitmapAsync(ModImagePayload payload, int width, ModMediaOperation operation, CancellationToken token)
    {
        await mediaDecodeGate.WaitAsync(token);
        try
        {
            // Keep at most one native decode in flight, including cancellation cleanup.
            var bitmap = await Task.Run(() => ModImageDecoder.Decode(payload, width, operation, token, LogMedia), token);
            if (token.IsCancellationRequested) { bitmap.Dispose(); token.ThrowIfCancellationRequested(); }
            return bitmap;
        }
        finally { mediaDecodeGate.Release(); }
    }

    private async Task ShowImageAsync(ModImage image, CancellationToken token)
    {
        if (session is not { } expected) return;
        bool inFiles = details.SelectedIndex == 1;
        SaveReadingPosition();
        if (inFiles) fileImageExpanded = true; else imageExpanded = true;
        int generation = ++readerGeneration;
        var progress = BeginStatus("original:" + image.ImageUrl, "Media", image.Caption.Length > 0 ? image.Caption : T("ViewImage"));
        var retry = DocumentRetry(expected, inFiles ? 1 : 0, expected.Article, expected.File, () =>
        {
            if (generation == readerGeneration && (inFiles ? fileImageExpanded : imageExpanded)) return;
            if (inFiles) fileImageExpanded = true; else imageExpanded = true;
            expandedMediaRefresh = () => ShowImageAsync(image, DetailToken);
        });
        var panel = Column(); var message = Text(image.Caption); panel.Children.Add(message);
        expandedMediaRefresh = () => ShowImageAsync(image, DetailToken);
        panel.Children.Add(Action(T("BackToReading"), () =>
        {
            readerGeneration++; expandedMediaRefresh = null;
            if (inFiles) { fileImageExpanded = false; RenderSelectedFile(); }
            else { imageExpanded = false; renderedSession = null; RenderReading(); }
            return Task.CompletedTask;
        }, independent: true));
        var target = inFiles ? fileReader : reading;
        var scroll = inFiles ? fileScroll : readingScroll;
        target.Children.Clear(); target.Children.Add(panel); scroll.Offset = default;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await FetchBitmapAsync(image.ImageUrl, 1600, token);
            if (!IsCurrent(expected, token) || generation != readerGeneration) return;
            var picture = new Image { Source = bitmap, Stretch = Stretch.Uniform, MinHeight = 160 };
            var frame = new Border { Child = picture, MinHeight = 160 };
            bool loading = false, visible = false;
            CancellationTokenSource? imageCancellation = null;
            async Task RestoreImage()
            {
                if (loading || !visible || picture.Source is not null && MediaFresh(image.ImageUrl) || !IsCurrent(expected, DetailToken) || generation != readerGeneration) return;
                loading = true;
                var cancellation = imageCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
                var imageToken = cancellation.Token;
                Bitmap? restored = null;
                var restoreStatus = BeginStatus("original:" + image.ImageUrl, "Media", image.Caption.Length > 0 ? image.Caption : T("ViewImage"));
                try
                {
                    restored = await FetchBitmapAsync(image.ImageUrl, 1600, imageToken);
                    if (!IsCurrent(expected, imageToken) || generation != readerGeneration) return;
                    var previous = picture.Source as Bitmap;
                    picture.Source = restored; restored = null; previous?.Dispose();
                    EndStatus(restoreStatus, ModPageOperationState.Succeeded);
                }
                catch (OperationCanceledException) when (imageToken.IsCancellationRequested) { EndStatus(restoreStatus, ModPageOperationState.Cancelled); }
                catch (Exception ex)
                {
                    if (IsCurrent(expected, imageToken) && generation == readerGeneration)
                        EndStatus(restoreStatus, ModPageOperationState.Failed, ImageFailure(ex, retry), "Media", retry);
                }
                finally
                {
                    restored?.Dispose(); loading = false;
                    if (!IsCurrent(expected, imageToken) || generation != readerGeneration) EndStatus(restoreStatus, ModPageOperationState.Cancelled);
                    if (ReferenceEquals(imageCancellation, cancellation)) imageCancellation = null;
                    cancellation.Dispose();
                    if (imageToken.IsCancellationRequested && visible && !disposed) await RestoreImage();
                }
            }
            frame.EffectiveViewportChanged += async (_, args) =>
            {
                visible = new Rect(frame.Bounds.Size).Intersects(args.EffectiveViewport);
                if (visible) await RestoreImage();
            };
            picture.DetachedFromVisualTree += (_, _) =>
            {
                visible = false; imageCancellation?.Cancel(); imageCancellation?.Dispose(); imageCancellation = null;
                if (picture.Source is Bitmap owned) { picture.Source = null; owned.Dispose(); }
            };
            panel.Children.Add(frame); bitmap = null; message.Text = image.Caption;
            EndStatus(progress, ModPageOperationState.Succeeded);
            expandedMediaRefresh = RestoreImage;
            panel.Children.Add(Action(T("Original"), () => { OpenUrl(image.ImageUrl); return Task.CompletedTask; }, independent: true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { EndStatus(progress, ModPageOperationState.Cancelled); }
        catch (Exception ex)
        {
            if (!IsCurrent(expected, token) || generation != readerGeneration) return;
            EndStatus(progress, ModPageOperationState.Failed, ImageFailure(ex, retry), "Media", retry);
        }
        finally { bitmap?.Dispose(); if (!IsCurrent(expected, token) || generation != readerGeneration) EndStatus(progress, ModPageOperationState.Cancelled); }
    }

    private void DisposeMedia() { readerCancellation?.Cancel(); mediaCache.Clear(); mediaCacheBytes = 0; }
}
