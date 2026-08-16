using ImageMagick;
using ImageMagick.Drawing;
using NanoPic.Core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace NanoPic.Codecs.Tests;

public abstract class ImageCodecContractTests
{
    protected abstract IImageCodec CreateCodec();

    [Fact]
    public void Supported_formats_expose_the_complete_NanoPic_contract()
    {
        var expected = new[]
        {
            ImageFormat.Jpeg,
            ImageFormat.Png,
            ImageFormat.Webp,
            ImageFormat.Gif,
            ImageFormat.Bmp,
            ImageFormat.Tiff,
            ImageFormat.Ico
        };

        Assert.Equal(expected.OrderBy(value => value), CreateCodec().SupportedFormats.OrderBy(value => value));
    }

    [Theory]
    [InlineData("photo-metadata.jpg", ImageFormat.Jpeg, 1, false)]
    [InlineData("transparent.png", ImageFormat.Png, 1, true)]
    [InlineData("sample.webp", ImageFormat.Webp, 1, false)]
    [InlineData("animated.gif", ImageFormat.Gif, 2, true)]
    [InlineData("palette.bmp", ImageFormat.Bmp, 1, false)]
    [InlineData("photo.tiff", ImageFormat.Tiff, 1, false)]
    [InlineData("alpha.ico", ImageFormat.Ico, 1, true)]
    public async Task Identify_reads_real_supported_inputs(
        string fileName,
        ImageFormat expectedFormat,
        int minimumFrames,
        bool expectedAlpha)
    {
        using var input = File.OpenRead(Asset(fileName));

        var result = await CreateCodec().IdentifyAsync(input, CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Failure));
        Assert.Equal(expectedFormat, result.Value?.Format);
        Assert.True(result.Value?.Width > 0);
        Assert.True(result.Value?.Height > 0);
        Assert.True(result.Value?.FrameCount >= minimumFrames);
        Assert.Equal(expectedAlpha, result.Value?.HasAlpha);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Webp)]
    [InlineData(ImageFormat.Gif)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Ico)]
    public async Task Encode_writes_the_selected_signature(ImageFormat outputFormat)
    {
        await WithOutputAsync(
            "transparent.png",
            ImageFormat.Png,
            outputFormat,
            new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFFFF")),
            new ImageEncodingOptions(ToOutputFormat(outputFormat), Quality: 80),
            async (codec, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.True(File.Exists(outputPath));
                using var output = File.OpenRead(outputPath);
                var detected = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
                Assert.True(detected.IsSuccess, detected.Failure?.UserMessage);
                Assert.Equal(outputFormat, detected.Value);
                Assert.True(encoded.Value?.Metadata.Width > 0);
                Assert.True(encoded.Value?.Metadata.Height > 0);
            });
    }

    [Fact]
    public async Task Identify_rejects_a_truncated_webp_as_a_structured_failure()
    {
        using var input = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("RIFF\u0010\0\0\0WEBP"));

        var result = await CreateCodec().IdentifyAsync(input, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.Failure?.Kind is ImageFailureKind.DecodeFailed or ImageFailureKind.UnsupportedFormat);
    }

    [Fact]
    public async Task Animated_gif_output_preserves_multiple_frames()
    {
        await WithOutputAsync(
            "animated.gif",
            ImageFormat.Gif,
            ImageFormat.Gif,
            new ImageTransformOptions(),
            new ImageEncodingOptions(ImageOutputFormat.Gif, Quality: 80),
            async (codec, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                using var output = File.OpenRead(outputPath);
                var identified = await codec.IdentifyAsync(output, CancellationToken.None);
                Assert.True(identified.IsSuccess, Describe(identified.Failure));
                Assert.True(identified.Value?.FrameCount > 1);
            });
    }

    [Fact]
    public async Task Transparent_png_output_preserves_alpha()
    {
        await WithOutputAsync(
            "transparent.png",
            ImageFormat.Png,
            ImageFormat.Png,
            new ImageTransformOptions(),
            new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
            async (codec, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                using var output = File.OpenRead(outputPath);
                var identified = await codec.IdentifyAsync(output, CancellationToken.None);
                Assert.True(identified.IsSuccess, Describe(identified.Failure));
                Assert.True(identified.Value?.HasAlpha);
            });
    }

    [Fact]
    public async Task Strip_metadata_removes_the_jpeg_exif_segment()
    {
        await WithOutputAsync(
            "photo-metadata.jpg",
            ImageFormat.Jpeg,
            ImageFormat.Jpeg,
            new ImageTransformOptions(StripMetadata: true),
            new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
            (_, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                var bytes = File.ReadAllBytes(outputPath);
                Assert.False(Contains(bytes, System.Text.Encoding.ASCII.GetBytes("Exif\0\0")));
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Target_size_success_never_exceeds_the_requested_bytes()
    {
        const long targetBytes = 10_000;
        await WithOutputAsync(
            "photo-metadata.jpg",
            ImageFormat.Jpeg,
            ImageFormat.Jpeg,
            new ImageTransformOptions(StripMetadata: true),
            new ImageEncodingOptions(
                ImageOutputFormat.Jpeg,
                Quality: 80,
                TargetSize: new TargetSizeOptions(targetBytes, AllowExceed: false)),
            (_, _, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.True(encoded.Value?.TargetSizeReached);
                Assert.True(encoded.Value?.Bytes <= targetBytes);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Auto_orient_applies_exif_orientation_before_encoding()
    {
        await WithGeneratedOutputAsync(
            "oriented.jpg",
            sourcePath =>
            {
                using var source = new MagickImage(MagickColors.Black, 20, 30);
                new Drawables()
                    .FillColor(MagickColors.Red)
                    .Rectangle(0, 0, 9, 14)
                    .FillColor(MagickColors.Lime)
                    .Rectangle(10, 0, 19, 14)
                    .FillColor(MagickColors.Blue)
                    .Rectangle(0, 15, 9, 29)
                    .FillColor(MagickColors.Yellow)
                    .Rectangle(10, 15, 19, 29)
                    .Draw(source);
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
                source.SetProfile(exif);
                source.Orientation = OrientationType.RightTop;
                source.Format = MagickFormat.Jpeg;
                source.Write(sourcePath);
            },
            ImageFormat.Jpeg,
            ImageFormat.Jpeg,
            new ImageTransformOptions(AutoOrient: true),
            new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 90),
            (_, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.Equal(30, encoded.Value?.Metadata.Width);
                Assert.Equal(20, encoded.Value?.Metadata.Height);
                AssertColor(ReadBgraPixel(outputPath, 5, 5), red: 0, green: 0, blue: 255);
                AssertColor(ReadBgraPixel(outputPath, 24, 5), red: 255, green: 0, blue: 0);
                AssertColor(ReadBgraPixel(outputPath, 5, 14), red: 255, green: 255, blue: 0);
                AssertColor(ReadBgraPixel(outputPath, 24, 14), red: 0, green: 255, blue: 0);
                using var output = new MagickImage(outputPath);
                var orientation = output.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
                Assert.True(
                    orientation is null || orientation == (ushort)OrientationType.TopLeft,
                    $"Expected normalized EXIF orientation, actual {orientation}.");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Animated_gif_transforms_every_frame_and_keeps_animation()
    {
        await WithOutputAsync(
            "animated.gif",
            ImageFormat.Gif,
            ImageFormat.Gif,
            new ImageTransformOptions(Resize: new ImageResizeOptions(true, 8, 6, PreserveAspectRatio: false)),
            new ImageEncodingOptions(ImageOutputFormat.Gif, Quality: 80),
            async (codec, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                using var output = File.OpenRead(outputPath);
                var identified = await codec.IdentifyAsync(output, CancellationToken.None);
                Assert.True(identified.IsSuccess, Describe(identified.Failure));
                Assert.Equal(8, identified.Value?.Width);
                Assert.Equal(6, identified.Value?.Height);
                Assert.True(identified.Value?.FrameCount > 1);
            });
    }

    [Fact]
    public async Task Resize_can_ignore_aspect_ratio()
    {
        await WithOutputAsync(
            "transparent.png",
            ImageFormat.Png,
            ImageFormat.Png,
            new ImageTransformOptions(Resize: new ImageResizeOptions(true, 7, 5, PreserveAspectRatio: false)),
            new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
            (_, _, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.Equal(7, encoded.Value?.Metadata.Width);
                Assert.Equal(5, encoded.Value?.Metadata.Height);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Resize_fits_inside_both_bounds_when_preserving_aspect_ratio()
    {
        await WithOutputAsync(
            "transparent.png",
            ImageFormat.Png,
            ImageFormat.Png,
            new ImageTransformOptions(Resize: new ImageResizeOptions(true, 7, 5, PreserveAspectRatio: true)),
            new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
            (_, _, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.Equal(5, encoded.Value?.Metadata.Width);
                Assert.Equal(5, encoded.Value?.Metadata.Height);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Invalid_resize_is_returned_as_a_structured_configuration_failure()
    {
        await WithOutputAsync(
            "transparent.png",
            ImageFormat.Png,
            ImageFormat.Png,
            new ImageTransformOptions(Resize: new ImageResizeOptions(true, 0, 5, PreserveAspectRatio: false)),
            new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
            (_, _, encoded) =>
            {
                Assert.False(encoded.IsSuccess);
                Assert.Equal(ImageFailureKind.InvalidConfiguration, encoded.Failure?.Kind);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Brightness_multiplies_color_channels()
    {
        await WithGeneratedOutputAsync(
            "gray.png",
            sourcePath =>
            {
                using var source = new MagickImage(new MagickColor("#808080"), 4, 4)
                {
                    Format = MagickFormat.Png
                };
                source.Write(sourcePath);
            },
            ImageFormat.Png,
            ImageFormat.Png,
            new ImageTransformOptions(BrightnessPercent: 50),
            new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
            (_, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                var pixel = ReadBgraPixel(outputPath, 1, 1);
                Assert.InRange(pixel[0], 55, 70);
                Assert.InRange(pixel[1], 55, 70);
                Assert.InRange(pixel[2], 55, 70);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Text_watermark_changes_pixels_near_the_bottom_right()
    {
        await WithGeneratedOutputAsync(
            "white.png",
            sourcePath =>
            {
                using var source = new MagickImage(MagickColors.White, 240, 100)
                {
                    Format = MagickFormat.Png
                };
                source.Write(sourcePath);
            },
            ImageFormat.Png,
            ImageFormat.Png,
            new ImageTransformOptions(
                Watermark: new ImageWatermarkOptions(true, "NanoPic", "#000000", 100, FontSize: 24, Margin: 4)),
            new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
            (_, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.True(CountDarkPixels(outputPath, minimumY: 50) > 10);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Background_flattening_removes_alpha_for_opaque_outputs()
    {
        await WithGeneratedOutputAsync(
            "transparent-source.png",
            sourcePath =>
            {
                using var source = new MagickImage(MagickColors.Transparent, 8, 8)
                {
                    Format = MagickFormat.Png
                };
                source.Write(sourcePath);
            },
            ImageFormat.Png,
            ImageFormat.Jpeg,
            new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FF0000")),
            new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 100),
            (_, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.False(encoded.Value?.Metadata.HasAlpha);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Metadata_is_preserved_through_pixel_transforms_by_default()
    {
        const string artist = "NanoPic codec contract";
        await WithGeneratedOutputAsync(
            "metadata.jpg",
            sourcePath =>
            {
                using var source = new MagickImage(MagickColors.Blue, 8, 6);
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Artist, artist);
                source.SetProfile(exif);
                source.Format = MagickFormat.Jpeg;
                source.Write(sourcePath);
            },
            ImageFormat.Jpeg,
            ImageFormat.Jpeg,
            new ImageTransformOptions(
                AutoOrient: false,
                Resize: new ImageResizeOptions(true, 4, 3, PreserveAspectRatio: false)),
            new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 90),
            (_, outputPath, encoded) =>
            {
                Assert.True(encoded.IsSuccess, Describe(encoded.Failure));
                Assert.Equal(4, encoded.Value?.Metadata.Width);
                Assert.Equal(3, encoded.Value?.Metadata.Height);
                using var output = new MagickImage(outputPath);
                Assert.Equal(artist, output.GetExifProfile()?.GetValue(ExifTag.Artist)?.Value);
                return Task.CompletedTask;
            });
    }

    private async Task WithOutputAsync(
        string sourceFile,
        ImageFormat sourceFormat,
        ImageFormat outputFormat,
        ImageTransformOptions transform,
        ImageEncodingOptions encoding,
        Func<IImageCodec, string, ImageOperationResult<ImageEncodedOutput>, Task> verify)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-CodecContract-");
        try
        {
            var outputPath = Path.Combine(directory.FullName, "encoded.tmp");
            var codec = CreateCodec();
            var request = new ImageEncodeRequest(
                Asset(sourceFile),
                outputPath,
                sourceFormat,
                outputFormat,
                transform,
                encoding,
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);
            await verify(codec, outputPath, result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task WithGeneratedOutputAsync(
        string sourceFileName,
        Action<string> createSource,
        ImageFormat sourceFormat,
        ImageFormat outputFormat,
        ImageTransformOptions transform,
        ImageEncodingOptions encoding,
        Func<IImageCodec, string, ImageOperationResult<ImageEncodedOutput>, Task> verify)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-CodecTransform-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, sourceFileName);
            var outputPath = Path.Combine(directory.FullName, "encoded.tmp");
            createSource(sourcePath);
            var codec = CreateCodec();
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                sourceFormat,
                outputFormat,
                transform,
                encoding,
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);
            await verify(codec, outputPath, result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Asset(string fileName) => Path.Combine(AppContext.BaseDirectory, "assets", fileName);

    private static string Describe(ImageOperationFailure? failure) =>
        $"{failure?.Kind}: {failure?.UserMessage}{Environment.NewLine}{failure?.Exception}";

    private static bool Contains(byte[] source, byte[] value)
    {
        for (var offset = 0; offset <= source.Length - value.Length; offset++)
        {
            var matches = true;
            for (var index = 0; index < value.Length; index++)
            {
                if (source[offset + index] != value[index])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] ReadBgraPixel(string path, int x, int y)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static int CountDarkPixels(string path, int minimumY)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var count = 0;
        for (var y = Math.Max(0, minimumY); y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var offset = checked((y * stride) + (x * 4));
                if (pixels[offset] < 240 || pixels[offset + 1] < 240 || pixels[offset + 2] < 240)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void AssertColor(byte[] bgra, byte red, byte green, byte blue)
    {
        const int tolerance = 35;
        Assert.InRange(bgra[2], Math.Max(0, red - tolerance), Math.Min(byte.MaxValue, red + tolerance));
        Assert.InRange(bgra[1], Math.Max(0, green - tolerance), Math.Min(byte.MaxValue, green + tolerance));
        Assert.InRange(bgra[0], Math.Max(0, blue - tolerance), Math.Min(byte.MaxValue, blue + tolerance));
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
