using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NanoPic.Core;

namespace NanoPic.Codecs;

public sealed record PngTargetCandidate(int Quality, long Bytes, bool WasLossy);

public sealed record PngTargetSearchResult(
    PngTargetCandidate Selected,
    bool TargetReached,
    bool ExceededTarget,
    bool Resized,
    int FinalWidth,
    int FinalHeight,
    string? Notice,
    IReadOnlyList<BitmapFrame> FinalFrames);

public static class PngTargetSizeSearch
{
    private static readonly int[] InitialCandidateQualities =
    {
        100, 95, 90, 85, 80, 75, 70, 65, 60, 50, 40, 30, 20, 10
    };

    public static async Task<ImageOperationResult<PngTargetSearchResult>> SearchAsync(
        IReadOnlyList<BitmapFrame> initialFrames,
        string temporaryOutputPath,
        TargetSizeOptions targetOptions,
        Func<IReadOnlyList<BitmapFrame>, string, int, CancellationToken, Task<long>> encodeRunner,
        Func<IReadOnlyList<BitmapFrame>, ImageResizeOptions, IReadOnlyList<BitmapFrame>> resizeRunner,
        CancellationToken cancellationToken)
    {
        if (targetOptions is null) throw new ArgumentNullException(nameof(targetOptions));

        if (initialFrames == null || initialFrames.Count == 0)
        {
            return ImageOperationResult<PngTargetSearchResult>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "没有可用于搜索目标大小的图像帧。");
        }

        if (encodeRunner is null) throw new ArgumentNullException(nameof(encodeRunner));
        if (resizeRunner is null) throw new ArgumentNullException(nameof(resizeRunner));

