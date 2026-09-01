using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NanoPic.Core;
using NanoPic.Infrastructure;

namespace NanoPic.App;

/// <summary>导入请求的来源，用于日志与统计，不改变导入规则。</summary>
public enum ImportSource
{
    FilePicker = 0,
    FolderPicker,
    DragDrop,
    ShellContextMenu
}

/// <summary>单个路径未能进入队列的原因。</summary>
public enum ImportIssueKind
{
    AlreadyQueued = 0,
    DuplicateInRequest,
    PathNotFound,
    AccessDenied,
    UnsupportedFormat,
    EmptyFile,
    FileTooLarge,
    DirectoryNotAccepted,
    ItemPathUnavailable
}

public sealed record ImportIssue(string Path, ImportIssueKind Kind, string Message);

public sealed record ImportRequest(
    IReadOnlyList<string> Paths,
    ImportSource Source,
    bool AllowDirectories = true,
    int UnavailableItemCount = 0);

public sealed record ImportSummary(
    int Added,
    int Duplicated,
    IReadOnlyList<ImportIssue> Issues)
{
    public int Skipped => Issues.Count;

    /// <summary>没有任何文件进入队列，且确实有过输入。用于决定是否弹出一次汇总提示。</summary>
    public bool IsCompleteFailure => Added == 0 && Duplicated == 0 && Issues.Count > 0;

    public string BuildStatusText()
    {
        var text = new StringBuilder();
        text.Append($"已添加 {Added} 项");
        if (Duplicated > 0)
        {
            text.Append($"，重复 {Duplicated} 项");
        }

        if (Issues.Count > 0)
        {
            text.Append($"，跳过 {Issues.Count} 项");
        }

        return text.ToString();
    }

    /// <summary>按原因归类的一次性汇总文案，用于全部失败时的单次提示。</summary>
    public string BuildIssueSummary()
    {
        if (Issues.Count == 0)
        {
            return string.Empty;
        }

        var groups = Issues
            .GroupBy(issue => issue.Kind)
            .OrderBy(group => group.Key)
            .Select(group => $"{DescribeKind(group.Key)} {group.Count()} 项");
        return string.Join("；", groups);
    }

    public static string DescribeKind(ImportIssueKind kind) => kind switch
    {
        ImportIssueKind.AlreadyQueued => "队列中已存在",
        ImportIssueKind.DuplicateInRequest => "本次重复",
        ImportIssueKind.PathNotFound => "路径不存在",
        ImportIssueKind.AccessDenied => "无权限或读取失败",
        ImportIssueKind.UnsupportedFormat => "不支持的图片格式",
        ImportIssueKind.EmptyFile => "文件为空",
        ImportIssueKind.FileTooLarge => "文件超过 512 MB",
        ImportIssueKind.DirectoryNotAccepted => "不接收文件夹",
        ImportIssueKind.ItemPathUnavailable => "无法取得真实文件路径",
        _ => "已跳过"
    };
}

/// <summary>
/// 队列接收端。协调器只通过该接口读写队列，避免把 WPF 集合语义带进导入逻辑。
/// 实现方必须保证 <see cref="Contains"/> 与 <see cref="Add"/> 在同一线程上被顺序调用。
/// </summary>
public interface IQueueImportSink
{
    bool Contains(string normalizedPath);
    void Add(string path, long bytes);
}

/// <summary>
/// 所有导入入口（添加文件、添加文件夹、拖放、快捷键、Shell 右键菜单）唯一的汇入点。
/// 请求串行执行（FIFO），配合“进行中预留路径”集合保证并发调用不会重复加入同一路径。
/// </summary>
public sealed class QueueImportCoordinator
{
    public const long MaxFileBytes = 512L * 1024L * 1024L;

    private readonly SupportedImageFileScanner _scanner;
    private readonly IQueueImportSink _sink;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxFileBytes;

