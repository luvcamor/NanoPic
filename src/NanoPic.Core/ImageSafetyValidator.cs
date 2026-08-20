namespace NanoPic.Core;

public enum SafetyAction
{
    Pass,
    Reject,
    Downscale
}

public sealed record SafetyValidationResult(
    SafetyAction Action,
    ImageOperationFailure? Failure,
    int? TargetWidth,
    int? TargetHeight);

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

        if (pixels * metadata.FrameCount > limits.HardMaxTotalPixels)
        {
            return new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像多帧总像素超过绝对安全预算上限。");
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

    public static SafetyValidationResult ValidateWithAction(ImageMetadata metadata, ImageSafetyLimits limits)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        if (limits is null) throw new ArgumentNullException(nameof(limits));

        // --- Hard limits: always reject, never downscale ---

        if (metadata.SourceBytes < 0 || metadata.SourceBytes > limits.MaxSourceBytes)
        {
            return new SafetyValidationResult(SafetyAction.Reject, new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像文件大小超过安全处理上限。"), null, null);
        }

        if (metadata.Width <= 0 || metadata.Height <= 0)
        {
            return new SafetyValidationResult(SafetyAction.Reject, new ImageOperationFailure(
                ImageFailureKind.DecodeFailed,
                "图像尺寸无效。"), null, null);
        }

        if (metadata.FrameCount <= 0 || metadata.FrameCount > limits.MaxFrames)
        {
            return new SafetyValidationResult(SafetyAction.Reject, new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像帧数超过安全处理上限或图像帧信息无效。"), null, null);
        }

        var pixels = (long)metadata.Width * metadata.Height;

        // Hard pixel ceiling — absolute safety net, never downscale.
        if (pixels > limits.HardMaxPixels)
        {
            return new SafetyValidationResult(SafetyAction.Reject, new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图片尺寸超过 NanoPic 的绝对安全处理范围，已跳过。"), null, null);
        }

        if (pixels * metadata.FrameCount > limits.HardMaxTotalPixels)
        {
            return new SafetyValidationResult(SafetyAction.Reject, new ImageOperationFailure(
                ImageFailureKind.PixelBudgetExceeded,
                "图像多帧总像素超过绝对安全预算上限，已跳过。"), null, null);
        }

        // --- Soft limits: may downscale when AutoDownscaleOnExceed is enabled ---

        bool exceedDimensions = metadata.Width > limits.MaxWidth || metadata.Height > limits.MaxHeight;
        bool exceedPixels = pixels > limits.MaxPixels;

        if (exceedDimensions || exceedPixels)
        {
            if (limits.AutoDownscaleOnExceed)
            {
                double ratioWidth = (double)limits.MaxWidth / metadata.Width;
                double ratioHeight = (double)limits.MaxHeight / metadata.Height;
                double ratioPixels = Math.Sqrt((double)limits.MaxPixels / (double)pixels);

                double minRatio = Math.Min(1.0, Math.Min(ratioWidth, Math.Min(ratioHeight, ratioPixels)));

                int targetWidth = Math.Max(1, (int)(metadata.Width * minRatio));
                int targetHeight = Math.Max(1, (int)(metadata.Height * minRatio));

                return new SafetyValidationResult(SafetyAction.Downscale, null, targetWidth, targetHeight);
            }
            else
            {
                return new SafetyValidationResult(SafetyAction.Reject, new ImageOperationFailure(
                    ImageFailureKind.PixelBudgetExceeded,
                    "图像尺寸或像素总数超过安全处理上限。"), null, null);
            }
        }

        return new SafetyValidationResult(SafetyAction.Pass, null, null, null);
    }
}
