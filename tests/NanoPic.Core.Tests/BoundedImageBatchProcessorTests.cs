using NanoPic.Core;
using Xunit;

namespace NanoPic.Core.Tests;

public sealed class BoundedImageBatchProcessorTests
{
    [Fact]
    public async Task Batch_processing_never_exceeds_configured_parallelism()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Core-");
        try
        {
            var codec = new TrackingCodec(TimeSpan.FromMilliseconds(20));
            var processor = new BoundedImageBatchProcessor(new ImageFileProcessingService(codec));
            var requests = CreateRequests(directory.FullName, 12);

            var result = await processor.ProcessAsync(requests, 2, progress: null, CancellationToken.None);

            Assert.Equal(12, result.Progress.Total);
            Assert.Equal(12, result.Progress.Completed);
            Assert.Equal(12, result.Progress.Succeeded);
            Assert.Equal(0, result.Progress.Failed);
            Assert.Equal(0, result.Progress.Canceled);
            Assert.All(result.Items, item => Assert.True(item.IsSuccess, item.Failure?.UserMessage));
            Assert.InRange(codec.MaximumConcurrentEncodes, 1, 2);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Canceled_batch_returns_structured_canceled_results_without_outputs()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Core-");
        try
        {
            var codec = new TrackingCodec(TimeSpan.Zero);
            var processor = new BoundedImageBatchProcessor(new ImageFileProcessingService(codec));
            var requests = CreateRequests(directory.FullName, 3);
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();

            var result = await processor.ProcessAsync(requests, 2, progress: null, cancellationSource.Token);

            Assert.Equal(3, result.Progress.Canceled);
            Assert.All(result.Items, item => Assert.Equal(ImageFailureKind.TaskCanceled, item.Failure?.Kind));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "output-*.jpg"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Failed_item_does_not_stop_other_batch_items()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Core-");
        try
        {
            var codec = new TrackingCodec(TimeSpan.Zero, failedSourceName: "source-1.jpg");
            var processor = new BoundedImageBatchProcessor(new ImageFileProcessingService(codec));
            var requests = CreateRequests(directory.FullName, 3);

            var result = await processor.ProcessAsync(requests, 2, progress: null, CancellationToken.None);

            Assert.Equal(2, result.Progress.Succeeded);
            Assert.Equal(1, result.Progress.Failed);
            Assert.False(result.Items[1].IsSuccess);
            Assert.True(result.Items[0].IsSuccess);
            Assert.True(result.Items[2].IsSuccess);
            Assert.True(File.Exists(Path.Combine(directory.FullName, "output-0.jpg")));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "output-1.jpg")));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "output-2.jpg")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task File_processing_uses_synchronous_streams_for_codec_identification()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Core-");
        try
        {
            var codec = new TrackingCodec(TimeSpan.Zero);
            var request = CreateRequests(directory.FullName, 1)[0];

            var result = await new ImageFileProcessingService(codec).ProcessAsync(
                request,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.True(codec.AllIdentificationsUsedSynchronousFileStreams);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IReadOnlyList<ImageFileProcessRequest> CreateRequests(string directory, int count)
    {
        var requests = new List<ImageFileProcessRequest>(count);
        for (var index = 0; index < count; index++)
        {
            var sourcePath = Path.Combine(directory, $"source-{index}.jpg");
            File.WriteAllBytes(sourcePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            requests.Add(new ImageFileProcessRequest(
                sourcePath,
                Path.Combine(directory, $"output-{index}.jpg"),
                new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default));
        }

        return requests;
    }

    private sealed class TrackingCodec : IImageCodec
    {
        private int _concurrentEncodes;
        private int _maximumConcurrentEncodes;
        private int _nonSynchronousIdentifications;
        private readonly TimeSpan _delay;
        private readonly string? _failedSourceName;

        public TrackingCodec(TimeSpan delay, string? failedSourceName = null)
        {
            _delay = delay;
            _failedSourceName = failedSourceName;
        }

        public int MaximumConcurrentEncodes => Volatile.Read(ref _maximumConcurrentEncodes);

        public bool AllIdentificationsUsedSynchronousFileStreams =>
            Volatile.Read(ref _nonSynchronousIdentifications) == 0;

        public IReadOnlyCollection<ImageFormat> SupportedFormats { get; } = new HashSet<ImageFormat> { ImageFormat.Jpeg };

        public Task<ImageOperationResult<ImageMetadata>> IdentifyAsync(Stream input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is not FileStream { IsAsync: false })
            {
                Interlocked.Increment(ref _nonSynchronousIdentifications);
            }

            return Task.FromResult(ImageOperationResult<ImageMetadata>.Success(
                new ImageMetadata(ImageFormat.Jpeg, 1, 1, 1, false, input.CanSeek ? input.Length : 0)));
        }

        public async Task<ImageOperationResult<ImageEncodedOutput>> TransformAndEncodeAsync(
            ImageEncodeRequest request,
            CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrentEncodes);
            SetMaximum(concurrent);
            try
            {
                if (string.Equals(Path.GetFileName(request.SourcePath), _failedSourceName, StringComparison.OrdinalIgnoreCase))
                {
                    return ImageOperationResult<ImageEncodedOutput>.Failed(ImageFailureKind.Unknown, "模拟单项编码失败。");
                }

                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken);
                }

                await TestCompatibility.WriteAllBytesAsync(
                    request.TemporaryOutputPath,
                    new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 },
                    cancellationToken);
                var metadata = new ImageMetadata(ImageFormat.Jpeg, 1, 1, 1, false, 4);
                return ImageOperationResult<ImageEncodedOutput>.Success(new ImageEncodedOutput(metadata, 80, 4, true, false));
            }
            catch (OperationCanceledException)
            {
                return ImageOperationResult<ImageEncodedOutput>.Failed(ImageFailureKind.TaskCanceled, "图像编码已取消。");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentEncodes);
            }
        }

        private void SetMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentEncodes);
                if (candidate <= current || Interlocked.CompareExchange(ref _maximumConcurrentEncodes, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }
}