    public QueueImportCoordinator(SupportedImageFileScanner scanner, IQueueImportSink sink, long maxFileBytes = MaxFileBytes)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _maxFileBytes = maxFileBytes;
    }

    public Task<ImportSummary> ImportAsync(IEnumerable<string> paths, ImportSource source, CancellationToken cancellationToken) =>
        ImportAsync(new ImportRequest(paths?.ToArray() ?? Array.Empty<string>(), source), cancellationToken);

    public async Task<ImportSummary> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            return await ImportCoreAsync(request, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImportSummary> ImportCoreAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        var issues = new List<ImportIssue>();
        var added = 0;
        var duplicated = 0;

        for (var i = 0; i < request.UnavailableItemCount; i++)
        {
            issues.Add(new ImportIssue(string.Empty, ImportIssueKind.ItemPathUnavailable, "该项目没有可用的文件系统路径。"));
        }

        var requestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>();
        foreach (var raw in request.Paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!TryNormalize(raw, out var normalized))
            {
                issues.Add(new ImportIssue(raw, ImportIssueKind.PathNotFound, "路径格式无效。"));
                continue;
            }

            if (!requestPaths.Add(normalized))
            {
                duplicated++;
                continue;
            }

            targets.Add(normalized);
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isDirectory = Directory.Exists(PortablePath.ForFileSystem(target));
            if (isDirectory && !request.AllowDirectories)
            {
                issues.Add(new ImportIssue(target, ImportIssueKind.DirectoryNotAccepted, "该入口不接收文件夹。"));
                continue;
            }

            if (!isDirectory && !File.Exists(PortablePath.ForFileSystem(target)))
            {
                issues.Add(new ImportIssue(target, ImportIssueKind.PathNotFound, "文件不存在或已被移动。"));
                continue;
            }

            var scan = await _scanner
                .ScanAsync(target, new FileScanOptions(Recursive: isDirectory, MaxFileBytes: _maxFileBytes), cancellationToken)
                .ConfigureAwait(true);

            foreach (var issue in scan.Issues)
            {
                issues.Add(new ImportIssue(issue.Path, MapScanReason(issue.Reason), issue.Message));
            }

            foreach (var file in scan.Files)
            {
                if (!TryNormalize(file.Path, out var normalizedFile))
                {
                    issues.Add(new ImportIssue(file.Path, ImportIssueKind.PathNotFound, "路径格式无效。"));
                    continue;
                }

                if (_sink.Contains(normalizedFile))
                {
                    duplicated++;
                    continue;
                }

                // 预留窗口覆盖“扫描已产出但尚未落入队列”的瞬间；提交或失败后立即释放，
                // 使被移除或导入失败的路径稍后仍可重新导入。
                if (!_reserved.Add(normalizedFile))
                {
                    duplicated++;
                    continue;
                }

                try
                {
                    _sink.Add(file.Path, file.Bytes);
                    added++;
                }
                finally
                {
                    _reserved.Remove(normalizedFile);
                }
            }
        }

        return new ImportSummary(added, duplicated, issues);
    }

    private static ImportIssueKind MapScanReason(FileScanIssueReason reason) => reason switch
    {
        FileScanIssueReason.PathNotFound => ImportIssueKind.PathNotFound,
        FileScanIssueReason.AccessDenied => ImportIssueKind.AccessDenied,
        FileScanIssueReason.EmptyFile => ImportIssueKind.EmptyFile,
        FileScanIssueReason.FileTooLarge => ImportIssueKind.FileTooLarge,
        FileScanIssueReason.UnsupportedSignature => ImportIssueKind.UnsupportedFormat,
        _ => ImportIssueKind.AccessDenied
    };

    /// <summary>规范化为绝对路径，去掉扩展前缀与尾部分隔符，供 OrdinalIgnoreCase 去重使用。</summary>
    public static bool TryNormalize(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var display = PortablePath.ForDisplay(path.Trim());
            var full = Path.GetFullPath(display);
            if (full.Length > 3)
            {
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            normalized = full;
            return normalized.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return false;
        }
    }
}
