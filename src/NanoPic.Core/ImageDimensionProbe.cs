using System;
using System.IO;
using System.Text;

namespace NanoPic.Core;

public static class ImageDimensionProbe
{
    public static ImageOperationResult<ImageHeaderInfo> Probe(Stream stream, ImageFormat format)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanSeek)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(
                ImageFailureKind.DecodeFailed,
                "探测图像头部需要可定位的流。");
        }

        var originalPosition = stream.Position;
        try
        {
            var result = format switch
            {
                ImageFormat.Jpeg => ProbeJpeg(stream),
                ImageFormat.Png => ProbePng(stream),
                ImageFormat.Gif => ProbeGif(stream),
                ImageFormat.Bmp => ProbeBmp(stream),
                ImageFormat.Tiff => ProbeTiff(stream),
                ImageFormat.Webp => ProbeWebp(stream),
                ImageFormat.Ico => ProbeIco(stream),
                _ => ImageOperationResult<ImageHeaderInfo>.Failed(
                    ImageFailureKind.UnsupportedFormat,
                    "不支持对该格式进行头部尺寸探测。")
            };

            return result;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(
                ImageFailureKind.DecodeFailed,
                "解析图像头部元数据失败。",
                exception);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    public static bool TryProbe(Stream stream, ImageFormat format, out ImageHeaderInfo? headerInfo)
    {
        var result = Probe(stream, format);
        if (result.IsSuccess && result.Value is not null)
        {
            headerInfo = result.Value;
            return true;
        }

        headerInfo = null;
        return false;
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbeJpeg(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[12];
        if (stream.Read(buffer, 0, 2) < 2 || buffer[0] != 0xFF || buffer[1] != 0xD8)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 JPEG 文件头。");
        }

        var orientation = 1;
        int? width = null;
        int? height = null;

        while (stream.Position < stream.Length)
        {
            int markerPrefix = stream.ReadByte();
            if (markerPrefix < 0) break;
            if (markerPrefix != 0xFF) continue;

            int marker = stream.ReadByte();
            while (marker == 0xFF)
            {
                marker = stream.ReadByte();
            }

            if (marker < 0 || marker == 0xDA || marker == 0xD9) // SOS or EOI
            {
                break;
            }

            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
            {
                continue;
            }

            if (stream.Read(buffer, 0, 2) < 2) break;
            var length = (buffer[0] << 8) | buffer[1];
            if (length < 2) break;

            var payloadLength = length - 2;
            var markerPayloadPos = stream.Position;

            // Check for APP1 (EXIF)
            if (marker == 0xE1 && payloadLength >= 14)
            {
                var exifHeader = new byte[Math.Min(payloadLength, 65536)];
                var readBytes = stream.Read(exifHeader, 0, exifHeader.Length);
                if (readBytes >= 14 &&
                    exifHeader[0] == (byte)'E' && exifHeader[1] == (byte)'x' &&
                    exifHeader[2] == (byte)'i' && exifHeader[3] == (byte)'f' &&
                    exifHeader[4] == 0 && exifHeader[5] == 0)
                {
                    orientation = ParseExifOrientation(exifHeader, 6, readBytes - 6);
                }
            }
            // Check for SOF markers (SOF0..SOF3, SOF5..SOF7, SOF9..SOF11, SOF13..SOF15)
            else if (IsJpegSofMarker(marker) && payloadLength >= 5)
            {
                if (stream.Read(buffer, 0, 5) >= 5)
                {
                    // buffer[0]: precision
                    height = (buffer[1] << 8) | buffer[2];
                    width = (buffer[3] << 8) | buffer[4];
                }
            }

            if (width.HasValue && height.HasValue && orientation != 1)
            {
                // Found both dimensions and non-default orientation
                break;
            }

            var nextPos = markerPayloadPos + payloadLength;
            if (nextPos > stream.Length) break;
            stream.Position = nextPos;
        }

        if (width.HasValue && height.HasValue && width.Value > 0 && height.Value > 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Success(
                new ImageHeaderInfo(width.Value, height.Value, FrameCount: 1, ExifOrientation: orientation));
        }

        return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "未能从 JPEG 头部解析出有效尺寸。");
    }

    private static bool IsJpegSofMarker(int marker) =>
        marker switch
        {
            0xC0 or 0xC1 or 0xC2 or 0xC3 or
            0xC5 or 0xC6 or 0xC7 or
            0xC9 or 0xCA or 0xCB or
            0xCD or 0xCE or 0xCF => true,
            _ => false
        };

    private static int ParseExifOrientation(byte[] buffer, int offset, int length)
    {
        if (length < 8) return 1;
        bool isLittleEndian;
        if (buffer[offset] == 0x49 && buffer[offset + 1] == 0x49)
        {
            isLittleEndian = true;
        }
        else if (buffer[offset] == 0x4D && buffer[offset + 1] == 0x4D)
        {
            isLittleEndian = false;
        }
        else
        {
            return 1;
        }

        int ReadUInt16(int pos)
        {
            if (pos + 2 > length) return 0;
            return isLittleEndian
                ? buffer[offset + pos] | (buffer[offset + pos + 1] << 8)
                : (buffer[offset + pos] << 8) | buffer[offset + pos + 1];
        }

        int ReadUInt32(int pos)
        {
            if (pos + 4 > length) return 0;
            return isLittleEndian
                ? buffer[offset + pos] | (buffer[offset + pos + 1] << 8) | (buffer[offset + pos + 2] << 16) | (buffer[offset + pos + 3] << 24)
                : (buffer[offset + pos] << 24) | (buffer[offset + pos + 1] << 16) | (buffer[offset + pos + 2] << 8) | buffer[offset + pos + 3];
        }

        var magic = ReadUInt16(2);
        if (magic != 42) return 1;

        var ifd0Offset = ReadUInt32(4);
        if (ifd0Offset < 8 || ifd0Offset + 2 > length) return 1;

        var tagCount = ReadUInt16((int)ifd0Offset);
        var currentTagPos = (int)ifd0Offset + 2;

        for (int i = 0; i < tagCount && currentTagPos + 12 <= length; i++, currentTagPos += 12)
        {
            var tagId = ReadUInt16(currentTagPos);
            if (tagId == 0x0112) // Orientation
            {
                var tagType = ReadUInt16(currentTagPos + 2);
                if (tagType == 3) // SHORT
                {
                    var val = ReadUInt16(currentTagPos + 8);
                    if (val >= 1 && val <= 8)
                    {
                        return val;
                    }
                }
            }
        }

        return 1;
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbePng(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[32];
        if (stream.Read(buffer, 0, 8) < 8)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "PNG 文件过短。");
        }

        if (buffer[0] != 0x89 || buffer[1] != 0x50 || buffer[2] != 0x4E || buffer[3] != 0x47 ||
            buffer[4] != 0x0D || buffer[5] != 0x0A || buffer[6] != 0x1A || buffer[7] != 0x0A)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 PNG 签名。");
        }

        if (stream.Read(buffer, 0, 16) < 16)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "PNG IHDR 块不完整。");
        }

        // IHDR chunk: 4 bytes length, 4 bytes type ("IHDR"), 4 bytes width, 4 bytes height
        var chunkType = Encoding.ASCII.GetString(buffer, 4, 4);
        if (chunkType != "IHDR")
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "PNG 首个数据块不是 IHDR。");
        }

        var width = (buffer[8] << 24) | (buffer[9] << 16) | (buffer[10] << 8) | buffer[11];
        var height = (buffer[12] << 24) | (buffer[13] << 16) | (buffer[14] << 8) | buffer[15];

        if (width <= 0 || height <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "PNG 包含无效尺寸。");
        }

        var orientation = 1;
        // Optionally scan subsequent chunks for eXIf chunk to extract orientation
        while (stream.Position + 8 <= stream.Length)
        {
            if (stream.Read(buffer, 0, 8) < 8) break;
            var chunkLen = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
            var typeStr = Encoding.ASCII.GetString(buffer, 4, 4);
            if (chunkLen < 0 || stream.Position + chunkLen + 4 > stream.Length) break;

            if (typeStr == "eXIf" && chunkLen >= 8)
            {
                var exifData = new byte[chunkLen];
                if (stream.Read(exifData, 0, chunkLen) == chunkLen)
                {
                    orientation = ParseExifOrientation(exifData, 0, chunkLen);
                }
                break;
            }
            else if (typeStr == "IDAT" || typeStr == "IEND")
            {
                break;
            }
            else
            {
                stream.Position += chunkLen + 4; // Skip data + 4 bytes CRC
            }
        }

        return ImageOperationResult<ImageHeaderInfo>.Success(
            new ImageHeaderInfo(width, height, FrameCount: 1, ExifOrientation: orientation));
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbeGif(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[10];
        if (stream.Read(buffer, 0, 10) < 10)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "GIF 文件过短。");
        }

        var sig = Encoding.ASCII.GetString(buffer, 0, 6);
        if (sig != "GIF87a" && sig != "GIF89a")
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 GIF 签名。");
        }

        var width = buffer[6] | (buffer[7] << 8);
        var height = buffer[8] | (buffer[9] << 8);

        if (width <= 0 || height <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "GIF 包含无效尺寸。");
        }

        return ImageOperationResult<ImageHeaderInfo>.Success(
            new ImageHeaderInfo(width, height, FrameCount: null, ExifOrientation: 1));
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbeBmp(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[32];
        if (stream.Read(buffer, 0, 18) < 18)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "BMP 文件过短。");
        }

        if (buffer[0] != 0x42 || buffer[1] != 0x4D) // 'BM'
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 BMP 签名。");
        }

        var headerSize = buffer[14] | (buffer[15] << 8) | (buffer[16] << 16) | (buffer[17] << 24);
        int width;
        int height;

        if (headerSize == 12) // BITMAPCOREHEADER
        {
            if (stream.Read(buffer, 0, 4) < 4)
            {
                return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "BITMAPCOREHEADER 不完整。");
            }
            width = buffer[0] | (buffer[1] << 8);
            height = buffer[2] | (buffer[3] << 8);
        }
        else if (headerSize >= 40) // BITMAPINFOHEADER and newer
        {
            if (stream.Read(buffer, 0, 8) < 8)
            {
                return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "BITMAPINFOHEADER 不完整。");
            }
            width = Math.Abs(buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24));
            height = Math.Abs(buffer[4] | (buffer[5] << 8) | (buffer[6] << 16) | (buffer[7] << 24));
        }
        else
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "不受支持的 BMP 头类型。");
        }

        if (width <= 0 || height <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "BMP 包含无效尺寸。");
        }

        return ImageOperationResult<ImageHeaderInfo>.Success(
            new ImageHeaderInfo(width, height, FrameCount: 1, ExifOrientation: 1));
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbeTiff(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[8];
        if (stream.Read(buffer, 0, 8) < 8)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "TIFF 文件过短。");
        }

        bool isLittleEndian;
        if (buffer[0] == 0x49 && buffer[1] == 0x49)
        {
            isLittleEndian = true;
        }
        else if (buffer[0] == 0x4D && buffer[1] == 0x4D)
        {
            isLittleEndian = false;
        }
        else
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 TIFF 字节序。");
        }

        int ReadUInt16(byte[] b, int offset) => isLittleEndian
            ? b[offset] | (b[offset + 1] << 8)
            : (b[offset] << 8) | b[offset + 1];

        int ReadUInt32(byte[] b, int offset) => isLittleEndian
            ? b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24)
            : (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

        var magic = ReadUInt16(buffer, 2);
        if (magic != 42)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 TIFF 魔数。");
        }

        var ifd0Offset = (uint)ReadUInt32(buffer, 4);
        if (ifd0Offset < 8 || ifd0Offset >= stream.Length)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "TIFF IFD0 偏移无效。");
        }

        stream.Position = ifd0Offset;
        if (stream.Read(buffer, 0, 2) < 2)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "TIFF 无法读取标签数。");
        }

        var tagCount = ReadUInt16(buffer, 0);
        int? width = null;
        int? height = null;
        var orientation = 1;

        var entryBuffer = new byte[12];
        for (int i = 0; i < tagCount; i++)
        {
            if (stream.Read(entryBuffer, 0, 12) < 12) break;
            var tagId = ReadUInt16(entryBuffer, 0);
            var tagType = ReadUInt16(entryBuffer, 2);

            int ReadTagValue()
            {
                if (tagType == 3) // SHORT
                {
                    return ReadUInt16(entryBuffer, 8);
                }
                if (tagType == 4) // LONG
                {
                    return ReadUInt32(entryBuffer, 8);
                }
                return 0;
            }

            if (tagId == 0x0100) // ImageWidth
            {
                width = ReadTagValue();
            }
            else if (tagId == 0x0101) // ImageLength
            {
                height = ReadTagValue();
            }
            else if (tagId == 0x0112) // Orientation
            {
                var val = ReadTagValue();
                if (val >= 1 && val <= 8)
                {
                    orientation = val;
                }
            }
        }

        if (!width.HasValue || !height.HasValue || width.Value <= 0 || height.Value <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "未能从 TIFF 解析出有效尺寸。");
        }

        int? frameCount = 1;
        if (stream.Read(buffer, 0, 4) == 4)
        {
            var nextIfdOffset = (uint)ReadUInt32(buffer, 0);
            if (nextIfdOffset != 0)
            {
                frameCount = null; // Multi-page TIFF, exact frame count determined after decode
            }
        }

        return ImageOperationResult<ImageHeaderInfo>.Success(
            new ImageHeaderInfo(width.Value, height.Value, frameCount, orientation));
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbeWebp(Stream stream)
    {
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "WebP 文件过短。");
        }

        var riffBytes = reader.ReadBytes(4);
        if (riffBytes.Length != 4 || Encoding.ASCII.GetString(riffBytes) != "RIFF")
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "WebP 缺少 RIFF 文件头。");
        }

        var riffSize = reader.ReadUInt32();
        var webpBytes = reader.ReadBytes(4);
        if (webpBytes.Length != 4 || Encoding.ASCII.GetString(webpBytes) != "WEBP" || riffSize + 8L > stream.Length)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "WebP RIFF 长度或格式标记无效。");
        }

        var width = 0;
        var height = 0;
        var frameCount = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkBytes = reader.ReadBytes(4);
            if (chunkBytes.Length != 4) break;
            var chunk = Encoding.ASCII.GetString(chunkBytes);
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;

            if (chunkSize > stream.Length - chunkStart)
            {
                return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "WebP 数据块长度超出文件边界。");
            }

            var chunkEnd = chunkStart + chunkSize;

            if (chunk == "VP8X" && chunkSize >= 10)
            {
                reader.ReadByte(); // flags
                reader.ReadBytes(3);
                var wBytes = reader.ReadBytes(3);
                var hBytes = reader.ReadBytes(3);
                if (wBytes.Length == 3 && hBytes.Length == 3)
                {
                    width = 1 + (wBytes[0] | (wBytes[1] << 8) | (wBytes[2] << 16));
                    height = 1 + (hBytes[0] | (hBytes[1] << 8) | (hBytes[2] << 16));
                }
            }
            else if (chunk == "VP8 " && chunkSize >= 10 && (width == 0 || height == 0))
            {
                var header = reader.ReadBytes(10);
                if (header.Length == 10 && header[3] == 0x9D && header[4] == 0x01 && header[5] == 0x2A)
                {
                    width = (header[6] | (header[7] << 8)) & 0x3FFF;
                    height = (header[8] | (header[9] << 8)) & 0x3FFF;
                }
            }
            else if (chunk == "VP8L" && chunkSize >= 5 && (width == 0 || height == 0))
            {
                var sig = reader.ReadByte();
                var bits = reader.ReadUInt32();
                if (sig == 0x2F)
                {
                    width = 1 + (int)(bits & 0x3FFF);
                    height = 1 + (int)((bits >> 14) & 0x3FFF);
                }
            }
            else if (chunk == "ANMF")
            {
                frameCount++;
            }

            stream.Position = chunkEnd + (chunkSize & 1);
        }

        if (width <= 0 || height <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "WebP 未包含有效的画布尺寸。");
        }

        return ImageOperationResult<ImageHeaderInfo>.Success(
            new ImageHeaderInfo(width, height, Math.Max(1, frameCount), ExifOrientation: 1));
    }

    private static ImageOperationResult<ImageHeaderInfo> ProbeIco(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[6];
        if (stream.Read(buffer, 0, 6) < 6)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "ICO 文件过短。");
        }

        var reserved = buffer[0] | (buffer[1] << 8);
        var type = buffer[2] | (buffer[3] << 8);
        var count = buffer[4] | (buffer[5] << 8);

        if (reserved != 0 || type != 1 || count <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "无效的 ICO 文件头。");
        }

        var maxWidth = 0;
        var maxHeight = 0;
        var entryBuffer = new byte[16];

        for (int i = 0; i < count; i++)
        {
            if (stream.Read(entryBuffer, 0, 16) < 16) break;
            var w = entryBuffer[0] == 0 ? 256 : (int)entryBuffer[0];
            var h = entryBuffer[1] == 0 ? 256 : (int)entryBuffer[1];
            if (w * h > maxWidth * maxHeight)
            {
                maxWidth = w;
                maxHeight = h;
            }
        }

        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return ImageOperationResult<ImageHeaderInfo>.Failed(ImageFailureKind.DecodeFailed, "未能从 ICO 解析出有效图标帧。");
        }

        return ImageOperationResult<ImageHeaderInfo>.Success(
            new ImageHeaderInfo(maxWidth, maxHeight, count, ExifOrientation: 1));
    }
}
