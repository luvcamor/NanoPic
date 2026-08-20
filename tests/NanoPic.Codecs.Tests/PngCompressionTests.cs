using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using NanoPic.Codecs;
using NanoPic.Core;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class PngCompressionTests
{
    private static string CreateGradientPng(string directory, int width, int height)
    {
        var path = Path.Combine(directory, $"grad_{width}_{height}_{Guid.NewGuid():N}.png");
        using var image = new MagickImage(MagickColors.Transparent, (uint)width, (uint)height);
        image.Depth = 8;
        var pixels = image.GetPixels();
        var rand = new Random(12345);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = (byte)((x * 5 + rand.Next(0, 40)) % 256);
                var g = (byte)((y * 7 + rand.Next(0, 40)) % 256);
                var b = (byte)(((x + y) * 3 + rand.Next(0, 40)) % 256);
                pixels.SetPixel(x, y, new byte[] { r, g, b, 255 });
            }
        }
        image.Format = MagickFormat.Png32;
        image.Write(path);
        return path;
    }

    [Fact]
    public async Task C1_Png_Quality_10_vs_60_Produces_Different_Outputs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 200, 200);
            var outQ10 = Path.Combine(tempDir, "out10.png");
            var outQ60 = Path.Combine(tempDir, "out60.png");

            var codec = new WicImageCodec();
            var req10 = new ImageEncodeRequest(
                sourcePath, outQ10, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 10),
                ImageSafetyLimits.Default);

            var req60 = new ImageEncodeRequest(
                sourcePath, outQ60, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 60),
                ImageSafetyLimits.Default);

            var res10 = await codec.TransformAndEncodeAsync(req10, CancellationToken.None);
            var res60 = await codec.TransformAndEncodeAsync(req60, CancellationToken.None);

            Assert.True(res10.IsSuccess);
            Assert.True(res60.IsSuccess);

            var size10 = new FileInfo(outQ10).Length;
            var size60 = new FileInfo(outQ60).Length;

            // 质量 10 调色板更小且无抖动，文件体积应小于质量 60
            Assert.True(size10 < size60, $"Expected Q10 ({size10} bytes) < Q60 ({size60} bytes)");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task C2_Png_Quality_100_Preserves_Lossless()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-q100-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 40, 40);
            var outPath = Path.Combine(tempDir, "out100.png");

            var codec = new WicImageCodec();
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                // SettingsFormMapper always supplies this background option. It is
                // semantically inactive for PNG and must not disable source reuse.
                new ImageTransformOptions(
                    Background: new ImageBackgroundOptions(true, "#FFFFFF")),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 100),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.True(res.IsSuccess);

            using var srcImg = new MagickImage(sourcePath);
            using var dstImg = new MagickImage(outPath);
            Assert.Equal(srcImg.Width, dstImg.Width);
            Assert.Equal(srcImg.Height, dstImg.Height);
            Assert.Equal(0d, srcImg.Compare(dstImg, ErrorMetric.Absolute));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task C3b_TargetSize_AlreadySatisfied_Reuses_Source_Png()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-source-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png");
            Assert.True(File.Exists(sourcePath), $"Missing test asset: {sourcePath}");
            var sourceBytes = new FileInfo(sourcePath).Length;
            var outPath = Path.Combine(tempDir, "already-satisfied.png");

            var codec = new WicImageCodec();
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(
                    Background: new ImageBackgroundOptions(true, "#FFFFFF")),
                new ImageEncodingOptions(
                    ImageOutputFormat.Png,
                    Quality: 80,
                    TargetSize: new TargetSizeOptions(
                        TargetBytes: sourceBytes,
                        AllowExceed: false,
                        AllowResizeForTarget: false)),
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(req, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.NotNull(result.Value);
            Assert.True(result.Value!.TargetSizeReached);
            Assert.False(result.Value.ExceededTarget);
            Assert.Equal(sourceBytes, result.Value.Bytes);
            Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task C3_NoOp_Png_Avoids_Expansion_Via_SkipIfLarger()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png");
            Assert.True(File.Exists(sourcePath), $"Missing test asset: {sourcePath}");
            var sourceBytes = new FileInfo(sourcePath).Length;
            var outPath = Path.Combine(tempDir, "out_noop.png");

            var codec = new WicImageCodec();
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(
                    Background: new ImageBackgroundOptions(true, "#FFFFFF")),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 100),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.True(res.IsSuccess);

            var outBytes = new FileInfo(outPath).Length;
            Assert.True(outBytes <= sourceBytes, $"Output bytes ({outBytes}) must not exceed source bytes ({sourceBytes})");
            Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task C4_Png_With_Watermark_Does_Not_Skip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-wm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 100, 100);
            var outPath = Path.Combine(tempDir, "out_wm.png");

            var codec = new WicImageCodec();
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(Watermark: new ImageWatermarkOptions(true, "Sample Watermark", "#FF0000", 80)),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 100),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.True(res.IsSuccess);
            Assert.NotEqual(
                Convert.ToBase64String(File.ReadAllBytes(sourcePath)),
                Convert.ToBase64String(File.ReadAllBytes(outPath)));

            using var dstImg = new MagickImage(outPath);
            Assert.Equal((uint)100, dstImg.Width);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task T1_TargetSize_Reaches_Within_Original_Dimensions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 80, 80);
            var outPath = Path.Combine(tempDir, "out_target.png");

            var codec = new WicImageCodec();
            // 设定一个量化可以达到的 targetBytes
            var targetBytes = 5000L;
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80,
                    TargetSize: new TargetSizeOptions(targetBytes, AllowExceed: false, AllowResizeForTarget: false)),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.True(res.IsSuccess, res.Failure?.UserMessage);
            Assert.NotNull(res.Value);
            var outBytes = new FileInfo(outPath).Length;
            Assert.True(outBytes <= targetBytes);
            Assert.Equal(80, res.Value!.Metadata.Width);
            Assert.Equal(80, res.Value.Metadata.Height);
            Assert.False(res.Value.TargetSizeResized);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task T2_TargetSize_Unreachable_Fails_Without_Resize_Permission()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-unreach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 200, 200);
            var outPath = Path.Combine(tempDir, "out_unreach.png");

            var codec = new WicImageCodec();
            // 极低 targetBytes (100 字节)，原尺寸必无法达到
            var targetBytes = 100L;
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80,
                    TargetSize: new TargetSizeOptions(targetBytes, AllowExceed: false, AllowResizeForTarget: false)),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.False(res.IsSuccess);
            Assert.Equal(ImageFailureKind.TargetSizeUnreachable, res.Failure?.Kind);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task T3_TargetSize_AllowExceed_Returns_Smallest_Original_Candidate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-exceed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 100, 100);
            var outPath = Path.Combine(tempDir, "out_exceed.png");

            var codec = new WicImageCodec();
            // 极低 targetBytes (100 字节)，但 AllowExceed = true 且 AllowResizeForTarget = false
            var targetBytes = 100L;
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80,
                    TargetSize: new TargetSizeOptions(targetBytes, AllowExceed: true, AllowResizeForTarget: false)),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.True(res.IsSuccess);
            Assert.True(res.Value!.ExceededTarget);
            Assert.False(res.Value!.TargetSizeResized);
            Assert.Equal(100, res.Value!.Metadata.Width);
            Assert.Equal(100, res.Value!.Metadata.Height);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task T4_TargetSize_With_Resize_Permission_Adapts_Dimensions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-png-adapt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = CreateGradientPng(tempDir, 200, 200);
            var outPath = Path.Combine(tempDir, "out_adapt.png");

            var codec = new WicImageCodec();
            // 设定一个原尺寸很难达到但缩小后能达到的大小
            var targetBytes = 800L;
            var req = new ImageEncodeRequest(
                sourcePath, outPath, ImageFormat.Png, ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80,
                    TargetSize: new TargetSizeOptions(targetBytes, AllowExceed: false, AllowResizeForTarget: true)),
                ImageSafetyLimits.Default);

            var res = await codec.TransformAndEncodeAsync(req, CancellationToken.None);
            Assert.True(res.IsSuccess, res.Failure?.UserMessage);
            Assert.NotNull(res.Value);
            Assert.True(res.Value!.TargetSizeResized);
            Assert.True(res.Value.Metadata.Width < 200);
            Assert.True(res.Value.Metadata.Height < 200);
            Assert.NotNull(res.Value.TargetSizeNotice);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
