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
    public void Auto_downscale_suggests_safe_dimensions_when_pixel_budget_exceeded()
    {
        // 9879 * 10880 = 107,483,520 pixels (> 100,000,000)
        var metadata = new ImageMetadata(ImageFormat.Jpeg, 9879, 10880, 1, false, 2_925_600);
        var limits = ImageSafetyLimits.Default with { MaxPixels = 100_000_000, AutoDownscaleOnExceed = true };

        var result = ImageSafetyValidator.ValidateWithAction(metadata, limits);

        Assert.Equal(SafetyAction.Downscale, result.Action);
        Assert.NotNull(result.TargetWidth);
        Assert.NotNull(result.TargetHeight);
        Assert.True((long)result.TargetWidth.Value * result.TargetHeight.Value <= limits.MaxPixels);
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

    [Fact]
    public void Hard_max_pixels_rejects_even_when_auto_downscale_enabled()
    {
        // 25000 * 25000 = 625,000,000 pixels (> HardMaxPixels = 500,000,000)
        var metadata = new ImageMetadata(ImageFormat.Jpeg, 25000, 25000, 1, false, 1_000_000);
        var limits = ImageSafetyLimits.Default with { AutoDownscaleOnExceed = true };

        var result = ImageSafetyValidator.ValidateWithAction(metadata, limits);

        Assert.Equal(SafetyAction.Reject, result.Action);
        Assert.NotNull(result.Failure);
        Assert.Equal(ImageFailureKind.PixelBudgetExceeded, result.Failure!.Kind);
    }

    [Fact]
    public void Soft_max_pixels_downscales_when_auto_downscale_enabled()
    {
        // 16000 * 15000 = 240,000,000 pixels (> SoftMaxPixels 200MP, < HardMaxPixels 500MP)
        var metadata = new ImageMetadata(ImageFormat.Jpeg, 16000, 15000, 1, false, 1_000_000);
        var limits = ImageSafetyLimits.Default with { AutoDownscaleOnExceed = true };

        var result = ImageSafetyValidator.ValidateWithAction(metadata, limits);

        Assert.Equal(SafetyAction.Downscale, result.Action);
        Assert.Null(result.Failure);
        Assert.NotNull(result.TargetWidth);
        Assert.NotNull(result.TargetHeight);
        Assert.True((long)result.TargetWidth!.Value * result.TargetHeight!.Value <= limits.MaxPixels);
    }

    [Fact]
    public void Soft_max_pixels_rejects_when_auto_downscale_disabled()
    {
        var metadata = new ImageMetadata(ImageFormat.Jpeg, 16000, 15000, 1, false, 1_000_000);
        var limits = ImageSafetyLimits.Default with { AutoDownscaleOnExceed = false };

        var result = ImageSafetyValidator.ValidateWithAction(metadata, limits);

        Assert.Equal(SafetyAction.Reject, result.Action);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void Resize_planner_merges_auto_downscale_with_user_resize()
    {
        // Source: 12000 × 9000, safety says downscale to ~11547×8660
        // User resize: 1920 × 1080 — user target is smaller, so use it directly
        var safetyResult = new SafetyValidationResult(SafetyAction.Downscale, null, 11547, 8660);
        var userResize = new ImageResizeOptions(true, 1920, 1080, PreserveAspectRatio: true);

        var plan = ImageResizePlanner.Plan(12000, 9000, safetyResult, userResize);

        Assert.True(plan.ResizeRequired);
        Assert.False(plan.AutoDownsampled);
        Assert.Equal(1920, plan.Width);
        Assert.Equal(1080, plan.Height);
    }

    [Fact]
    public void Resize_planner_uses_safety_dimensions_when_user_resize_still_exceeds()
    {
        // Source: 20000 × 10000, safety says downscale to ~10000×5000
        // User resize: 16000 × 8000 = 128MP — still exceeds safe 100MP
        var safetyResult = new SafetyValidationResult(SafetyAction.Downscale, null, 10000, 5000);
        var userResize = new ImageResizeOptions(true, 16000, 8000, PreserveAspectRatio: true);

        var plan = ImageResizePlanner.Plan(20000, 10000, safetyResult, userResize);

        Assert.True(plan.ResizeRequired);
        Assert.True(plan.AutoDownsampled);
        Assert.Equal(10000, plan.Width);
        Assert.Equal(5000, plan.Height);
    }

    [Fact]
    public void Resize_planner_auto_downscale_only_no_user_resize()
    {
        var safetyResult = new SafetyValidationResult(SafetyAction.Downscale, null, 11547, 8660);
        var userResize = (ImageResizeOptions?)null;

        var plan = ImageResizePlanner.Plan(12000, 9000, safetyResult, userResize);

        Assert.True(plan.ResizeRequired);
        Assert.True(plan.AutoDownsampled);
        Assert.Equal(11547, plan.Width);
        Assert.Equal(8660, plan.Height);
        Assert.NotNull(plan.Notice);
    }

    [Fact]
    public void Resize_planner_user_resize_only_no_safety_downscale()
    {
        var safetyResult = new SafetyValidationResult(SafetyAction.Pass, null, null, null);
        var userResize = new ImageResizeOptions(true, 1920, 1080, PreserveAspectRatio: false);

        var plan = ImageResizePlanner.Plan(4000, 3000, safetyResult, userResize);

        Assert.True(plan.ResizeRequired);
        Assert.False(plan.AutoDownsampled);
        Assert.Equal(1920, plan.Width);
        Assert.Equal(1080, plan.Height);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Resize_planner_no_resize_when_neither_needed()
    {
        var safetyResult = new SafetyValidationResult(SafetyAction.Pass, null, null, null);
        var userResize = (ImageResizeOptions?)null;

        var plan = ImageResizePlanner.Plan(4000, 3000, safetyResult, userResize);

        Assert.False(plan.ResizeRequired);
        Assert.False(plan.AutoDownsampled);
        Assert.Equal(4000, plan.Width);
        Assert.Equal(3000, plan.Height);
    }

    [Fact]
    public void Concurrency_policy_limits_large_images()
    {
        var limit = OversizedImageConcurrencyPolicy.LimitFor(80_000_000, 8);
        Assert.Equal(4, limit.MaxConcurrentTasks);

        limit = OversizedImageConcurrencyPolicy.LimitFor(150_000_000, 8);
        Assert.Equal(2, limit.MaxConcurrentTasks);

        limit = OversizedImageConcurrencyPolicy.LimitFor(250_000_000, 8);
        Assert.Equal(1, limit.MaxConcurrentTasks);
    }

    [Fact]
    public void Concurrency_policy_does_not_limit_small_images()
    {
        var limit = OversizedImageConcurrencyPolicy.LimitFor(30_000_000, 8);
        Assert.Equal(8, limit.MaxConcurrentTasks);
    }

    [Fact]
    public void Concurrency_policy_respects_user_max_threads()
    {
        // User only allows 2 threads — even small images shouldn't exceed that
        var limit = OversizedImageConcurrencyPolicy.LimitFor(30_000_000, 2);
        Assert.Equal(2, limit.MaxConcurrentTasks);
    }

    [Fact]
    public void Concurrency_policy_effective_takes_most_restrictive()
    {
        var pixels = new long[] { 30_000_000, 80_000_000, 150_000_000 };
        var limit = OversizedImageConcurrencyPolicy.EffectiveConcurrency(pixels, 8);

        Assert.Equal(2, limit.MaxConcurrentTasks); // 150MP wins
        Assert.NotNull(limit.Reason);
    }

    [Fact]
    public void Concurrency_policy_effective_returns_user_max_when_all_small()
    {
        var pixels = new long[] { 10_000_000, 30_000_000, 40_000_000 };
        var limit = OversizedImageConcurrencyPolicy.EffectiveConcurrency(pixels, 8);

        Assert.Equal(8, limit.MaxConcurrentTasks);
        Assert.Null(limit.Reason);
    }

    [Fact]
    public void Oversized_image_settings_holds_raw_values()
    {
        var valid = new OversizedImageSettings(200_000_000, true);
        Assert.Equal(200_000_000, valid.SoftMaxPixels);
        Assert.True(valid.AutoDownsample);

        var small = new OversizedImageSettings(10_000_000, false);
        Assert.Equal(10_000_000, small.SoftMaxPixels);
        Assert.False(small.AutoDownsample);
    }

    [Fact]
    public void Oversized_image_settings_default_is_200_mp()
    {
        var defaults = OversizedImageSettings.Default;
        Assert.Equal(200_000_000, defaults.SoftMaxPixels);
        Assert.True(defaults.AutoDownsample);
    }
}
