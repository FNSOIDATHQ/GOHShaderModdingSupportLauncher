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

    private Control InlineImage(ModImage image, ModImageReference? reference = null)
    {
        var owner = session;
        var panel = Column();
        var picture = new Image { Height = 160, MaxWidth = 360, Stretch = Stretch.Uniform, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var message = Text(T("ImageOnScroll"), 12);
        panel.Children.Add(picture); panel.Children.Add(message);
        var caption = image.Caption.Length == 0 ? T("ViewImage") : image.Caption;
        panel.Children.Add(Action(T("ViewImage") + " · " + caption, OpenImageAsync, independent: true));
        async Task OpenImageAsync()
        {
            var expected = owner;
            var token = DetailToken;
            int generation = readerGeneration;
            if (expected is null || session != expected) return;
            if (reference is null) { await ShowImageAsync(image, token); return; }
            message.Text = T("Working");
            var response = await Task.Run(() => SourceForProvider(expected.Mod.Id.Provider).GetImageAsync(reference.PageUrl, cancellationToken: token), token);
            if (!IsCurrent(expected, token) || generation != readerGeneration) return;
            LogMediaParse(reference.PageUrl, ModMediaOperation.ImagePage, response);
            message.Text = ReadStatus(response, OpenImageAsync);
            if (response.Content is { } full) await ShowImageAsync(full, token);
        }
        bool loading = false, visible = false;
        Button? retry = null;
        CancellationTokenSource? tileCancellation = null;
        async Task LoadThumbnail()
        {
            if (loading || picture.Source is not null || owner is null || session != owner) return;
            var expected = owner;
            loading = true;
            var cancellation = tileCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
            var token = cancellation.Token;
            Bitmap? bitmap = null;
            try
            {
                message.Text = T("Working");
                bitmap = await FetchBitmapAsync(image.ThumbnailUrl ?? image.ImageUrl, 360, token);
                if (!IsCurrent(expected, token)) return;
                picture.Source = bitmap; bitmap = null;
                message.Text = image.Caption;
                if (retry is not null) { panel.Children.Remove(retry); retry = null; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsCurrent(expected, token))
                {
                    message.Text = ImageFailure(ex, LoadThumbnail);
                    if (retry is null) { retry = Action(T("RetryContent"), LoadThumbnail, independent: true); panel.Children.Add(retry); }
                }
            }
            finally
            {
                bitmap?.Dispose(); loading = false;
                if (ReferenceEquals(tileCancellation, cancellation)) tileCancellation = null;
                cancellation.Dispose();
                if (token.IsCancellationRequested && visible && !disposed) await LoadThumbnail();
            }
        }
        picture.EffectiveViewportChanged += async (_, args) =>
        {
            visible = new Rect(picture.Bounds.Size).Intersects(args.EffectiveViewport);
            if (visible && !disposed) await LoadThumbnail();
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            visible = false;
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
        var transfer = new ModImageClient(imageClient, runtime.PrepareVerificationRequest,
            (uri, status) => runtime.RequireVerification(uri, status), LogMedia);
        var payload = await transfer.GetAsync(url, provider, operation, token);
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
        SaveReadingPosition();
        imageExpanded = true;
        int generation = ++readerGeneration;
        var panel = Column(); var message = Text(T("Working")); panel.Children.Add(message);
        panel.Children.Add(Action(T("BackToReading"), () => { readerGeneration++; imageExpanded = false; renderedSession = null; RenderReading(); return Task.CompletedTask; }, independent: true));
        reading.Children.Clear(); reading.Children.Add(panel); readingScroll.Offset = default;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await FetchBitmapAsync(image.ImageUrl, 1600, token);
            if (!IsCurrent(expected, token) || generation != readerGeneration) return;
            var picture = new Image { Source = bitmap, Stretch = Stretch.Uniform, MinHeight = 160 };
            bool loading = false, visible = false;
            CancellationTokenSource? imageCancellation = null;
            async Task RestoreImage()
            {
                if (loading || !visible || picture.Source is not null || !IsCurrent(expected, token) || generation != readerGeneration) return;
                loading = true;
                var cancellation = imageCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var imageToken = cancellation.Token;
                Bitmap? restored = null;
                try
                {
                    restored = await FetchBitmapAsync(image.ImageUrl, 1600, imageToken);
                    if (!IsCurrent(expected, imageToken) || generation != readerGeneration) return;
                    picture.Source = restored; restored = null;
                }
                catch (OperationCanceledException) when (imageToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (IsCurrent(expected, imageToken) && generation == readerGeneration)
                        message.Text = ImageFailure(ex, RestoreImage);
                }
                finally
                {
                    restored?.Dispose(); loading = false;
                    if (ReferenceEquals(imageCancellation, cancellation)) imageCancellation = null;
                    cancellation.Dispose();
                    if (imageToken.IsCancellationRequested && visible && !disposed) await RestoreImage();
                }
            }
            picture.EffectiveViewportChanged += async (_, args) =>
            {
                visible = new Rect(picture.Bounds.Size).Intersects(args.EffectiveViewport);
                if (visible) await RestoreImage();
            };
            picture.DetachedFromVisualTree += (_, _) =>
            {
                visible = false; imageCancellation?.Cancel(); imageCancellation?.Dispose(); imageCancellation = null;
                if (picture.Source is Bitmap owned) { picture.Source = null; owned.Dispose(); }
            };
            panel.Children.Add(picture); bitmap = null; message.Text = image.Caption;
            panel.Children.Add(Action(T("Original"), () => { OpenUrl(image.ImageUrl); return Task.CompletedTask; }, independent: true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent(expected, token) || generation != readerGeneration) return;
            message.Text = ImageFailure(ex, () => ShowImageAsync(image, DetailToken));
            status.Text = T("RequestFailed");
            panel.Children.Add(Action(T("RetryContent"), () => ShowImageAsync(image, DetailToken), independent: true));
        }
        finally { bitmap?.Dispose(); }
    }

    private void DisposeMedia() { readerCancellation?.Cancel(); mediaCache.Clear(); mediaCacheBytes = 0; }
}
