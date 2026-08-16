namespace NanoPic.Core;

public enum ImageProcessingCapability
{
    CpuBackend = 0,
    GpuAcceleration,
    BlindWatermark
}

public sealed record ImageProcessingCapabilityStatus(
    ImageProcessingCapability Capability,
    bool IsAvailable,
    ImageFailureKind? UnavailableKind,
    string Message);

public interface IImageProcessingCapabilityProvider
{
    ImageProcessingCapabilityStatus Get(ImageProcessingCapability capability);

    IReadOnlyCollection<ImageProcessingCapabilityStatus> GetAll();
}

public sealed class DefaultImageProcessingCapabilityProvider : IImageProcessingCapabilityProvider
{
    private static readonly IReadOnlyDictionary<ImageProcessingCapability, ImageProcessingCapabilityStatus> Statuses =
        new Dictionary<ImageProcessingCapability, ImageProcessingCapabilityStatus>
        {
            [ImageProcessingCapability.CpuBackend] = new(
                ImageProcessingCapability.CpuBackend,
                IsAvailable: true,
                UnavailableKind: null,
                Message: "CPU 图像处理后端可用。"),
            [ImageProcessingCapability.GpuAcceleration] = new(
                ImageProcessingCapability.GpuAcceleration,
                IsAvailable: false,
                UnavailableKind: ImageFailureKind.AccelerationUnavailable,
                Message: "GPU 加速当前不可用，任务将使用 CPU 后端。"),
            [ImageProcessingCapability.BlindWatermark] = new(
                ImageProcessingCapability.BlindWatermark,
                IsAvailable: false,
                UnavailableKind: ImageFailureKind.LegacyUnimplemented,
                Message: "盲水印为旧版未实现功能，当前版本明确不提供处理结果。")
        };

    public ImageProcessingCapabilityStatus Get(ImageProcessingCapability capability) =>
        Statuses.TryGetValue(capability, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(capability), capability, "未知图像处理能力。");

    public IReadOnlyCollection<ImageProcessingCapabilityStatus> GetAll() => Statuses.Values.ToArray();
}
