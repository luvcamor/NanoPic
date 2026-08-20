using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class PngCompressionIntegrationTests
{
    private static string CreateSamplePng(string directory, int width, int height)
    {
        var path = Path.Combine(directory, $"sample_{width}_{height}_{Guid.NewGuid():N}.png");
        using var image = new MagickImage(MagickColors.Transparent, (uint)width, (uint)height);
        image.Depth = 8;
        var pixels = image.GetPixels();
        var rand = new Random(42);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = (byte)((x * 3 + rand.Next(0, 50)) % 256);
                var g = (byte)((y * 5 + rand.Next(0, 50)) % 256);
                var b = (byte)(((x + y) * 2 + rand.Next(0, 50)) % 256);
                pixels.SetPixel(x, y, new byte[] { r, g, b, 255 });
            }
        }
        image.Format = MagickFormat.Png32;
        image.Write(path);
        return path;
    }

    [Fact]
    public async Task Issue4_Scenario_PngQuality_ReducesFileSize_WithoutDimensionChange()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-issue4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 构造代表性 400x400 PNG 样本
            var sourcePath = CreateSamplePng(tempDir, 400, 400);
            var sourceBytes = new FileInfo(sourcePath).Length;
            var destPath = Path.Combine(tempDir, "output_q60.png");

            var codec = new WicImageCodec();
            var service = new ImageFileProcessingService(codec);

            var request = new ImageFileProcessRequest(
                sourcePath,
                destPath,
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 60),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Overwrite);

            var result = await service.ProcessAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.True(File.Exists(destPath));

            var outputBytes = new FileInfo(destPath).Length;
            Assert.True(outputBytes < sourceBytes, $"Output bytes ({outputBytes}) should be significantly less than source bytes ({sourceBytes})");

            // 验证宽高未被缩小
            Assert.Equal(400, result.Value!.Source.Width);
            Assert.Equal(400, result.Value!.Source.Height);
            Assert.Equal(400, result.Value!.Output!.Metadata.Width);
            Assert.Equal(400, result.Value!.Output!.Metadata.Height);
            Assert.False(result.Value!.AutoDownsampled);
            Assert.False(result.Value!.TargetSizeResized);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TargetSize_OriginalDimensionPriority_DoesNotResizeByDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-targetsize-no-resize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = CreateSamplePng(tempDir, 200, 200);
            var destPath = Path.Combine(tempDir, "output_target.png");

            var codec = new WicImageCodec();
            var service = new ImageFileProcessingService(codec);

            // 设定较小目标且 AllowResizeForTarget = false, AllowExceed = true
            var request = new ImageFileProcessRequest(
                sourcePath,
                destPath,
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80,
                    TargetSize: new TargetSizeOptions(TargetBytes: 1000, AllowExceed: true, AllowResizeForTarget: false)),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Overwrite);

            var result = await service.ProcessAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.Equal(200, result.Value!.Output!.Metadata.Width);
            Assert.Equal(200, result.Value!.Output!.Metadata.Height);
            Assert.False(result.Value!.TargetSizeResized);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TargetSize_ResizeAllowed_DownscalesWhenUnreachable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-targetsize-resize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = CreateSamplePng(tempDir, 200, 200);
            var destPath = Path.Combine(tempDir, "output_target_resized.png");

            var codec = new WicImageCodec();
            var service = new ImageFileProcessingService(codec);

            // 设定极小目标且 AllowResizeForTarget = true
            var request = new ImageFileProcessRequest(
                sourcePath,
                destPath,
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80,
                    TargetSize: new TargetSizeOptions(TargetBytes: 800, AllowExceed: false, AllowResizeForTarget: true)),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Overwrite);

            var result = await service.ProcessAsync(request, CancellationToken.None);

            if (result.IsSuccess)
            {
                Assert.True(result.Value!.TargetSizeResized);
                Assert.True(result.Value!.Output!.Metadata.Width < 200);
                Assert.NotNull(result.Value!.TargetSizeNotice);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Settings_Compatibility_And_RoundTrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var store = new JsonSettingsStore(settingsPath);

            // 1. 测试旧版无 AllowResizeForTarget 字段的 JSON 加载兼容
            var oldJson = @"{
                ""SchemaVersion"": 1,
                ""System"": { ""MaxThreads"": 4, ""TopMost"": false, ""UseGpu"": false, ""AutoDownscaleOnExceed"": true },
                ""Watermark"": { ""Enabled"": false, ""Text"": """", ""ColorHex"": ""#000000"", ""OpacityPercent"": 100, ""FontFamily"": ""Segoe UI"", ""FontSize"": 24 },
                ""Resize"": { ""Enabled"": false, ""Width"": 1920, ""Height"": 1080, ""PreserveAspectRatio"": true },
                ""Graph"": { ""BackgroundColorHex"": ""#FFFFFF"", ""BrightnessPercent"": 100 },
                ""Compress"": { ""OutputFormat"": 1, ""Quality"": 75, ""AllowExceedTarget"": false, ""TargetBytes"": 204800, ""UseTargetSize"": true, ""OutputFilenameTemplate"": ""{name}"", ""OutputIndex"": 1 },
                ""Ui"": { ""OutputDirectory"": """" }
            }";
            File.WriteAllText(settingsPath, oldJson);

            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.Null(loaded.Failure);
            Assert.False(loaded.Settings.Compress.AllowResizeForTarget);

            // 2. 测试开启 AllowResizeForTarget 并保存
            var updated = loaded.Settings with
            {
                Compress = loaded.Settings.Compress with { AllowResizeForTarget = true }
            };

            var saveResult = await store.SaveAsync(updated, CancellationToken.None);
            Assert.True(saveResult.Saved);

            var reloaded = await store.LoadAsync(CancellationToken.None);
            Assert.Null(reloaded.Failure);
            Assert.True(reloaded.Settings.Compress.AllowResizeForTarget);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
