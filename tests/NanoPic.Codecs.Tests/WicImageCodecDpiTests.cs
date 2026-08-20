using NanoPic.Codecs;
using NanoPic.Core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class WicImageCodecDpiTests
{
    [Fact]
    public async Task Resize_of_72_dpi_source_fills_entire_canvas_opaquely()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-DPI72-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "dpi72.jpg");
            var outputPath = Path.Combine(directory.FullName, "resized.png");
            using (var source = new ImageMagick.MagickImage(new ImageMagick.MagickColor("#808080"), 113, 150)
            {
                Format = ImageMagick.MagickFormat.Jpeg,
                Density = new ImageMagick.Density(72, 72, ImageMagick.DensityUnit.PixelsPerInch)
            })
            {
                source.Write(sourcePath);
            }

            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Png,
                new ImageTransformOptions(Resize: new ImageResizeOptions(true, 800, 600, true)),
                new ImageEncodingOptions(ImageOutputFormat.Png),
                ImageSafetyLimits.Default);
            var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");
            var stats = DecodeBgraStats(outputPath);
            Assert.Equal(452, stats.Width);
            Assert.Equal(600, stats.Height);
            Assert.True(stats.OpaqueFraction > 0.999, $"Expected fully opaque output, opaque fraction={stats.OpaqueFraction}.");
            Assert.True(stats.MeanLuminance > 100, $"Expected full canvas coverage, mean luminance={stats.MeanLuminance}.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Transform_chain_of_300_dpi_source_stays_opaque()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-DPI300-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "dpi300.png");
            var outputPath = Path.Combine(directory.FullName, "transformed.png");
            using (var source = new ImageMagick.MagickImage(new ImageMagick.MagickColor("#4466AA"), 400, 300)
            {
                Format = ImageMagick.MagickFormat.Png,
                Density = new ImageMagick.Density(300, 300, ImageMagick.DensityUnit.PixelsPerInch)
            })
            {
                source.Write(sourcePath);
            }

            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Png,
                ImageFormat.Png,
                new ImageTransformOptions(
                    Resize: new ImageResizeOptions(true, 200, 150, true),
                    BrightnessPercent: 140,
                    Watermark: new ImageWatermarkOptions(true, "NanoPic", "#FF0000", 90, 24, 16)),
                new ImageEncodingOptions(ImageOutputFormat.Png),
                ImageSafetyLimits.Default);

            var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");
            var stats = DecodeBgraStats(outputPath);
            Assert.Equal(200, stats.Width);
            Assert.Equal(150, stats.Height);
            Assert.True(stats.OpaqueFraction > 0.999, $"Expected fully opaque output, opaque fraction={stats.OpaqueFraction}.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(ImageWatermarkPosition.TopLeft)]
    [InlineData(ImageWatermarkPosition.TopRight)]
    [InlineData(ImageWatermarkPosition.BottomLeft)]
    [InlineData(ImageWatermarkPosition.BottomRight)]
    [InlineData(ImageWatermarkPosition.Center)]
    public async Task Watermark_lands_in_the_requested_region(ImageWatermarkPosition position)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-WmPos-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "white.png");
            var outputPath = Path.Combine(directory.FullName, "marked.png");
            using (var source = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 400, 300)
            {
                Format = ImageMagick.MagickFormat.Png
            })
            {
                source.Write(sourcePath);
            }

            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Png,
                ImageFormat.Png,
                new ImageTransformOptions(Watermark: new ImageWatermarkOptions(
                    true,
                    "NanoPic",
                    "#FF0000",
                    OpacityPercent: 100,
                    FontSize: 24,
                    Margin: 8,
                    Position: position)),
                new ImageEncodingOptions(ImageOutputFormat.Png),
                ImageSafetyLimits.Default);

            var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);
            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");

            var redByQuadrant = CountRedByQuadrant(outputPath, 400, 300);
            var expectedQuadrant = position switch
            {
                ImageWatermarkPosition.TopLeft => "TL",
                ImageWatermarkPosition.TopRight => "TR",
                ImageWatermarkPosition.BottomLeft => "BL",
                ImageWatermarkPosition.BottomRight => "BR",
                _ => "C"
            };
            Assert.True(redByQuadrant[expectedQuadrant] > 30,
                $"Position {position}: expected red text in {expectedQuadrant}, counts={Describe(redByQuadrant)}.");

            foreach (var quadrant in new[] { "TL", "TR", "BL", "BR" })
            {
                if (quadrant != expectedQuadrant)
                {
                    Assert.True(redByQuadrant[quadrant] == 0,
                        $"Position {position}: unexpected red text in {quadrant}, counts={Describe(redByQuadrant)}.");
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Watermark_random_position_lands_in_exactly_one_region()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-WmRand-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "white.png");
            using (var source = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 400, 300)
            {
                Format = ImageMagick.MagickFormat.Png
            })
            {
                source.Write(sourcePath);
            }

            var regionsSeen = new System.Collections.Generic.HashSet<string>();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var outputPath = Path.Combine(directory.FullName, $"marked-{attempt}.png");
                var request = new ImageEncodeRequest(
                    sourcePath,
                    outputPath,
                    ImageFormat.Png,
                    ImageFormat.Png,
                    new ImageTransformOptions(Watermark: new ImageWatermarkOptions(
                        true,
                        "NanoPic",
                        "#FF0000",
                        OpacityPercent: 100,
                        FontSize: 24,
                        Margin: 8,
                        Position: ImageWatermarkPosition.Random)),
                    new ImageEncodingOptions(ImageOutputFormat.Png),
                    ImageSafetyLimits.Default);

                var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);
                Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");

                var counts = CountRedByQuadrant(outputPath, 400, 300);
                var nonZero = counts.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToList();
                Assert.Single(nonZero);
                regionsSeen.Add(nonZero[0]);
            }

            Assert.True(regionsSeen.Count >= 2,
                $"Random placement should vary across runs, saw only [{string.Join(",", regionsSeen)}].");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static System.Collections.Generic.Dictionary<string, int> CountRedByQuadrant(string path, int width, int height)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(pixels, checked(converted.PixelWidth * 4), 0);
        var counts = new System.Collections.Generic.Dictionary<string, int>
        {
            ["TL"] = 0,
            ["TR"] = 0,
            ["BL"] = 0,
            ["BR"] = 0,
            ["C"] = 0
        };
        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var offset = (y * converted.PixelWidth + x) * 4;
                if (pixels[offset + 2] > 200 && pixels[offset + 1] < 80 && pixels[offset] < 80)
                {
                    var horizontal = x < width * 0.3 ? "L" : x >= width - width * 0.3 ? "R" : null;
                    var vertical = y < height * 0.2 ? "T" : y >= height - height * 0.2 ? "B" : null;
                    var key = horizontal is null || vertical is null ? "C" : vertical + horizontal;
                    counts[key]++;
                }
            }
        }

        return counts;
    }

    private static string Describe(System.Collections.Generic.Dictionary<string, int> counts) =>
        string.Join(",", counts.Select(pair => $"{pair.Key}={pair.Value}"));

    private static (int Width, int Height, double OpaqueFraction, double MeanLuminance) DecodeBgraStats(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        converted.CopyPixels(pixels, checked(width * 4), 0);
        double opaque = 0;
        double luminance = 0;
        var samples = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset + 3] >= 250)
                {
                    opaque++;
                }

                luminance += (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3.0;
                samples++;
            }
        }

        return (width, height, opaque / samples, luminance / samples);
    }
}
