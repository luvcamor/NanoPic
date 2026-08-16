namespace NanoPic.Core;

public sealed class BoundedImageBatchProcessor
{
    private readonly ImageFileProcessingService _processingService;

    public BoundedImageBatchProcessor(ImageFileProcessingService processingService)
    {
        _processingService = processingService ?? throw new ArgumentNullException(nameof(processingService));
    }

    public async Task<ImageBatchResult> ProcessAsync(
        IReadOnlyList<ImageFileProcessRequest> requests,
        int maxDegreeOfParallelism,
        IProgress<ImageBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (requests is null) throw new ArgumentNullException(nameof(requests));
        if (maxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "最大并发数必须大于零。");
        }

        if (requests.Count == 0)
        {
            var emptyProgress = new ImageBatchProgress(0, 0, 0, 0, 0);
            progress?.Report(emptyProgress);
            return new ImageBatchResult(Array.Empty<ImageOperationResult<ImageFileProcessResult>>(), emptyProgress);
        }

        var results = new ImageOperationResult<ImageFileProcessResult>[requests.Count];
        var nextIndex = -1;
        var completed = 0;
        var succeeded = 0;
        var failed = 0;
        var canceled = 0;
        var workerCount = Math.Min(maxDegreeOfParallelism, requests.Count);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => RunWorkerAsync())
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        var finalProgress = new ImageBatchProgress(requests.Count, completed, succeeded, failed, canceled);
        progress?.Report(finalProgress);
        return new ImageBatchResult(results, finalProgress);

        async Task RunWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= requests.Count)
                {
                    return;
                }

                ImageOperationResult<ImageFileProcessResult> result;
                if (cancellationToken.IsCancellationRequested)
                {
                    result = ImageOperationResult<ImageFileProcessResult>.Failed(
                        ImageFailureKind.TaskCanceled,
                        "批处理已取消，任务未开始。");
                }
                else
                {
                    result = await _processingService.ProcessAsync(requests[index], cancellationToken).ConfigureAwait(false);
                }

                results[index] = result;
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
        }
    }
}
