namespace NanoPic.Core;

public static class ImageFileSignatureInspector
{
    private const int SignatureLength = 32;

    public static async Task<ImageOperationResult<ImageFormat>> DetectAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        if (!input.CanRead)
        {
            return ImageOperationResult<ImageFormat>.Failed(
                ImageFailureKind.FileAccessConflict,
                "无法读取图像输入流。");
        }

        var originalPosition = input.CanSeek ? input.Position : 0L;
        var buffer = new byte[SignatureLength];
        var bytesRead = 0;

        try
        {
            while (bytesRead < buffer.Length)
            {
                var count = await input.ReadAsync(
                    buffer,
                    bytesRead,
                    buffer.Length - bytesRead,
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
            }
        }
        catch (OperationCanceledException)
        {
            return ImageOperationResult<ImageFormat>.Failed(ImageFailureKind.TaskCanceled, "图像识别已取消。");
        }
        catch (IOException exception)
        {
            return ImageOperationResult<ImageFormat>.Failed(
                ImageFailureKind.FileAccessConflict,
                "读取图像文件时发生 I/O 错误。",
                exception);
        }
        finally
        {
            if (input.CanSeek)
            {
                input.Position = originalPosition;
            }
        }

        return Detect(buffer, bytesRead);
    }

    public static ImageOperationResult<ImageFormat> Detect(byte[] bytes) => Detect(bytes, bytes?.Length ?? 0);

    private static ImageOperationResult<ImageFormat> Detect(byte[] bytes, int length)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        if (length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Jpeg);
        }

        if (length >= 8 && Matches(bytes, 0, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Png);
        }

        if (length >= 12 &&
            MatchesAscii(bytes, 0, "RIFF") &&
            MatchesAscii(bytes, 8, "WEBP"))
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Webp);
        }

        if (length >= 6 &&
            (MatchesAscii(bytes, 0, "GIF87a") || MatchesAscii(bytes, 0, "GIF89a")))
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Gif);
        }

        if (length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Bmp);
        }

        if (length >= 4 &&
            ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
             (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A) ||
             (bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2B && bytes[3] == 0x00) ||
             (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2B)))
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Tiff);
        }

        if (length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
        {
            return ImageOperationResult<ImageFormat>.Success(ImageFormat.Ico);
        }

        return ImageOperationResult<ImageFormat>.Failed(
            ImageFailureKind.UnsupportedFormat,
            "文件签名不是受支持的图像格式。");
    }

    private static bool Matches(byte[] bytes, int offset, byte[] expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (bytes[offset + index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesAscii(byte[] bytes, int offset, string expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (bytes[offset + index] != (byte)expected[index])
            {
                return false;
            }
        }

        return true;
    }

    public static string GetCanonicalExtension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Webp => ".webp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tiff",
        ImageFormat.Ico => ".ico",
        _ => string.Empty
    };

    public static string GetOutputExtension(ImageFormat format, string sourcePath, bool preserveSourceExtension)
    {
        var canonicalExtension = GetCanonicalExtension(format);
        if (!preserveSourceExtension || string.IsNullOrEmpty(canonicalExtension))
        {
            return canonicalExtension;
        }

        // Only preserve extensions that match the detected format, never a misleading suffix.
        var sourceExtension = Path.GetExtension(sourcePath);
        var matchesFormat = format switch
        {
            ImageFormat.Jpeg => sourceExtension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif",
            ImageFormat.Tiff => sourceExtension.ToLowerInvariant() is ".tif" or ".tiff",
            _ => string.Equals(sourceExtension, canonicalExtension, StringComparison.OrdinalIgnoreCase)
        };

        return matchesFormat ? sourceExtension : canonicalExtension;
    }

    public static ImageFormat ToImageFormat(ImageOutputFormat outputFormat, ImageFormat sourceFormat) => outputFormat switch
    {
        ImageOutputFormat.Original => sourceFormat,
        ImageOutputFormat.Jpeg => ImageFormat.Jpeg,
        ImageOutputFormat.Png => ImageFormat.Png,
        ImageOutputFormat.Webp => ImageFormat.Webp,
        ImageOutputFormat.Gif => ImageFormat.Gif,
        ImageOutputFormat.Bmp => ImageFormat.Bmp,
        ImageOutputFormat.Tiff => ImageFormat.Tiff,
        ImageOutputFormat.Ico => ImageFormat.Ico,
        _ => ImageFormat.Unknown
    };
}
