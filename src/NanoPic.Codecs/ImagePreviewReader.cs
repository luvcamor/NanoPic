using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NanoPic.Core;
using ImageMetadata = NanoPic.Core.ImageMetadata;

namespace NanoPic.Codecs;

public sealed record ImagePreview(BitmapSource Image, int OriginalWidth, int OriginalHeight, bool Detail);

// Preview never quantizes or writes back the source. WebP uses dwebp's lossless PNG bridge.
public static class ImagePreviewReader
{
    private static readonly SemaphoreSlim DecodeGate = new(1);
    public static async Task<ImageOperationResult<ImagePreview>> ReadAsync(string path, bool detail,
        ImageSafetyLimits limits, CancellationToken token, double centerX = 0.5, double centerY = 0.5)
    {
        try
        {
            await DecodeGate.WaitAsync(token).ConfigureAwait(false);
            try { return await Task.Run(() => Read(path, detail, limits, token, centerX, centerY), token).ConfigureAwait(false); }
            finally { DecodeGate.Release(); }
        }
        catch (OperationCanceledException) { return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.TaskCanceled, "预览已取消。"); }
    }

    private static ImageOperationResult<ImagePreview> Read(string path, bool detail, ImageSafetyLimits limits, CancellationToken token, double centerX, double centerY)
    {
        var temporary = string.Empty;
        try
        {
            token.ThrowIfCancellationRequested();
            // Preview has its own smaller decode budget; returned buffers are at
            // most 2048² pixels (overview) or 1024² pixels (native detail).
            limits = limits with { MaxPixels = Math.Min(limits.MaxPixels, 64_000_000) };
            path = PortablePath.ForFileSystem(path);
            var originalPath = path;
            ImageFormat format;
            long bytes;
            using (var input = File.OpenRead(path))
            {
                bytes = input.Length;
                if (bytes > limits.MaxSourceBytes) return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.PixelBudgetExceeded, "图片文件过大，无法安全预览。");
                var header = new byte[32]; var read = input.Read(header, 0, header.Length);
                if (read != header.Length) Array.Resize(ref header, read);
                var detected = ImageFileSignatureInspector.Detect(header);
                if (!detected.IsSuccess) return new ImageOperationResult<ImagePreview>(default, detected.Failure);
                format = detected.Value;
                var probe = ImageDimensionProbe.Probe(input, format);
                if (!probe.IsSuccess || probe.Value is null) return new ImageOperationResult<ImagePreview>(default, probe.Failure);
                var metadata = new ImageMetadata(format, probe.Value.Width, probe.Value.Height, probe.Value.FrameCount ?? 1, false, bytes);
                var safety = ImageSafetyValidator.ValidateWithAction(metadata, limits);
                if (safety.Action != SafetyAction.Pass)
                    return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.PixelBudgetExceeded, "这张图片超过预览安全上限，请先另存为较小尺寸再查看。");
            }
            if (format == ImageFormat.Webp)
            {
                temporary = Path.Combine(Path.GetTempPath(), "nanopic-compare-" + Guid.NewGuid().ToString("N") + ".png");
                LibWebpTools.DecodeToPng(path, temporary, token);
                path = temporary;
            }
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count == 0) return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.DecodeFailed, "图片中没有可预览的画面。");
            var frame = format == ImageFormat.Webp ? WebpColorProfile.Attach(decoder.Frames[0], originalPath, token) : decoder.Frames[0];
            var post = ImageSafetyValidator.ValidateWithAction(new ImageMetadata(format, frame.PixelWidth,
                frame.PixelHeight, decoder.Frames.Count, false, bytes), limits);
            if (post.Action != SafetyAction.Pass || (long)frame.PixelWidth * frame.PixelHeight * frame.Format.BitsPerPixel > 256L * 1024 * 1024 * 8)
                return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.PixelBudgetExceeded, "图片解码后的尺寸超过预览安全上限。");
            token.ThrowIfCancellationRequested();
            BitmapSource pixels = frame;
            if (frame.ColorContexts is { Count: > 0 })
                pixels = new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            WicImageCodec.TryReadExifOrientation(frame.Metadata as BitmapMetadata, out var orientation);
            var matrix = orientation switch
            {
                2 => new Matrix(-1, 0, 0, 1, 0, 0), 3 => new Matrix(-1, 0, 0, -1, 0, 0),
                4 => new Matrix(1, 0, 0, -1, 0, 0), 5 => new Matrix(0, 1, 1, 0, 0, 0),
                6 => new Matrix(0, 1, -1, 0, 0, 0), 7 => new Matrix(0, -1, -1, 0, 0, 0),
                8 => new Matrix(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
            };
            if (!matrix.IsIdentity) pixels = new TransformedBitmap(pixels, new MatrixTransform(matrix));
            var width = pixels.PixelWidth; var height = pixels.PixelHeight;
            if (detail)
            {
                var w = Math.Min(1024, width); var h = Math.Min(1024, height);
                var x = Math.Max(0, Math.Min(width - w, (int)Math.Round(width * centerX - w / 2.0)));
                var y = Math.Max(0, Math.Min(height - h, (int)Math.Round(height * centerY - h / 2.0)));
                pixels = new CroppedBitmap(pixels, new Int32Rect(x, y, w, h));
            }
            else
            {
                var scale = Math.Min(1, 2048.0 / Math.Max(width, height));
                if (scale < 1) pixels = new TransformedBitmap(pixels, new ScaleTransform(scale, scale));
            }
            var converted = new FormatConvertedBitmap(pixels, PixelFormats.Bgra32, null, 0);
            var stride = checked(converted.PixelWidth * 4);
            var data = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(data, stride, 0);
            token.ThrowIfCancellationRequested();
            // Detach from file handles, profiles and decoder RCWs, with pixel-based DPI.
            var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, data, stride);
            image.Freeze();
            return ImageOperationResult<ImagePreview>.Success(new ImagePreview(image, width, height, detail));
        }
        catch (OperationCanceledException) { return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.TaskCanceled, "预览已取消。"); }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException)
        { return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.PixelBudgetExceeded, "图片太大，当前内存无法完成预览。", ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException or ArgumentException or FileFormatException or InvalidOperationException or LibWebpToolException)
        { return ImageOperationResult<ImagePreview>.Failed(ImageFailureKind.DecodeFailed, "无法读取这张图片的预览。", ex); }
        finally
        {
            if (temporary.Length > 0) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
