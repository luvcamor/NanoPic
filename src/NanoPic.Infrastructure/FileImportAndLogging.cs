using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NanoPic.Core;

namespace NanoPic.Infrastructure;

public sealed record FileScanOptions(bool Recursive, int MaxDepth = 16, long MaxFileBytes = 512L * 1024L * 1024L);
public sealed record SupportedImageFile(string Path, ImageFormat Format, long Bytes);

/// <summary>
/// 扫描器拒绝一个候选文件的具体原因。<see cref="FileScanIssue.Kind"/> 只表达粗粒度失败类别，
/// 无法区分“超过大小上限”“空文件”“签名不受支持”，导入层需要按具体原因分别汇总。
/// </summary>
public enum FileScanIssueReason
{
    Unspecified = 0,
    PathNotFound,
    AccessDenied,
    EmptyFile,
    FileTooLarge,
    UnsupportedSignature
}

public sealed record FileScanIssue(
    string Path,
    ImageFailureKind Kind,
    string Message,
    FileScanIssueReason Reason = FileScanIssueReason.Unspecified);

public sealed record FileScanResult(IReadOnlyList<SupportedImageFile> Files, IReadOnlyList<FileScanIssue> Issues);

public sealed class SupportedImageFileScanner
{
    public async Task<FileScanResult> ScanAsync(string rootPath, FileScanOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("路径不能为空。", nameof(rootPath));
        if (options is null) throw new ArgumentNullException(nameof(options));

        var files = new List<SupportedImageFile>();
        var issues = new List<FileScanIssue>();
        if (File.Exists(PortablePath.ForFileSystem(rootPath)))
        {
            await TryAddFileAsync(rootPath, options.MaxFileBytes, reportUnsupported: true, files, issues, cancellationToken).ConfigureAwait(false);
            return new FileScanResult(files, issues);
        }

        if (!Directory.Exists(PortablePath.ForFileSystem(rootPath)))
        {
            issues.Add(new FileScanIssue(
                rootPath,
                ImageFailureKind.FileAccessConflict,
                "导入路径不存在。",
                FileScanIssueReason.PathNotFound));
            return new FileScanResult(files, issues);
        }

        await ScanDirectoryAsync(rootPath, 0, options, files, issues, cancellationToken).ConfigureAwait(false);
        return new FileScanResult(files, issues);
    }

    private async Task ScanDirectoryAsync(
        string directory,
        int depth,
        FileScanOptions options,
        List<SupportedImageFile> files,
        List<FileScanIssue> issues,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > options.MaxDepth || IsReparsePoint(directory))
        {
            return;
        }

        string[] childFiles;
        string[] childDirectories;
        try
        {
            var fileSystemDirectory = PortablePath.ForFileSystem(directory);
            // 目录枚举是惰性的；在 try 内物化，确保 MoveNext 阶段的权限/IO 异常也会被记录成单项问题。
            childFiles = Directory.GetFiles(fileSystemDirectory);
            childDirectories = options.Recursive ? Directory.GetDirectories(fileSystemDirectory) : Array.Empty<string>();
        }
        catch (UnauthorizedAccessException exception)
        {
            issues.Add(new FileScanIssue(directory, ImageFailureKind.FileAccessConflict, exception.Message, FileScanIssueReason.AccessDenied));
            return;
        }
        catch (IOException exception)
        {
            issues.Add(new FileScanIssue(directory, ImageFailureKind.FileAccessConflict, exception.Message, FileScanIssueReason.AccessDenied));
            return;
        }

        foreach (var file in childFiles)
        {
            // 目录递归中不把非图片文件报成问题：普通文件夹本来就混有文档等无关文件。
            await TryAddFileAsync(file, options.MaxFileBytes, reportUnsupported: false, files, issues, cancellationToken).ConfigureAwait(false);
        }

