using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NanoPic.Core;

public sealed class BoundedImageBatchProcessor
{
    private sealed class AsyncAutoResetEvent
    {
        private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();
        private bool _signaled;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (_waiters)
            {
                if (_signaled)
                {
                    _signaled = false;
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.AddLast(tcs);
                if (cancellationToken.CanBeCanceled)
                {
                    var registration = cancellationToken.Register(() =>
                    {
                        lock (_waiters)
                        {
                            _waiters.Remove(tcs);
                            tcs.TrySetCanceled(cancellationToken);
                        }
                    });
                    _ = tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
                }

                return tcs.Task;
            }
        }

        public void Set()
        {
            lock (_waiters)
            {
                if (_waiters.Count > 0)
                {
                    var next = _waiters.First!.Value;
                    _waiters.RemoveFirst();
                    next.TrySetResult(true);
                }
                else
                {
                    _signaled = true;
                }
            }
        }
    }

    private readonly ImageFileProcessingService _processingService;

    public BoundedImageBatchProcessor(ImageFileProcessingService processingService)
    {
        _processingService = processingService ?? throw new ArgumentNullException(nameof(processingService));
    }

    public Task<ImageBatchResult> ProcessAsync(
        IReadOnlyList<ImageFileProcessRequest> requests,
        int maxDegreeOfParallelism,
        IProgress<ImageBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        return ProcessAsync(requests, maxDegreeOfParallelism, imagePixelCounts: null, progress, cancellationToken);
    }

    public async Task<ImageBatchResult> ProcessAsync(
        IReadOnlyList<ImageFileProcessRequest> requests,
        int maxDegreeOfParallelism,
        IReadOnlyList<long>? imagePixelCounts,
        IProgress<ImageBatchProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? protectedSourcePaths = null)
    {
        if (requests is null) throw new ArgumentNullException(nameof(requests));
        if (maxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "最大并发数必须大于零。");
        }

        if (imagePixelCounts is not null && imagePixelCounts.Count != requests.Count)
        {
            throw new ArgumentException("像素统计列表长度必须与请求列表长度一致。", nameof(imagePixelCounts));
        }

        if (requests.Count == 0)
        {
            var emptyProgress = new ImageBatchProgress(0, 0, 0, 0, 0);
            progress?.Report(emptyProgress);
            return new ImageBatchResult(Array.Empty<ImageOperationResult<ImageFileProcessResult>>(), emptyProgress);
        }

        var planned = await Task.Run(() => ImageBatchPathPlanner.Plan(requests, cancellationToken, protectedSourcePaths)).ConfigureAwait(false);
        var results = new ImageOperationResult<ImageFileProcessResult>[requests.Count];
        var activeLimits = new List<int>();
        var lockObj = new object();
        var slotAvailable = new AsyncAutoResetEvent();
        var runningTasks = new List<Task>();
        var oomTriggered = false;

        var completed = 0;
        var succeeded = 0;
        var failed = 0;
        var canceled = 0;

        int GetLimitForIndex(int idx)
        {
            if (imagePixelCounts is null) return maxDegreeOfParallelism;
            var pixels = imagePixelCounts[idx];
            if (pixels <= 0)
            {
                // Unknown or invalid pixel count: treat as highest risk (concurrency 1)
                return 1;
            }
            return OversizedImageConcurrencyPolicy.LimitFor(pixels, maxDegreeOfParallelism).MaxConcurrentTasks;
        }

        // Serialize counters and notifications so concurrent completions never send
        // inconsistent totals or move the UI progress backwards.
        var progressLock = new object();
        void ReportItem(int index, ImageOperationResult<ImageFileProcessResult>? result)
        {
            lock (progressLock)
            {
                if (result is not null)
                {
                    results[index] = result;
                    completed++;
                    if (result.IsSuccess) succeeded++;
                    else if (result.Failure?.Kind == ImageFailureKind.TaskCanceled) canceled++;
                    else failed++;
                }
                progress?.Report(new ImageBatchProgress(requests.Count, completed, succeeded,
                    failed, canceled, new ImageBatchItemProgress(index, result)));
            }
        }

