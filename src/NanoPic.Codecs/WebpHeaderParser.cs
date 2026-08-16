using System;
using System.IO;
using System.Text;
using NanoPic.Core;

namespace NanoPic.Codecs;

internal static class WebpHeaderParser
{
    public static ImageMetadata Read(Stream input)
    {
        if (!input.CanSeek)
        {
            throw new NotSupportedException("WebP 元数据识别需要可定位的输入流。");
        }

        var originalPosition = input.Position;
        try
        {
            input.Position = 0;
            using var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
            if (ReadFourCc(reader) != "RIFF")
            {
                throw new InvalidDataException("WebP 缺少 RIFF 文件头。");
            }

            var riffSize = reader.ReadUInt32();
            if (ReadFourCc(reader) != "WEBP" || riffSize + 8L > input.Length)
            {
                throw new InvalidDataException("WebP RIFF 长度或格式标记无效。");
            }

            var width = 0;
            var height = 0;
            var hasAlpha = false;
            var frameCount = 0;
            while (input.Position + 8 <= input.Length)
            {
                var chunk = ReadFourCc(reader);
                var chunkSize = reader.ReadUInt32();
                var chunkStart = input.Position;
                var chunkEnd = checked(chunkStart + chunkSize);
                if (chunkEnd > input.Length)
                {
                    throw new InvalidDataException("WebP 数据块长度超出文件边界。");
                }

                if (chunk == "VP8X" && chunkSize >= 10)
                {
                    var flags = reader.ReadByte();
                    reader.ReadBytes(3);
                    width = 1 + ReadUInt24(reader);
                    height = 1 + ReadUInt24(reader);
                    hasAlpha |= (flags & 0x10) != 0;
                }
                else if (chunk == "VP8 " && chunkSize >= 10 && (width == 0 || height == 0))
                {
                    var header = reader.ReadBytes(10);
                    if (header.Length != 10 || header[3] != 0x9D || header[4] != 0x01 || header[5] != 0x2A)
                    {
                        throw new InvalidDataException("WebP VP8 帧头无效。");
                    }

                    width = (header[6] | header[7] << 8) & 0x3FFF;
                    height = (header[8] | header[9] << 8) & 0x3FFF;
                }
                else if (chunk == "VP8L" && chunkSize >= 5 && (width == 0 || height == 0))
                {
                    var signature = reader.ReadByte();
                    var bits = reader.ReadUInt32();
                    if (signature != 0x2F)
                    {
                        throw new InvalidDataException("WebP VP8L 帧头无效。");
                    }

                    width = 1 + (int)(bits & 0x3FFF);
                    height = 1 + (int)((bits >> 14) & 0x3FFF);
                    hasAlpha |= ((bits >> 28) & 1) != 0;
                }
                else if (chunk == "ALPH")
                {
                    hasAlpha = true;
                }
                else if (chunk == "ANMF")
                {
                    frameCount++;
                }

                input.Position = chunkEnd + (chunkSize & 1);
            }

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException("WebP 未包含有效的画布尺寸。");
            }

            return new ImageMetadata(
                ImageFormat.Webp,
                width,
                height,
                Math.Max(1, frameCount),
                hasAlpha,
                input.Length);
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException("图像文件头不完整。");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static int ReadUInt24(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(3);
        if (bytes.Length != 3)
        {
            throw new EndOfStreamException("WebP 尺寸字段不完整。");
        }

        return bytes[0] | bytes[1] << 8 | bytes[2] << 16;
    }
}