        if (targetOptions.TargetBytes <= 0 ||
            targetOptions.MinQuality is < 1 or > 100 ||
            targetOptions.MaxQuality is < 1 or > 100 ||
            targetOptions.MinQuality > targetOptions.MaxQuality)
        {
            return ImageOperationResult<PngTargetSearchResult>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "PNG 目标大小或质量范围无效。");
        }

        var currentFrames = initialFrames;
        var originalWidth = initialFrames[0].PixelWidth;
        var originalHeight = initialFrames[0].PixelHeight;
        var resized = false;
        const int maxResizePasses = 5;

        for (var pass = 0; pass < maxResizePasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentWidth = currentFrames[0].PixelWidth;
            var currentHeight = currentFrames[0].PixelHeight;

            // 在当前尺寸下搜索候选
            var evaluated = new Dictionary<int, long>();
            var candidates = new List<PngTargetCandidate>();

            // 1. 评估预设梯度，并始终包含用户指定的 Min/Max 边界。
            var initialQualities = new SortedSet<int>(Comparer<int>.Create(
                (left, right) => right.CompareTo(left)))
            {
                targetOptions.MinQuality,
                targetOptions.MaxQuality
            };
            foreach (var quality in InitialCandidateQualities)
            {
                if (quality >= targetOptions.MinQuality && quality <= targetOptions.MaxQuality)
                {
                    initialQualities.Add(quality);
                }
            }

            foreach (var q in initialQualities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!evaluated.ContainsKey(q))
                {
                    var bytes = await encodeRunner(currentFrames, temporaryOutputPath, q, cancellationToken).ConfigureAwait(false);
                    evaluated[q] = bytes;
                    candidates.Add(new PngTargetCandidate(q, bytes, q < 100));
                }
            }

            // 寻找 <= TargetBytes 的最高质量候选
            var matchingCandidates = candidates.Where(c => c.Bytes <= targetOptions.TargetBytes).ToList();
            if (matchingCandidates.Count > 0)
            {
                // 找出初步满足条件的最高 Quality
                var bestCandidate = matchingCandidates.OrderByDescending(c => c.Quality).First();

                // 在最佳候选和最近的更高失败候选之间逐级细化，避免一次
                // 中点采样遗漏更高的可达 Quality。
                var higherFailing = candidates
                    .Where(c => c.Quality > bestCandidate.Quality && c.Bytes > targetOptions.TargetBytes)
                    .OrderBy(c => c.Quality)
                    .FirstOrDefault();

                if (higherFailing != null)
                {
                    for (var refinedQuality = higherFailing.Quality - 1;
                         refinedQuality > bestCandidate.Quality;
                         refinedQuality--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (evaluated.ContainsKey(refinedQuality))
                        {
                            continue;
                        }

                        var refinedBytes = await encodeRunner(
                            currentFrames,
                            temporaryOutputPath,
                            refinedQuality,
                            cancellationToken).ConfigureAwait(false);
                        evaluated[refinedQuality] = refinedBytes;
                        candidates.Add(new PngTargetCandidate(
                            refinedQuality,
                            refinedBytes,
                            refinedQuality < 100));
                        if (refinedBytes <= targetOptions.TargetBytes)
                        {
                            bestCandidate = new PngTargetCandidate(
                                refinedQuality,
                                refinedBytes,
                                refinedQuality < 100);
                            break;
                        }
                    }
                }

                string? notice = null;
                if (resized)
                {
                    notice = $"为达到目标大小已调整分辨率: {originalWidth}×{originalHeight} → {currentWidth}×{currentHeight}";
                }

                return ImageOperationResult<PngTargetSearchResult>.Success(new PngTargetSearchResult(
                    bestCandidate,
                    TargetReached: true,
                    ExceededTarget: false,
                    Resized: resized,
                    FinalWidth: currentWidth,
                    FinalHeight: currentHeight,
                    Notice: notice,
                    FinalFrames: currentFrames));
            }

            // 当前尺寸下所有候选均 > TargetBytes
            var smallestCandidate = candidates.OrderBy(c => c.Bytes).First();

            // 若用户不允许缩小尺寸 (AllowResizeForTarget == false)
            if (!targetOptions.AllowResizeForTarget)
            {
                if (targetOptions.AllowExceed)
                {
                    // 允许超出：输出原尺寸最小体积候选
                    return ImageOperationResult<PngTargetSearchResult>.Success(new PngTargetSearchResult(
                        smallestCandidate,
                        TargetReached: false,
                        ExceededTarget: true,
                        Resized: false,
                        FinalWidth: currentWidth,
                        FinalHeight: currentHeight,
                        Notice: null,
                        FinalFrames: currentFrames));
                }

                return ImageOperationResult<PngTargetSearchResult>.Failed(
                    ImageFailureKind.TargetSizeUnreachable,
                    "无法在当前尺寸下达到目标大小。可开启“允许缩小图片尺寸”以进一步压缩。");
            }

            // 用户允许缩小尺寸 (AllowResizeForTarget == true)
            // 根据体积比例计算下一轮缩放尺寸
            var areaRatio = (double)targetOptions.TargetBytes / smallestCandidate.Bytes;
            var lengthScale = Math.Min(0.92, Math.Sqrt(areaRatio) * 0.95);

            var newWidth = Math.Max(16, (int)Math.Floor(currentWidth * lengthScale));
            var newHeight = Math.Max(16, (int)Math.Floor(currentHeight * lengthScale));

            if (newWidth <= 16 || newHeight <= 16 || (newWidth == currentWidth && newHeight == currentHeight))
            {
                // 已经缩至极限无法再缩
                if (targetOptions.AllowExceed)
                {
                    return ImageOperationResult<PngTargetSearchResult>.Success(new PngTargetSearchResult(
                        smallestCandidate,
                        TargetReached: false,
                        ExceededTarget: true,
                        Resized: resized,
                        FinalWidth: currentWidth,
                        FinalHeight: currentHeight,
                        Notice: resized ? $"为接近目标大小已调整分辨率: {originalWidth}×{originalHeight} → {currentWidth}×{currentHeight}" : null,
                        FinalFrames: currentFrames));
                }

                return ImageOperationResult<PngTargetSearchResult>.Failed(
                    ImageFailureKind.TargetSizeUnreachable,
                    "即使降低分辨率后依然无法达到目标大小。");
            }

            var resizeOptions = new ImageResizeOptions(Enabled: true, Width: newWidth, Height: newHeight, PreserveAspectRatio: true);
            currentFrames = resizeRunner(currentFrames, resizeOptions);
            resized = true;
        }

        return ImageOperationResult<PngTargetSearchResult>.Failed(
            ImageFailureKind.TargetSizeUnreachable,
            "达到最大尝试次数仍无法满足目标大小。");
    }
}
