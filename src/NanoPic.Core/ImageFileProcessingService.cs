using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NanoPic.Core;

public sealed class ImageFileProcessingService
{
    private sealed class KeyedLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    private static readonly object LocksGate = new();
    private static readonly Dictionary<string, KeyedLockEntry> DestinationLocks = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<IDisposable> AcquireDestinationLockAsync(string path, CancellationToken cancellationToken)
    {
        var canonicalKey = Path.GetFullPath(PortablePath.ForFileSystem(path));
        KeyedLockEntry entry;
        lock (LocksGate)
        {
            if (!DestinationLocks.TryGetValue(canonicalKey, out entry!))
            {
                entry = new KeyedLockEntry();
                DestinationLocks[canonicalKey] = entry;
            }
            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (LocksGate)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    DestinationLocks.Remove(canonicalKey);
                    entry.Semaphore.Dispose();
                }
            }
            throw;
        }

        return new Releaser(canonicalKey, entry);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private readonly KeyedLockEntry _entry;
        private bool _disposed;

        public Releaser(string key, KeyedLockEntry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _entry.Semaphore.Release();
            lock (LocksGate)
            {
                _entry.RefCount--;
                if (_entry.RefCount == 0)
                {
                    DestinationLocks.Remove(_key);
                    _entry.Semaphore.Dispose();
                }
            }
        }
    }

    private readonly IImageCodec _codec;

    public ImageFileProcessingService(IImageCodec codec)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public async Task<ImageOperationResult<ImageFileProcessResult>> ProcessAsync(
        ImageFileProcessRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return ImageOperationResult<ImageFileProcessResult>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "输入路径或输出路径不能为空。");
        }

        var temporaryPath = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFormat detectedFormat;
            ImageMetadata sourceMetadata;
            SafetyValidationResult safetyResult;
            int plannedWidth;
            int plannedHeight;

            using (var source = new FileStream(
                PortablePath.ForFileSystem(request.SourcePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65_536,
                useAsync: false))
            {
                // 1. 文件大小检查
                if (source.Length > request.SafetyLimits.MaxSourceBytes)
                {
                    return ImageOperationResult<ImageFileProcessResult>.Failed(
                        ImageFailureKind.PixelBudgetExceeded,
                        "图像文件大小超过安全处理上限。");
                }

                // 2. 签名检测
                var detected = await ImageFileSignatureInspector.DetectAsync(source, cancellationToken).ConfigureAwait(false);
                if (!detected.IsSuccess)
                {
                    return new ImageOperationResult<ImageFileProcessResult>(default, detected.Failure);
                }

                if (!_codec.SupportedFormats.Contains(detected.Value))
                {
                    return ImageOperationResult<ImageFileProcessResult>.Failed(
                        ImageFailureKind.UnsupportedFormat,
                        "图像格式不受当前编解码器支持。");
                }

                detectedFormat = detected.Value;

                // 3. 解码前纯头探测（Header Probe）
                var probeResult = ImageDimensionProbe.Probe(source, detectedFormat);
                if (probeResult.IsSuccess && probeResult.Value is not null)
                {
                    var probeInfo = probeResult.Value;
                    var preSwap = request.Transform.AutoOrient && probeInfo.ExifOrientation is >= 5 and <= 8;
                    var prePlannedWidth = preSwap ? probeInfo.Height : probeInfo.Width;
                    var prePlannedHeight = preSwap ? probeInfo.Width : probeInfo.Height;

                    var preMetadata = new ImageMetadata(
                        detectedFormat,
                        prePlannedWidth,
                        prePlannedHeight,
                        probeInfo.FrameCount ?? 1,
                        HasAlpha: false,
                        SourceBytes: source.Length,
                        ExifOrientation: probeInfo.ExifOrientation);

                    // 4. 解码前硬/软安全判定
                    var preSafetyResult = ImageSafetyValidator.ValidateWithAction(preMetadata, request.SafetyLimits);
                    if (preSafetyResult.Action == SafetyAction.Reject)
                    {
                        return new ImageOperationResult<ImageFileProcessResult>(default, preSafetyResult.Failure);
                    }
                }

                // 5. 允许继续后，调用底层获取完整 Metadata
                source.Position = 0;
                var identified = await _codec.IdentifyAsync(source, cancellationToken).ConfigureAwait(false);
                if (!identified.IsSuccess || identified.Value is null)
                {
                    return new ImageOperationResult<ImageFileProcessResult>(default, identified.Failure);
                }

                sourceMetadata = identified.Value;
                var postSwap = request.Transform.AutoOrient && sourceMetadata.ExifOrientation is >= 5 and <= 8;
                plannedWidth = postSwap ? sourceMetadata.Height : sourceMetadata.Width;
                plannedHeight = postSwap ? sourceMetadata.Width : sourceMetadata.Height;

                var orientedMetadata = sourceMetadata with
                {
                    Width = plannedWidth,
                    Height = plannedHeight
                };

                // 6. 解码后二次校验
                safetyResult = ImageSafetyValidator.ValidateWithAction(orientedMetadata, request.SafetyLimits);
                if (safetyResult.Action == SafetyAction.Reject)
                {
                    return new ImageOperationResult<ImageFileProcessResult>(default, safetyResult.Failure);
                }
            }

            var outputFormat = ImageFileSignatureInspector.ToImageFormat(request.Encoding.OutputFormat, detectedFormat);
            if (outputFormat == ImageFormat.Unknown || !_codec.SupportedFormats.Contains(outputFormat))
            {
                return ImageOperationResult<ImageFileProcessResult>.Failed(
                    ImageFailureKind.UnsupportedFormat,
                    "所选输出格式不受当前编解码器支持。");
            }

            var requestedDestination = ApplyOutputExtension(request, outputFormat);
            var destination = await PrepareDestinationAsync(
                requestedDestination,
                request.ConflictPolicy,
                sourceMetadata,
                cancellationToken).ConfigureAwait(false);
            if (!destination.IsSuccess || destination.Value is null)
            {
                return new ImageOperationResult<ImageFileProcessResult>(default, destination.Failure);
            }

            if (destination.Value.Skipped)
            {
                return ImageOperationResult<ImageFileProcessResult>.Success(
                    new ImageFileProcessResult(
                        destination.Value.Path,
                        sourceMetadata,
                        Output: null,
                        ReplacedExistingOutput: false,
                        SkippedExistingOutput: true,
                        AutoDownsampled: false,
                        ResizeNotice: null));
            }

            using (await AcquireDestinationLockAsync(destination.Value.Path, cancellationToken).ConfigureAwait(false))
            {
                destination = await PrepareDestinationAsync(
                    requestedDestination,
                    request.ConflictPolicy,
                    sourceMetadata,
                    cancellationToken).ConfigureAwait(false);
                if (!destination.IsSuccess || destination.Value is null)
                {
                    return new ImageOperationResult<ImageFileProcessResult>(default, destination.Failure);
                }

                if (destination.Value.Skipped)
                {
                    return ImageOperationResult<ImageFileProcessResult>.Success(
                        new ImageFileProcessResult(
                            destination.Value.Path,
                            sourceMetadata,
                            Output: null,
                            ReplacedExistingOutput: false,
                            SkippedExistingOutput: true,
                            AutoDownsampled: false,
                            ResizeNotice: null));
                }

                temporaryPath = CreateTemporaryPath(destination.Value.Path);
                var effectiveTransform = request.Transform;
                var resizePlan = ImageResizePlanner.Plan(
                    plannedWidth,
                    plannedHeight,
                    safetyResult,
                    request.Transform.Resize);

                if (resizePlan.ResizeRequired)
                {
                    var preserveAspectRatio = request.Transform.Resize?.PreserveAspectRatio ?? true;
                    effectiveTransform = effectiveTransform with
                    {
                        Resize = new ImageResizeOptions(
                            Enabled: true,
                            Width: resizePlan.Width,
                            Height: resizePlan.Height,
                            PreserveAspectRatio: preserveAspectRatio)
                    };
                }

                var encodeRequest = new ImageEncodeRequest(
                    PortablePath.ForFileSystem(request.SourcePath),
                    PortablePath.ForFileSystem(temporaryPath),
                    detectedFormat,
                    outputFormat,
                    effectiveTransform,
                    request.Encoding with { OutputFormat = ToOutputFormat(outputFormat) },
                    request.SafetyLimits);

                var encoded = await _codec.TransformAndEncodeAsync(encodeRequest, cancellationToken).ConfigureAwait(false);
                if (!encoded.IsSuccess || encoded.Value is null)
                {
                    return new ImageOperationResult<ImageFileProcessResult>(default, encoded.Failure);
                }

                var verified = await VerifyOutputAsync(temporaryPath, outputFormat, encoded.Value.Metadata, cancellationToken).ConfigureAwait(false);
                if (!verified.IsSuccess)
                {
                    return new ImageOperationResult<ImageFileProcessResult>(default, verified.Failure);
                }

                var committedPath = CommitTemporaryFile(
                    temporaryPath,
                    destination.Value.Path,
                    destination.Value.ReplaceExisting,
                    request.ConflictPolicy);
                temporaryPath = string.Empty;

                return ImageOperationResult<ImageFileProcessResult>.Success(
                    new ImageFileProcessResult(
                        committedPath,
                        sourceMetadata,
                        encoded.Value,
                        destination.Value.ReplaceExisting,
                        SkippedExistingOutput: false,
                        AutoDownsampled: resizePlan.AutoDownsampled,
                        ResizeNotice: resizePlan.Notice,
                        TargetSizeResized: encoded.Value.TargetSizeResized,
                        TargetSizeNotice: encoded.Value.TargetSizeNotice));
            }
        }
        catch (OperationCanceledException)
        {
            return ImageOperationResult<ImageFileProcessResult>.Failed(ImageFailureKind.TaskCanceled, "图像处理已取消。");
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is IOException or UnauthorizedAccessException)
        {
            return ImageOperationResult<ImageFileProcessResult>.Failed(ImageFailureKind.TaskCanceled, "图像处理已取消。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageOperationResult<ImageFileProcessResult>.Failed(
                ImageFailureKind.FileAccessConflict,
                "没有访问输入或输出文件的权限。",
                exception);
        }
        catch (IOException exception)
        {
            return ImageOperationResult<ImageFileProcessResult>.Failed(
                ImageFailureKind.FileAccessConflict,
                "读写输入或输出文件时发生 I/O 错误。",
                exception);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private async Task<ImageOperationResult<PreparedDestination>> PrepareDestinationAsync(
        string requestedPath,
        OutputConflictPolicy policy,
        ImageMetadata source,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(requestedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return ImageOperationResult<PreparedDestination>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "输出路径必须包含有效目录。 ");
        }

        Directory.CreateDirectory(PortablePath.ForFileSystem(directory));
        var candidate = requestedPath;
        var exists = File.Exists(PortablePath.ForFileSystem(candidate));
        if (!exists)
        {
            return ImageOperationResult<PreparedDestination>.Success(new PreparedDestination(candidate, ReplaceExisting: false, Skipped: false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return policy switch
        {
            OutputConflictPolicy.Overwrite => ImageOperationResult<PreparedDestination>.Success(
                new PreparedDestination(candidate, ReplaceExisting: true, Skipped: false)),
            OutputConflictPolicy.Skip => ImageOperationResult<PreparedDestination>.Success(
                new PreparedDestination(candidate, ReplaceExisting: false, Skipped: true)),
            OutputConflictPolicy.AutoRename => ImageOperationResult<PreparedDestination>.Success(
                new PreparedDestination(CreateAutoRenamedPath(candidate), ReplaceExisting: false, Skipped: false)),
            _ => ImageOperationResult<PreparedDestination>.Failed(
                ImageFailureKind.FileAccessConflict,
                "输出文件已存在。请更改冲突处理策略后重试。")
        };
    }

    private async Task<ImageOperationResult<ImageMetadata>> VerifyOutputAsync(
        string outputPath,
        ImageFormat expectedFormat,
        ImageMetadata expectedMetadata,
        CancellationToken cancellationToken)
    {
        var fileSystemOutputPath = PortablePath.ForFileSystem(outputPath);
        if (!File.Exists(fileSystemOutputPath) || new FileInfo(fileSystemOutputPath).Length == 0)
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.OutputVerificationFailed,
                "编码器未写出有效的输出文件。");
        }

        using var output = new FileStream(
            fileSystemOutputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            useAsync: false);

        var detected = await ImageFileSignatureInspector.DetectAsync(output, cancellationToken).ConfigureAwait(false);
        if (!detected.IsSuccess || detected.Value != expectedFormat)
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.OutputVerificationFailed,
                "输出文件签名与选择的编码格式不一致。");
        }

        output.Position = 0;
        var identified = await _codec.IdentifyAsync(output, cancellationToken).ConfigureAwait(false);
        if (!identified.IsSuccess || identified.Value is null)
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.OutputVerificationFailed,
                "输出文件无法被重新读取验证。",
                identified.Failure?.Exception);
        }

        if (identified.Value.Width != expectedMetadata.Width || identified.Value.Height != expectedMetadata.Height)
        {
            return ImageOperationResult<ImageMetadata>.Failed(
                ImageFailureKind.OutputVerificationFailed,
                "输出文件尺寸与编码结果不一致。");
        }

        return ImageOperationResult<ImageMetadata>.Success(identified.Value);
    }

    private static string CommitTemporaryFile(
        string temporaryPath,
        string destinationPath,
        bool replaceExisting,
        OutputConflictPolicy conflictPolicy)
    {
        var fileSystemTemporaryPath = PortablePath.ForFileSystem(temporaryPath);
        var fileSystemDestinationPath = PortablePath.ForFileSystem(destinationPath);
        if (replaceExisting && File.Exists(fileSystemDestinationPath))
        {
            File.Replace(fileSystemTemporaryPath, fileSystemDestinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return destinationPath;
        }

        if (conflictPolicy == OutputConflictPolicy.Fail && File.Exists(fileSystemDestinationPath))
        {
            throw new IOException("目标文件已存在且冲突策略为拒绝。");
        }

        for (var attempt = 0; attempt < 64; attempt++)
        {
            try
            {
                File.Move(fileSystemTemporaryPath, fileSystemDestinationPath);
                return PortablePath.ForFileSystem(fileSystemDestinationPath);
            }
            catch (IOException) when (attempt < 63 && conflictPolicy == OutputConflictPolicy.AutoRename)
            {
                var renamedPath = CreateAutoRenamedPath(fileSystemDestinationPath);
                if (string.Equals(renamedPath, fileSystemDestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }

                fileSystemDestinationPath = PortablePath.ForFileSystem(renamedPath);
            }
        }

        throw new IOException("无法完成输出文件落盘。");
    }

    private static string ApplyOutputExtension(ImageFileProcessRequest request, ImageFormat outputFormat)
    {
        var extension = ImageFileSignatureInspector.GetOutputExtension(
            outputFormat,
            request.SourcePath,
            preserveSourceExtension: request.Encoding.OutputFormat == ImageOutputFormat.Original);
        if (string.IsNullOrEmpty(extension))
        {
            throw new ArgumentOutOfRangeException(nameof(outputFormat), outputFormat, "输出格式没有受支持的扩展名。");
        }

        return Path.ChangeExtension(request.DestinationPath, extension);
    }

    private static string CreateTemporaryPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("输出路径缺少目录。");
        return Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static string CreateAutoRenamedPath(string requestedPath)
    {
        var directory = Path.GetDirectoryName(requestedPath) ?? throw new InvalidOperationException("输出路径缺少目录。");
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(requestedPath);
        var extension = Path.GetExtension(requestedPath);
        for (var index = 1; index <= 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileNameWithoutExtension}_{index}{extension}");
            if (!File.Exists(PortablePath.ForFileSystem(candidate)))
            {
                return candidate;
            }
        }

        throw new IOException("无法为输出文件生成唯一名称。");
    }

    private static ImageOutputFormat ToOutputFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ImageOutputFormat.Jpeg,
        ImageFormat.Png => ImageOutputFormat.Png,
        ImageFormat.Webp => ImageOutputFormat.Webp,
        ImageFormat.Gif => ImageOutputFormat.Gif,
        ImageFormat.Bmp => ImageOutputFormat.Bmp,
        ImageFormat.Tiff => ImageOutputFormat.Tiff,
        ImageFormat.Ico => ImageOutputFormat.Ico,
        _ => ImageOutputFormat.Original
    };

    private static void DeleteIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fileSystemPath = PortablePath.ForFileSystem(path);
            if (File.Exists(fileSystemPath))
            {
                File.Delete(fileSystemPath);
            }
        }
        catch (IOException)
        {
            // The original processing result is more useful than a best-effort temporary-file cleanup error.
        }
        catch (UnauthorizedAccessException)
        {
            // The original processing result is more useful than a best-effort temporary-file cleanup error.
        }
    }

    private sealed record PreparedDestination(string Path, bool ReplaceExisting, bool Skipped);
}
