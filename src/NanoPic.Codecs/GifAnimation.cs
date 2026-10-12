using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NanoPic.Core;

namespace NanoPic.Codecs;

/// <summary>GIF canvas/control semantics. WIC handles LZW; its WPF encoder drops control extensions.</summary>
internal sealed class GifAnimation
{
    private sealed record FrameInfo(int Left, int Top, int Width, int Height, int Delay, int Disposal, int TransparentIndex, bool DifferentLocalBackground);
    private readonly List<FrameInfo> _frames = new();
    private int _width, _height;
    private byte[] _background = new byte[4];
    private ushort? _loop;

    public static GifAnimation Read(string path, ImageSafetyLimits limits, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var header = Encoding.ASCII.GetString(ReadExactly(reader, 6));
        if (header != "GIF87a" && header != "GIF89a") throw new InvalidDataException("GIF 文件头无效。");
        if (stream.Length > limits.MaxSourceBytes) throw new InvalidDataException("GIF 文件超过安全预算。");
        var result = new GifAnimation { _width = reader.ReadUInt16(), _height = reader.ReadUInt16() };
        var packed = reader.ReadByte(); var backgroundIndex = reader.ReadByte(); reader.ReadByte();
        if ((packed & 128) != 0)
        {
            var palette = ReadExactly(reader, 3 * (1 << ((packed & 7) + 1)));
            if (backgroundIndex * 3 + 2 < palette.Length)
                result._background = new[] { palette[backgroundIndex * 3 + 2], palette[backgroundIndex * 3 + 1], palette[backgroundIndex * 3], (byte)255 };
        }
        var delay = 0; var disposal = 0; var transparent = -1;
        var blocks = 0;
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++blocks > 100_000) throw new InvalidDataException("GIF 数据块数量超过安全预算。");
            var marker = reader.ReadByte();
            if (marker == 0x3b) break;
            if (marker == 0x21)
            {
                var label = reader.ReadByte();
                if (label == 0xf9)
                {
                    if (reader.ReadByte() != 4) throw new InvalidDataException("GIF 控制块无效。");
                    var flags = reader.ReadByte(); delay = reader.ReadUInt16(); var index = reader.ReadByte();
                    if (reader.ReadByte() != 0) throw new InvalidDataException("GIF 控制块未结束。");
                    disposal = (flags >> 2) & 7; transparent = (flags & 1) != 0 ? index : -1;
                }
                else if (label == 0xff)
                {
                    var app = Encoding.ASCII.GetString(ReadExactly(reader, reader.ReadByte()));
                    var data = ReadBlocks(reader, token, captureLimit: 3);
                    if ((app == "NETSCAPE2.0" || app == "ANIMEXTS1.0") && data.Length >= 3 && data[0] == 1)
                        result._loop = (ushort)(data[1] | data[2] << 8);
                }
                else { ReadBlocks(reader, token); }
            }
            else if (marker == 0x2c)
            {
                var left = reader.ReadUInt16(); var top = reader.ReadUInt16();
                var width = reader.ReadUInt16(); var height = reader.ReadUInt16(); var flags = reader.ReadByte();
                if (width == 0 || height == 0 || left + width > result._width || top + height > result._height)
                    throw new InvalidDataException("GIF 帧超出画布。");
                var differentLocalBackground = false;
                if ((flags & 128) != 0)
                {
                    var local = ReadExactly(reader, 3 * (1 << ((flags & 7) + 1)));
                    // This is a compatibility guard, NOT the GIF background definition:
                    // some decoders resolve disposal backgrounds through the local table.
                    // A different colour at that index therefore makes raster rewriting unsafe.
                    var offset = backgroundIndex * 3;
                    differentLocalBackground = offset + 2 >= local.Length ||
                        local[offset] != result._background[2] || local[offset + 1] != result._background[1] || local[offset + 2] != result._background[0];
                }
                reader.ReadByte(); ReadBlocks(reader, token); // WIC decodes this LZW payload.
                if (result._frames.Count >= limits.MaxFrames) throw new InvalidDataException("GIF 帧数超过安全预算。");
                result._frames.Add(new FrameInfo(left, top, width, height, delay, disposal, transparent, differentLocalBackground));
                delay = disposal = 0; transparent = -1;
            }
            else throw new InvalidDataException("GIF 数据块无效。");
        }
        return result;
    }

    // These disposal backgrounds do not have a consistent rendered interpretation
    // across WIC/GDI+ and other decoders. Preserve their original blocks when possible;
    // refuse pixel-changing multi-frame output rather than invent a new animation.
    public bool RequiresOriginalFrameData => _frames.Take(Math.Max(0, _frames.Count - 1)).Any(frame =>
        frame.Disposal == 2 && (frame.DifferentLocalBackground ||
        (frame.TransparentIndex >= 0) != (_frames[0].TransparentIndex >= 0)));

    /// <summary>Remove descriptive extensions without changing any image or animation bytes.</summary>
    public static long StripMetadata(string sourcePath, string outputPath, CancellationToken token)
    {
        using var source = File.OpenRead(sourcePath);
        using var reader = new BinaryReader(source);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        var header = ReadExactly(reader, 13);
        writer.Write(header);
        if ((header[10] & 128) != 0) writer.Write(ReadExactly(reader, 3 * (1 << ((header[10] & 7) + 1))));
        var buffer = new byte[65536];
        for (var blocks = 0; blocks < 100000; blocks++)
        {
            token.ThrowIfCancellationRequested();
            var start = source.Position; var marker = reader.ReadByte();
            if (marker == 0x3b)
            { writer.Write(marker); writer.Flush(); output.Flush(true); return output.Length; }
            var preserve = true;
            if (marker == 0x21)
            {
                var label = reader.ReadByte();
                if (label == 0xff)
                {
                    var app = Encoding.ASCII.GetString(ReadExactly(reader, reader.ReadByte()));
                    preserve = app == "NETSCAPE2.0" || app == "ANIMEXTS1.0";
                }
                else preserve = label == 0xf9 || label == 0x01; // GCE and rendering plain-text blocks
                ReadBlocks(reader, token);
            }
            else if (marker == 0x2c)
            {
                var descriptor = ReadExactly(reader, 9);
                if ((descriptor[8] & 128) != 0) ReadExactly(reader, 3 * (1 << ((descriptor[8] & 7) + 1)));
                reader.ReadByte(); ReadBlocks(reader, token);
            }
            else throw new InvalidDataException("GIF 数据块无效，未保存结果。");
            var end = source.Position;
            if (!preserve) continue;
            source.Position = start;
            while (source.Position < end)
            {
                token.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, end - source.Position));
                if (count == 0) throw new EndOfStreamException("GIF 数据被截断。");
                writer.Write(buffer, 0, count);
            }
        }
        throw new InvalidDataException("GIF 数据块数量超过安全预算。");
    }

    public IReadOnlyList<BitmapFrame> Coalesce(IReadOnlyList<BitmapFrame> decoded, ImageSafetyLimits limits, CancellationToken token, bool firstFrameOnly = false)
    {
        var pixels = (long)_width * _height;
        if (_width <= 0 || _height <= 0 || decoded.Count != _frames.Count || decoded.Count == 0 ||
            pixels > limits.HardMaxPixels || pixels * decoded.Count > limits.HardMaxTotalPixels)
            throw new InvalidDataException("GIF 画布或帧预算无效。");
        if (ImageDecodeBudget.Validate(_width, _height, decoded.Count) is { } memoryFailure)
            throw new InvalidDataException(memoryFailure.UserMessage);
        var stride = checked(_width * 4); var canvas = new byte[checked(stride * _height)];
        var background = _frames[0].TransparentIndex >= 0 ? new byte[4] : _background;
        Clear(canvas, 0, 0, _width, _height, stride, background);
        var count = firstFrameOnly ? 1 : decoded.Count;
        var result = new List<BitmapFrame>(count);
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var info = _frames[i]; var source = decoded[i];
            if (source.PixelWidth != info.Width || source.PixelHeight != info.Height)
                throw new InvalidDataException("GIF 解码帧尺寸与控制信息不一致。");
            var previous = info.Disposal == 3 ? (byte[])canvas.Clone() : null;
            var region = source.Format == PixelFormats.Bgra32 ? (BitmapSource)source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var row = new byte[checked(info.Width * 4)];
            for (var y = 0; y < info.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                region.CopyPixels(new Int32Rect(0, y, info.Width, 1), row, row.Length, 0);
                for (var x = 0; x < info.Width; x++)
                    if (row[x * 4 + 3] != 0) Buffer.BlockCopy(row, x * 4, canvas, (info.Top + y) * stride + (info.Left + x) * 4, 4);
            }
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", (ushort)info.Delay);
            // Coalesced frames are complete canvases, so clear before the next one.
            metadata.SetQuery("/grctlext/Disposal", (byte)2);
            if (_loop.HasValue)
            {
                metadata.SetQuery("/appext/Application", Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                metadata.SetQuery("/appext/Data", new byte[] { 3, 1, (byte)_loop.Value, (byte)(_loop.Value >> 8), 0 });
            }
            var frame = BitmapFrame.Create(bitmap, null, metadata, null); frame.Freeze(); result.Add(frame);
            if (info.Disposal == 2)
                Clear(canvas, info.Left, info.Top, info.Width, info.Height, stride, info.TransparentIndex >= 0 ? new byte[4] : _background);
            else if (previous is not null) canvas = previous;
        }
        return result;
    }

    public static BitmapMetadata PreserveTiming(BitmapMetadata? original, BitmapMetadata? prepared)
    {
        var result = prepared ?? new BitmapMetadata("gif");
        foreach (var query in new[] { "/grctlext/Delay", "/grctlext/Disposal", "/appext/Application", "/appext/Data" })
            if (original?.ContainsQuery(query) == true) result.SetQuery(query, original.GetQuery(query));
        return result;
    }

    public static IReadOnlyList<BitmapFrame> NormalizeCanvas(IReadOnlyList<BitmapFrame> frames)
    {
        var width = frames.Max(f => f.PixelWidth); var height = frames.Max(f => f.PixelHeight);
        if (frames.All(f => f.PixelWidth == width && f.PixelHeight == height)) return frames;
        if (ImageDecodeBudget.Validate(width, height, frames.Count) is { } failure)
            throw new InvalidDataException(failure.UserMessage);
        var result = new List<BitmapFrame>(frames.Count);
        foreach (var frame in frames)
        {
            var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
            source.CopyPixels(new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight), pixels, stride, 0);
            var canvas = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var expanded = BitmapFrame.Create(canvas, null, frame.Metadata as BitmapMetadata, null);
            expanded.Freeze(); result.Add(expanded);
        }
        return result;
    }

    public static void Write(IReadOnlyList<BitmapFrame> frames, string path, CancellationToken token)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
        writer.Write(checked((ushort)frames[0].PixelWidth)); writer.Write(checked((ushort)frames[0].PixelHeight));
        writer.Write((byte)0x80); writer.Write((byte)0); writer.Write((byte)0); writer.Write(new byte[6]);
        var firstMetadata = frames[0].Metadata as BitmapMetadata;
        if (firstMetadata?.Format == "gif" && firstMetadata.GetQuery("/appext/Data") is byte[] loop && loop.Length >= 4)
        {
            writer.Write(new byte[] { 0x21, 0xff, 11 }); writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            writer.Write(new byte[] { 3, 1, loop[2], loop[3], 0 });
        }
        var transparentCanvas = frames.Any(frame => ContainsTransparency(frame, token));
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            // Quantize once; WIC supplies a proven LZW encoder. We preserve its palette
            // ordering and explicitly emit the control extension it drops in WPF.
            var reserveTransparentSlot = transparentCanvas && !ContainsTransparency(frame, token);
            var indexed = PngQuantizer.Quantize(BinaryAlpha(frame, token), 99, token, out _, reserveTransparentSlot ? 255 : 256);
            if (indexed.Palette is null)
                throw new InvalidDataException("GIF 调色板转换失败。");
            if (transparentCanvas && indexed.Palette.Colors.All(c => c.A >= 128))
            {
                // Even a fully opaque canvas needs a transparent disposal background
                // when a later full canvas contains transparency.
                var pixels = new byte[checked(indexed.PixelWidth * indexed.PixelHeight)];
                indexed.CopyPixels(pixels, indexed.PixelWidth, 0);
                for (var i = 0; i < pixels.Length; i++) pixels[i]++;
                var palette = new BitmapPalette(new[] { Colors.Transparent }.Concat(indexed.Palette.Colors).ToList());
                indexed = BitmapSource.Create(indexed.PixelWidth, indexed.PixelHeight, 96, 96, PixelFormats.Indexed8,
                    palette, pixels, indexed.PixelWidth);
            }
            var transparentIndex = indexed.Palette.Colors.Select((c, i) => new { c, i }).Where(x => x.c.A < 128).Select(x => x.i).DefaultIfEmpty(-1).First();
            var encoder = new GifBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(indexed));
            using var bytes = new MemoryStream(); encoder.Save(bytes); bytes.Position = 0;
            using var reader = new BinaryReader(bytes, Encoding.ASCII, leaveOpen: true);
            ReadExactly(reader, 10); var packed = reader.ReadByte(); reader.ReadByte(); reader.ReadByte();
            var globalPalette = (packed & 128) != 0 ? ReadExactly(reader, 3 * (1 << ((packed & 7) + 1))) : Array.Empty<byte>();
            while (true)
            {
                var marker = reader.ReadByte();
                if (marker == 0x21) { reader.ReadByte(); ReadBlocks(reader, token); continue; }
                if (marker != 0x2c) throw new InvalidDataException("GIF 编码器没有生成图像块。");
                var descriptor = ReadExactly(reader, 9); var localFlags = descriptor[8];
                var palette = (localFlags & 128) != 0 ? ReadExactly(reader, 3 * (1 << ((localFlags & 7) + 1))) : globalPalette;
                var sizeBits = (localFlags & 128) != 0 ? localFlags & 7 : packed & 7;
                if (palette.Length == 0) throw new InvalidDataException("GIF 编码输出缺少调色板。");
                if (transparentIndex >= 0)
                {
                    // Do not silently attach transparency to a reordered colour index.
                    for (var i = 0; i < indexed.Palette.Colors.Count; i++)
                    {
                        var c = indexed.Palette.Colors[i];
                        if (i * 3 + 2 >= palette.Length || palette[i * 3] != c.R || palette[i * 3 + 1] != c.G || palette[i * 3 + 2] != c.B)
                            throw new InvalidDataException("GIF 编码器改变了透明调色板顺序，未保存结果。");
                    }
                }
                var metadata = frame.Metadata as BitmapMetadata;
                var delay = metadata?.Format == "gif" && metadata.GetQuery("/grctlext/Delay") is ushort d ? d : (ushort)0;
                writer.Write(new byte[] { 0x21, 0xf9, 4, (byte)(8 | (transparentIndex >= 0 ? 1 : 0)) });
                writer.Write(delay); writer.Write((byte)Math.Max(0, transparentIndex)); writer.Write((byte)0);
                writer.Write((byte)0x2c); descriptor[0] = descriptor[1] = descriptor[2] = descriptor[3] = 0;
                descriptor[8] = (byte)(0x80 | (localFlags & 0x40) | sizeBits);
                writer.Write(descriptor); writer.Write(palette);
                writer.Write(reader.ReadByte());
                int count;
                while ((count = reader.ReadByte()) != 0)
                { token.ThrowIfCancellationRequested(); writer.Write((byte)count); writer.Write(ReadExactly(reader, count)); }
                writer.Write((byte)0); break;
            }
        }
        writer.Write((byte)0x3b); writer.Flush(); output.Flush(true);
    }

    private static void Clear(byte[] pixels, int left, int top, int width, int height, int stride, byte[] color)
    {
        var row = new byte[checked(width * 4)];
        if (color.Any(c => c != 0))
            for (var x = 0; x < width; x++) Buffer.BlockCopy(color, 0, row, x * 4, 4);
        for (var y = top; y < top + height; y++)
            Buffer.BlockCopy(row, 0, pixels, y * stride + left * 4, row.Length);
    }
    private static byte[] ReadExactly(BinaryReader reader, int count)
    { var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException("GIF 数据被截断。"); return bytes; }
    private static bool ContainsTransparency(BitmapSource source, CancellationToken token)
    {
        var pixels = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var row = new byte[checked(source.PixelWidth * 4)];
        for (var y = 0; y < source.PixelHeight; y++)
        {
            token.ThrowIfCancellationRequested();
            pixels.CopyPixels(new Int32Rect(0, y, source.PixelWidth, 1), row, row.Length, 0);
            for (var x = 3; x < row.Length; x += 4) if (row[x] < 128) return true;
        }
        return false;
    }

    private static BitmapSource BinaryAlpha(BitmapSource source, CancellationToken token)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            if (pixels[i + 3] < 128) pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
            else pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze(); return bitmap;
    }
    private static byte[] ReadBlocks(BinaryReader reader, CancellationToken token, int captureLimit = 0)
    {
        using var data = new MemoryStream(); int count;
        while ((count = reader.ReadByte()) != 0)
        {
            token.ThrowIfCancellationRequested();
            var retain = Math.Max(0, Math.Min(count, captureLimit - (int)data.Length));
            if (retain > 0) { var bytes = ReadExactly(reader, retain); data.Write(bytes, 0, retain); }
            var skip = count - retain;
            if (reader.BaseStream.Length - reader.BaseStream.Position < skip)
                throw new EndOfStreamException("GIF 数据被截断。");
            reader.BaseStream.Position += skip;
        }
        return data.ToArray();
    }
}
