using System.Runtime.InteropServices;

namespace NanoPic.Core;

/// <summary>Conservative decode admission, separate from the user pixel/resize policy.</summary>
public static class ImageDecodeBudget
{
    public const long MaximumEstimatedBytes = 1024L * 1024 * 1024;
    public const int EstimatedBytesPerPixel = 16; // decoder + working/converted/rendered buffers

    public static ImageOperationFailure? Validate(int width, int height, int frameCount, long? budgetBytes = null, int decodedBitsPerPixel = 32)
    {
        var budget = budgetBytes ?? CurrentBudget();
        if (width <= 0 || height <= 0 || frameCount <= 0 || budget <= 0)
            return new ImageOperationFailure(ImageFailureKind.PixelBudgetExceeded, "无法安全估计图片解码内存。");
        var pixels = (long)width * height;
        // Divide, rather than multiplying attacker-controlled dimensions and frame count.
        var estimatedBytesPerPixel = Math.Max(EstimatedBytesPerPixel, ((decodedBitsPerPixel + 7L) / 8) * 4);
        if (pixels > budget / estimatedBytesPerPixel / frameCount)
            return new ImageOperationFailure(ImageFailureKind.PixelBudgetExceeded,
                "图片预计解码内存超过当前安全预算，已停止处理。安全缩小仍需要解码；请先用支持解码阶段缩小的工具另存较小图片。");
        return null;
    }

    private static long CurrentBudget()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return MaximumEstimatedBytes;
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (!GlobalMemoryStatusEx(ref state)) return MaximumEstimatedBytes;
        return Math.Min(MaximumEstimatedBytes, (long)(state.AvailablePhysical / 4));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
