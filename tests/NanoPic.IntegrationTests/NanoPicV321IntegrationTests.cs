using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NanoPic.Core;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class NanoPicV321IntegrationTests
{
    [Fact]
    public async Task KeyedLock_PreventsConcurrentDestinationConflict()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-lock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath1 = Path.Combine(tempDir, "source1.jpg");
            var sourcePath2 = Path.Combine(tempDir, "source2.jpg");
            var targetPath = Path.Combine(tempDir, "target.jpg");

            var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9 };
            File.WriteAllBytes(sourcePath1, jpegBytes);
            File.WriteAllBytes(sourcePath2, jpegBytes);

            var slowCodec = new DelayedCodec(TimeSpan.FromMilliseconds(50));
            var service = new ImageFileProcessingService(slowCodec);

            var req1 = new ImageFileProcessRequest(
                sourcePath1,
                targetPath,
                new ImageEncodingOptions(ImageOutputFormat.Jpeg),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Overwrite);

            var req2 = new ImageFileProcessRequest(
                sourcePath2,
                targetPath,
                new ImageEncodingOptions(ImageOutputFormat.Jpeg),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Overwrite);

            var task1 = service.ProcessAsync(req1, CancellationToken.None);
            var task2 = service.ProcessAsync(req2, CancellationToken.None);

            var results = await Task.WhenAll(task1, task2);

            Assert.All(results, r => Assert.True(r.IsSuccess, r.Failure?.UserMessage));
            Assert.True(File.Exists(targetPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConflictPolicyFail_CorrectlyFailsWhenTargetAlreadyExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-conflict-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = Path.Combine(tempDir, "source.jpg");
            var targetPath = Path.Combine(tempDir, "target.jpg");

            var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9 };
            File.WriteAllBytes(sourcePath, jpegBytes);
            File.WriteAllBytes(targetPath, jpegBytes); // Existing file

            var service = new ImageFileProcessingService(new DelayedCodec(TimeSpan.Zero));
            var req = new ImageFileProcessRequest(
                sourcePath,
                targetPath,
                new ImageEncodingOptions(ImageOutputFormat.Jpeg),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Fail);

            var result = await service.ProcessAsync(req, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.NotNull(result.Failure);
            Assert.Equal(ImageFailureKind.FileAccessConflict, result.Failure!.Kind);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_FillsMissingSectionsWithDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            // JSON missing Processing, MetadataNote, OversizedImage sections
            var partialJson = @"{
                ""SchemaVersion"": 1,
                ""System"": { ""MaxThreads"": 4, ""TopMost"": true, ""UseGpu"": false, ""AutoDownscaleOnExceed"": true },
                ""Watermark"": { ""Enabled"": false, ""Text"": """", ""ColorHex"": ""#000000"", ""OpacityPercent"": 100, ""FontFamily"": ""Segoe UI"", ""FontSize"": 24 },
                ""Resize"": { ""Enabled"": false, ""Width"": 1920, ""Height"": 1080, ""PreserveAspectRatio"": true },
                ""Graph"": { ""BackgroundColorHex"": ""#FFFFFF"", ""BrightnessPercent"": 100 },
                ""Compress"": { ""OutputFormat"": 0, ""Quality"": 80, ""AllowExceedTarget"": false, ""TargetBytes"": 204800, ""UseTargetSize"": false, ""OutputFilenameTemplate"": ""{name}"", ""OutputIndex"": 1 },
                ""Ui"": { ""OutputDirectory"": """" }
            }";

            File.WriteAllText(settingsPath, partialJson);
            var store = new JsonSettingsStore(settingsPath);
            var loadResult = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(SettingsLoadSource.CurrentFile, loadResult.Source);
            Assert.NotNull(loadResult.Settings.Processing);
            Assert.NotNull(loadResult.Settings.MetadataNote);
            Assert.NotNull(loadResult.Settings.OversizedImage);
            Assert.Equal(200_000_000, loadResult.Settings.OversizedImage.SoftMaxPixels);
            Assert.True(loadResult.Settings.OversizedImage.AutoDownsample);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RedactingLogger_RedactsUncAndSpecialPaths()
    {
        // 裸路径后的尾随文本会被一并吞掉：路径段允许空格后无法与行内后续文字区分，按"宁多脱敏"处理。
        var message1 = @"Processing \\server\share\folder\image.png failed";
        var message2 = @"Processing \\?\C:\VeryLongPath\image.png failed";
        var message3 = @"Processing C:\Users\Alice\Pictures\test.jpg completed";

        Assert.Equal("Processing <path>", RedactingFileLogger.Redact(message1));
        Assert.Equal("Processing <path>", RedactingFileLogger.Redact(message2));
        Assert.Equal("Processing <path>", RedactingFileLogger.Redact(message3));
    }

    [Fact]
    public void RedactingLogger_RedactsPathsWithSpaces()
    {
        // .NET 异常消息中路径通常带引号，应精确截断并保留路径外的说明文字（含外侧引号）。
        var quoted = "Access to the path 'C:\\Users\\Foo Bar\\图片 1.png' is denied.";
        Assert.Equal("Access to the path '<path>' is denied.", RedactingFileLogger.Redact(quoted));

        var quotedCn = "对路径 \"C:\\Users\\李四 的 相册\\photo.png\" 的访问被拒绝。";
        Assert.Equal("对路径 \"<path>\" 的访问被拒绝。", RedactingFileLogger.Redact(quotedCn));

        var bare = "读取 C:\\Users\\Foo Bar\\img.png 时出错";
        Assert.Equal("读取 <path>", RedactingFileLogger.Redact(bare));
    }

    private sealed class DelayedCodec : IImageCodec
    {
        private readonly TimeSpan _delay;

        public DelayedCodec(TimeSpan delay)
        {
            _delay = delay;
        }

        public IReadOnlyCollection<ImageFormat> SupportedFormats { get; } = new HashSet<ImageFormat> { ImageFormat.Jpeg };

        public Task<ImageOperationResult<ImageMetadata>> IdentifyAsync(Stream input, CancellationToken cancellationToken)
        {
            return Task.FromResult(ImageOperationResult<ImageMetadata>.Success(
                new ImageMetadata(ImageFormat.Jpeg, 100, 100, 1, false, input.Length)));
        }

        public async Task<ImageOperationResult<ImageEncodedOutput>> TransformAndEncodeAsync(ImageEncodeRequest request, CancellationToken cancellationToken)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            File.WriteAllBytes(request.TemporaryOutputPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            var metadata = new ImageMetadata(ImageFormat.Jpeg, 100, 100, 1, false, 4);
            return ImageOperationResult<ImageEncodedOutput>.Success(new ImageEncodedOutput(metadata, 80, 4, true, false));
        }
    }
}
