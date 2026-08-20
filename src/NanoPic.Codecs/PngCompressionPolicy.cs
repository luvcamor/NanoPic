using System;

namespace NanoPic.Codecs;

public sealed record PngQuantizationParameters(
    int MaxColors,
    double DitherStrength,
    bool EnableDithering);

public static class PngCompressionPolicy
{
    public static PngQuantizationParameters GetParameters(int quality)
    {
        var clampedQuality = Math.Max(1, Math.Min(100, quality));
        if (clampedQuality == 100)
        {
            // Quality 100 为无损模式，不进行有损量化
            return new PngQuantizationParameters(
                MaxColors: 256,
                DitherStrength: 0.0,
                EnableDithering: false);
        }

        // 质量 1..99：非线性映射调色板大小 (16 到 256)
        // 公式：16 + (q/100)^1.6 * 240，取整至 16-256 之间
        var ratio = clampedQuality / 100.0;
        var rawColors = 16.0 + Math.Pow(ratio, 1.6) * 240.0;
        var maxColors = Math.Max(16, Math.Min(256, (int)Math.Round(rawColors)));

        // 抖动策略：
        // 高质量 (>=70) 开启适当抖动减少色带；
        // 中质量 (40..69) 降低抖动强度；
        // 低质量 (<40) 关闭抖动以极大降低 PNG 熵（显著减小文件体积）
        bool enableDithering;
        double ditherStrength;

        if (clampedQuality >= 70)
        {
            enableDithering = true;
            ditherStrength = Math.Min(1.0, 0.4 + (clampedQuality - 70) * 0.02);
        }
        else if (clampedQuality >= 40)
        {
            enableDithering = true;
            ditherStrength = 0.15 + (clampedQuality - 40) * 0.007;
        }
        else
        {
            enableDithering = false;
            ditherStrength = 0.0;
        }

        return new PngQuantizationParameters(maxColors, ditherStrength, enableDithering);
    }
}
