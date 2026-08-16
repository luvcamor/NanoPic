using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NanoPic.Core;
using CoreImageMetadata = NanoPic.Core.ImageMetadata;

namespace NanoPic.Codecs;

public sealed class WicImageCodec : IImageCodec
{
    private static readonly IReadOnlyCollection<ImageFormat> Formats = new HashSet<ImageFormat>
    {
        ImageFormat.Jpeg,
        ImageFormat.Png,
        ImageFormat.Webp,
        ImageFormat.Gif,
        ImageFormat.Bmp,
        ImageFormat.Tiff,
        ImageFormat.Ico
    };

    public IReadOnlyCollection<ImageFormat> SupportedFormats => Formats;

    public async Task<ImageOperationResult<CoreImageMetadata>> IdentifyAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        var signature = await ImageFileSignatureInspector.DetectAsync(input, cancellationToken).ConfigureAwait(false);
        if (!signature.IsSuccess || !Formats.Contains(signature.Value))
        {
            return ImageOperationResult<CoreImageMetadata>.Failed(
                ImageFailureKind.UnsupportedFormat,
                "图像格式不受 WIC 编解码器支持。",
                signature.Failure?.Exception);
        }

        try
        {
            if (signature.Value == ImageFormat.Webp)
            {
                return await Task.Run(
                    () => ImageOperationResult<CoreImageMetadata>.Success(WebpHeaderParser.Read(input)),
                    cancellationToken).ConfigureAwait(false);
            }

            var sourceBytes = input.CanSeek ? input.Length : 0L;
            if (CanDecodeDirectly(input))
            {
                var originalPosition = input.CanSeek ? input.Position : 0L;
                try
                {
                    return await DecodeMetadataAsync(
                        input,
                        signature.Value,
                        sourceBytes,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (input.CanSeek)
                    {
                        input.Position = originalPosition;
                    }
                }
            }

            using var snapshot = await CreateSnapshotAsync(input, cancellationToken).ConfigureAwait(false);
            return await DecodeMetadataAsync(
                snapshot,
                signature.Value,
                sourceBytes > 0 ? sourceBytes : snapshot.Length,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ImageOperationResult<CoreImageMetadata>.Failed(ImageFailureKind.TaskCanceled, "图像识别已取消。");
        }
        catch (InvalidDataException exception)
        {
            return ImageOperationResult<CoreImageMetadata>.Failed(
                ImageFailureKind.DecodeFailed,
                "WIC 无法读取图像数据。",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return ImageOperationResult<CoreImageMetadata>.Failed(
                ImageFailureKind.DecodeFailed,
                "WIC 无法读取图像数据。",
                exception);
        }
    }

    private static bool CanDecodeDirectly(Stream input) =>
        input is MemoryStream || input is FileStream { IsAsync: false };

    private static Task<ImageOperationResult<CoreImageMetadata>> DecodeMetadataAsync(
        Stream input,
        ImageFormat format,
        long sourceBytes,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoder = BitmapDecoder.Create(
                    input,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                return ImageOperationResult<CoreImageMetadata>.Success(
                    CreateMetadata(decoder.Frames, format, sourceBytes));
            }, cancellationToken);

    private static async Task<MemoryStream> CreateSnapshotAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var originalPosition = input.CanSeek ? input.Position : 0L;
        var remainingLength = input.CanSeek ? Math.Max(0L, input.Length - originalPosition) : 0L;
        var capacity = remainingLength is > 0 and <= int.MaxValue ? (int)remainingLength : 0;
        var snapshot = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
        try
        {
            await input.CopyToAsync(snapshot, 65_536, cancellationToken).ConfigureAwait(false);
            snapshot.Position = 0;
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
        finally
        {
            if (input.CanSeek)
            {
                input.Position = originalPosition;
            }
        }
    }

    public async Task<ImageOperationResult<ImageEncodedOutput>> TransformAndEncodeAsync(
        ImageEncodeRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (!Formats.Contains(request.SourceFormat) || !Formats.Contains(request.OutputFormat))
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.UnsupportedFormat,
                "输入或输出格式不受 WIC 编解码器支持。");
        }

        if (request.Encoding.Quality is < 1 or > 100)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "图像质量必须在 1 到 100 之间。");
        }

        try
        {
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<BitmapFrame> sourceFrames;
                var decodedWebpPath = string.Empty;
                var sourcePath = request.SourcePath;
                try
                {
                    if (request.SourceFormat == ImageFormat.Webp)
                    {
                        decodedWebpPath = CreateSiblingTemporaryPath(request.TemporaryOutputPath, "decoded", ".png");
                        LibWebpTools.DecodeToPng(request.SourcePath, decodedWebpPath, cancellationToken);
                        sourcePath = decodedWebpPath;
                    }

                    using var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 65_536,
                        useAsync: false);
                    var decoder = BitmapDecoder.Create(
                        source,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    sourceFrames = decoder.Frames.ToArray();
                }
                finally
                {
                    DeleteIfExists(decodedWebpPath);
                }

                if (sourceFrames.Count == 0)
                {
                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                        ImageFailureKind.DecodeFailed,
                        "图像不包含可处理的帧。");
                }

                var sourceMetadata = CreateMetadata(sourceFrames, request.SourceFormat, new FileInfo(request.SourcePath).Length);
                var safetyFailure = ImageSafetyValidator.Validate(sourceMetadata, request.SafetyLimits);
                if (safetyFailure is not null)
                {
                    return new ImageOperationResult<ImageEncodedOutput>(default, safetyFailure);
                }

                var outputFrames = PrepareFrames(sourceFrames, request.SourceFormat, request.OutputFormat, request.Transform);
                var outputMetadata = CreateMetadata(outputFrames, request.OutputFormat, sourceBytes: 0L);
                var outputSafetyFailure = ImageSafetyValidator.Validate(outputMetadata, request.SafetyLimits);
                if (outputSafetyFailure is not null)
                {
                    return new ImageOperationResult<ImageEncodedOutput>(default, outputSafetyFailure);
                }

                var effectiveQuality = request.Encoding.Quality;
                var targetReached = true;
                var exceededTarget = false;
                if (request.Encoding.TargetSize is { } targetSize)
                {
                    var targetSearch = await TargetSizeSearch.FindAsync(
                        targetSize,
                        (quality, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            Write(outputFrames, request.TemporaryOutputPath, request.OutputFormat, quality, token);
                            return Task.FromResult(ImageOperationResult<long>.Success(
                                new FileInfo(request.TemporaryOutputPath).Length));
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (!targetSearch.IsSuccess || targetSearch.Value is null)
                    {
                        return new ImageOperationResult<ImageEncodedOutput>(default, targetSearch.Failure);
                    }

                    effectiveQuality = targetSearch.Value.Selected.Quality;
                    targetReached = targetSearch.Value.TargetReached;
                    exceededTarget = targetSearch.Value.ExceededTarget;
                }

                Write(outputFrames, request.TemporaryOutputPath, request.OutputFormat, effectiveQuality, cancellationToken);
                var bytes = new FileInfo(request.TemporaryOutputPath).Length;
                if (bytes <= 0)
                {
                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                        ImageFailureKind.OutputVerificationFailed,
                        "WIC 编码器未写出有效图像数据。");
                }

                return ImageOperationResult<ImageEncodedOutput>.Success(
                    new ImageEncodedOutput(
                        outputMetadata with { SourceBytes = bytes },
                        effectiveQuality,
                        bytes,
                        targetReached,
                        exceededTarget));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(ImageFailureKind.TaskCanceled, "图像编码已取消。");
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "图像处理参数无效。",
                exception);
        }
        catch (UnauthorizedAccessException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.FileAccessConflict,
                "没有读写图像文件的权限。",
                exception);
        }
        catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.FileAccessConflict,
                "读写图像文件时发生 I/O 错误。",
                exception);
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.DecodeFailed,
                "WIC 无法解码或编码图像。",
                exception);
        }
        catch (LibWebpToolException exception)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.DecodeFailed,
                "libwebp 无法处理图像。",
                exception);
        }
    }

    private static IReadOnlyList<BitmapFrame> PrepareFrames(
        IReadOnlyList<BitmapFrame> sourceFrames,
        ImageFormat sourceFormat,
        ImageFormat outputFormat,
        ImageTransformOptions transform)
    {
        var frameCount = outputFormat is ImageFormat.Gif or ImageFormat.Tiff ? sourceFrames.Count : 1;
        var result = new List<BitmapFrame>(frameCount);
        for (var index = 0; index < frameCount; index++)
        {
            var source = sourceFrames[index];
            BitmapSource prepared = source;
            var pixelsChanged = false;
            var orientationChanged = false;
            if (transform.AutoOrient)
            {
                prepared = ApplyExifOrientation(prepared, out orientationChanged);
                pixelsChanged |= orientationChanged;
            }

            if (transform.Resize is { Enabled: true } resize)
            {
                prepared = Resize(prepared, resize);
                pixelsChanged = true;
            }

            if (transform.BrightnessPercent != 100)
            {
                prepared = AdjustBrightness(prepared, transform.BrightnessPercent);
                pixelsChanged = true;
            }

            if (transform.Watermark is { Enabled: true } watermark &&
                !string.IsNullOrWhiteSpace(watermark.Text))
            {
                prepared = ApplyWatermark(prepared, watermark);
                pixelsChanged = true;
            }

            var shouldFlatten = !SupportsAlpha(outputFormat) &&
                (HasAlpha(prepared) || transform.Background is { FlattenTransparency: true });
            if (shouldFlatten)
            {
                var background = transform.Background?.ColorHex ?? "#FFFFFF";
                prepared = Flatten(prepared, ParseColor(background));
                pixelsChanged = true;
            }

            var preserveMetadata = !transform.StripMetadata;
            var metadata = preserveMetadata
                ? CloneMetadata(source.Metadata as BitmapMetadata, orientationChanged)
                : null;
            metadata = ApplyMetadataNote(metadata, sourceFormat, outputFormat, transform.MetadataNote);
            var frame = BitmapFrame.Create(
                prepared,
                preserveMetadata && !pixelsChanged ? source.Thumbnail : null,
                metadata,
                preserveMetadata ? source.ColorContexts : null);
            frame.Freeze();
            result.Add(frame);
        }

        return outputFormat == ImageFormat.Ico
            ? PngIcoEncoder.CreateFrames(result[0])
            : result;
    }

    private static BitmapMetadata? ApplyMetadataNote(
        BitmapMetadata? metadata,
        ImageFormat sourceFormat,
        ImageFormat outputFormat,
        ImageMetadataNoteOptions? note)
    {
        if (note is not { Enabled: true } || string.IsNullOrWhiteSpace(note.Text))
        {
            return metadata;
        }

        var text = note.Text.Trim();
        var (container, queryPath, ifdPath, ifdName) = outputFormat switch
        {
            ImageFormat.Jpeg => ("jpg", "/app1/ifd/{ushort=40092}", "/app1/ifd", "ifd"),
            ImageFormat.Tiff => ("tiff", "/ifd/{ushort=40092}", "/ifd", "ifd"),
            ImageFormat.Png => ("png", "/tEXt/{str=Comment}", null, null),
            _ => ((string?)null, (string?)null, null, null)
        };
        if (container is null)
        {
            return metadata;
        }

        try
        {
            if (outputFormat == ImageFormat.Png)
            {
                var pngTarget = sourceFormat == ImageFormat.Png || metadata is null
                    ? metadata ?? new BitmapMetadata("png")
                    : new BitmapMetadata("png");
                pngTarget.SetQuery("/tEXt/{str=Description}", text);
                pngTarget.SetQuery("/tEXt/{str=Comment}", text);
                return pngTarget;
            }

            // The cloned metadata carries the SOURCE container (e.g. a PNG tree written
            // into a JPEG), and the JPEG encoder drops EXIF branches planted onto such a
            // tree. Only reuse it when the source was already the same family.
            var nativeSameFamily = outputFormat switch
            {
                ImageFormat.Jpeg => sourceFormat == ImageFormat.Jpeg,
                ImageFormat.Tiff => sourceFormat == ImageFormat.Tiff,
                _ => false
            };
            var target = metadata is not null && nativeSameFamily
                ? metadata
                : new BitmapMetadata(container);
            if (outputFormat == ImageFormat.Jpeg)
            {
                EnsureQueryBlock(target, "/app1", "app1");
            }

            EnsureQueryBlock(target, ifdPath, ifdName);
            // 270 (ImageDescription) is what Windows Explorer surfaces as 标题/主题;
            // 40092 (XPComment) is the legacy comment field kept for older readers.
            target.SetQuery(queryPath, text);
            var descriptionPath = outputFormat == ImageFormat.Jpeg
                ? "/app1/ifd/{ushort=270}"
                : "/ifd/{ushort=270}";
            target.SetQuery(descriptionPath, text);
            return target;
        }
        catch (Exception exception) when (
            exception is NotSupportedException or ArgumentException or InvalidOperationException or COMException)
        {
            return metadata;
        }
    }

    private static void EnsureQueryBlock(BitmapMetadata metadata, string? path, string? blockName)
    {
        if (path is null || blockName is null)
        {
            return;
        }

        try
        {
            if (metadata.GetQuery(path) is null)
            {
                metadata.SetQuery(path, new BitmapMetadata(blockName));
            }
        }
        catch (Exception exception) when (
            exception is NotSupportedException or ArgumentException or InvalidOperationException or COMException)
        {
        }
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, out bool changed)
    {
        var orientation = ReadExifOrientation(source.Metadata as BitmapMetadata);
        changed = orientation is >= 2 and <= 8;
        if (!changed)
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var swapsDimensions = orientation >= 5;
        var outputWidth = swapsDimensions ? height : width;
        var outputHeight = swapsDimensions ? width : height;
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, width, 0),
            3 => new Matrix(-1, 0, 0, -1, width, height),
            4 => new Matrix(1, 0, 0, -1, 0, height),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, height, 0),
            7 => new Matrix(0, -1, -1, 0, height, width),
            8 => new Matrix(0, -1, 1, 0, 0, width),
            _ => Matrix.Identity
        };

        return Render(
            source,
            outputWidth,
            outputHeight,
            drawing =>
            {
                drawing.PushTransform(new MatrixTransform(matrix));
                drawing.DrawImage(source, new Rect(0, 0, width, height));
                drawing.Pop();
            });
    }

    private static ushort ReadExifOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return 1;
        }

        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                {
                    return Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or InvalidCastException or OverflowException)
            {
            }
        }

        return 1;
    }

    private static BitmapMetadata? CloneMetadata(BitmapMetadata? source, bool normalizeOrientation)
    {
        if (source is null)
        {
            return null;
        }

        var clone = (BitmapMetadata)source.Clone();
        if (!normalizeOrientation)
        {
            return clone;
        }

        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (clone.ContainsQuery(query))
                {
                    clone.SetQuery(query, (ushort)1);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }

        return clone;
    }

    private static BitmapSource Resize(BitmapSource source, ImageResizeOptions resize)
    {
        if (resize.Width is <= 0 || resize.Height is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resize), "缩放尺寸必须为正数。");
        }

        if (resize.Width is null && resize.Height is null)
        {
            return source;
        }

        var targetWidth = resize.Width ?? source.PixelWidth;
        var targetHeight = resize.Height ?? source.PixelHeight;
        if (resize.PreserveAspectRatio)
        {
            var widthRatio = resize.Width is { } width
                ? width / (double)source.PixelWidth
                : double.PositiveInfinity;
            var heightRatio = resize.Height is { } height
                ? height / (double)source.PixelHeight
                : double.PositiveInfinity;
            var ratio = Math.Min(widthRatio, heightRatio);
            targetWidth = Math.Max(1, checked((int)Math.Round(source.PixelWidth * ratio)));
            targetHeight = Math.Max(1, checked((int)Math.Round(source.PixelHeight * ratio)));
        }

        return Render(
            source,
            targetWidth,
            targetHeight,
            drawing => drawing.DrawImage(source, new Rect(0, 0, targetWidth, targetHeight)));
    }

    private static BitmapSource AdjustBrightness(BitmapSource source, int brightnessPercent)
    {
        if (brightnessPercent is < 0 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(brightnessPercent), "亮度必须在 0 到 200 之间。");
        }

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var scanline = new byte[stride];
        var output = new WriteableBitmap(
            converted.PixelWidth,
            converted.PixelHeight,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            PixelFormats.Bgra32,
            null);
        var factor = brightnessPercent / 100d;
        for (var y = 0; y < converted.PixelHeight; y++)
        {
            converted.CopyPixels(new Int32Rect(0, y, converted.PixelWidth, 1), scanline, stride, 0);
            for (var offset = 0; offset < stride; offset += 4)
            {
                scanline[offset] = ScaleChannel(scanline[offset], factor);
                scanline[offset + 1] = ScaleChannel(scanline[offset + 1], factor);
                scanline[offset + 2] = ScaleChannel(scanline[offset + 2], factor);
            }

            output.WritePixels(new Int32Rect(0, y, converted.PixelWidth, 1), scanline, stride, 0);
        }

        output.Freeze();
        return output;
    }

    private static byte ScaleChannel(byte value, double factor) =>
        (byte)Math.Min(byte.MaxValue, Math.Round(value * factor));

    private static readonly Random WatermarkPositionRandom = new();

    private static ImageWatermarkPosition NextRandomWatermarkPosition()
    {
        lock (WatermarkPositionRandom)
        {
            return (ImageWatermarkPosition)WatermarkPositionRandom.Next(0, 5);
        }
    }

    private static BitmapSource ApplyWatermark(BitmapSource source, ImageWatermarkOptions watermark)
    {
        if (watermark.OpacityPercent is < 0 or > 100 || watermark.FontSize <= 0 || watermark.Margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(watermark), "水印参数无效。");
        }

        var color = ParseColor(watermark.ColorHex);
        color.A = (byte)Math.Round(byte.MaxValue * watermark.OpacityPercent / 100d);
        var text = new FormattedText(
            watermark.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            watermark.FontSize,
            new SolidColorBrush(color),
            source.DpiX > 0 ? source.DpiX / 96d : 1d);
        var textWidth = text.WidthIncludingTrailingWhitespace;
        var textHeight = text.Height;
        var position = watermark.Position == ImageWatermarkPosition.Random
            ? NextRandomWatermarkPosition()
            : watermark.Position;
        double x;
        double y;
        switch (position)
        {
            case ImageWatermarkPosition.TopLeft:
                x = watermark.Margin;
                y = watermark.Margin;
                break;
            case ImageWatermarkPosition.TopRight:
                x = source.PixelWidth - watermark.Margin - textWidth;
                y = watermark.Margin;
                break;
            case ImageWatermarkPosition.BottomLeft:
                x = watermark.Margin;
                y = source.PixelHeight - watermark.Margin - textHeight;
                break;
            case ImageWatermarkPosition.Center:
                x = (source.PixelWidth - textWidth) / 2d;
                y = (source.PixelHeight - textHeight) / 2d;
                break;
            case ImageWatermarkPosition.BottomRight:
            default:
                x = source.PixelWidth - watermark.Margin - textWidth;
                y = source.PixelHeight - watermark.Margin - textHeight;
                break;
        }

        x = Math.Max(0, x);
        y = Math.Max(0, y);

        return Render(
            source,
            source.PixelWidth,
            source.PixelHeight,
            drawing =>
            {
                drawing.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                drawing.DrawText(text, new Point(x, y));
            });
    }

    private static BitmapSource Render(
        BitmapSource source,
        int width,
        int height,
        Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        using (var drawing = visual.RenderOpen())
        {
            draw(drawing);
        }

        var rendered = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static BitmapSource Flatten(BitmapSource source, Color color)
    {
        color.A = byte.MaxValue;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            drawing.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        var rendered = new RenderTargetBitmap(
            source.PixelWidth,
            source.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("背景颜色格式无效。", nameof(value), exception);
        }
    }

    private static bool SupportsAlpha(ImageFormat format) =>
        format is ImageFormat.Png or ImageFormat.Webp or ImageFormat.Gif or ImageFormat.Ico;

    private static void Write(
        IReadOnlyList<BitmapFrame> frames,
        string outputPath,
        ImageFormat outputFormat,
        int quality,
        CancellationToken cancellationToken)
    {
        if (outputFormat == ImageFormat.Webp)
        {
            var pngPath = CreateSiblingTemporaryPath(outputPath, "webp-source", ".png");
            try
            {
                WriteWic(frames, pngPath, ImageFormat.Png, quality);
                LibWebpTools.EncodeFromPng(pngPath, outputPath, quality, cancellationToken);
            }
            finally
            {
                DeleteIfExists(pngPath);
            }

            return;
        }

        if (outputFormat == ImageFormat.Ico)
        {
            PngIcoEncoder.Write(frames, outputPath);
            return;
        }

        WriteWic(frames, outputPath, outputFormat, quality);
    }

    private static void WriteWic(
        IReadOnlyList<BitmapFrame> frames,
        string outputPath,
        ImageFormat outputFormat,
        int quality)
    {
        var encoder = CreateEncoder(outputFormat, quality);
        var frameCount = outputFormat is ImageFormat.Gif or ImageFormat.Tiff ? frames.Count : 1;
        for (var index = 0; index < frameCount; index++)
        {
            encoder.Frames.Add(frames[index]);
        }

        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65_536,
            useAsync: false);
        encoder.Save(output);
        output.Flush(flushToDisk: true);
    }

    private static string CreateSiblingTemporaryPath(string outputPath, string purpose, string extension)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("输出路径缺少目录。");
        return Path.Combine(directory, ".nanopic-" + purpose + "-" + Guid.NewGuid().ToString("N") + extension);
    }

    private static void DeleteIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static BitmapEncoder CreateEncoder(ImageFormat outputFormat, int quality) => outputFormat switch
    {
        ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = quality },
        ImageFormat.Png => new PngBitmapEncoder(),
        ImageFormat.Gif => new GifBitmapEncoder(),
        ImageFormat.Bmp => new BmpBitmapEncoder(),
        ImageFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
        _ => throw new NotSupportedException("WIC 不支持所选输出格式。")
    };

    private static CoreImageMetadata CreateMetadata(
        IReadOnlyList<BitmapFrame> frames,
        ImageFormat format,
        long sourceBytes)
    {
        var first = frames[0];
        return new CoreImageMetadata(
            format,
            first.PixelWidth,
            first.PixelHeight,
            frames.Count,
            SupportsAlpha(format) && frames.Any(HasAlpha),
            sourceBytes);
    }

    private static bool HasAlpha(BitmapSource source)
    {
        if (HasGifTransparency(source))
        {
            return true;
        }

        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return false;
        }

        var pixels = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(pixels.PixelWidth * 4);
        var scanline = new byte[stride];
        for (var y = 0; y < pixels.PixelHeight; y++)
        {
            pixels.CopyPixels(new Int32Rect(0, y, pixels.PixelWidth, 1), scanline, stride, 0);
            for (var alpha = 3; alpha < stride; alpha += 4)
            {
                if (scanline[alpha] < byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasGifTransparency(BitmapSource source)
    {
        const string transparencyFlag = "/grctlext/TransparencyFlag";
        if (source.Metadata is not BitmapMetadata metadata)
        {
            return false;
        }

        try
        {
            return metadata.ContainsQuery(transparencyFlag) &&
                   metadata.GetQuery(transparencyFlag) is bool enabled &&
                   enabled;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
