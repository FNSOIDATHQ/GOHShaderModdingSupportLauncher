using Avalonia.Media.Imaging;
using GOHShaderModdingSupportLauncher.ModSubscriptions;
using SkiaSharp;

namespace GOHShaderModdingSupportLauncher;

internal static class ModImageDecoder
{
    internal static Bitmap Decode(ModImagePayload payload, int width, ModMediaOperation operation,
        CancellationToken token, Action<ModHttpDiagnostic>? diagnostic = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        int sourceWidth = 0, sourceHeight = 0;
        try
        {
            var bitmap = DecodeRaster(payload.Bytes, width, int.MaxValue, token, out sourceWidth, out sourceHeight);
            Report();
            return bitmap;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Report(ModHttpStage.Cancelled); throw; }
        catch (Exception ex)
        {
            var failure = ex is ModSourceException sourceError ? sourceError.Failure :
                new(ModSourceError.ParseFailed, "The image could not be decoded.", DiagnosticKind: ModHttpFailureKind.ImageDecode);
            Report(failure: failure.DiagnosticKind, exceptionType: ex.GetType().Name);
            throw new ModSourceException(failure);
        }
        void Report(ModHttpStage stage = ModHttpStage.Decode, ModHttpFailureKind? failure = null, string? exceptionType = null) =>
            ModHttpDiagnostics.Report(diagnostic, new(ModHttpDiagnostics.Host(payload.FinalUri), stage, 1,
                FailureKind: failure, ExceptionType: exceptionType, Media: operation, ContentType: payload.ContentType,
                Bytes: payload.Bytes.Length, PixelWidth: sourceWidth, PixelHeight: sourceHeight));
    }

    internal static Bitmap DecodeRaster(byte[] bytes, int width, int height, CancellationToken token,
        out int sourceWidth, out int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        sourceWidth = sourceHeight = 0;
        token.ThrowIfCancellationRequested();
        using var metadata = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(metadata);
        if (codec is null) throw new InvalidDataException("No raster decoder accepted the image.");
        sourceWidth = codec.Info.Width;
        sourceHeight = codec.Info.Height;
        // Bound both source pixels and output memory before allocating native pixels.
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 16384 || sourceHeight > 16384 ||
            (long)sourceWidth * sourceHeight > 40_000_000)
            throw new ModSourceException(new(ModSourceError.ParseFailed, "The image dimensions exceed the display limit.", DiagnosticKind: ModHttpFailureKind.ImageDimensions));
        int decodeWidth = (int)Math.Clamp((long)height * sourceWidth / sourceHeight, 1, Math.Min(width, sourceWidth));
        if ((long)decodeWidth * decodeWidth * sourceHeight / sourceWidth > 8_000_000)
            decodeWidth = Math.Max(1, (int)Math.Sqrt(8_000_000d * sourceWidth / sourceHeight));
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = Bitmap.DecodeToWidth(stream, decodeWidth);
        if (token.IsCancellationRequested)
        {
            bitmap.Dispose();
            token.ThrowIfCancellationRequested();
        }
        return bitmap;
    }
}
