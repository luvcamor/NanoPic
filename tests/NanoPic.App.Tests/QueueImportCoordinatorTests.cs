using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NanoPic.App;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.App.Tests;

public sealed class QueueImportCoordinatorTests : IDisposable
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegSignature = { 0xFF, 0xD8, 0xFF, 0xE0 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nanopic-import-" + Guid.NewGuid().ToString("N"));

    public QueueImportCoordinatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecordingSink : IQueueImportSink
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Added { get; } = new();

        public bool Contains(string normalizedPath) => _paths.Contains(normalizedPath);

        public void Add(string path, long bytes)
        {
            Added.Add(path);
            Assert.True(QueueImportCoordinator.TryNormalize(path, out var normalized));
            _paths.Add(normalized);
        }

        public void Seed(string path)
        {
            Assert.True(QueueImportCoordinator.TryNormalize(path, out var normalized));
            _paths.Add(normalized);
        }
    }

    private string WriteImage(string name, byte[] signature, int totalBytes = 96)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[totalBytes];
        Array.Copy(signature, bytes, signature.Length);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static QueueImportCoordinator CreateCoordinator(RecordingSink sink, long maxFileBytes = QueueImportCoordinator.MaxFileBytes) =>
        new(new SupportedImageFileScanner(), sink, maxFileBytes);

    [Fact]
    public async Task A1_SameRequestDuplicatePathIsAddedOnce()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var file = WriteImage("photo.png", PngSignature);

        var summary = await coordinator.ImportAsync(
            new ImportRequest(new[] { file, file }, ImportSource.FilePicker),
            CancellationToken.None);

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Duplicated);
        Assert.Single(sink.Added);
    }

    [Fact]
    public async Task A2_ConcurrentRequestsWithSamePathAddOnce()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var file = WriteImage("shared.png", PngSignature);

        var first = coordinator.ImportAsync(new ImportRequest(new[] { file }, ImportSource.DragDrop), CancellationToken.None);
        var second = coordinator.ImportAsync(new ImportRequest(new[] { file }, ImportSource.ShellContextMenu), CancellationToken.None);
        var summaries = await Task.WhenAll(first, second);

        Assert.Single(sink.Added);
        Assert.Equal(1, summaries.Sum(summary => summary.Added));
        Assert.Equal(1, summaries.Sum(summary => summary.Duplicated));
    }

    [Fact]
    public async Task A3_PathAlreadyInQueueCountsAsDuplicate()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var file = WriteImage("queued.png", PngSignature);
        sink.Seed(file);

        var summary = await coordinator.ImportAsync(new ImportRequest(new[] { file }, ImportSource.FilePicker), CancellationToken.None);

        Assert.Equal(0, summary.Added);
        Assert.Equal(1, summary.Duplicated);
        Assert.Empty(sink.Added);
    }

    [Fact]
    public async Task A4_CaseAndSeparatorVariantsAreTheSamePath()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var file = WriteImage("图片 与 符号 & (1).jpg", JpegSignature);
        var variant = file.ToUpperInvariant().Replace('\\', '/');

        var summary = await coordinator.ImportAsync(
            new ImportRequest(new[] { file, variant }, ImportSource.FilePicker),
            CancellationToken.None);

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Duplicated);
    }

    [Fact]
    public async Task A5_MissingPathIsReportedAndCanBeRetriedAfterCreation()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var target = Path.Combine(_root, "later.png");

        var first = await coordinator.ImportAsync(new ImportRequest(new[] { target }, ImportSource.ShellContextMenu), CancellationToken.None);
        Assert.Equal(0, first.Added);
        Assert.Equal(ImportIssueKind.PathNotFound, Assert.Single(first.Issues).Kind);
        Assert.True(first.IsCompleteFailure);

        WriteImage("later.png", PngSignature);
        var second = await coordinator.ImportAsync(new ImportRequest(new[] { target }, ImportSource.ShellContextMenu), CancellationToken.None);

        Assert.Equal(1, second.Added);
        Assert.Empty(second.Issues);
    }

    [Fact]
    public async Task A6_OversizedFileIsClassifiedAsTooLargeNotUnsupported()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink, maxFileBytes: 64);
        var file = WriteImage("big.png", PngSignature, totalBytes: 4096);

        var summary = await coordinator.ImportAsync(new ImportRequest(new[] { file }, ImportSource.FilePicker), CancellationToken.None);

        Assert.Equal(0, summary.Added);
        Assert.Equal(ImportIssueKind.FileTooLarge, Assert.Single(summary.Issues).Kind);
    }

    [Fact]
    public async Task A7_EmptyAndUnsupportedFilesAreClassifiedSeparately()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var empty = Path.Combine(_root, "empty.png");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        var text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "not an image at all, definitely not an image header");

        var summary = await coordinator.ImportAsync(new ImportRequest(new[] { empty, text }, ImportSource.FilePicker), CancellationToken.None);

        Assert.Equal(0, summary.Added);
        Assert.Contains(summary.Issues, issue => issue.Kind == ImportIssueKind.EmptyFile);
        Assert.Contains(summary.Issues, issue => issue.Kind == ImportIssueKind.UnsupportedFormat);
    }

    [Fact]
    public async Task A8_DirectoryImportIsRecursiveButRejectedForShellRequests()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        WriteImage(Path.Combine("nested", "a.png"), PngSignature);
        WriteImage(Path.Combine("nested", "deep", "b.jpg"), JpegSignature);
        var directory = Path.Combine(_root, "nested");

        var folderSummary = await coordinator.ImportAsync(
            new ImportRequest(new[] { directory }, ImportSource.FolderPicker),
            CancellationToken.None);
        Assert.Equal(2, folderSummary.Added);

        var shellSummary = await coordinator.ImportAsync(
            new ImportRequest(new[] { directory }, ImportSource.ShellContextMenu, AllowDirectories: false),
            CancellationToken.None);
        Assert.Equal(0, shellSummary.Added);
        Assert.Equal(ImportIssueKind.DirectoryNotAccepted, Assert.Single(shellSummary.Issues).Kind);
    }

    [Fact]
    public async Task A9_DirectoryScanDoesNotReportUnrelatedFilesAsIssues()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        WriteImage(Path.Combine("mixed", "a.png"), PngSignature);
        File.WriteAllText(Path.Combine(_root, "mixed", "readme.txt"), "documentation only");

        var summary = await coordinator.ImportAsync(
            new ImportRequest(new[] { Path.Combine(_root, "mixed") }, ImportSource.FolderPicker),
            CancellationToken.None);

        Assert.Equal(1, summary.Added);
        Assert.Empty(summary.Issues);
    }

    [Fact]
    public async Task A10_UnavailableShellItemsAreSummarizedOnce()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var file = WriteImage("real.png", PngSignature);

        var summary = await coordinator.ImportAsync(
            new ImportRequest(new[] { file }, ImportSource.ShellContextMenu, AllowDirectories: false, UnavailableItemCount: 2),
            CancellationToken.None);

        Assert.Equal(1, summary.Added);
        Assert.Equal(2, summary.Issues.Count(issue => issue.Kind == ImportIssueKind.ItemPathUnavailable));
        Assert.Equal("已添加 1 项，跳过 2 项", summary.BuildStatusText());
        Assert.Contains("无法取得真实文件路径 2 项", summary.BuildIssueSummary());
    }

    [Fact]
    public async Task A11_LargeSelectionAddsEveryFileExactlyOnce()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var files = Enumerable.Range(0, 200)
            .Select(index => WriteImage(Path.Combine("bulk", $"file-{index}.png"), PngSignature))
            .ToArray();

        var summary = await coordinator.ImportAsync(new ImportRequest(files, ImportSource.ShellContextMenu, AllowDirectories: false), CancellationToken.None);

        Assert.Equal(200, summary.Added);
        Assert.Equal(200, sink.Added.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task A12_RedundantSegmentsResolveToTheSamePath()
    {
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(sink);
        var file = WriteImage("relative.png", PngSignature);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var viaParentSegment = Path.Combine(_root, "sub", "..", "relative.png");
        var withDotSegment = Path.Combine(_root, ".", "relative.png");

        var summary = await coordinator.ImportAsync(
            new ImportRequest(new[] { file, viaParentSegment, withDotSegment }, ImportSource.FilePicker),
            CancellationToken.None);

        Assert.Equal(1, summary.Added);
        Assert.Equal(2, summary.Duplicated);
    }
}
