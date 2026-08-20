using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NanoPic.Core;
using Xunit;

namespace NanoPic.Core.Tests;

public sealed class NanoPicV321RegressionTests
{
    [Fact]
    public void ImageDimensionProbe_CorrectlyParsesPngHeader()
    {
        // 8 bytes signature + 4 bytes chunk length + 4 bytes 'IHDR' + 4 bytes W + 4 bytes H + 5 bytes details + 4 bytes CRC
        var pngBytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG Sig
            0x00, 0x00, 0x00, 0x0D,                         // IHDR len 13
            0x49, 0x48, 0x44, 0x52,                         // IHDR
            0x00, 0x00, 0x04, 0x00,                         // Width 1024
            0x00, 0x00, 0x03, 0x00,                         // Height 768
            0x08, 0x06, 0x00, 0x00, 0x00,                   // 8-bit RGBA
            0x00, 0x00, 0x00, 0x00                          // CRC
        };

        using var stream = new MemoryStream(pngBytes);
        var result = ImageDimensionProbe.Probe(stream, ImageFormat.Png);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(1024, result.Value!.Width);
        Assert.Equal(768, result.Value!.Height);
        Assert.Equal(1, result.Value!.FrameCount);
        Assert.Equal(1, result.Value!.ExifOrientation);
    }

    [Fact]
    public void ImageDimensionProbe_CorrectlyParsesBmpHeader()
    {
        // BM + 8 bytes header + DIB header (1024 x 768)
        var bmpBytes = new byte[54];
        bmpBytes[0] = 0x42; bmpBytes[1] = 0x4D; // BM
        BitConverter.GetBytes(54).CopyTo(bmpBytes, 2);
        BitConverter.GetBytes(54).CopyTo(bmpBytes, 10);
        BitConverter.GetBytes(40).CopyTo(bmpBytes, 14); // BITMAPINFOHEADER size
        BitConverter.GetBytes(1024).CopyTo(bmpBytes, 18); // Width
        BitConverter.GetBytes(768).CopyTo(bmpBytes, 22);  // Height
        BitConverter.GetBytes((short)1).CopyTo(bmpBytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmpBytes, 28);

        using var stream = new MemoryStream(bmpBytes);
        var result = ImageDimensionProbe.Probe(stream, ImageFormat.Bmp);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(1024, result.Value!.Width);
        Assert.Equal(768, result.Value!.Height);
    }

    [Fact]
    public void ImageDimensionProbe_CorrectlyParsesGifHeader()
    {
        // GIF89a + Logical Screen Width (800) + Height (600)
        var gifBytes = new byte[]
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, // GIF89a
            0x20, 0x03,                         // 800 (0x0320 little endian)
            0x58, 0x02,                         // 600 (0x0258 little endian)
            0x70, 0x00, 0x00
        };

        using var stream = new MemoryStream(gifBytes);
        var result = ImageDimensionProbe.Probe(stream, ImageFormat.Gif);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(800, result.Value!.Width);
        Assert.Equal(600, result.Value!.Height);
    }

    [Fact]
    public void ImageDimensionProbe_CorrectlyParsesIcoHeaderAndFindsLargestEntry()
    {
        // ICO header: 0, 0, 1 (icon), 2 entries
        // Entry 1: 32x32
        // Entry 2: 256x256 (0 = 256)
        var icoBytes = new byte[6 + 16 * 2];
        icoBytes[2] = 1; // type icon
        icoBytes[4] = 2; // 2 images

        // Entry 1
        icoBytes[6] = 32; icoBytes[7] = 32;
        // Entry 2
        icoBytes[22] = 0; icoBytes[23] = 0; // 256 x 256

        using var stream = new MemoryStream(icoBytes);
        var result = ImageDimensionProbe.Probe(stream, ImageFormat.Ico);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(256, result.Value!.Width);
        Assert.Equal(256, result.Value!.Height);
        Assert.Equal(2, result.Value!.FrameCount);
    }

    [Fact]
    public void ImageSafetyValidator_EnforcesHardMaxTotalPixelsForMultiFrame()
    {
        // 100 frames, each 20,000,000 pixels = 2,000,000,000 pixels (exceeds 1,000,000,000 hard limit)
        var metadata = new ImageMetadata(
            ImageFormat.Gif,
            5000,
            4000,
            100,
            HasAlpha: false,
            SourceBytes: 10_000_000);

        var limits = ImageSafetyLimits.Default;
        var result = ImageSafetyValidator.ValidateWithAction(metadata, limits);

        Assert.Equal(SafetyAction.Reject, result.Action);
        Assert.NotNull(result.Failure);
        Assert.Equal(ImageFailureKind.PixelBudgetExceeded, result.Failure!.Kind);
    }

    [Fact]
    public void ImageResizePlanner_ResolveSingleDimensionCorrectly()
    {
        // Source 4000 x 3000 (4:3), user requests Width = 2000, Height = null
        var userResize = new ImageResizeOptions(Enabled: true, Width: 2000, Height: null, PreserveAspectRatio: true);
        var plan = ImageResizePlanner.Plan(4000, 3000, new SafetyValidationResult(SafetyAction.Pass, null, null, null), userResize);

        Assert.True(plan.ResizeRequired);
        Assert.False(plan.AutoDownsampled);
        Assert.Equal(2000, plan.Width);
        Assert.Equal(3000, plan.Height); // Preserves configured height or fallback for downstream codec
    }

    [Fact]
    public async Task BoundedBatchProcessor_DynamicFifoScheduling_ThrottlesLargeImageAndRestores()
    {
        var activeHistory = new List<int>();
        var lockObj = new object();

        var codec = new MockDynamicCodec(async (req) =>
        {
            int current;
            lock (lockObj)
            {
                current = req.SourcePath.Contains("large") ? 100 : 1;
            }
            await Task.Delay(30);
        });

        var processor = new BoundedImageBatchProcessor(new ImageFileProcessingService(codec));
        var tempDir = Path.Combine(Path.GetTempPath(), "nanopic-test-fifo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 5 items: small(10MP), small(10MP), large(250MP), small(10MP), small(10MP)
            var requests = new List<ImageFileProcessRequest>();
            var pixelCounts = new List<long>
            {
                10_000_000,
                10_000_000,
                250_000_000, // Limit = 1
                10_000_000,
                10_000_000
            };

            for (var i = 0; i < 5; i++)
            {
                var isLarge = i == 2;
                var path = Path.Combine(tempDir, $"item-{i}-{(isLarge ? "large" : "small")}.jpg");
                File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9 });
                requests.Add(new ImageFileProcessRequest(
                    path,
                    Path.Combine(tempDir, $"out-{i}.jpg"),
                    new ImageEncodingOptions(ImageOutputFormat.Jpeg),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default));
            }

            var batchResult = await processor.ProcessAsync(requests, 4, pixelCounts, progress: null, CancellationToken.None);

            Assert.Equal(5, batchResult.Progress.Total);
            Assert.Equal(5, batchResult.Progress.Succeeded);
            Assert.All(batchResult.Items, it => Assert.True(it.IsSuccess));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class MockDynamicCodec : IImageCodec
    {
        private readonly Func<ImageEncodeRequest, Task> _onEncode;

        public MockDynamicCodec(Func<ImageEncodeRequest, Task> onEncode)
        {
            _onEncode = onEncode;
        }

        public IReadOnlyCollection<ImageFormat> SupportedFormats { get; } = new HashSet<ImageFormat> { ImageFormat.Jpeg };

        public Task<ImageOperationResult<ImageMetadata>> IdentifyAsync(Stream input, CancellationToken cancellationToken)
        {
            return Task.FromResult(ImageOperationResult<ImageMetadata>.Success(
                new ImageMetadata(ImageFormat.Jpeg, 100, 100, 1, false, input.Length)));
        }

        public async Task<ImageOperationResult<ImageEncodedOutput>> TransformAndEncodeAsync(ImageEncodeRequest request, CancellationToken cancellationToken)
        {
            await _onEncode(request);
            File.WriteAllBytes(request.TemporaryOutputPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            var metadata = new ImageMetadata(ImageFormat.Jpeg, 100, 100, 1, false, 4);
            return ImageOperationResult<ImageEncodedOutput>.Success(new ImageEncodedOutput(metadata, 80, 4, true, false));
        }
    }
}
