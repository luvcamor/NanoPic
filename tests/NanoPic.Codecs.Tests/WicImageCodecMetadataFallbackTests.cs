using ImageMagick;
using NanoPic.Codecs;
using NanoPic.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class WicImageCodecMetadataFallbackTests
{
    [Fact]
    public async Task Jpeg_metadata_COM_failure_falls_back_to_safe_metadata()
    {
        var attempts = new List<ImageMetadataFallbackLevel>();
        var codec = new WicImageCodec(level =>
        {
            attempts.Add(level);
            if (level < ImageMetadataFallbackLevel.SafeMetadata)
            {
                throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
            }
        });

        var result = await EncodeAssetAsync(codec, "photo-metadata.jpg", ImageFormat.Jpeg);

        Assert.True(result.IsSuccess, Describe(result.Failure));
        Assert.Equal(ImageMetadataFallbackLevel.SafeMetadata, result.Value?.MetadataFallbackLevel);
        Assert.Contains(ImageMetadataFallbackLevel.Full, attempts);
        Assert.Contains(ImageMetadataFallbackLevel.SafeMetadata, attempts);
        Assert.Contains("元数据", result.Value?.MetadataFallbackNotice);
    }

    [Fact]
    public async Task Wrapped_COM_failure_is_unwrapped_for_fallback()
    {
        var codec = new WicImageCodec(level =>
        {
            if (level == ImageMetadataFallbackLevel.Full)
            {
                throw new TargetInvocationException(
                    new COMException("synthetic metadata failure", unchecked((int)0x88982F8E)));
            }
        });

        var result = await EncodeAssetAsync(codec, "photo-metadata.jpg", ImageFormat.Jpeg);

        Assert.True(result.IsSuccess, Describe(result.Failure));
        Assert.True(result.Value?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full);
    }

    [Fact]
    public async Task Exhausted_metadata_fallback_returns_encode_failure_with_safe_diagnostics()
    {
        var codec = new WicImageCodec(level =>
            throw new COMException($"synthetic {level}", unchecked((int)0x88982F8E)));

        var result = await EncodeAssetAsync(codec, "photo-metadata.jpg", ImageFormat.Jpeg);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageFailureKind.EncodeFailed, result.Failure?.Kind);
        var exception = Assert.IsType<AggregateException>(result.Failure?.Exception);
        Assert.Contains("Full:COMException:0x88982F8E", exception.Data["NanoPic.SafeDiagnostic"] as string);
    }

    [Fact]
    public async Task Non_jpeg_input_does_not_enter_metadata_fallback()
    {
        var attempts = new List<ImageMetadataFallbackLevel>();
        var codec = new WicImageCodec(level =>
        {
            attempts.Add(level);
            throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
        });

        var result = await EncodeAssetAsync(codec, "transparent.png", ImageFormat.Png);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageFailureKind.EncodeFailed, result.Failure?.Kind);
        Assert.Equal(new[] { ImageMetadataFallbackLevel.Full }, attempts);
    }

    [Fact]
    public async Task L3_bakes_orientation_even_when_user_auto_orient_is_disabled()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-L3-Orientation-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "oriented.jpg");
            var outputPath = Path.Combine(directory.FullName, "output.jpg");
            using (var image = new MagickImage(MagickColors.Red, 40, 20))
            {
                image.Format = MagickFormat.Jpeg;
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
                image.SetProfile(exif);
                image.Orientation = OrientationType.RightTop;
                image.Write(sourcePath);
            }

            var codec = new WicImageCodec(level =>
            {
                if (level != ImageMetadataFallbackLevel.WithoutSourceMetadata)
                {
                    throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
                }
            });
            var request = Request(sourcePath, outputPath, ImageFormat.Jpeg, autoOrient: false);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, Describe(result.Failure));
            Assert.Equal(ImageMetadataFallbackLevel.WithoutSourceMetadata, result.Value?.MetadataFallbackLevel);
            Assert.Equal(20, result.Value?.Metadata.Width);
            Assert.Equal(40, result.Value?.Metadata.Height);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_fallback_never_restores_larger_source_bytes()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-NoReuse-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "tiny.jpg");
            var outputPath = Path.Combine(directory.FullName, "output.jpg");
            using (var image = new MagickImage(MagickColors.Red, 8, 8))
            {
                image.Format = MagickFormat.Jpeg;
                image.Quality = 1;
                image.Write(sourcePath);
            }
            var sourceBytes = File.ReadAllBytes(sourcePath);
            var codec = new WicImageCodec(level =>
            {
                if (level == ImageMetadataFallbackLevel.Full)
                {
                    throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
                }
            });
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Jpeg,
                new ImageTransformOptions(AutoOrient: false),
                new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 100),
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, Describe(result.Failure));
            Assert.True(result.Value?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full);
            Assert.True(result.Value?.Bytes >= sourceBytes.Length);
            Assert.Contains("输出未缩小", result.Value?.MetadataFallbackNotice);
            Assert.False(sourceBytes.SequenceEqual(File.ReadAllBytes(outputPath)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Jpeg_input_uses_metadata_fallback_when_output_is_png()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-CrossFormat-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.jpg");
            var outputPath = Path.Combine(directory.FullName, "output.png");
            File.Copy(Asset("photo-metadata.jpg"), sourcePath);
            var codec = new WicImageCodec(level =>
            {
                if (level == ImageMetadataFallbackLevel.Full)
                {
                    throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
                }
            });
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Png,
                new ImageTransformOptions(),
                new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 75),
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, Describe(result.Failure));
            Assert.True(result.Value?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full);
            Assert.Equal(ImageFormat.Png, result.Value?.Metadata.Format);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Strip_metadata_keeps_existing_single_attempt_semantics()
    {
        var attempts = new List<ImageMetadataFallbackLevel>();
        var codec = new WicImageCodec(level =>
        {
            attempts.Add(level);
            throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
        });
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-Strip-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.jpg");
            var outputPath = Path.Combine(directory.FullName, "output.jpg");
            File.Copy(Asset("photo-metadata.jpg"), sourcePath);
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Jpeg,
                new ImageTransformOptions(StripMetadata: true, AutoOrient: false),
                new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 75),
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(ImageFailureKind.EncodeFailed, result.Failure?.Kind);
            Assert.Equal(new[] { ImageMetadataFallbackLevel.Full }, attempts);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_fallback_restarts_target_size_processing()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-Target-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.jpg");
            var outputPath = Path.Combine(directory.FullName, "output.jpg");
            File.Copy(Asset("photo-metadata.jpg"), sourcePath);
            var codec = new WicImageCodec(level =>
            {
                if (level == ImageMetadataFallbackLevel.Full)
                {
                    throw new COMException("synthetic metadata failure", unchecked((int)0x88982F8E));
                }
            });
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Jpeg,
                new ImageTransformOptions(),
                new ImageEncodingOptions(
                    ImageOutputFormat.Jpeg,
                    Quality: 75,
                    TargetSize: new TargetSizeOptions(
                        TargetBytes: 1,
                        AllowExceed: true,
                        AllowResizeForTarget: false)),
                ImageSafetyLimits.Default);

            var result = await codec.TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess, Describe(result.Failure));
            Assert.True(result.Value?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full);
            Assert.True(result.Value?.ExceededTarget);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_Jpeg_resize_is_not_misclassified_as_metadata_failure()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-InvalidResize-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.jpg");
            var outputPath = Path.Combine(directory.FullName, "output.jpg");
            File.Copy(Asset("photo-metadata.jpg"), sourcePath);
            var request = new ImageEncodeRequest(
                sourcePath,
                outputPath,
                ImageFormat.Jpeg,
                ImageFormat.Jpeg,
                new ImageTransformOptions(Resize: new ImageResizeOptions(true, 0, 10, true)),
                new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 75),
                ImageSafetyLimits.Default);

            var result = await new WicImageCodec().TransformAndEncodeAsync(request, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(ImageFailureKind.InvalidConfiguration, result.Failure?.Kind);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<ImageOperationResult<ImageEncodedOutput>> EncodeAssetAsync(
        WicImageCodec codec,
        string fileName,
        ImageFormat format)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Metadata-Fallback-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, fileName);
            var outputPath = Path.Combine(directory.FullName, "output" + Path.GetExtension(fileName));
            File.Copy(Asset(fileName), sourcePath);
            var result = await codec.TransformAndEncodeAsync(
                Request(sourcePath, outputPath, format, autoOrient: false),
                CancellationToken.None);
            if (result.IsSuccess)
            {
                Assert.True(File.Exists(outputPath));
            }
            return result;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ImageEncodeRequest Request(
        string sourcePath,
        string outputPath,
        ImageFormat format,
        bool autoOrient) =>
        new(
            sourcePath,
            outputPath,
            format,
            format,
            new ImageTransformOptions(AutoOrient: autoOrient),
            new ImageEncodingOptions(ToOutputFormat(format), Quality: 75),
            ImageSafetyLimits.Default);

    private static ImageOutputFormat ToOutputFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ImageOutputFormat.Jpeg,
        ImageFormat.Png => ImageOutputFormat.Png,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static string Asset(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "assets", fileName);

    private static string Describe(ImageOperationFailure? failure) =>
        $"{failure?.Kind}: {failure?.UserMessage}{Environment.NewLine}{failure?.Exception}";
}
