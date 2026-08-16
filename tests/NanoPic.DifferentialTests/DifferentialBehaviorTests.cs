using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ImageMagick;
using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.DifferentialTests;

public sealed class DifferentialBehaviorTests
{
    [Fact]
    public async Task Recorded_legacy_jpeg_and_new_original_output_have_equivalent_observable_contracts()
    {
        var baseline = JsonSerializer.Deserialize<LegacyJpegBaseline>(
            await TestCompatibility.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets", "legacy-jpeg-quality-80.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(baseline);
        var source = Asset(baseline.SourceFile);
        Assert.Equal(baseline.SourceSha256, await Sha256Async(source), ignoreCase: true);

        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Differential-Jpeg-");
        try
        {
            var destination = Path.Combine(directory.FullName, "new-output.bin");
            var result = await Service().ProcessAsync(
                Request(source, destination, ImageOutputFormat.Original, quality: baseline.Quality),
                CancellationToken.None);

            Assert.True(result.IsSuccess, Describe(result));
            var output = Assert.IsType<ImageEncodedOutput>(result.Value?.Output);
            Assert.Equal(ImageFormat.Jpeg, output.Metadata.Format);
            Assert.Equal(baseline.Width, output.Metadata.Width);
            Assert.Equal(baseline.Height, output.Metadata.Height);
            Assert.Equal(baseline.FrameCount, output.Metadata.FrameCount);
            Assert.Equal(baseline.HasAlpha, output.Metadata.HasAlpha);
            Assert.Equal(baseline.Quality, output.Quality);
            Assert.EndsWith(".jpg", result.Value?.OutputPath, StringComparison.OrdinalIgnoreCase);
            var outputBytes = await TestCompatibility.ReadAllBytesAsync(result.Value!.OutputPath);
            Assert.Equal("FFD8", TestCompatibility.ToHexString(outputBytes, count: 2));
            Assert.InRange(output.Bytes, baseline.OutputBytes / 2, baseline.OutputBytes * 2);
            Assert.False(string.Equals(
                baseline.OutputSha256,
                await Sha256Async(result.Value.OutputPath),
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Unicode_directory_and_path_longer_than_260_characters_scan_and_process_successfully()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Differential-LongPath-");
        try
        {
            var sourceDirectory = directory.FullName;
            for (var index = 0; index < 11; index++)
            {
                sourceDirectory = Path.Combine(sourceDirectory, $"相册_{index:D2}_abcdefghijklmnop");
            }
            Directory.CreateDirectory(PortablePath.ForFileSystem(sourceDirectory));
            var source = Path.Combine(sourceDirectory, "照片_透明图层.png");
            File.Copy(Asset("transparent.png"), PortablePath.ForFileSystem(source));
            Assert.True(source.Length > 260, $"Expected a long path but got {source.Length}: {source}");

            var scan = await new SupportedImageFileScanner().ScanAsync(
                directory.FullName,
                new FileScanOptions(Recursive: true, MaxDepth: 32),
                CancellationToken.None);
            var scanned = Assert.Single(scan.Files);
            Assert.Equal(source, scanned.Path);

            var output = Path.Combine(sourceDirectory, "输出_结果.png");
            var result = await Service().ProcessAsync(
                Request(source, output, ImageOutputFormat.Png),
                CancellationToken.None);
            Assert.True(result.IsSuccess, Describe(result));
            Assert.True(File.Exists(PortablePath.ForFileSystem(output)));
        }
        finally
        {
            Directory.Delete(PortablePath.ForFileSystem(directory.FullName, forceExtendedPath: true), recursive: true);
        }
    }

    [Fact]
    public async Task Batch_of_100_real_images_completes_without_name_collisions_or_failures()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Differential-Batch-");
        try
        {
            var inputDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "输入"));
            var outputDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "输出"));
            var requests = new List<ImageFileProcessRequest>(100);
            for (var index = 0; index < 100; index++)
            {
                var source = Path.Combine(inputDirectory.FullName, $"图片_{index:D3}.png");
                File.Copy(Asset("transparent.png"), source);
                requests.Add(Request(
                    source,
                    Path.Combine(outputDirectory.FullName, $"结果_{index:D3}.png"),
                    ImageOutputFormat.Png));
            }

            var result = await new BoundedImageBatchProcessor(Service()).ProcessAsync(
                requests,
                maxDegreeOfParallelism: 4,
                progress: null,
                CancellationToken.None);

            Assert.Equal(100, result.Progress.Total);
            Assert.Equal(100, result.Progress.Completed);
            Assert.Equal(100, result.Progress.Succeeded);
            Assert.Equal(0, result.Progress.Failed);
            Assert.Equal(0, result.Progress.Canceled);
            Assert.Equal(100, Directory.EnumerateFiles(outputDirectory.FullName, "*.png").Count());
            Assert.Equal(100, result.Items.Select(item => item.Value?.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Locked_existing_output_returns_structured_failure_and_preserves_original_bytes()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Differential-Locked-");
        try
        {
            var destination = Path.Combine(directory.FullName, "locked.jpg");
            var originalBytes = new byte[] { 1, 2, 3, 4, 5 };
            await TestCompatibility.WriteAllBytesAsync(destination, originalBytes);
            using var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = await Service().ProcessAsync(
                Request(Asset("photo-metadata.jpg"), destination, ImageOutputFormat.Jpeg, OutputConflictPolicy.Overwrite),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(ImageFailureKind.FileAccessConflict, result.Failure?.Kind);
            locked.Position = 0;
            var current = new byte[originalBytes.Length];
            Assert.Equal(
                originalBytes.Length,
                await locked.ReadAsync(current, 0, current.Length, CancellationToken.None));
            Assert.Equal(originalBytes, current);
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, ".*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Directory_without_write_permission_returns_structured_failure_without_output()
    {
        if (!TestCompatibility.IsWindows())
        {
            return;
        }

        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Differential-Denied-");
        var outputDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "denied"));
        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows identity has no SID.");
        var denyRule = new FileSystemAccessRule(
            identity,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.AppendData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        var security = outputDirectory.GetAccessControl();
        security.AddAccessRule(denyRule);
        outputDirectory.SetAccessControl(security);
        try
        {
            var destination = Path.Combine(outputDirectory.FullName, "denied.png");
            var result = await Service().ProcessAsync(
                Request(Asset("transparent.png"), destination, ImageOutputFormat.Png),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(ImageFailureKind.FileAccessConflict, result.Failure?.Kind);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            var restored = outputDirectory.GetAccessControl();
            restored.RemoveAccessRuleSpecific(denyRule);
            outputDirectory.SetAccessControl(restored);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Transparency_and_metadata_policies_are_explicit_in_new_outputs()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Differential-Policies-");
        try
        {
            var jpegPath = Path.Combine(directory.FullName, "flattened.jpg");
            var flattened = await Service().ProcessAsync(
                new ImageFileProcessRequest(
                    Asset("transparent.png"),
                    jpegPath,
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                    new ImageTransformOptions(Background: new ImageBackgroundOptions(true, "#FFFFFF")),
                    ImageSafetyLimits.Default),
                CancellationToken.None);
            Assert.True(flattened.IsSuccess, Describe(flattened));
            Assert.False(flattened.Value?.Output?.Metadata.HasAlpha);

            var strippedPath = Path.Combine(directory.FullName, "stripped.jpg");
            var stripped = await Service().ProcessAsync(
                new ImageFileProcessRequest(
                    Asset("photo-metadata.jpg"),
                    strippedPath,
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg, Quality: 80),
                    new ImageTransformOptions(StripMetadata: true),
                    ImageSafetyLimits.Default),
                CancellationToken.None);
            Assert.True(stripped.IsSuccess, Describe(stripped));
            using var output = new MagickImage(strippedPath);
            Assert.Null(output.GetExifProfile());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ImageFileProcessingService Service() => new(new WicImageCodec());

    private static ImageFileProcessRequest Request(
        string source,
        string destination,
        ImageOutputFormat format,
        int quality = 80) =>
        Request(source, destination, format, OutputConflictPolicy.Fail, quality);

    private static ImageFileProcessRequest Request(
        string source,
        string destination,
        ImageOutputFormat format,
        OutputConflictPolicy conflictPolicy,
        int quality = 80) =>
        new(
            source,
            destination,
            new ImageEncodingOptions(format, quality),
            new ImageTransformOptions(),
            ImageSafetyLimits.Default,
            conflictPolicy);

    private static string Asset(string fileName) => Path.Combine(AppContext.BaseDirectory, "assets", "legacy-inputs", fileName);

    private static Task<string> Sha256Async(string path) =>
        Task.FromResult(TestCompatibility.ComputeSha256(path));

    private static string Describe(ImageOperationResult<ImageFileProcessResult> result) =>
        $"{result.Failure?.Kind}: {result.Failure?.UserMessage}{Environment.NewLine}{result.Failure?.Exception}";

    private sealed record LegacyJpegBaseline(
        string SourceFile,
        string SourceSha256,
        string OutputFormat,
        int Quality,
        int Width,
        int Height,
        int FrameCount,
        bool HasAlpha,
        long OutputBytes,
        string OutputSha256,
        string HeaderHex,
        string DifferencePolicy);
}
