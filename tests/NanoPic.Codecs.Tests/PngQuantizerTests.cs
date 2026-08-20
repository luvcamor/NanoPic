using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NanoPic.Codecs;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class PngQuantizerTests
{
    private static BitmapSource CreateTestBitmap(int width, int height, double dpi, Action<byte[], int, int, int> fillPixel)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                fillPixel(buffer, offset, x, y);
            }
        }

        var source = BitmapSource.Create(
            width,
            height,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            null,
            buffer,
            stride);
        source.Freeze();
        return source;
    }

    [Fact]
    public void Q1_ExactColors_LessEqual256_LosslessPalette()
    {
        // 32 种纯色块图像 (32 x 32)
        var bitmap = CreateTestBitmap(32, 32, 96, (buf, offset, x, y) =>
        {
            var colorId = (byte)(x % 32);
            buf[offset] = (byte)(colorId * 7);      // B
            buf[offset + 1] = (byte)(colorId * 5);  // G
            buf[offset + 2] = (byte)(colorId * 3);  // R
            buf[offset + 3] = 255;                  // A
        });

        var quantized = PngQuantizer.Quantize(bitmap, 80, CancellationToken.None, out var info);

        Assert.False(info.WasLossy);
        Assert.Equal(32, info.PaletteSize);
        Assert.Equal(32, quantized.PixelWidth);
        Assert.Equal(32, quantized.PixelHeight);
        Assert.Equal(PixelFormats.Indexed8, quantized.Format);

        // 验证解码后的像素与原图完全一致
        var convertedBack = new FormatConvertedBitmap(quantized, PixelFormats.Bgra32, null, 0);
        var origBuffer = new byte[32 * 32 * 4];
        var convBuffer = new byte[32 * 32 * 4];
        bitmap.CopyPixels(origBuffer, 32 * 4, 0);
        convertedBack.CopyPixels(convBuffer, 32 * 4, 0);

        for (var i = 0; i < origBuffer.Length; i++)
        {
            Assert.Equal(origBuffer[i], convBuffer[i]);
        }
    }

    [Fact]
    public void Q2_ExactColors_256_Indexed8_DpiPreserved()
    {
        // 256 种精确颜色，DPI 300
        var bitmap = CreateTestBitmap(16, 16, 300, (buf, offset, x, y) =>
        {
            var colorId = (byte)(y * 16 + x);
            buf[offset] = colorId;
            buf[offset + 1] = (byte)(255 - colorId);
            buf[offset + 2] = (byte)((colorId * 3) % 256);
            buf[offset + 3] = 255;
        });

        var quantized = PngQuantizer.Quantize(bitmap, 80, CancellationToken.None, out var info);

        Assert.False(info.WasLossy);
        Assert.Equal(256, info.PaletteSize);
        Assert.Equal(300.0, quantized.DpiX, 1);
        Assert.Equal(300.0, quantized.DpiY, 1);
    }

    [Fact]
    public void Q3_HighColorCount_EntersLossyQuantizer()
    {
        // 1000 种不同渐变色的图像
        var bitmap = CreateTestBitmap(64, 64, 96, (buf, offset, x, y) =>
        {
            buf[offset] = (byte)(x * 4);
            buf[offset + 1] = (byte)(y * 4);
            buf[offset + 2] = (byte)((x + y) * 2);
            buf[offset + 3] = 255;
        });

        var quantized = PngQuantizer.Quantize(bitmap, 80, CancellationToken.None, out var info);

        Assert.True(info.WasLossy);
        Assert.True(info.PaletteSize <= 256 && info.PaletteSize >= 16);
        Assert.Equal(PixelFormats.Indexed8, quantized.Format);
    }

    [Fact]
    public void Q4_QualityScalesPaletteAndPolicy()
    {
        var bitmap = CreateTestBitmap(64, 64, 96, (buf, offset, x, y) =>
        {
            buf[offset] = (byte)(x * 4);
            buf[offset + 1] = (byte)(y * 4);
            buf[offset + 2] = (byte)((x + y) * 2);
            buf[offset + 3] = 255;
        });

        var q20 = PngCompressionPolicy.GetParameters(20);
        var q60 = PngCompressionPolicy.GetParameters(60);
        var q90 = PngCompressionPolicy.GetParameters(90);

        Assert.True(q20.MaxColors < q60.MaxColors);
        Assert.True(q60.MaxColors < q90.MaxColors);
        Assert.False(q20.EnableDithering); // 低质量关闭抖动
        Assert.True(q90.EnableDithering);  // 高质量开启抖动
    }

    [Fact]
    public void Q5_Quality100_DoesNotQuantizeLossy()
    {
        var bitmap = CreateTestBitmap(64, 64, 96, (buf, offset, x, y) =>
        {
            buf[offset] = (byte)(x * 4);
            buf[offset + 1] = (byte)(y * 4);
            buf[offset + 2] = (byte)((x + y) * 2);
            buf[offset + 3] = 255;
        });

        var quantized = PngQuantizer.Quantize(bitmap, 100, CancellationToken.None, out var info);

        Assert.False(info.WasLossy);
        // Quality 100 对 >256 色的图像不进行有损量化
    }

    [Fact]
    public void Q5b_Quality100_Stops_ExactColor_Tracking_After_257_Colors()
    {
        var bitmap = CreateTestBitmap(512, 512, 96, (buf, offset, x, y) =>
        {
            var colorId = y * 512 + x;
            buf[offset] = (byte)(colorId & 0xFF);
            buf[offset + 1] = (byte)((colorId >> 8) & 0xFF);
            buf[offset + 2] = (byte)((colorId >> 16) & 0xFF);
            buf[offset + 3] = 255;
        });

        var quantized = PngQuantizer.Quantize(bitmap, 100, CancellationToken.None, out var info);

        Assert.False(info.WasLossy);
        Assert.Equal(257, info.UniqueColorCount);
        Assert.Equal(PixelFormats.Bgra32, quantized.Format);
    }

    [Fact]
    public void Q6_TransparentPixels_Preserved()
    {
        // 包含左半纯透明、右半彩色的图像
        var bitmap = CreateTestBitmap(32, 32, 96, (buf, offset, x, y) =>
        {
            if (x < 16)
            {
                buf[offset] = 0;
                buf[offset + 1] = 0;
                buf[offset + 2] = 0;
                buf[offset + 3] = 0;
            }
            else
            {
                buf[offset] = (byte)(x * 8);
                buf[offset + 1] = (byte)(y * 8);
                buf[offset + 2] = 200;
                buf[offset + 3] = 255;
            }
        });

        var quantized = PngQuantizer.Quantize(bitmap, 60, CancellationToken.None, out var info);

        var converted = new FormatConvertedBitmap(quantized, PixelFormats.Bgra32, null, 0);
        var checkBuf = new byte[32 * 32 * 4];
        converted.CopyPixels(checkBuf, 32 * 4, 0);

        // 检查左上角透明像素
        Assert.Equal(0, checkBuf[3]); // Alpha 应为 0
        // 检查右半边彩色像素
        var rightOffset = (0 * 32 + 20) * 4;
        Assert.True(checkBuf[rightOffset + 3] > 200);
    }

    [Fact]
    public void Q7_TranslucentGradient_AlphaPreserved()
    {
        // 半透明渐变，同时让 RGB 随 y 变化以强制进入 >256 色量化路径。
        var bitmap = CreateTestBitmap(32, 32, 96, (buf, offset, x, y) =>
        {
            buf[offset] = (byte)(y * 8);
            buf[offset + 1] = (byte)(100 + y * 3);
            buf[offset + 2] = (byte)(150 - y * 3);
            buf[offset + 3] = (byte)(x * 8); // Alpha 从 0 到 248
        });

        var quantized = PngQuantizer.Quantize(bitmap, 80, CancellationToken.None, out var info);
        Assert.NotNull(quantized.Palette);
        Assert.True(quantized.Palette.Colors.Count >= 2);

        var converted = new FormatConvertedBitmap(quantized, PixelFormats.Bgra32, null, 0);
        var output = new byte[32 * 32 * 4];
        converted.CopyPixels(output, 32 * 4, 0);
        Assert.Equal(0, output[3]);
        Assert.InRange(output[(31 * 4) + 3], 220, 255);

        var distinctAlpha = new HashSet<byte>();
        for (var x = 0; x < 32; x++)
        {
            distinctAlpha.Add(output[(x * 4) + 3]);
        }
        Assert.True(distinctAlpha.Count >= 8, $"Expected an alpha gradient, got {distinctAlpha.Count} levels.");
    }

    [Fact]
    public void Q8_Cancellation_ThrowsOperationCanceledException()
    {
        var bitmap = CreateTestBitmap(128, 128, 96, (buf, offset, x, y) =>
        {
            buf[offset] = (byte)x;
            buf[offset + 1] = (byte)y;
            buf[offset + 2] = (byte)(x + y);
            buf[offset + 3] = 255;
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            PngQuantizer.Quantize(bitmap, 60, cts.Token, out _);
        });
    }

    [Fact]
    public void Q9_DeterministicOutput()
    {
        var bitmap = CreateTestBitmap(64, 64, 96, (buf, offset, x, y) =>
        {
            buf[offset] = (byte)(x * 3);
            buf[offset + 1] = (byte)(y * 3);
            buf[offset + 2] = (byte)((x + y) * 2);
            buf[offset + 3] = 255;
        });

        var run1 = PngQuantizer.Quantize(bitmap, 60, CancellationToken.None, out var info1);
        var run2 = PngQuantizer.Quantize(bitmap, 60, CancellationToken.None, out var info2);

        Assert.Equal(info1.PaletteSize, info2.PaletteSize);
        Assert.Equal(info1.WasLossy, info2.WasLossy);

        var buf1 = new byte[64 * 64];
        var buf2 = new byte[64 * 64];
        run1.CopyPixels(buf1, 64, 0);
        run2.CopyPixels(buf2, 64, 0);

        for (var i = 0; i < buf1.Length; i++)
        {
            Assert.Equal(buf1[i], buf2[i]);
        }
    }
}