        ImageOperationResult<ImageFileProcessResult> SkippedResult() =>
            Volatile.Read(ref oomTriggered)
                ? ImageOperationResult<ImageFileProcessResult>.Failed(
                    ImageFailureKind.PixelBudgetExceeded, "因内存不足，任务已跳过。")
                : ImageOperationResult<ImageFileProcessResult>.Failed(
                    ImageFailureKind.TaskCanceled, "批处理已取消，任务未开始。");

        for (var index = 0; index < requests.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref oomTriggered))
            {
                ReportItem(index, SkippedResult());
                continue;
            }

            if (!planned[index].IsSuccess || planned[index].Value is null)
            {
                ReportItem(index, new ImageOperationResult<ImageFileProcessResult>(default, planned[index].Failure));
                continue;
            }

            var taskLimit = GetLimitForIndex(index);

            var admitted = false;
            // FIFO admission wait
            while (true)
            {
                if (cancellationToken.IsCancellationRequested || Volatile.Read(ref oomTriggered)) break;

                lock (lockObj)
                {
                    var currentActive = activeLimits.Count;
                    var minActiveLimit = currentActive == 0 ? maxDegreeOfParallelism : activeLimits.Min();
                    var allowedCapacity = Math.Min(maxDegreeOfParallelism, Math.Min(minActiveLimit, taskLimit));

                    if (currentActive < allowedCapacity)
                    {
                        activeLimits.Add(taskLimit);
                        admitted = true;
                        break;
                    }
                }

                try
                {
                    await slotAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // Drain running workers before returning; never abandon them.
                }
            }

            if (!admitted)
            {
                ReportItem(index, SkippedResult());
                continue;
            }

            var currentIndex = index;
            var currentReq = planned[currentIndex].Value!;
            var currentLimit = taskLimit;

            var task = Task.Run(async () =>
            {
                try
                {
                    ImageOperationResult<ImageFileProcessResult> result;
                    if (cancellationToken.IsCancellationRequested || Volatile.Read(ref oomTriggered))
                    {
                        result = ImageOperationResult<ImageFileProcessResult>.Failed(
                            ImageFailureKind.TaskCanceled, "批处理已取消，任务未开始。");
                    }
                    else
                    {
                        try
                        {
                            ReportItem(currentIndex, null);
                            result = await _processingService.ProcessAsync(currentReq, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OutOfMemoryException oom)
                        {
                            Volatile.Write(ref oomTriggered, true);
                            result = ImageOperationResult<ImageFileProcessResult>.Failed(
                                ImageFailureKind.PixelBudgetExceeded,
                                "系统内存不足，已终止处理。",
                                oom);
                        }
                        catch (OperationCanceledException)
                        {
                            result = ImageOperationResult<ImageFileProcessResult>.Failed(
                                ImageFailureKind.TaskCanceled, "任务已取消。");
                        }
                        catch (Exception ex)
                        {
                            result = ImageOperationResult<ImageFileProcessResult>.Failed(
                                ImageFailureKind.Unknown, "处理过程中发生未预期错误。", ex);
                        }
                    }

                    ReportItem(currentIndex, result);
                }
                finally
                {
                    lock (lockObj)
                    {
                        activeLimits.Remove(currentLimit);
                    }
                    slotAvailable.Set();
                }
            });

            runningTasks.Add(task);
        }

        await Task.WhenAll(runningTasks).ConfigureAwait(false);

        var finalProgress = new ImageBatchProgress(
            requests.Count,
            Volatile.Read(ref completed),
            Volatile.Read(ref succeeded),
            Volatile.Read(ref failed),
            Volatile.Read(ref canceled));
        progress?.Report(finalProgress);

        return new ImageBatchResult(results, finalProgress);
    }
}
