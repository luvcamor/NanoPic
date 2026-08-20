using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NanoPic.Codecs;
using NanoPic.Core;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class NanoPicV321CodecRegressionTests
{
    [Theory]
    [InlineData(ImageFormat.Jpeg, true)]
    [InlineData(ImageFormat.Webp, true)]
    [InlineData(ImageFormat.Png, true)]
    [InlineData(ImageFormat.Bmp, false)]
    [InlineData(ImageFormat.Gif, false)]
    [InlineData(ImageFormat.Tiff, false)]
    [InlineData(ImageFormat.Ico, false)]
    public void SupportsQualitySearch_CorrectlyDifferentiatesFormats(ImageFormat format, bool expected)
    {
        var actual = WicImageCodec.SupportsQualitySearch(format);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WicImageCodec_PreservesSourceDpiOnResizeAndFlatten()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-dpi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = Path.Combine(tempDir, "dpi300.png");
            var outputPath = Path.Combine(tempDir, "out.png");

            // Create a 300 DPI PNG (100x100)
            var pixelData = new byte[100 * 100 * 4];
            for (var i = 0; i < pixelData.Length; i += 4)
            {
                pixelData[i] = 255;     // B
                pixelData[i + 1] = 0;   // G
                pixelData[i + 2] = 0;   // R
                pixelData[i + 3] = 255; // A
            }

            var bitmap = BitmapSource.Create(100, 100, 300, 300, System.Windows.Media.PixelFormats.Bgra32, null, pixelData, 400);
            using (var stream = File.Create(sourcePath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            var codec = new WicImageCodec();
            var req = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Png,
                ImageFormat.Png,
                new ImageTransformOptions(
                    Resize: new ImageResizeOptions(true, 50, 50, true),
                    Background: new ImageBackgroundOptions(true, "#00FF00")),
                new ImageEncodingOptions(ImageOutputFormat.Png),
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(req, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.True(File.Exists(outputPath));

            using (var outStream = File.OpenRead(outputPath))
            {
                var decoder = BitmapDecoder.Create(outStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                Assert.Equal(50, frame.PixelWidth);
                Assert.Equal(50, frame.PixelHeight);
                Assert.True(Math.Abs(frame.DpiX - 300) < 1.0, $"Expected 300 DPI, got {frame.DpiX}");
                Assert.True(Math.Abs(frame.DpiY - 300) < 1.0, $"Expected 300 DPI, got {frame.DpiY}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
