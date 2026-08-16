using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoPic.Codecs;

internal static class PngIcoEncoder
{
    private static readonly int[] StandardSizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static IReadOnlyList<BitmapFrame> CreateFrames(BitmapSource source)
    {
        var maximumSourceSize = Math.Max(source.PixelWidth, source.PixelHeight);
        var maximumIconSize = Math.Min(256, maximumSourceSize);
        var sizes = StandardSizes.Where(size => size <= maximumIconSize).ToList();
        if (sizes.Count == 0 || sizes[sizes.Count - 1] != maximumIconSize)
        {
            sizes.Add(Math.Max(1, maximumIconSize));
        }

        return sizes.Distinct().OrderBy(size => size).Select(size => RenderFrame(source, size)).ToArray();
    }

    public static void Write(IReadOnlyList<BitmapFrame> frames, string outputPath)
    {
        if (frames.Count == 0 || frames.Count > ushort.MaxValue)
        {
            throw new InvalidDataException("ICO 必须包含 1 到 65535 个图像帧。");
        }

        var payloads = frames.Select(EncodePng).ToArray();
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        var payloadOffset = 6 + 16 * frames.Count;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var width = Math.Min(256, frame.PixelWidth);
            var height = Math.Min(256, frame.PixelHeight);
            writer.Write(width == 256 ? (byte)0 : (byte)width);
            writer.Write(height == 256 ? (byte)0 : (byte)height);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)payloads[index].Length);
            writer.Write((uint)payloadOffset);
            payloadOffset = checked(payloadOffset + payloads[index].Length);
        }

        foreach (var payload in payloads)
        {
            writer.Write(payload);
        }

        writer.Flush();
        output.Flush(flushToDisk: true);
    }

    private static BitmapFrame RenderFrame(BitmapSource source, int size)
    {
        var scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var x = (size - width) / 2.0;
        var y = (size - height) / 2.0;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(source, new Rect(x, y, width, height));
        }

        var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        var frame = BitmapFrame.Create(rendered);
        frame.Freeze();
        return frame;
    }

    private static byte[] EncodePng(BitmapFrame frame)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(frame);
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
