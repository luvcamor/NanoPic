namespace NanoPic.Core;

public static class ImageSafetyValidator
{
    public static ImageOperationFailure? Validate(ImageMetadata metadata, ImageSafetyLimits limits)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        if (limits is null) throw new ArgumentNullException(nameof(limits));

        if (metadata.SourceBytes < 0 || metadata.SourceBytes > limits.MaxSourceBytes)
        {
            return new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像文件大小超过安全处理上限。");
        }

        if (metadata.Width <= 0 || metadata.Height <= 0)
        {
            return new ImageOperationFailure(
                ImageFailureKind.DecodeFailed,
                "图像尺寸无效。");
        }

        if (metadata.Width > limits.MaxWidth || metadata.Height > limits.MaxHeight)
        {
            return new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像宽度或高度超过安全处理上限。");
        }

        var pixels = (long)metadata.Width * metadata.Height;
        if (pixels > limits.MaxPixels)
        {
            return new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像像素总数超过安全处理上限。");
        }

        if (metadata.FrameCount <= 0 || metadata.FrameCount > limits.MaxFrames)
        {
            return new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像帧数超过安全处理上限或图像帧信息无效。");
        }

        return null;
    }

    public static ImageOperationResult<ImageMetadata> ValidateResult(ImageMetadata metadata, ImageSafetyLimits limits)
    {
        var failure = Validate(metadata, limits);
        return failure is null
            ? ImageOperationResult<ImageMetadata>.Success(metadata)
            : new ImageOperationResult<ImageMetadata>(default, failure);
    }
}
