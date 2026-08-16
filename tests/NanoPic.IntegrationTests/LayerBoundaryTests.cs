using ImageMagick;
using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class LayerBoundaryTests
{
    [Fact]
    public void Selected_codec_and_layer_marker_are_available()
    {
        Assert.Equal(ImageFormat.Webp, ImageFormat.Webp);
        Assert.Equal("1.6.0", LibWebpCodecEngine.Version);
        Assert.Equal("NanoPic.Infrastructure", InfrastructureAssemblyMarker.LayerName);
    }

    [Fact]
    public async Task Process_atomically_replaces_source_file_in_place_when_overwrite_is_selected()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-InPlace-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    sourcePath,
                    new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 72),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default,
                    OutputConflictPolicy.Overwrite),
                CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            Assert.True(result.Value?.ReplacedExistingOutput);
            Assert.Equal(sourcePath, result.Value?.OutputPath);
            using var output = File.OpenRead(sourcePath);
            var signature = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
            Assert.Equal(ImageFormat.Png, signature.Value);
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, ".source.png.*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_uses_detected_input_format_and_selected_output_format_not_extensions()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "input-misleading.jpg");
            var requestedDestination = Path.Combine(directory.FullName, "result-misleading.bin");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    requestedDestination,
                    new ImageEncodingOptions(ImageOutputFormat.Webp, Quality: 80),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            Assert.NotNull(result.Value);
            Assert.EndsWith(".webp", result.Value.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.Value.OutputPath));
            Assert.Equal(ImageFormat.Png, result.Value.Source.Format);
            Assert.Equal(ImageFormat.Webp, result.Value.Output?.Metadata.Format);
            using var output = File.OpenRead(result.Value.OutputPath);
            var detected = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
            Assert.True(detected.IsSuccess, detected.Failure?.UserMessage);
            Assert.Equal(ImageFormat.Webp, detected.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_original_output_uses_detected_source_signature_not_source_extension()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "input-misleading.jpg");
            var destinationPath = Path.Combine(directory.FullName, "origin-result.dat");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Original, Quality: 80),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.EndsWith(".png", result.Value?.OutputPath, StringComparison.OrdinalIgnoreCase);
            using var output = File.OpenRead(result.Value!.OutputPath);
            var signature = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
            Assert.Equal(ImageFormat.Png, signature.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Unreachable_target_size_returns_structured_failure_and_leaves_no_output()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var requestedDestination = Path.Combine(directory.FullName, "target.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    requestedDestination,
                    new ImageEncodingOptions(
                        ImageOutputFormat.Jpeg,
                        Quality: 80,
                        TargetSize: new TargetSizeOptions(TargetBytes: 1, AllowExceed: false)),
                    new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFFFF")),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(ImageFailureKind.TargetSizeUnreachable, result.Failure?.Kind);
            Assert.False(File.Exists(requestedDestination));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, ".target.jpg.*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_atomically_replaces_existing_output_when_overwrite_is_selected()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var destinationPath = Path.Combine(directory.FullName, "existing.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);
            await TestCompatibility.WriteAllTextAsync(destinationPath, "old output must not survive");

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                    new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFFFF")),
                    ImageSafetyLimits.Default,
                    OutputConflictPolicy.Overwrite),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.True(result.Value?.ReplacedExistingOutput);
            using var output = File.OpenRead(destinationPath);
            var signature = await ImageFileSignatureInspector.DetectAsync(output, CancellationToken.None);
            Assert.Equal(ImageFormat.Jpeg, signature.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_auto_orients_exif_before_encoding()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "oriented.jpg");
            var destinationPath = Path.Combine(directory.FullName, "oriented-output.jpg");
            using (var source = new MagickImage(MagickColors.Red, 2, 3))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
                source.SetProfile(exif);
                source.Orientation = OrientationType.RightTop;
                source.Format = MagickFormat.Jpeg;
                source.Write(sourcePath);
            }

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                    new ImageTransformOptions(AutoOrient: true),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            Assert.Equal(3, result.Value?.Output?.Metadata.Width);
            Assert.Equal(2, result.Value?.Output?.Metadata.Height);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_strips_metadata_when_requested()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "metadata.jpg");
            var destinationPath = Path.Combine(directory.FullName, "metadata-output.jpg");
            using (var source = new MagickImage(MagickColors.Blue, 3, 2))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Artist, "NanoPic metadata test");
                source.SetProfile(exif);
                source.Format = MagickFormat.Jpeg;
                source.Write(sourcePath);
            }

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                    new ImageTransformOptions(StripMetadata: true),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            using var output = new MagickImage(destinationPath);
            Assert.Null(output.GetExifProfile()?.GetValue(ExifTag.Artist));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_can_return_explicit_exceeded_target_output_when_allowed()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var destinationPath = Path.Combine(directory.FullName, "allowed.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(
                        ImageOutputFormat.Jpeg,
                        Quality: 80,
                        TargetSize: new TargetSizeOptions(TargetBytes: 1, AllowExceed: true)),
                    new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFFFF")),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.False(result.Value?.Output?.TargetSizeReached);
            Assert.True(result.Value?.Output?.ExceededTarget);
            Assert.True(File.Exists(destinationPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_applies_resize_brightness_and_text_watermark()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var destinationPath = Path.Combine(directory.FullName, "transformed.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Png, Quality: 80),
                    new ImageTransformOptions(
                        Resize: new ImageResizeOptions(true, 3, 2, PreserveAspectRatio: false),
                        BrightnessPercent: 80,
                        Watermark: new ImageWatermarkOptions(true, "NanoPic", "#FF0000", 80, FontSize: 10, Margin: 0)),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, $"{result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}");
            Assert.Equal(3, result.Value?.Output?.Metadata.Width);
            Assert.Equal(2, result.Value?.Output?.Metadata.Height);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_flattens_transparent_input_when_encoding_jpeg()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Integration-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var destinationPath = Path.Combine(directory.FullName, "result.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                    new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFFFF")),
                    ImageSafetyLimits.Default),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.False(result.Value?.Output?.Metadata.HasAlpha);
            Assert.True(File.Exists(destinationPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
