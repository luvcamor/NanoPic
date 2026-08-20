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
        if (initialFrames == null || initialFrames.Count == 0)
        {
            return ImageOperationResult<PngTargetSearchResult>.Failed(
                ImageFailureKind.InvalidConfiguration,
                "没有可用于搜索目标大小的图像帧。");
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

            // 1. 评估预设梯度
            foreach (var q in InitialCandidateQualities)
            {
                if (q < targetOptions.MinQuality || q > targetOptions.MaxQuality)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!evaluated.ContainsKey(q))
                {
                    var bytes = await encodeRunner(currentFrames, temporaryOutputPath, q, cancellationToken).ConfigureAwait(false);
                    evaluated[q] = bytes;
                    candidates.Add(new PngTargetCandidate(q, bytes, q < 100));
                }
            }

            if (candidates.Count == 0)
            {
                // 若范围外，至少测一次 MinQuality 与 MaxQuality
                var minQ = Math.Max(1, Math.Min(100, targetOptions.MinQuality));
                var maxQ = Math.Max(1, Math.Min(100, targetOptions.MaxQuality));
                if (!evaluated.ContainsKey(maxQ))
                {
                    var bytes = await encodeRunner(currentFrames, temporaryOutputPath, maxQ, cancellationToken).ConfigureAwait(false);
                    evaluated[maxQ] = bytes;
                    candidates.Add(new PngTargetCandidate(maxQ, bytes, maxQ < 100));
                }
                if (!evaluated.ContainsKey(minQ))
                {
                    var bytes = await encodeRunner(currentFrames, temporaryOutputPath, minQ, cancellationToken).ConfigureAwait(false);
                    evaluated[minQ] = bytes;
                    candidates.Add(new PngTargetCandidate(minQ, bytes, minQ < 100));
                }
            }

            // 寻找 <= TargetBytes 的最高质量候选
            var matchingCandidates = candidates.Where(c => c.Bytes <= targetOptions.TargetBytes).ToList();
            if (matchingCandidates.Count > 0)
            {
                // 找出初步满足条件的最高 Quality
                var bestCandidate = matchingCandidates.OrderByDescending(c => c.Quality).First();

                // 尝试在 bestCandidate.Quality 和更高一层未达标的 Quality 之间做 1-2 步局部细化
                var higherFailing = candidates
                    .Where(c => c.Quality > bestCandidate.Quality && c.Bytes > targetOptions.TargetBytes)
                    .OrderBy(c => c.Quality)
                    .FirstOrDefault();

                if (higherFailing != null && higherFailing.Quality - bestCandidate.Quality > 3)
                {
                    var midQ = (bestCandidate.Quality + higherFailing.Quality) / 2;
                    if (!evaluated.ContainsKey(midQ))
                    {
                        var midBytes = await encodeRunner(currentFrames, temporaryOutputPath, midQ, cancellationToken).ConfigureAwait(false);
                        evaluated[midQ] = midBytes;
                        if (midBytes <= targetOptions.TargetBytes)
                        {
                            bestCandidate = new PngTargetCandidate(midQ, midBytes, midQ < 100);
                        }
                    }
                }

                // 重新写出选定的最优质量产物
                await encodeRunner(currentFrames, temporaryOutputPath, bestCandidate.Quality, cancellationToken).ConfigureAwait(false);

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
                    await encodeRunner(currentFrames, temporaryOutputPath, smallestCandidate.Quality, cancellationToken).ConfigureAwait(false);
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
                    await encodeRunner(currentFrames, temporaryOutputPath, smallestCandidate.Quality, cancellationToken).ConfigureAwait(false);
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