        foreach (var childDirectory in childDirectories)
        {
            await ScanDirectoryAsync(childDirectory, depth + 1, options, files, issues, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryAddFileAsync(
        string path,
        long maxFileBytes,
        bool reportUnsupported,
        List<SupportedImageFile> files,
        List<FileScanIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IsReparsePoint(path))
            {
                return;
            }

            var fileSystemPath = PortablePath.ForFileSystem(path);
            var info = new FileInfo(fileSystemPath);
            if (info.Length <= 0)
            {
                issues.Add(new FileScanIssue(
                    path,
                    ImageFailureKind.UnsupportedFormat,
                    "文件内容为空。",
                    FileScanIssueReason.EmptyFile));
                return;
            }

            if (info.Length > maxFileBytes)
            {
                issues.Add(new FileScanIssue(
                    path,
                    ImageFailureKind.PixelBudgetExceeded,
                    $"文件大小超过 {maxFileBytes / (1024L * 1024L)} MB 安全上限。",
                    FileScanIssueReason.FileTooLarge));
                return;
            }

            using var stream = new FileStream(fileSystemPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, useAsync: true);
            var signature = await ImageFileSignatureInspector.DetectAsync(stream, cancellationToken).ConfigureAwait(false);
            if (signature.IsSuccess)
            {
                files.Add(new SupportedImageFile(PortablePath.ForDisplay(path), signature.Value, info.Length));
            }
            else if (reportUnsupported)
            {
                issues.Add(new FileScanIssue(
                    path,
                    ImageFailureKind.UnsupportedFormat,
                    signature.Failure?.UserMessage ?? "文件签名不是受支持的图片格式。",
                    FileScanIssueReason.UnsupportedSignature));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            issues.Add(new FileScanIssue(path, ImageFailureKind.FileAccessConflict, exception.Message, FileScanIssueReason.AccessDenied));
        }
        catch (FileNotFoundException exception)
        {
            issues.Add(new FileScanIssue(path, ImageFailureKind.FileAccessConflict, exception.Message, FileScanIssueReason.PathNotFound));
        }
        catch (DirectoryNotFoundException exception)
        {
            issues.Add(new FileScanIssue(path, ImageFailureKind.FileAccessConflict, exception.Message, FileScanIssueReason.PathNotFound));
        }
        catch (IOException exception)
        {
            issues.Add(new FileScanIssue(path, ImageFailureKind.FileAccessConflict, exception.Message, FileScanIssueReason.AccessDenied));
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(PortablePath.ForFileSystem(path)) & FileAttributes.ReparsePoint) != 0;
}

public static class OutputNameTemplate
{
    public static ImageOperationResult<string> Render(
        string template,
        string sourcePath,
        ImageFormat outputFormat,
        int index,
        bool preserveSourceExtension = false)
    {
        if (index < 1 || !File.Exists(sourcePath))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "输出命名模板、输入文件或序号无效。");
        }

        var extension = ImageFileSignatureInspector.GetOutputExtension(outputFormat, sourcePath, preserveSourceExtension);
        if (string.IsNullOrEmpty(extension))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.UnsupportedFormat, "输出格式没有可用的文件扩展名。");
        }

        var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? "{name}" : template;
        var hasDynamicToken =
            ContainsToken(effectiveTemplate, "{index}") ||
            ContainsToken(effectiveTemplate, "{name}") ||
            ContainsToken(effectiveTemplate, "{ext}") ||
            ContainsToken(effectiveTemplate, "{date}") ||
            ContainsToken(effectiveTemplate, "{time}");
        var fileTime = File.GetLastWriteTime(sourcePath);
        var value = ReplaceToken(effectiveTemplate, "{index}", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        value = ReplaceToken(value, "{name}", Path.GetFileNameWithoutExtension(sourcePath));
        value = ReplaceToken(value, "{ext}", extension.TrimStart('.'));
        value = ReplaceToken(value, "{date}", fileTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        value = ReplaceToken(value, "{time}", fileTime.ToString("HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture));
        if (!hasDynamicToken)
        {
            value = $"{value}_{index}";
        }
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "输出文件名包含不支持的字符。");
        }

        return ImageOperationResult<string>.Success(Path.ChangeExtension(value, extension));
    }

    private static string ReplaceToken(string value, string token, string replacement)
    {
        var searchIndex = 0;
        while (true)
        {
            var tokenIndex = value.IndexOf(token, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (tokenIndex < 0)
            {
                return value;
            }

            value = value.Remove(tokenIndex, token.Length).Insert(tokenIndex, replacement);
            searchIndex = tokenIndex + replacement.Length;
        }
    }

    private static bool ContainsToken(string value, string token) =>
        value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
}

public sealed class RedactingFileLogger
    {
        // 匹配 Windows 驱动器路径、UNC 路径（\\server\share 或 \\?\）、以及 Unix 绝对路径。
        // 路径段允许空格（Windows 用户目录普遍含空格）；引号与常见中文标点作为路径边界，
        // .NET 异常消息中带引号的路径可被精确截断；裸路径后的行内尾随文本可能一并被吞掉，
        // 属"宁多脱敏不泄露"的取舍，完整原文请开启 VerbosePaths。
        private static readonly Regex PathPattern = new(
            @"(?i)(?:\\\\(?:\?|\.)\\[^\r\n""'<>|，。；：！？]+|\\\\[a-z0-9_$.-]+\\[^\r\n""'<>|，。；：！？]+|[a-z]:\\[^\r\n""'<>|，。；：！？]+|/(?:Users|home|tmp|var|etc)/[^\r\n""'<>|，。；：！？]+)",
            RegexOptions.Compiled);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private const long MaxLogFileSizeBytes = 10L * 1024L * 1024L;

    public RedactingFileLogger(string logPath, bool verbosePaths = false)
    {
        LogPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
        VerbosePaths = verbosePaths;
    }

    public string LogPath { get; }
    public bool VerbosePaths { get; }

    public static string Redact(string message) =>
        string.IsNullOrEmpty(message) ? string.Empty : PathPattern.Replace(message, "<path>");

    public async Task WriteAsync(string level, string message, Exception? exception, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(level)) throw new ArgumentException("日志级别不能为空。", nameof(level));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("日志内容不能为空。", nameof(message));
        var directory = Path.GetDirectoryName(LogPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("日志路径必须包含有效目录。");
        }

        var detail = VerbosePaths ? message : Redact(message);
        if (exception is not null)
        {
            detail += VerbosePaths
                ? Environment.NewLine + exception
                : $" ({exception.GetType().Name})";
            if (!VerbosePaths && exception.Data["NanoPic.SafeDiagnostic"] is string safeDiagnostic &&
                !string.IsNullOrWhiteSpace(safeDiagnostic))
            {
                detail += $" [{Redact(safeDiagnostic)}]";
            }
        }

        var line = $"{DateTimeOffset.UtcNow:O}\t{level}\t{detail}{Environment.NewLine}";
        Directory.CreateDirectory(directory);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RotateIfNeeded(LogPath);
            await AppendWithRetryAsync(line, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 多实例共享同一日志文件：用 FileShare.ReadWrite 追加并在争用时短暂重试，
    /// 否则另一个进程写入期间的日志会被静默丢弃，事后无法复盘。
    /// </summary>
    private async Task AppendWithRetryAsync(string line, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    LogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(20 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogFileSizeBytes)
            {
                var backupPath = path + ".old";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(path, backupPath);
            }
        }
        catch
        {
            // 日志轮转为最佳努力，不阻止正常日志记录
        }
    }
}
