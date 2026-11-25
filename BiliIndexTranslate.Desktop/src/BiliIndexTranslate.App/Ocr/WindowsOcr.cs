using BiliIndexTranslate.Core.Translation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace BiliIndexTranslate.App.Ocr;

internal enum OcrFailure
{
    None,
    /// <summary>The data URL could not be decoded into an image.</summary>
    InvalidImage,
    /// <summary>No OCR engine could be created: none of the profile languages has an OCR language pack.</summary>
    EngineUnavailable,
    /// <summary>The engine ran but found no text.</summary>
    NoText,
    /// <summary>Unexpected exception; <see cref="OcrOutcome.Detail"/> holds the exception type name only.</summary>
    Error,
}

/// <summary>Recognized text or the reason there is none. The text itself never goes to a log.</summary>
internal sealed record OcrOutcome(string Text, OcrFailure Failure, string? Detail = null)
{
    public bool IsSuccess => Failure == OcrFailure.None;
}

/// <summary>
/// Offline text recognition with the Windows.Media.Ocr engine. The screenshot stays in memory: PNG data URL,
/// decoded to a <see cref="SoftwareBitmap"/>, recognized, disposed. Nothing is written to disk or sent anywhere.
/// </summary>
internal static class WindowsOcr
{
    public static async Task<OcrOutcome> RecognizeAsync(string pngDataUrl, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var bytes = DecodeDataUrl(pngDataUrl);
            if (bytes is null) return new OcrOutcome(string.Empty, OcrFailure.InvalidImage, "data URL");

            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return new OcrOutcome(string.Empty, OcrFailure.EngineUnavailable);

            SoftwareBitmap bitmap;
            try
            {
                bitmap = await DecodeBitmapAsync(bytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new OcrOutcome(string.Empty, OcrFailure.InvalidImage, ex.GetType().Name);
            }

            using (bitmap)
            {
                ct.ThrowIfCancellationRequested();
                var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                var text = OcrText.Join(result.Lines.Select(LineText));
                return text.Length == 0
                    ? new OcrOutcome(string.Empty, OcrFailure.NoText)
                    : new OcrOutcome(text, OcrFailure.None);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OcrOutcome(string.Empty, OcrFailure.Error, ex.GetType().Name);
        }
    }

    /// <summary>
    /// For Chinese and Japanese the engine puts a space between recognized words, so a line is rebuilt from its
    /// words with the same CJK-aware joining used between lines.
    /// </summary>
    private static string LineText(OcrLine line)
    {
        if (line.Words.Count == 0) return line.Text;
        return OcrText.JoinWords(line.Words.Select(w => w.Text));
    }

    private static async Task<SoftwareBitmap> DecodeBitmapAsync(byte[] bytes, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        var maxDim = (double)OcrEngine.MaxImageDimension;
        double width = decoder.PixelWidth, height = decoder.PixelHeight;

        if (width <= maxDim && height <= maxDim)
        {
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask(ct).ConfigureAwait(false);
        }

        // Larger than the engine accepts: shrink in proportion so the longer edge fits.
        var scale = maxDim / Math.Max(width, height);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Floor(width * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Floor(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(ct).ConfigureAwait(false);
    }

    private static byte[]? DecodeDataUrl(string dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Convert.FromBase64String(dataUrl[(comma + 1)..]); }
        catch (FormatException) { return null; }
    }
}
