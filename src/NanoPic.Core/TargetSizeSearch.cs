namespace NanoPic.Core;

public sealed record TargetSizeCandidate(int Quality, long Bytes);

public sealed record TargetSizeSearchResult(
    TargetSizeCandidate Selected,
    bool TargetReached,
    bool ExceededTarget,
    IReadOnlyList<TargetSizeCandidate> EvaluatedCandidates);

public static class TargetSizeSearch
{
    public static async Task<ImageOperationResult<TargetSizeSearchResult>> FindAsync(
        TargetSizeOptions options,
        Func<int, CancellationToken, Task<ImageOperationResult<long>>> encodeAndMeasureAsync,
        CancellationToken cancellationToken)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (encodeAndMeasureAsync is null) throw new ArgumentNullException(nameof(encodeAndMeasureAsync));

        if (options.TargetBytes <= 0 || options.MinQuality is < 1 or > 100 ||
            options.MaxQuality is < 1 or > 100 || options.MinQuality > options.MaxQuality)
        {
            return ImageOperationResult<TargetSizeSearchResult>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "目标大小或质量范围无效。");
        }

        var evaluated = new List<TargetSizeCandidate>();
        TargetSizeCandidate? bestWithinTarget = null;
        TargetSizeCandidate? smallestCandidate = null;
        var low = options.MinQuality;
        var high = options.MaxQuality;

        try
        {
            while (low <= high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quality = low + ((high - low) / 2);
                var measured = await encodeAndMeasureAsync(quality, cancellationToken).ConfigureAwait(false);
                if (!measured.IsSuccess)
                {
                    return new ImageOperationResult<TargetSizeSearchResult>(default, measured.Failure);
                }

                var candidate = new TargetSizeCandidate(quality, measured.Value);
                evaluated.Add(candidate);
                if (smallestCandidate is null || candidate.Bytes < smallestCandidate.Bytes)
                {
                    smallestCandidate = candidate;
                }

                if (candidate.Bytes <= options.TargetBytes)
                {
                    if (bestWithinTarget is null || candidate.Quality > bestWithinTarget.Quality)
                    {
                        bestWithinTarget = candidate;
                    }

                    low = quality + 1;
                }
                else
                {
                    high = quality - 1;
                }
            }

            if (bestWithinTarget is not null)
            {
                return ImageOperationResult<TargetSizeSearchResult>.Success(
                    new TargetSizeSearchResult(bestWithinTarget, TargetReached: true, ExceededTarget: false, evaluated));
            }

            if (smallestCandidate is null)
            {
                return ImageOperationResult<TargetSizeSearchResult>.Failed(
                    ImageFailureKind.TargetSizeUnreachable,
                    "在允许的质量范围内无法生成目标大小的图像。");
            }

            if (!options.AllowExceed)
            {
                return ImageOperationResult<TargetSizeSearchResult>.Failed(
                    ImageFailureKind.TargetSizeUnreachable,
                    "无法在不超过目标大小的前提下生成图像。");
            }

            return ImageOperationResult<TargetSizeSearchResult>.Success(
                new TargetSizeSearchResult(smallestCandidate, TargetReached: false, ExceededTarget: true, evaluated));
        }
        catch (OperationCanceledException)
        {
            return ImageOperationResult<TargetSizeSearchResult>.Failed(ImageFailureKind.TaskCanceled, "目标大小压缩已取消。");
        }
    }
}
