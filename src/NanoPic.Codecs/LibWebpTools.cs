using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace NanoPic.Codecs;

public static class LibWebpCodecEngine
{
    public const string Version = "1.6.0";
    public const string SourceUrl = "https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0-windows-x64.zip";
    public const string ArchiveSha256 = "48886F506B21F62E4661F0F4CBFCA19800897C385128E8902542D29A950C93F1";

    public static string ProbeVersion() => LibWebpTools.ProbeVersion(CancellationToken.None);
}

internal sealed class LibWebpToolException : Exception
{
    public LibWebpToolException(string message)
        : base(message)
    {
    }

    public LibWebpToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class LibWebpTools
{
    private const string CwebpResource = "NanoPic.Codecs.Native.libwebp.cwebp.exe";
    private const string DwebpResource = "NanoPic.Codecs.Native.libwebp.dwebp.exe";
    private const string CwebpSha256 = "6A2F5CB5DCE71366353AB1D9CAF9C636E039F25703ACFCE1C148EED346F2F72A";
    private const string DwebpSha256 = "17C1488BF84B7834E9AA908BB40AFD0BE2E55D57567D210E96336C56BB6AE993";
    private static readonly object ExtractionLock = new();

    public static void DecodeToPng(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var tools = EnsureExtracted();
        Run(
            tools.Dwebp,
            Quote(inputPath) + " -quiet -o " + Quote(outputPath),
            cancellationToken,
            "libwebp 无法解码 WebP 图像。");
    }

    public static void EncodeFromPng(
        string inputPath,
        string outputPath,
        int quality,
        CancellationToken cancellationToken)
    {
        var tools = EnsureExtracted();
        var arguments = string.Format(
            CultureInfo.InvariantCulture,
            "-quiet -mt -m 4 -q {0} -metadata none {1} -o {2}",
            quality,
            Quote(inputPath),
            Quote(outputPath));
        Run(tools.Cwebp, arguments, cancellationToken, "libwebp 无法编码 WebP 图像。");
    }

    public static string ProbeVersion(CancellationToken cancellationToken)
    {
        var tools = EnsureExtracted();
        return Run(tools.Cwebp, "-version", cancellationToken, "无法读取 libwebp 版本。").Trim();
    }

    private static (string Cwebp, string Dwebp) EnsureExtracted()
    {
        lock (ExtractionLock)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[]
            {
                Path.Combine(localAppData, "NanoPic", "Codecs", "libwebp-" + LibWebpCodecEngine.Version),
                Path.Combine(Path.GetTempPath(), "NanoPic", "Codecs", "libwebp-" + LibWebpCodecEngine.Version)
            };

            Exception? lastFailure = null;
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(root);
                    var cwebp = Path.Combine(root, "cwebp.exe");
                    var dwebp = Path.Combine(root, "dwebp.exe");
                    ExtractVerified(CwebpResource, CwebpSha256, cwebp);
                    ExtractVerified(DwebpResource, DwebpSha256, dwebp);
                    return (cwebp, dwebp);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    lastFailure = exception;
                }
            }

            throw new LibWebpToolException("无法释放 libwebp 运行文件。", lastFailure ?? new IOException("没有可写的 codec 缓存目录。"));
        }
    }

    private static void ExtractVerified(string resourceName, string expectedSha256, string destinationPath)
    {
        if (File.Exists(destinationPath) && string.Equals(Hash(destinationPath), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new LibWebpToolException("发布物缺少内嵌 libwebp 资源：" + resourceName))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!string.Equals(Hash(temporaryPath), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("内嵌 libwebp 文件哈希校验失败。");
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Run(
        string executablePath,
        string arguments,
        CancellationToken cancellationToken,
        string failureMessage)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new LibWebpToolException(failureMessage);
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            while (!process.WaitForExit(100))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            var output = standardOutput.GetAwaiter().GetResult();
            var error = standardError.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new LibWebpToolException(failureMessage + " " + Sanitize(error));
            }

            return output;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new LibWebpToolException(failureMessage, exception);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private static string Sanitize(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine.Substring(0, 300);
    }
}
