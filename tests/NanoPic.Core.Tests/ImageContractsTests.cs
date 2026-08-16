using NanoPic.Core;
using Xunit;

namespace NanoPic.Core.Tests;

public sealed class ImageContractsTests
{
    [Fact]
    public void Successful_result_has_no_failure()
    {
        var result = ImageOperationResult<ImageFormat>.Success(ImageFormat.Jpeg);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Failure);
        Assert.Equal(ImageFormat.Jpeg, result.Value);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ImageFormat.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ImageFormat.Png)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ImageFormat.Gif)]
    [InlineData(new byte[] { 0x42, 0x4D }, ImageFormat.Bmp)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00 }, ImageFormat.Ico)]
    public void Detect_identifies_supported_formats_from_bytes_not_extension(byte[] bytes, ImageFormat expected)
    {
        var result = ImageFileSignatureInspector.Detect(bytes);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Detect_identifies_webp_from_riff_and_webp_markers()
    {
        var result = ImageFileSignatureInspector.Detect(
            System.Text.Encoding.ASCII.GetBytes("RIFF\u0010\0\0\0WEBPVP8 "));

        Assert.True(result.IsSuccess);
        Assert.Equal(ImageFormat.Webp, result.Value);
    }

    [Fact]
    public void Pixel_budget_returns_structured_failure()
    {
        var metadata = new ImageMetadata(ImageFormat.Png, 10_000, 10_000, 1, true, 10);
        var limits = new ImageSafetyLimits(1_024, 20_000, 20_000, 50_000_000, 4);

        var result = ImageSafetyValidator.ValidateResult(metadata, limits);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageFailureKind.PixelBudgetExceeded, result.Failure?.Kind);
    }

    [Fact]
    public void Capability_provider_keeps_cpu_fallback_and_explicit_legacy_unimplemented_status()
    {
        var provider = new DefaultImageProcessingCapabilityProvider();

        var cpu = provider.Get(ImageProcessingCapability.CpuBackend);
        var gpu = provider.Get(ImageProcessingCapability.GpuAcceleration);
        var blindWatermark = provider.Get(ImageProcessingCapability.BlindWatermark);

        Assert.True(cpu.IsAvailable);
        Assert.False(gpu.IsAvailable);
        Assert.Equal(ImageFailureKind.AccelerationUnavailable, gpu.UnavailableKind);
        Assert.False(blindWatermark.IsAvailable);
        Assert.Equal(ImageFailureKind.LegacyUnimplemented, blindWatermark.UnavailableKind);
    }

    [Fact]
    public async Task Target_size_unreachable_is_structured_failure_when_exceed_is_not_allowed()
    {
        var options = new TargetSizeOptions(TargetBytes: 5_120, AllowExceed: false, MinQuality: 1, MaxQuality: 100);

        var result = await TargetSizeSearch.FindAsync(
            options,
            (quality, _) => Task.FromResult(ImageOperationResult<long>.Success(10_000L + quality)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageFailureKind.TargetSizeUnreachable, result.Failure?.Kind);
    }

    [Fact]
    public async Task Target_size_can_return_explicit_exceeded_state_when_allowed()
    {
        var options = new TargetSizeOptions(TargetBytes: 5_120, AllowExceed: true, MinQuality: 1, MaxQuality: 100);

        var result = await TargetSizeSearch.FindAsync(
            options,
            (quality, _) => Task.FromResult(ImageOperationResult<long>.Success(10_000L + quality)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.TargetReached);
        Assert.True(result.Value.ExceededTarget);
        Assert.True(result.Value.Selected.Bytes > options.TargetBytes);
    }
}
