using NanoPic.Codecs;
using NanoPic.Core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class WicImageCodecMetadataNoteTests
{
    [Theory]
    [InlineData(ImageFormat.Jpeg, "/app1/ifd/{ushort=40092}")]
    [InlineData(ImageFormat.Tiff, "/ifd/{ushort=40092}")]
    [InlineData(ImageFormat.Png, "/tEXt/{str=Comment}")]
    public async Task Metadata_note_round_trips_in_the_output(ImageFormat format, string queryPath)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-Note-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "white.png");
            var outputPath = Path.Combine(directory.FullName, "marked" + (format == ImageFormat.Jpeg ? ".jpg" : format == ImageFormat.Tiff ? ".tiff" : ".png"));
            using (var source = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 64, 48)
            {
                Format = ImageMagick.MagickFormat.Png
            })
            {
                source.Write(sourcePath);
            }

            // EXIF text tags are encoded through the system code page, so non-ASCII
            // notes only round-trip on matching locales; keep the transport test
            // locale-independent.
            var note = "source: https://example.com/photo/42";
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Png,
                format,
                new ImageTransformOptions(MetadataNote: new ImageMetadataNoteOptions(true, note)),
                new ImageEncodingOptions(ToOutputFormat(format)),
                ImageSafetyLimits.Default);

            var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);
            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");

            using var stream = File.OpenRead(outputPath);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var value = frame.Metadata is BitmapMetadata metadata ? metadata.GetQuery(queryPath) : null;
            Assert.True(string.Equals(value as string, note, StringComparison.Ordinal),
                $"Expected note at {queryPath}, got [{value}].");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_note_survives_strip_metadata()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-WIC-NoteStrip-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "photo.jpg");
            var outputPath = Path.Combine(directory.FullName, "out.jpg");
            using (var source = new ImageMagick.MagickImage(ImageMagick.MagickColors.SkyBlue, 64, 48)
            {
                Format = ImageMagick.MagickFormat.Jpeg
            })
            {
                source.Write(sourcePath);
            }

            var note = "internal-source-tag";
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Jpeg,
                new ImageTransformOptions(StripMetadata: true, MetadataNote: new ImageMetadataNoteOptions(true, note)),
                new ImageEncodingOptions(ImageOutputFormat.Jpeg),
                ImageSafetyLimits.Default);

            var encoded = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);
            Assert.True(encoded.IsSuccess, $"{encoded.Failure?.UserMessage}{Environment.NewLine}{encoded.Failure?.Exception}");

            using var stream = File.OpenRead(outputPath);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var metadata = Assert.IsType<BitmapMetadata>(frame.Metadata);
            Assert.Equal(note, metadata.GetQuery("/app1/ifd/{ushort=40092}"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ImageOutputFormat ToOutputFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ImageOutputFormat.Jpeg,
        ImageFormat.Tiff => ImageOutputFormat.Tiff,
        _ => ImageOutputFormat.Png
    };
}
