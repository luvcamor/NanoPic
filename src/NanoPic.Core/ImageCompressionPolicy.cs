namespace NanoPic.Core;

/// <summary>Parameter safeguards, not a claim of perceptual or pixel losslessness.</summary>
public static class ImageCompressionPolicy
{
    public const string FidelityTargetUnreachable =
        "保持清晰时，无法压到这么小。请调大文件大小、选择“文件更小”，或在更多选项中允许缩小尺寸。";

    public static bool UsesLosslessWebp(ImageEncodingOptions options) =>
        options.Preference == CompressionPreference.PreserveFidelity &&
        options.ContentKind == ImageContentKind.TextOrScreenshot;

    public static ImageOperationResult<ImageEncodingOptions> Resolve(ImageEncodingOptions options, ImageFormat format)
    {
        if (!Enum.IsDefined(typeof(CompressionPreference), options.Preference) ||
            !Enum.IsDefined(typeof(ImageContentKind), options.ContentKind) || options.Quality is < 1 or > 100)
            return ImageOperationResult<ImageEncodingOptions>.Failed(ImageFailureKind.InvalidConfiguration, "压缩策略、内容类型或质量参数无效。");
        if (options.TargetSize is { } target &&
            (target.TargetBytes <= 0 || target.MinQuality is < 1 or > 100 || target.MaxQuality is < 1 or > 100 || target.MinQuality > target.MaxQuality))
            return ImageOperationResult<ImageEncodingOptions>.Failed(ImageFailureKind.InvalidConfiguration, "目标大小或质量范围无效。");
        if (options.Preference == CompressionPreference.SizeFirst)
            return ImageOperationResult<ImageEncodingOptions>.Success(options);

        var text = options.ContentKind == ImageContentKind.TextOrScreenshot;
        var fixedQuality = format == ImageFormat.Png || (format == ImageFormat.Webp && UsesLosslessWebp(options));
        var minimum = fixedQuality ? 100 : text ? 90 : 85;
        var quality = fixedQuality ? 100 : text ? 95 : 90;
        var effectiveTarget = options.TargetSize;
        if (effectiveTarget is not null)
        {
            if (effectiveTarget.MaxQuality < minimum)
                return ImageOperationResult<ImageEncodingOptions>.Failed(ImageFailureKind.TargetSizeUnreachable, FidelityTargetUnreachable);
            effectiveTarget = effectiveTarget with { MinQuality = Math.Max(minimum, effectiveTarget.MinQuality) };
        }
        return ImageOperationResult<ImageEncodingOptions>.Success(options with { Quality = quality, TargetSize = effectiveTarget });
    }
}
