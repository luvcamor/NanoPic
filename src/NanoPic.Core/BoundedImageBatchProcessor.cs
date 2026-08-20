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

                _waiters.AddLast(tcs);
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
        CancellationToken cancellationToken)
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

        for (var index = 0; index < requests.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref oomTriggered))
            {
                var cancelKind = Volatile.Read(ref oomTriggered)
                    ? ImageFailureKind.PixelBudgetExceeded
                    : ImageFailureKind.TaskCanceled;
                var cancelMessage = Volatile.Read(ref oomTriggered)
                    ? "因内存不足，任务已跳过。"
                    : "批处理已取消，任务未开始。";

                results[index] = ImageOperationResult<ImageFileProcessResult>.Failed(cancelKind, cancelMessage);
                var completedNow = Interlocked.Increment(ref completed);
                if (cancelKind == ImageFailureKind.TaskCanceled)
                {
                    Interlocked.Increment(ref canceled);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                progress?.Report(new ImageBatchProgress(
                    requests.Count,
                    completedNow,
                    Volatile.Read(ref succeeded),
                    Volatile.Read(ref failed),
                    Volatile.Read(ref canceled)));
                continue;
            }

            var taskLimit = GetLimitForIndex(index);

            // FIFO admission wait
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref oomTriggered)) break;

                lock (lockObj)
                {
                    var currentActive = activeLimits.Count;
                    var minActiveLimit = currentActive == 0 ? maxDegreeOfParallelism : activeLimits.Min();
                    var allowedCapacity = Math.Min(maxDegreeOfParallelism, Math.Min(minActiveLimit, taskLimit));

                    if (currentActive < allowedCapacity)
                    {
                        activeLimits.Add(taskLimit);
                        break;
                    }
                }

                await slotAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Volatile.Read(ref oomTriggered))
            {
                results[index] = ImageOperationResult<ImageFileProcessResult>.Failed(
                    ImageFailureKind.PixelBudgetExceeded, "因内存不足，任务已跳过。");
                Interlocked.Increment(ref completed);
                Interlocked.Increment(ref failed);
                continue;
            }

            var currentIndex = index;
            var currentReq = requests[currentIndex];
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
                                ImageFailureKind.Unknown, $"处理过程中发生未预期异常：{ex.Message}", ex);
                        }
                    }

                    results[currentIndex] = result;
                    var completedNow = Interlocked.Increment(ref completed);
                    if (result.IsSuccess)
                    {
                        Interlocked.Increment(ref succeeded);
                    }
                    else if (result.Failure?.Kind == ImageFailureKind.TaskCanceled)
                    {
                        Interlocked.Increment(ref canceled);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }

                    progress?.Report(new ImageBatchProgress(
                        requests.Count,
                        completedNow,
                        Volatile.Read(ref succeeded),
                        Volatile.Read(ref failed),
                        Volatile.Read(ref canceled)));
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
