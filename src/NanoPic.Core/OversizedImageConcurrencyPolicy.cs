namespace NanoPic.Core;

/// <summary>
/// Lightweight per-image concurrency limiter. When a batch contains oversized
/// images, the actual concurrency is capped below the user's MaxThreads to
/// reduce peak memory pressure during WIC decode.
///
/// The rules are conservative initial defaults; they should be tuned through
/// real-world pressure testing.
/// </summary>
public static class OversizedImageConcurrencyPolicy
{
    /// <summary>
    /// Returns the maximum number of concurrent tasks allowed for an image
    /// with the given total pixel count, subject to the user's overall
    /// MaxThreads ceiling.
    /// </summary>
    /// <param name="totalPixels">Source image width × height.</param>
    /// <param name="userMaxThreads">User-configured maximum concurrency.</param>
    public static OversizedImageConcurrencyLimit LimitFor(
        long totalPixels,
        int userMaxThreads)
    {
        if (userMaxThreads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userMaxThreads));
        }

        var pixelBasedLimit = totalPixels switch
        {
            < 50_000_000 => userMaxThreads,
            < 100_000_000 => Math.Min(userMaxThreads, 4),
            < 200_000_000 => Math.Min(userMaxThreads, 2),
            _ => 1
        };

        if (pixelBasedLimit >= userMaxThreads)
        {
            return new OversizedImageConcurrencyLimit(userMaxThreads, null);
        }

        return new OversizedImageConcurrencyLimit(
            pixelBasedLimit,
            $"超大图片（{totalPixels / 1_000_000} MP）已将并发限制为 {pixelBasedLimit}");
    }

    /// <summary>
    /// Returns the effective concurrency for a batch of images: the minimum
    /// of the user's ceiling and the most restrictive per-image limit found
    /// in the batch.
    /// </summary>
    public static OversizedImageConcurrencyLimit EffectiveConcurrency(
        IReadOnlyList<long> imagePixelCounts,
        int userMaxThreads)
    {
        if (imagePixelCounts is null) throw new ArgumentNullException(nameof(imagePixelCounts));
        if (userMaxThreads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userMaxThreads));
        }

        if (imagePixelCounts.Count == 0)
        {
            return new OversizedImageConcurrencyLimit(userMaxThreads, null);
        }

        var mostRestrictive = userMaxThreads;
        string? reason = null;
        long? triggeringPixels = null;

        foreach (var pixels in imagePixelCounts)
        {
            var limit = LimitFor(pixels, userMaxThreads);
            if (limit.MaxConcurrentTasks < mostRestrictive)
            {
                mostRestrictive = limit.MaxConcurrentTasks;
                reason = limit.Reason;
                triggeringPixels = pixels;
            }
        }

        if (mostRestrictive >= userMaxThreads)
        {
            return new OversizedImageConcurrencyLimit(userMaxThreads, null);
        }

        return new OversizedImageConcurrencyLimit(mostRestrictive, reason);
    }
}