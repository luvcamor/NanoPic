using NanoPic.Codecs;
using NanoPic.Core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class WicImageCodecTests
{
    [Fact]
    public async Task Background_flattening_uses_the_requested_color()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-Background-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "transparent.png");
            var outputPath = Path.Combine(directory.FullName, "flattened.jpg");
            using (var source = new ImageMagick.MagickImage(ImageMagick.MagickColors.Transparent, 8, 8)
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
                ImageFormat.Jpeg,
                new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FF0000")),
                new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 100),
                ImageSafetyLimits.Default);

            var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");
            var pixel = ReadBgraPixel(outputPath, 4, 4);
            Assert.True(
                pixel[2] > 200 && pixel[1] < 40 && pixel[0] < 40,
                $"Expected red background, actual BGRA={pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("photo-metadata.jpg", ImageFormat.Jpeg)]
    [InlineData("transparent.png", ImageFormat.Png)]
    [InlineData("sample.webp", ImageFormat.Webp)]
    [InlineData("animated.gif", ImageFormat.Gif)]
    [InlineData("palette.bmp", ImageFormat.Bmp)]
    [InlineData("photo.tiff", ImageFormat.Tiff)]
    [InlineData("alpha.ico", ImageFormat.Ico)]
    public async Task Identify_reads_Windows_native_formats(string fileName, ImageFormat expectedFormat)
    {
        using var input = File.OpenRead(Asset(fileName));

        var result = await new WicImageCodec().IdentifyAsync(input, CancellationToken.None);

        Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
        Assert.Equal(expectedFormat, result.Value?.Format);
        Assert.True(result.Value?.Width > 0);
        Assert.True(result.Value?.Height > 0);
    }

    [Fact]
    public async Task Identify_accepts_async_file_stream_after_signature_probe()
    {
        using var input = new FileStream(
            Asset("transparent.png"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            useAsync: true);
        var detected = await ImageFileSignatureInspector.DetectAsync(input, CancellationToken.None);
        Assert.True(detected.IsSuccess, detected.Failure?.UserMessage);
        input.Position = 0;

        var result = await new WicImageCodec().IdentifyAsync(input, CancellationToken.None);

        Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
        Assert.Equal(ImageFormat.Png, result.Value?.Format);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Webp)]
    [InlineData(ImageFormat.Gif)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Ico)]
    public async Task Encode_writes_Windows_native_formats(ImageFormat outputFormat)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-");
        try
        {
            var outputPath = Path.Combine(directory.FullName, "encoded.tmp");
            var request = new ImageEncodeRequest(
                Asset("transparent.png"),
                outputPath,
                ImageFormat.Png,
                outputFormat,
                new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFF")),
                new ImageEncodingOptions(ToOutputFormat(outputFormat), Quality: 80),
                ImageSafetyLimits.Default);

            var result = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            using var output = File.OpenRead(outputPath);
            var detected = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
            Assert.True(detected.IsSuccess, detected.Failure?.UserMessage);
            Assert.Equal(outputFormat, detected.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Gif_to_gif_preserves_multiple_frames()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-Gif-");
        try
        {
            var outputPath = Path.Combine(directory.FullName, "animated.gif");
            var request = new ImageEncodeRequest(
                Asset("animated.gif"),
                outputPath,
                ImageFormat.Gif,
                ImageFormat.Gif,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Gif, Quality: 80),
                ImageSafetyLimits.Default);

            var codec = new WicImageCodec();
            var encoded = await codec.TransformAndEncodeAsync(request, CancellationToken.None);
            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");
            using var output = File.OpenRead(outputPath);
            var identified = await codec.IdentifyAsync(output, CancellationToken.None);
            Assert.True(identified.IsSuccess, identified.Failure?.UserMessage);
            Assert.True(identified.Value?.FrameCount > 1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Webp_input_decodes_through_libwebp_and_encodes_to_png()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-libwebp-decode-");
        try
        {
            var outputPath = Path.Combine(directory.FullName, "decoded.png");
            var request = new ImageEncodeRequest(
                Asset("sample.webp"),
                outputPath,
                ImageFormat.Webp,
                ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
                ImageSafetyLimits.Default);

            var result = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            using var output = File.OpenRead(outputPath);
            var detected = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
            Assert.Equal(ImageFormat.Png, detected.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Embedded_libwebp_tools_report_the_pinned_version()
    {
        Assert.StartsWith(LibWebpCodecEngine.Version, LibWebpCodecEngine.ProbeVersion());
    }

    [Fact]
    public async Task Ico_output_contains_multiple_alpha_frames_and_can_be_read_by_WIC()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-Ico-");
        try
        {
            var outputPath = Path.Combine(directory.FullName, "multi.ico");
            var request = new ImageEncodeRequest(
                Asset("transparent.png"),
                outputPath,
                ImageFormat.Png,
                ImageFormat.Ico,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Ico, Quality: 80),
                ImageSafetyLimits.Default);

            var codec = new WicImageCodec();
            var encoded = await codec.TransformAndEncodeAsync(request, CancellationToken.None);
            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");
            using var output = File.OpenRead(outputPath);
            var identified = await codec.IdentifyAsync(output, CancellationToken.None);
            Assert.True(identified.IsSuccess, identified.Failure?.UserMessage);
            Assert.True(identified.Value?.FrameCount > 1);
            Assert.True(identified.Value?.HasAlpha);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Asset(string fileName) => Path.Combine(AppContext.BaseDirectory, "assets", fileName);

    private static byte[] ReadBgraPixel(string path, int x, int y)
    {
        using var input = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            input,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            decoder.Frames[0],
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            0);
        var pixel = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static ImageOutputFormat ToOutputFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ImageOutputFormat.Jpeg,
        ImageFormat.Png => ImageOutputFormat.Png,
        ImageFormat.Webp => ImageOutputFormat.Webp,
        ImageFormat.Gif => ImageOutputFormat.Gif,
        ImageFormat.Bmp => ImageOutputFormat.Bmp,
        ImageFormat.Tiff => ImageOutputFormat.Tiff,
        ImageFormat.Ico => ImageOutputFormat.Ico,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
