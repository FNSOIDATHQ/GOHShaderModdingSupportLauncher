using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private readonly Dictionary<Uri, byte[]> mediaCache = [];
    private long mediaCacheBytes;

    private Control InlineImage(ModImage image, ModImageReference? reference = null)
    {
        var owner = session;
        var panel = Column();
        var picture = new Image { Height = 160, MaxWidth = 360, Stretch = Stretch.Uniform, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var message = Text(T("ImageOnScroll"), 12);
        panel.Children.Add(picture); panel.Children.Add(message);
        var caption = image.Caption.Length == 0 ? T("ViewImage") : image.Caption;
        panel.Children.Add(Action(T("ViewImage") + " · " + caption, async () =>
        {
            var expected = session;
            var token = DetailToken;
            int generation = readerGeneration;
            if (expected is null) return;
            if (reference is null) { await ShowImageAsync(image, token); return; }
            message.Text = T("Working");
            var response = await Task.Run(() => SourceForProvider(expected.Mod.Id.Provider).GetImageAsync(reference.PageUrl, cancellationToken: token), token);
            if (!IsCurrent(expected, token) || generation != readerGeneration) return;
            message.Text = ReadStatus(response, () => ShowImageAsync(image, token));
            if (response.Content is { } full) await ShowImageAsync(full, token);
        }, independent: true));
        bool loading = false, visible = false;
        CancellationTokenSource? tileCancellation = null;
        async Task LoadThumbnail()
        {
            if (loading || picture.Source is not null || owner is null || session != owner) return;
            var expected = owner;
            loading = true;
            tileCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
            var token = tileCancellation.Token;
            Bitmap? bitmap = null;
            try
            {
                message.Text = T("Working");
                bitmap = await FetchBitmapAsync(image.ThumbnailUrl ?? image.ImageUrl, 360, token);
                if (!IsCurrent(expected, token)) return;
                picture.Source = bitmap; bitmap = null;
                message.Text = image.Caption;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsCurrent(expected, token))
                {
                    message.Text = ex is ModSourceException sourceError
                        ? ReadStatus(new ModSourceResult<ModImage>(null, null, sourceError.Failure), LoadThumbnail) : T("ImageFailed");
                    var retry = Action(T("RetryContent"), LoadThumbnail, independent: true); panel.Children.Add(retry);
                }
            }
            finally
            {
                bitmap?.Dispose(); loading = false;
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
        if (!AnonymousModHttpClient.IsPublicHttps(url)) throw new InvalidDataException(T("InvalidImage"));
        if (mediaCache.TryGetValue(url, out var cached))
        {
            using var cachedStream = new MemoryStream(cached, writable: false);
            return await Task.Run(() => Bitmap.DecodeToWidth(cachedStream, width), token);
        }
        await runtime!.Ready.WaitAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var host = ModHostLimiter.Shared.For(url);
        bool acquired = false;
        byte[] data;
        try
        {
            runtime.PrepareVerificationRequest(request);
            await host.Gate.WaitAsync(token); acquired = true;
            TimeSpan wait = host.NextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            runtime.PrepareVerificationRequest(request);
            host.NextRequest = DateTimeOffset.UtcNow.AddSeconds(1);
            using var response = await imageClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (ModChallengeDetector.IsChallenge(response)) runtime.RequireVerification(url, response.StatusCode);
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            bool html = contentType is "text/html" or "application/xhtml+xml";
            const int maxBytes = 20 * 1024 * 1024;
            const int maxChallengeBytes = 64 * 1024;
            if (!html && response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException(T("ImageTooLarge"));
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int limit = html ? maxChallengeBytes : maxBytes;
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + count > limit)
                {
                    if (!html) throw new InvalidDataException(T("ImageTooLarge"));
                    int remaining = limit - checked((int)bytes.Length);
                    if (remaining > 0) await bytes.WriteAsync(buffer.AsMemory(0, remaining), token);
                    break;
                }
                await bytes.WriteAsync(buffer.AsMemory(0, count), token);
            }
            var prefix = System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, Math.Min(checked((int)bytes.Length), maxChallengeBytes));
            if (html || prefix.AsSpan().TrimStart('\uFEFF').TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                if (ModChallengeDetector.IsChallengeHtml(prefix)) runtime.RequireVerification(url, response.StatusCode);
                response.EnsureSuccessStatusCode(); throw new InvalidDataException(T("InvalidImage"));
            }
            response.EnsureSuccessStatusCode(); data = bytes.ToArray();
        }
        finally { if (acquired) host.Gate.Release(); }
        token.ThrowIfCancellationRequested();
        using var imageStream = new MemoryStream(data, writable: false);
        var decoded = await Task.Run(() => Bitmap.DecodeToWidth(imageStream, width), token);
        // Keep a bounded byte cache; visual controls own and release their decoded bitmaps.
        while (mediaCache.Count > 0 && mediaCacheBytes + data.Length > 32 * 1024 * 1024)
        {
            var first = mediaCache.First(); mediaCacheBytes -= first.Value.Length; mediaCache.Remove(first.Key);
        }
        if (mediaCache.TryAdd(url, data)) mediaCacheBytes += data.Length;
        return decoded;
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
                imageCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var imageToken = imageCancellation.Token;
                Bitmap? restored = null;
                try
                {
                    restored = await FetchBitmapAsync(image.ImageUrl, 1600, imageToken);
                    if (!IsCurrent(expected, imageToken) || generation != readerGeneration) return;
                    picture.Source = restored; restored = null;
                }
                catch (OperationCanceledException) when (imageToken.IsCancellationRequested) { }
                catch (Exception) { if (IsCurrent(expected, imageToken)) message.Text = T("ImageFailed"); }
                finally
                {
                    restored?.Dispose(); loading = false;
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
            message.Text = ex is ModSourceException sourceError
                ? ReadStatus(new ModSourceResult<ModImage>(null, null, sourceError.Failure), () => ShowImageAsync(image, DetailToken))
                : T("ImageFailed") + ": " + ex.Message;
            status.Text = T("RequestFailed");
            panel.Children.Add(Action(T("RetryContent"), () => ShowImageAsync(image, DetailToken), independent: true));
        }
        finally { bitmap?.Dispose(); }
    }

    private void DisposeMedia() { readerCancellation?.Cancel(); mediaCache.Clear(); mediaCacheBytes = 0; }
}
