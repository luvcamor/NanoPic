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
    private sealed record FramePreparationResult(
        IReadOnlyList<BitmapFrame> Frames,
        bool PixelsChanged,
        bool MetadataChanged,
        bool OrientationChanged);

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
                        decodedWebpPath = CreateTemporaryPath("decoded", ".png");
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
                var isDownscalingPlanned = request.SafetyLimits.AutoDownscaleOnExceed || request.Transform.Resize is { Enabled: true };
                var sourceSafetyLimits = isDownscalingPlanned
                    ? request.SafetyLimits with { MaxWidth = int.MaxValue, MaxHeight = int.MaxValue, MaxPixels = long.MaxValue }
                    : request.SafetyLimits;
                var safetyFailure = ImageSafetyValidator.Validate(sourceMetadata, sourceSafetyLimits);
                if (safetyFailure is not null)
                {
                    return new ImageOperationResult<ImageEncodedOutput>(default, safetyFailure);
                }

                var preparation = PrepareFrames(
                    sourceFrames,
                    request.SourceFormat,
                    request.OutputFormat,
                    request.Transform);
                var outputFrames = preparation.Frames;
                var outputMetadata = CreateMetadata(outputFrames, request.OutputFormat, sourceBytes: 0L);
                var outputSafetyFailure = ImageSafetyValidator.Validate(outputMetadata, request.SafetyLimits);
                if (outputSafetyFailure is not null)
                {
                    return new ImageOperationResult<ImageEncodedOutput>(default, outputSafetyFailure);
                }

                var effectiveQuality = request.Encoding.Quality;
                var targetReached = true;
                var exceededTarget = false;
                var targetSizeResized = false;
                string? targetSizeNotice = null;
                var outputAlreadyWritten = false;
                var canReusePngSource = CanReuseSourceFile(request, preparation);

                if (request.Encoding.TargetSize is { } targetSize)
                {
                    if (request.OutputFormat == ImageFormat.Png && canReusePngSource)
                    {
                        var sourceFileBytes = new FileInfo(request.SourcePath).Length;
                        if (sourceFileBytes > 0 && sourceFileBytes <= targetSize.TargetBytes)
                        {
                            File.Copy(request.SourcePath, request.TemporaryOutputPath, overwrite: true);
                            return ImageOperationResult<ImageEncodedOutput>.Success(
                                new ImageEncodedOutput(
                                    outputMetadata with { SourceBytes = sourceFileBytes },
                                    Quality: 100,
                                    Bytes: sourceFileBytes,
                                    TargetSizeReached: true,
                                    ExceededTarget: false));
                        }
                    }

                    if (request.OutputFormat == ImageFormat.Png)
                    {
                        var candidatePaths = new Dictionary<string, string>(StringComparer.Ordinal);
                        try
                        {
                            var pngSearch = await PngTargetSizeSearch.SearchAsync(
                                outputFrames,
                                request.TemporaryOutputPath,
                                targetSize,
                                (frames, _, q, token) =>
                                {
                                    token.ThrowIfCancellationRequested();
                                    var key = CreatePngCandidateKey(frames, q);
                                    if (candidatePaths.TryGetValue(key, out var cachedPath))
                                    {
                                        return Task.FromResult(new FileInfo(cachedPath).Length);
                                    }

                                    var candidatePath = CreateTemporaryPath("png-candidate", ".png");
                                    try
                                    {
                                        WritePng(frames, candidatePath, q, token);
                                        candidatePaths.Add(key, candidatePath);
                                        return Task.FromResult(new FileInfo(candidatePath).Length);
                                    }
                                    catch
                                    {
                                        DeleteIfExists(candidatePath);
                                        throw;
                                    }
                                },
                                (frames, resizeOptions) =>
                                {
                                    var newFrames = new List<BitmapFrame>(frames.Count);
                                    foreach (var frame in frames)
                                    {
                                        var resizedSource = Resize(frame, resizeOptions);
                                        var newFrame = BitmapFrame.Create(
                                            resizedSource,
                                            null,
                                            frame.Metadata as BitmapMetadata,
                                            null);
                                        newFrame.Freeze();
                                        newFrames.Add(newFrame);
                                    }
                                    return newFrames;
                                },
                                cancellationToken).ConfigureAwait(false);

                            if (!pngSearch.IsSuccess || pngSearch.Value is null)
                            {
                                return new ImageOperationResult<ImageEncodedOutput>(default, pngSearch.Failure);
                            }

                            var result = pngSearch.Value;
                            var selectedKey = CreatePngCandidateKey(result.FinalFrames, result.Selected.Quality);
                            if (!candidatePaths.TryGetValue(selectedKey, out var selectedPath))
                            {
                                return ImageOperationResult<ImageEncodedOutput>.Failed(
                                    ImageFailureKind.OutputVerificationFailed,
                                    "PNG 目标大小搜索缺少选中候选产物。");
                            }

                            File.Copy(selectedPath, request.TemporaryOutputPath, overwrite: true);
                            effectiveQuality = result.Selected.Quality;
                            targetReached = result.TargetReached;
                            exceededTarget = result.ExceededTarget;
                            targetSizeResized = result.Resized;
                            targetSizeNotice = result.Notice;
                            outputFrames = result.FinalFrames;
                            outputMetadata = CreateMetadata(outputFrames, request.OutputFormat, sourceBytes: 0L);
                            outputAlreadyWritten = true;
                        }
                        finally
                        {
                            foreach (var candidatePath in candidatePaths.Values)
                            {
                                DeleteIfExists(candidatePath);
                            }
                        }
                    }
                    else
                    {
                        var maxAdaptivePasses = 5;
                        TargetSizeSearchResult? successfulSearch = null;
                        var formatSupportsQuality = SupportsQualitySearch(request.OutputFormat);

                        for (var pass = 0; pass < maxAdaptivePasses; pass++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (formatSupportsQuality)
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

                                if (targetSearch.IsSuccess && targetSearch.Value is { TargetReached: true })
                                {
                                    successfulSearch = targetSearch.Value;
                                    break;
                                }

                                if (targetSearch.IsSuccess && targetSearch.Value is { ExceededTarget: true } && targetSize.AllowExceed)
                                {
                                    successfulSearch = targetSearch.Value;
                                    break;
                                }

                                if (!targetSize.AllowResizeForTarget)
                                {
                                    if (targetSize.AllowExceed && targetSearch.IsSuccess && targetSearch.Value is { Selected: { } selCandidate })
                                    {
                                        successfulSearch = targetSearch.Value;
                                        break;
                                    }

                                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                                        ImageFailureKind.TargetSizeUnreachable,
                                        "无法在当前尺寸下达到目标大小。可开启“允许缩小图片尺寸”以进一步压缩。");
                                }

                                if (!targetSize.AllowExceed && targetSearch.Value is { Selected: { } smallestCandidate } && smallestCandidate.Bytes > targetSize.TargetBytes)
                                {
                                    var areaRatio = (double)targetSize.TargetBytes / smallestCandidate.Bytes;
                                    var lengthScale = Math.Min(0.92, Math.Sqrt(areaRatio) * 0.95);

                                    var currentWidth = outputFrames[0].PixelWidth;
                                    var currentHeight = outputFrames[0].PixelHeight;
                                    var newWidth = Math.Max(16, (int)Math.Floor(currentWidth * lengthScale));
                                    var newHeight = Math.Max(16, (int)Math.Floor(currentHeight * lengthScale));

                                    if (newWidth <= 16 || newHeight <= 16 || (newWidth == currentWidth && newHeight == currentHeight))
                                    {
                                        break;
                                    }

                                    var adaptiveResize = new ImageResizeOptions(Enabled: true, Width: newWidth, Height: newHeight, PreserveAspectRatio: true);
                                    var newFrames = new List<BitmapFrame>(outputFrames.Count);
                                    foreach (var frame in outputFrames)
                                    {
                                        var resizedSource = Resize(frame, adaptiveResize);
                                        var newFrame = BitmapFrame.Create(
                                            resizedSource,
                                            null,
                                            frame.Metadata as BitmapMetadata,
                                            null);
                                        newFrame.Freeze();
                                        newFrames.Add(newFrame);
                                    }

                                    outputFrames = newFrames;
                                    outputMetadata = CreateMetadata(outputFrames, request.OutputFormat, sourceBytes: 0L);
                                    targetSizeResized = true;
                                    targetSizeNotice = $"为达到目标大小已调整分辨率: {sourceMetadata.Width}×{sourceMetadata.Height} → {newWidth}×{newHeight}";
                                    continue;
                                }

                                if (!targetSearch.IsSuccess && targetSearch.Failure?.Kind == ImageFailureKind.TargetSizeUnreachable)
                                {
                                    if (!targetSize.AllowResizeForTarget)
                                    {
                                        return new ImageOperationResult<ImageEncodedOutput>(default, targetSearch.Failure);
                                    }

                                    Write(outputFrames, request.TemporaryOutputPath, request.OutputFormat, 1, cancellationToken);
                                    var currentBytes = new FileInfo(request.TemporaryOutputPath).Length;
                                    if (currentBytes <= targetSize.TargetBytes)
                                    {
                                        successfulSearch = new TargetSizeSearchResult(
                                            new TargetSizeCandidate(1, currentBytes),
                                            TargetReached: true, ExceededTarget: false,
                                            new List<TargetSizeCandidate>());
                                        break;
                                    }

                                    var areaRatio = (double)targetSize.TargetBytes / currentBytes;
                                    var lengthScale = Math.Min(0.92, Math.Sqrt(areaRatio) * 0.95);

                                    var currentWidth = outputFrames[0].PixelWidth;
                                    var currentHeight = outputFrames[0].PixelHeight;
                                    var newWidth = Math.Max(16, (int)Math.Floor(currentWidth * lengthScale));
                                    var newHeight = Math.Max(16, (int)Math.Floor(currentHeight * lengthScale));

                                    if (newWidth <= 16 || newHeight <= 16 || (newWidth == currentWidth && newHeight == currentHeight))
                                    {
                                        break;
                                    }

                                    var adaptiveResize = new ImageResizeOptions(Enabled: true, Width: newWidth, Height: newHeight, PreserveAspectRatio: true);
                                    var newFrames = new List<BitmapFrame>(outputFrames.Count);
                                    foreach (var frame in outputFrames)
                                    {
                                        var resizedSource = Resize(frame, adaptiveResize);
                                        var newFrame = BitmapFrame.Create(
                                            resizedSource,
                                            null,
                                            frame.Metadata as BitmapMetadata,
                                            null);
                                        newFrame.Freeze();
                                        newFrames.Add(newFrame);
                                    }

                                    outputFrames = newFrames;
                                    outputMetadata = CreateMetadata(outputFrames, request.OutputFormat, sourceBytes: 0L);
                                    targetSizeResized = true;
                                    targetSizeNotice = $"为达到目标大小已调整分辨率: {sourceMetadata.Width}×{sourceMetadata.Height} → {newWidth}×{newHeight}";
                                    continue;
                                }

                                if (!targetSearch.IsSuccess)
                                {
                                    return new ImageOperationResult<ImageEncodedOutput>(default, targetSearch.Failure);
                                }

                                successfulSearch = targetSearch.Value;
                                break;
                            }
                            else
                            {
                                // 格式不支持 quality（BMP, GIF, TIFF, ICO）
                                Write(outputFrames, request.TemporaryOutputPath, request.OutputFormat, effectiveQuality, cancellationToken);
                                var currentBytes = new FileInfo(request.TemporaryOutputPath).Length;
                                if (currentBytes <= targetSize.TargetBytes)
                                {
                                    successfulSearch = new TargetSizeSearchResult(
                                        new TargetSizeCandidate(effectiveQuality, currentBytes),
                                        TargetReached: true,
                                        ExceededTarget: false,
                                        new List<TargetSizeCandidate>());
                                    break;
                                }

                                if (targetSize.AllowExceed)
                                {
                                    successfulSearch = new TargetSizeSearchResult(
                                        new TargetSizeCandidate(effectiveQuality, currentBytes),
                                        TargetReached: false,
                                        ExceededTarget: true,
                                        new List<TargetSizeCandidate>());
                                    break;
                                }

                                if (!targetSize.AllowResizeForTarget)
                                {
                                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                                        ImageFailureKind.TargetSizeUnreachable,
                                        "无法在当前尺寸下达到目标大小。可开启“允许缩小图片尺寸”以进一步压缩。");
                                }

                                var areaRatio = (double)targetSize.TargetBytes / currentBytes;
                                var lengthScale = Math.Min(0.92, Math.Sqrt(areaRatio) * 0.95);

                                var currentWidth = outputFrames[0].PixelWidth;
                                var currentHeight = outputFrames[0].PixelHeight;
                                var newWidth = Math.Max(16, (int)Math.Floor(currentWidth * lengthScale));
                                var newHeight = Math.Max(16, (int)Math.Floor(currentHeight * lengthScale));

                                if (newWidth <= 16 || newHeight <= 16 || (newWidth == currentWidth && newHeight == currentHeight))
                                {
                                    break;
                                }

                                var adaptiveResize = new ImageResizeOptions(Enabled: true, Width: newWidth, Height: newHeight, PreserveAspectRatio: true);
                                var newFrames = new List<BitmapFrame>(outputFrames.Count);
                                foreach (var frame in outputFrames)
                                {
                                    var resizedSource = Resize(frame, adaptiveResize);
                                    var newFrame = BitmapFrame.Create(
                                        resizedSource,
                                        null,
                                        frame.Metadata as BitmapMetadata,
                                        null);
                                    newFrame.Freeze();
                                    newFrames.Add(newFrame);
                                }

                                outputFrames = newFrames;
                                outputMetadata = CreateMetadata(outputFrames, request.OutputFormat, sourceBytes: 0L);
                                targetSizeResized = true;
                                targetSizeNotice = $"为达到目标大小已调整分辨率: {sourceMetadata.Width}×{sourceMetadata.Height} → {newWidth}×{newHeight}";
                            }
                        }

                        if (successfulSearch is null)
                        {
                            return ImageOperationResult<ImageEncodedOutput>.Failed(
                                ImageFailureKind.TargetSizeUnreachable,
                                "无法在当前尺寸下达到目标大小，即使降低分辨率后依然超出上限。");
                        }

                        effectiveQuality = successfulSearch.Selected.Quality;
                        targetReached = successfulSearch.TargetReached;
                        exceededTarget = successfulSearch.ExceededTarget;
                    }
                }

                if (!outputAlreadyWritten)
                {
                    Write(outputFrames, request.TemporaryOutputPath, request.OutputFormat, effectiveQuality, cancellationToken);
                }
                var bytes = new FileInfo(request.TemporaryOutputPath).Length;
                if (bytes <= 0)
                {
                    return ImageOperationResult<ImageEncodedOutput>.Failed(
                        ImageFailureKind.OutputVerificationFailed,
                        "WIC 编码器未写出有效图像数据。");
                }

                // skip-if-larger: 纯 no-op 重编码若变大则保留源文件
                if (canReusePngSource)
                {
                    var sourceFileBytes = new FileInfo(request.SourcePath).Length;
                    if (bytes >= sourceFileBytes && sourceFileBytes > 0)
                    {
                        File.Copy(request.SourcePath, request.TemporaryOutputPath, overwrite: true);
                        bytes = sourceFileBytes;
                        if (request.Encoding.TargetSize is { } finalTarget)
                        {
                            targetReached = sourceFileBytes <= finalTarget.TargetBytes;
                            exceededTarget = !targetReached;
                        }
                    }
                }

                return ImageOperationResult<ImageEncodedOutput>.Success(
                    new ImageEncodedOutput(
                        outputMetadata with { SourceBytes = bytes },
                        effectiveQuality,
                        bytes,
                        targetReached,
                        exceededTarget,
                        targetSizeResized,
                        targetSizeNotice));
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

    public static bool SupportsQualitySearch(ImageFormat format) =>
        format is ImageFormat.Jpeg or ImageFormat.Webp or ImageFormat.Png;

    private static FramePreparationResult PrepareFrames(
        IReadOnlyList<BitmapFrame> sourceFrames,
        ImageFormat sourceFormat,
        ImageFormat outputFormat,
        ImageTransformOptions transform)
    {
        var frameCount = outputFormat is ImageFormat.Gif or ImageFormat.Tiff ? sourceFrames.Count : 1;
        var result = new List<BitmapFrame>(frameCount);
        var anyPixelsChanged = false;
        var anyOrientationChanged = false;
        var anyMetadataChanged = transform.StripMetadata ||
            transform.MetadataNote is { Enabled: true } note && !string.IsNullOrWhiteSpace(note.Text);
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
                anyOrientationChanged |= orientationChanged;
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
                preserveMetadata && !pixelsChanged ? source.ColorContexts : null);
            frame.Freeze();
            result.Add(frame);
            anyPixelsChanged |= pixelsChanged;
        }

        IReadOnlyList<BitmapFrame> finalFrames = result;
        if (outputFormat == ImageFormat.Ico)
        {
            var bestFrame = result.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).FirstOrDefault() ?? result[0];
            finalFrames = PngIcoEncoder.CreateFrames(bestFrame);
        }

        anyMetadataChanged |= anyOrientationChanged;
        return new FramePreparationResult(
            finalFrames,
            anyPixelsChanged,
            anyMetadataChanged,
            anyOrientationChanged);
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

        var dpiX = source.DpiX > 0 ? source.DpiX : 96;
        var dpiY = source.DpiY > 0 ? source.DpiY : 96;
        if (Math.Abs(dpiX - 96) > 0.01 || Math.Abs(dpiY - 96) > 0.01)
        {
            var stride = checked(width * 4);
            var pixelBuffer = new byte[stride * height];
            rendered.CopyPixels(pixelBuffer, stride, 0);
            var dpiPreserved = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Pbgra32,
                null,
                pixelBuffer,
                stride);
            dpiPreserved.Freeze();
            return dpiPreserved;
        }

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

        var dpiX = source.DpiX > 0 ? source.DpiX : 96;
        var dpiY = source.DpiY > 0 ? source.DpiY : 96;
        if (Math.Abs(dpiX - 96) > 0.01 || Math.Abs(dpiY - 96) > 0.01)
        {
            var stride = checked(source.PixelWidth * 4);
            var pixelBuffer = new byte[stride * source.PixelHeight];
            rendered.CopyPixels(pixelBuffer, stride, 0);
            var dpiPreserved = BitmapSource.Create(
                source.PixelWidth,
                source.PixelHeight,
                dpiX,
                dpiY,
                PixelFormats.Pbgra32,
                null,
                pixelBuffer,
                stride);
            dpiPreserved.Freeze();
            return dpiPreserved;
        }

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

    private static bool CanReuseSourceFile(
        ImageEncodeRequest request,
        FramePreparationResult preparation)
    {
        if (request.SourceFormat != ImageFormat.Png || request.OutputFormat != ImageFormat.Png)
        {
            return false;
        }

        return !preparation.PixelsChanged && !preparation.MetadataChanged;
    }

    private static void Write(
        IReadOnlyList<BitmapFrame> frames,
        string outputPath,
        ImageFormat outputFormat,
        int quality,
        CancellationToken cancellationToken)
    {
        if (outputFormat == ImageFormat.Webp)
        {
            var pngPath = CreateTemporaryPath("webp-source", ".png");
            try
            {
                WriteWic(frames, pngPath, ImageFormat.Png, 100);
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

        if (outputFormat == ImageFormat.Png)
        {
            WritePng(frames, outputPath, quality, cancellationToken);
            return;
        }

        WriteWic(frames, outputPath, outputFormat, quality);
    }

    private static void WritePng(
        IReadOnlyList<BitmapFrame> frames,
        string outputPath,
        int quality,
        CancellationToken cancellationToken)
    {
        var encoder = new PngBitmapEncoder();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var quantized = PngQuantizer.Quantize(frame, quality, cancellationToken, out var info);
            var pixelsChanged = info.WasLossy || quantized.Format != frame.Format;

            var pngFrame = BitmapFrame.Create(
                quantized,
                pixelsChanged ? null : frame.Thumbnail,
                frame.Metadata as BitmapMetadata,
                pixelsChanged ? null : frame.ColorContexts);
            pngFrame.Freeze();
            encoder.Frames.Add(pngFrame);
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

    private static string CreatePngCandidateKey(
        IReadOnlyList<BitmapFrame> frames,
        int quality) =>
        quality.ToString(CultureInfo.InvariantCulture) + ":" +
        string.Join(
            "|",
            frames.Select(frame =>
                frame.PixelWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                frame.PixelHeight.ToString(CultureInfo.InvariantCulture)));

    private static string CreateTemporaryPath(string purpose, string extension)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NanoPic", "temp");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, ".nanopic-" + purpose + "-" + Guid.NewGuid().ToString("N") + extension);
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
        var bestFrame = frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).FirstOrDefault() ?? frames[0];
        var orientation = ReadExifOrientation(bestFrame.Metadata as BitmapMetadata);
        return new CoreImageMetadata(
            format,
            bestFrame.PixelWidth,
            bestFrame.PixelHeight,
            frames.Count,
            SupportsAlpha(format) && frames.Any(HasAlpha),
            sourceBytes,
            orientation == 0 ? 1 : (int)orientation);
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
