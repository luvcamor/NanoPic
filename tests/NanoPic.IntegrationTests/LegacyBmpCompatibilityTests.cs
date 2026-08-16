using ImageMagick;
using NanoPic.Codecs;
using NanoPic.Core;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class LegacyBmpCompatibilityTests
{
    [Fact]
    public async Task Process_legacy_os2_palette_bmp_to_png()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "assets",
            "legacy-inputs",
            "palette.bmp"));
        Assert.True(File.Exists(sourcePath), $"Missing legacy BMP baseline asset: {sourcePath}");

        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-LegacyBmp-");
        try
        {
            var destinationPath = Path.Combine(directory.FullName, "palette-output.png");
            var service = new ImageFileProcessingService(new WicImageCodec());

            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.Kind}: {result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            Assert.True(File.Exists(destinationPath));

            using var output = new MagickImage(destinationPath);
            Assert.Equal(MagickFormat.Png, output.Format);
            Assert.True(output.Width > 0);
            Assert.True(output.Height > 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_animated_gif_to_gif_preserves_multiple_frames()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "assets",
            "legacy-inputs",
            "animated.gif"));
        Assert.True(File.Exists(sourcePath), $"Missing animated GIF baseline asset: {sourcePath}");

        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-AnimatedGif-");
        try
        {
            var destinationPath = Path.Combine(directory.FullName, "animated-output.gif");
            var service = new ImageFileProcessingService(new WicImageCodec());

            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Gif, Quality: 80),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.Kind}: {result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            Assert.True(File.Exists(destinationPath));
            Assert.True(result.Value?.Output?.Metadata.FrameCount > 1);

            using var output = new MagickImageCollection(destinationPath);
            Assert.True(output.Count > 1);
            Assert.All(output, frame => Assert.Equal(MagickFormat.Gif, frame.Format));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
