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
        Bitmap? bitmap = null;
        int sourceWidth = 0, sourceHeight = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            using var metadata = new MemoryStream(payload.Bytes, writable: false);
            using var codec = SKCodec.Create(metadata);
            if (codec is null) throw new InvalidDataException("No raster decoder accepted the image.");
            sourceWidth = codec.Info.Width; sourceHeight = codec.Info.Height;
            // Bound both source pixels and output memory before allocating native pixels.
            if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 16384 || sourceHeight > 16384 ||
                (long)sourceWidth * sourceHeight > 40_000_000)
                throw new ModSourceException(new(ModSourceError.ParseFailed, "The image dimensions exceed the display limit.", DiagnosticKind: ModHttpFailureKind.ImageDimensions));
            int decodeWidth = Math.Min(width, sourceWidth);
            if ((long)decodeWidth * decodeWidth * sourceHeight / sourceWidth > 8_000_000)
                decodeWidth = Math.Max(1, (int)Math.Sqrt(8_000_000d * sourceWidth / sourceHeight));
            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(payload.Bytes, writable: false);
            bitmap = Bitmap.DecodeToWidth(stream, decodeWidth);
            token.ThrowIfCancellationRequested();
            Report();
            var result = bitmap; bitmap = null; return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Report(ModHttpStage.Cancelled); throw; }
        catch (Exception ex)
        {
            var failure = ex is ModSourceException sourceError ? sourceError.Failure :
                new(ModSourceError.ParseFailed, "The image could not be decoded.", DiagnosticKind: ModHttpFailureKind.ImageDecode);
            Report(failure: failure.DiagnosticKind, exceptionType: ex.GetType().Name);
            throw new ModSourceException(failure);
        }
        finally { bitmap?.Dispose(); }

        void Report(ModHttpStage stage = ModHttpStage.Decode, ModHttpFailureKind? failure = null, string? exceptionType = null) =>
            ModHttpDiagnostics.Report(diagnostic, new(ModHttpDiagnostics.Host(payload.FinalUri), stage, 1,
                FailureKind: failure, ExceptionType: exceptionType, Media: operation, ContentType: payload.ContentType,
                Bytes: payload.Bytes.Length, PixelWidth: sourceWidth, PixelHeight: sourceHeight));
    }
}
