using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using ImageMagick.Drawing;
using NanoPic.Core;

namespace NanoPic.Codecs;

public sealed class MagickNetImageCodec : IImageCodec
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

    public async Task<ImageOperationResult<ImageMetadata>> IdentifyAsync(Stream input, CancellationToken cancellationToken)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        var signature = await ImageFileSignatureInspector.DetectAsync(input, cancellationToken).ConfigureAwait(false);
        if (!signature.IsSuccess)
        {
            return new ImageOperationResult<ImageMetadata>(default, signature.Failure);
        }

        if (!Formats.Contains(signature.Value))
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.UnsupportedFormat,
                "图像格式不受当前编解码器支持。");
        }

        try
        {
            var metadata = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var images = new MagickImageCollection();
                images.Ping(input, new MagickReadSettings { Format = ToMagickFormat(signature.Value) });
                if (images.Count == 0)
                {
                    return ImageOperationResult<ImageMetadata>.Failed(
                        ImageFailureKind.DecodeFailed,
                        "图像不包含可读取的帧。");
                }

                var firstFrame = images[0];
                var sourceBytes = input.CanSeek ? input.Length : 0L;
                return ImageOperationResult<ImageMetadata>.Success(new ImageMetadata(
                    signature.Value,
                    checked((int)firstFrame.Width),
                    checked((int)firstFrame.Height),
                    checked((int)images.Count),
                    firstFrame.HasAlpha,
                    sourceBytes));
            }, cancellationToken).ConfigureAwait(false);

            return metadata;
        }
        catch (OperationCanceledException)
        {
            return ImageOperationResult<ImageMetadata>.Failed(ImageFailureKind.TaskCanceled, "图像识别已取消。");
        }
        catch (MagickException exception)
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.DecodeFailed,
                "无法读取图像数据。",
                exception);
        }
        catch (OverflowException exception)
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.PixelBudgetExceeded,
                "图像尺寸超出支持范围。",
                exception);
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
                "输入或输出格式不受当前编解码器支持。");
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
                using var source = new FileStream(
                    request.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 65_536,
                    useAsync: false);
                using var images = new MagickImageCollection(
                    source,
                    new MagickReadSettings { Format = ToMagickFormat(request.SourceFormat) });
                if (images.Count == 0)
                {
                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                        ImageFailureKind.DecodeFailed,
                        "图像不包含可处理的帧。");
                }

                ApplyTransforms(images, request.Transform, request.OutputFormat);

                var initialMetadata = CreateMetadata(images, request.OutputFormat, sourceBytes: 0L);
                var safetyFailure = ImageSafetyValidator.Validate(initialMetadata, request.SafetyLimits);
                if (safetyFailure is not null)
                {
                    return new ImageOperationResult<ImageEncodedOutput>(default, safetyFailure);
                }

                var effectiveQuality = request.Encoding.Quality;
                var targetReached = true;
                var exceededTarget = false;
                if (request.Encoding.TargetSize is { } targetSize)
                {
                    var targetSearch = await TargetSizeSearch.FindAsync(
                        targetSize,
                        async (quality, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            Write(images, request.TemporaryOutputPath, request.OutputFormat, quality);
                            var bytes = new FileInfo(request.TemporaryOutputPath).Length;
                            return await Task.FromResult(ImageOperationResult<long>.Success(bytes)).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (!targetSearch.IsSuccess || targetSearch.Value is null)
                    {
                        return new ImageOperationResult<ImageEncodedOutput>(default, targetSearch.Failure);
                    }

                    effectiveQuality = targetSearch.Value.Selected.Quality;
                    targetReached = targetSearch.Value.TargetReached;
                    exceededTarget = targetSearch.Value.ExceededTarget;
                    Write(images, request.TemporaryOutputPath, request.OutputFormat, effectiveQuality);
                }
                else
                {
                    Write(images, request.TemporaryOutputPath, request.OutputFormat, effectiveQuality);
                }

                var bytes = new FileInfo(request.TemporaryOutputPath).Length;
                if (bytes <= 0)
                {
                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                        ImageFailureKind.OutputVerificationFailed,
                        "编码器未写出有效图像数据。");
                }

                var outputMetadata = CreateMetadata(images, request.OutputFormat, bytes);
                return ImageOperationResult<ImageEncodedOutput>.Success(new ImageEncodedOutput(
                    outputMetadata,
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
        catch (MagickException exception)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.DecodeFailed,
                "图像变换或编码失败。",
                exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "图像处理参数无效。",
                exception);
        }
        catch (IOException exception)
        {
            return ImageOperationResult<ImageEncodedOutput>.Failed(
                ImageFailureKind.FileAccessConflict,
                "无法写入图像输出文件。",
                exception);
        }
    }

    private static void ApplyTransforms(MagickImageCollection images, ImageTransformOptions transform, ImageFormat outputFormat)
    {
        foreach (var image in images)
        {
            if (transform.AutoOrient)
            {
                ApplyExifOrientation(image);
                image.AutoOrient();
            }

            if (transform.Resize is { Enabled: true } resize)
            {
                ApplyResize(image, resize);
            }

            if (transform.BrightnessPercent != 100)
            {
                image.Evaluate(Channels.All, EvaluateOperator.Multiply, transform.BrightnessPercent / 100d);
            }

            if (transform.Watermark is { Enabled: true } watermark && !string.IsNullOrWhiteSpace(watermark.Text))
            {
                ApplyWatermark(image, watermark);
            }

            if (transform.Background is { FlattenTransparency: true } background && SupportsAlpha(outputFormat) is false)
            {
                image.BackgroundColor = new MagickColor(background.ColorHex);
                image.Alpha(AlphaOption.Remove);
            }

            if (transform.StripMetadata)
            {
                image.Strip();
            }
        }
    }

    private static void ApplyExifOrientation(IMagickImage<byte> image)
    {
        var exifOrientation = image.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
        if (exifOrientation is ushort orientation &&
            orientation != (ushort)OrientationType.TopLeft &&
            Enum.IsDefined(typeof(OrientationType), (OrientationType)orientation))
        {
            image.Orientation = (OrientationType)orientation;
        }
    }

    private static void ApplyResize(IMagickImage image, ImageResizeOptions resize)
    {
        if (resize.Width is <= 0 || resize.Height is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resize), "缩放尺寸必须为正数。");
        }

        var targetWidth = resize.Width ?? checked((int)image.Width);
        var targetHeight = resize.Height ?? checked((int)image.Height);
        if (resize.PreserveAspectRatio && (resize.Width is null || resize.Height is null))
        {
            var ratio = resize.Width is { } width
                ? width / (double)image.Width
                : targetHeight / (double)image.Height;
            targetWidth = Math.Max(1, checked((int)Math.Round(image.Width * ratio)));
            targetHeight = Math.Max(1, checked((int)Math.Round(image.Height * ratio)));
        }

        var geometry = new MagickGeometry((uint)targetWidth, (uint)targetHeight)
        {
            IgnoreAspectRatio = !resize.PreserveAspectRatio
        };
        image.Resize(geometry);
    }

    private static void ApplyWatermark(IMagickImage<byte> image, ImageWatermarkOptions watermark)
    {
        if (watermark.OpacityPercent is < 0 or > 100 || watermark.FontSize <= 0 || watermark.Margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(watermark), "水印参数无效。");
        }

        var color = new MagickColor(watermark.ColorHex)
        {
            A = (byte)Math.Round(255d * watermark.OpacityPercent / 100d)
        };
        var x = checked((int)image.Width - watermark.Margin);
        var y = checked((int)image.Height - watermark.Margin);
        new Drawables()
            .FillColor(color)
            .FontPointSize(watermark.FontSize)
            .TextAlignment(TextAlignment.Right)
            .Text(x, y, watermark.Text)
            .Draw(image);
    }

    private static void Write(MagickImageCollection images, string outputPath, ImageFormat outputFormat, int quality)
    {
        foreach (var image in images)
        {
            image.Format = ToMagickFormat(outputFormat);
            image.Quality = (uint)quality;
        }

        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65_536,
            useAsync: false);
        images.Write(output, ToMagickFormat(outputFormat));
    }

    private static ImageMetadata CreateMetadata(MagickImageCollection images, ImageFormat format, long sourceBytes)
    {
        var firstFrame = images[0];
        return new ImageMetadata(
            format,
            checked((int)firstFrame.Width),
            checked((int)firstFrame.Height),
            checked((int)images.Count),
            firstFrame.HasAlpha,
            sourceBytes);
    }

    private static bool SupportsAlpha(ImageFormat format) => format is ImageFormat.Png or ImageFormat.Webp or ImageFormat.Gif or ImageFormat.Ico;

    private static MagickFormat ToMagickFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => MagickFormat.Jpeg,
        ImageFormat.Png => MagickFormat.Png,
        ImageFormat.Webp => MagickFormat.WebP,
        ImageFormat.Gif => MagickFormat.Gif,
        ImageFormat.Bmp => MagickFormat.Bmp,
        ImageFormat.Tiff => MagickFormat.Tiff,
        ImageFormat.Ico => MagickFormat.Icon,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的输出格式。")
    };
}
