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
public sealed record FileScanIssue(string Path, ImageFailureKind Kind, string Message);
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
            await TryAddFileAsync(rootPath, options.MaxFileBytes, files, issues, cancellationToken).ConfigureAwait(false);
            return new FileScanResult(files, issues);
        }

        if (!Directory.Exists(PortablePath.ForFileSystem(rootPath)))
        {
            issues.Add(new FileScanIssue(rootPath, ImageFailureKind.FileAccessConflict, "导入路径不存在。"));
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

        IEnumerable<string> childFiles;
        IEnumerable<string> childDirectories;
        try
        {
            var fileSystemDirectory = PortablePath.ForFileSystem(directory);
            childFiles = Directory.EnumerateFiles(fileSystemDirectory);
            childDirectories = options.Recursive ? Directory.EnumerateDirectories(fileSystemDirectory) : Array.Empty<string>();
        }
        catch (UnauthorizedAccessException exception)
        {
            issues.Add(new FileScanIssue(directory, ImageFailureKind.FileAccessConflict, exception.Message));
            return;
        }
        catch (IOException exception)
        {
            issues.Add(new FileScanIssue(directory, ImageFailureKind.FileAccessConflict, exception.Message));
            return;
        }

        foreach (var file in childFiles)
        {
            await TryAddFileAsync(file, options.MaxFileBytes, files, issues, cancellationToken).ConfigureAwait(false);
        }

        foreach (var childDirectory in childDirectories)
        {
            await ScanDirectoryAsync(childDirectory, depth + 1, options, files, issues, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryAddFileAsync(
        string path,
        long maxFileBytes,
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
            if (info.Length <= 0 || info.Length > maxFileBytes)
            {
                return;
            }

            using var stream = new FileStream(fileSystemPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, useAsync: true);
            var signature = await ImageFileSignatureInspector.DetectAsync(stream, cancellationToken).ConfigureAwait(false);
            if (signature.IsSuccess)
            {
                files.Add(new SupportedImageFile(PortablePath.ForDisplay(path), signature.Value, info.Length));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            issues.Add(new FileScanIssue(path, ImageFailureKind.FileAccessConflict, exception.Message));
        }
        catch (IOException exception)
        {
            issues.Add(new FileScanIssue(path, ImageFailureKind.FileAccessConflict, exception.Message));
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
    // 匹配 Windows 驱动器路径、UNC 路径（\\server\share 或 \\?\）、以及 Unix 绝对路径
    private static readonly Regex PathPattern = new(
        @"(?i)(?:\\\\(?:\?|\.)\\[^\s\r\n""'<>|]+|\\\\[a-z0-9_$.-]+\\[^\s\r\n""'<>|]+|[a-z]:\\[^\s\r\n""'<>|]+|/(?:Users|home|tmp|var|etc)/[^\s\r\n""'<>|]+)",
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
        }

        var line = $"{DateTimeOffset.UtcNow:O}\t{level}\t{detail}{Environment.NewLine}";
        Directory.CreateDirectory(directory);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RotateIfNeeded(LogPath);

            using (var writer = new StreamWriter(LogPath, append: true, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _writeLock.Release();
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
