namespace NanoPic.Core;

public enum ImageFormat
{
    Unknown = 0,
    Jpeg,
    Png,
    Webp,
    Gif,
    Bmp,
    Tiff,
    Ico
}

public enum ImageOutputFormat
{
    Original = 0,
    Jpeg,
    Png,
    Webp,
    Gif,
    Bmp,
    Tiff,
    Ico
}

public enum ImageFailureKind
{
    None = 0,
    UnsupportedFormat,
    DecodeFailed,
    PixelBudgetExceeded,
    TargetSizeUnreachable,
    OutputVerificationFailed,
    FileAccessConflict,
    TaskCanceled,
    AccelerationUnavailable,
    LegacyUnimplemented,
    InvalidConfiguration,
    Unknown
}

public enum OutputConflictPolicy
{
    Fail = 0,
    Overwrite,
    Skip,
    AutoRename
}

public sealed record ImageMetadata(
    ImageFormat Format,
    int Width,
    int Height,
    int FrameCount,
    bool HasAlpha,
    long SourceBytes);

public sealed record ImageSafetyLimits(
    long MaxSourceBytes,
    int MaxWidth,
    int MaxHeight,
    long MaxPixels,
    int MaxFrames)
{
    public static ImageSafetyLimits Default { get; } = new(
        MaxSourceBytes: 512L * 1024L * 1024L,
        MaxWidth: 32_768,
        MaxHeight: 32_768,
        MaxPixels: 100_000_000,
        MaxFrames: 1_000);
}

public sealed record ImageResizeOptions(
    bool Enabled,
    int? Width,
    int? Height,
    bool PreserveAspectRatio = true);

public enum ImageWatermarkPosition
{
    BottomRight = 0,
    BottomLeft,
    TopRight,
    TopLeft,
    Center,
    Random
}

public sealed record ImageWatermarkOptions(
    bool Enabled,
    string Text,
    string ColorHex,
    int OpacityPercent,
    int FontSize = 24,
    int Margin = 16,
    ImageWatermarkPosition Position = ImageWatermarkPosition.BottomRight);

public sealed record ImageBackgroundOptions(
    bool FlattenTransparency,
    string ColorHex);

public sealed record ImageMetadataNoteOptions(
    bool Enabled,
    string Text);

public sealed record ImageTransformOptions(
    bool AutoOrient = true,
    ImageResizeOptions? Resize = null,
    int BrightnessPercent = 100,
    ImageWatermarkOptions? Watermark = null,
    ImageBackgroundOptions? Background = null,
    bool StripMetadata = false,
    ImageMetadataNoteOptions? MetadataNote = null);

public sealed record TargetSizeOptions(
    long TargetBytes,
    bool AllowExceed,
    int MinQuality = 1,
    int MaxQuality = 100);

public sealed record ImageEncodingOptions(
    ImageOutputFormat OutputFormat,
    int Quality = 80,
    TargetSizeOptions? TargetSize = null);

public sealed record ImageEncodeRequest(
    string SourcePath,
    string TemporaryOutputPath,
    ImageFormat SourceFormat,
    ImageFormat OutputFormat,
    ImageTransformOptions Transform,
    ImageEncodingOptions Encoding,
    ImageSafetyLimits SafetyLimits);

public sealed record ImageEncodedOutput(
    ImageMetadata Metadata,
    int Quality,
    long Bytes,
    bool TargetSizeReached,
    bool ExceededTarget);

public sealed record ImageFileProcessRequest(
    string SourcePath,
    string DestinationPath,
    ImageEncodingOptions Encoding,
    ImageTransformOptions Transform,
    ImageSafetyLimits SafetyLimits,
    OutputConflictPolicy ConflictPolicy = OutputConflictPolicy.Fail);

public sealed record ImageFileProcessResult(
    string OutputPath,
    ImageMetadata Source,
    ImageEncodedOutput? Output,
    bool ReplacedExistingOutput,
    bool SkippedExistingOutput);

public sealed record ImageBatchProgress(
    int Total,
    int Completed,
    int Succeeded,
    int Failed,
    int Canceled);

public sealed record ImageBatchResult(
    IReadOnlyList<ImageOperationResult<ImageFileProcessResult>> Items,
    ImageBatchProgress Progress);

public sealed record ImageOperationFailure(ImageFailureKind Kind, string UserMessage, Exception? Exception = null);

public sealed record ImageOperationResult<T>(T? Value, ImageOperationFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static ImageOperationResult<T> Success(T value) => new(value, null);

    public static ImageOperationResult<T> Failed(ImageFailureKind kind, string userMessage, Exception? exception = null) =>
        new(default, new ImageOperationFailure(kind, userMessage, exception));
}

public interface IImageCodec
{
    IReadOnlyCollection<ImageFormat> SupportedFormats { get; }

    Task<ImageOperationResult<ImageMetadata>> IdentifyAsync(Stream input, CancellationToken cancellationToken);

    Task<ImageOperationResult<ImageEncodedOutput>> TransformAndEncodeAsync(
        ImageEncodeRequest request,
        CancellationToken cancellationToken);
}
