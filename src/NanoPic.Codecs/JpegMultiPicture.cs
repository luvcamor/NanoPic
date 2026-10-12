using System;
using System.IO;
using System.Threading;

namespace NanoPic.Codecs;

/// <summary>MPF APP2 offsets refer to auxiliary JPEGs, not portable per-frame metadata.</summary>
internal static class JpegMultiPicture
{
    public static bool HasIndex(string path, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        return CopyHeaders(input, null, token);
    }

    // Only called on an exclusively owned, newly encoded temporary JPEG (one image).
    public static void RemoveStaleIndex(string path, CancellationToken token)
    {
        if (!HasIndex(path, token)) return;
        var cleaned = Path.Combine(Path.GetDirectoryName(path)!, ".nanopic-metadata-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(cleaned, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { CopyHeaders(input, output, token); output.Flush(true); }
            File.Copy(cleaned, path, overwrite: true);
        }
        finally { if (File.Exists(cleaned)) File.Delete(cleaned); }
    }

    private static bool CopyHeaders(Stream input, Stream? output, CancellationToken token)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.ASCII, leaveOpen: true);
        if (reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8) throw new InvalidDataException("JPEG 文件头无效。");
        output?.WriteByte(0xff); output?.WriteByte(0xd8);
        var found = false;
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            if (reader.ReadByte() != 0xff) throw new InvalidDataException("JPEG 标记无效。");
            byte marker;
            while ((marker = reader.ReadByte()) == 0xff) { }
            if (marker is 0xda or 0xd9)
            {
                if (output is not null)
                {
                    output.WriteByte(0xff); output.WriteByte(marker);
                    var buffer = new byte[65_536]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                }
                return found;
            }
            if (marker == 1 || marker is >= 0xd0 and <= 0xd7)
            { output?.WriteByte(0xff); output?.WriteByte(marker); continue; }
            var hi = reader.ReadByte(); var lo = reader.ReadByte(); var length = (hi << 8) | lo;
            if (length < 2) throw new InvalidDataException("JPEG 元数据长度无效。");
            var payload = reader.ReadBytes(length - 2);
            if (payload.Length != length - 2) throw new EndOfStreamException("JPEG 元数据被截断。");
            if (marker == 0xe2 && payload.Length >= 4 && payload[0] == 77 && payload[1] == 80 && payload[2] == 70 && payload[3] == 0)
            {
                found = true;
                if (output is null) return true;
                continue;
            }
            if (output is not null)
            {
                output.WriteByte(0xff); output.WriteByte(marker); output.WriteByte(hi); output.WriteByte(lo);
                output.Write(payload, 0, payload.Length);
            }
        }
        throw new InvalidDataException("JPEG 缺少图像数据。");
    }
}
