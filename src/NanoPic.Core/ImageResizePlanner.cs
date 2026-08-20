namespace NanoPic.Core;

/// <summary>
/// Merges auto-downsample (triggered by safety validation) with user resize
/// into a single EffectiveResize. Every image gets at most one Resize.
/// </summary>
public static class ImageResizePlanner
{
    /// <summary>
    /// Compute the unified resize plan. Callers must already have a
    /// <see cref="SafetyValidationResult"/> from
    /// <see cref="ImageSafetyValidator.ValidateWithAction"/>.
    /// </summary>
    /// <param name="sourceWidth">Original image width after EXIF orientation.</param>
    /// <param name="sourceHeight">Original image height after EXIF orientation.</param>
    /// <param name="safetyResult">Result from safety validation.</param>
    /// <param name="userResize">User's Resize settings (may be null or disabled).</param>
    public static ImageResizePlan Plan(
        int sourceWidth,
        int sourceHeight,
        SafetyValidationResult safetyResult,
        ImageResizeOptions? userResize)
    {
        if (safetyResult is null) throw new ArgumentNullException(nameof(safetyResult));

        if (safetyResult.Action == SafetyAction.Reject)
        {
            throw new InvalidOperationException(
                "Cannot plan resize for a rejected safety result.");
        }

        var hasUserResize = userResize is { Enabled: true };
        var hasSafetyDownscale = safetyResult.Action == SafetyAction.Downscale;

        if (!hasUserResize && !hasSafetyDownscale)
        {
            return new ImageResizePlan(
                sourceWidth,
                sourceHeight,
                ResizeRequired: false,
                AutoDownsampled: false,
                Notice: null);
        }

        // Case A: only safety downscale, no user resize
        if (!hasUserResize && hasSafetyDownscale)
        {
            var targetWidth = safetyResult.TargetWidth ?? sourceWidth;
            var targetHeight = safetyResult.TargetHeight ?? sourceHeight;
            var notice = $"已自动缩放：{sourceWidth} × {sourceHeight} → {targetWidth} × {targetHeight}";
            return new ImageResizePlan(
                targetWidth,
                targetHeight,
                ResizeRequired: true,
                AutoDownsampled: true,
                Notice: notice);
        }

        // Case B: only user resize, no safety downscale
        if (hasUserResize && !hasSafetyDownscale)
        {
            var uw = userResize!.Width ?? sourceWidth;
            var uh = userResize!.Height ?? sourceHeight;
            return new ImageResizePlan(
                uw, uh,
                ResizeRequired: true,
                AutoDownsampled: false,
                Notice: null);
        }

        // Case C: both safety downscale AND user resize
        // The effective target is min(user, safety) so we never exceed the safe limit.
        var safeWidth = safetyResult.TargetWidth ?? sourceWidth;
        var safeHeight = safetyResult.TargetHeight ?? sourceHeight;

        var (resolvedUserWidth, resolvedUserHeight) = ResolveUserResizeDimensions(sourceWidth, sourceHeight, userResize!);

        // Determine which is smaller in terms of total pixels
        long safePixels = (long)safeWidth * safeHeight;
        long userPixels = (long)resolvedUserWidth * resolvedUserHeight;

        int finalWidth, finalHeight;
        bool autoDownsampled;

        if (userPixels <= safePixels)
        {
            // User target is already within safe bounds — use it directly.
            finalWidth = userResize!.Width ?? resolvedUserWidth;
            finalHeight = userResize!.Height ?? resolvedUserHeight;
            autoDownsampled = false;
        }
        else
        {
            // User target exceeds safe bounds — clamp to safe dimensions.
            finalWidth = safeWidth;
            finalHeight = safeHeight;
            autoDownsampled = true;
        }

        var combinedNotice = autoDownsampled
            ? $"已自动缩放：{sourceWidth} × {sourceHeight} → {finalWidth} × {finalHeight}（用户目标尺寸超出安全范围）"
            : null;

        return new ImageResizePlan(
            finalWidth,
            finalHeight,
            ResizeRequired: true,
            AutoDownsampled: autoDownsampled,
            Notice: combinedNotice);
    }

    private static (int Width, int Height) ResolveUserResizeDimensions(
        int sourceWidth,
        int sourceHeight,
        ImageResizeOptions userResize)
    {
        if (userResize.Width.HasValue && userResize.Height.HasValue)
        {
            if (userResize.PreserveAspectRatio)
            {
                var widthRatio = (double)userResize.Width.Value / sourceWidth;
                var heightRatio = (double)userResize.Height.Value / sourceHeight;
                var ratio = Math.Min(widthRatio, heightRatio);
                var targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * ratio));
                var targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * ratio));
                return (targetWidth, targetHeight);
            }

            return (userResize.Width.Value, userResize.Height.Value);
        }

        if (userResize.Width.HasValue)
        {
            var targetWidth = userResize.Width.Value;
            var targetHeight = userResize.PreserveAspectRatio
                ? Math.Max(1, (int)Math.Round((double)sourceHeight * targetWidth / sourceWidth))
                : sourceHeight;
            return (targetWidth, targetHeight);
        }

        if (userResize.Height.HasValue)
        {
            var targetHeight = userResize.Height.Value;
            var targetWidth = userResize.PreserveAspectRatio
                ? Math.Max(1, (int)Math.Round((double)sourceWidth * targetHeight / sourceHeight))
                : sourceWidth;
            return (targetWidth, targetHeight);
        }

        return (sourceWidth, sourceHeight);
    }
}