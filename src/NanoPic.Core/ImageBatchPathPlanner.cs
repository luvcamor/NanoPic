using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace NanoPic.Core;

// Plan the whole batch before any writer starts. Per-destination locks alone
// cannot stop one job from overwriting a later job's input.
internal static class ImageBatchPathPlanner
{
    public static ImageOperationResult<ImageFileProcessRequest>[] Plan(
        IReadOnlyList<ImageFileProcessRequest> requests, CancellationToken token, IReadOnlyList<string>? protectedSources = null)
    {
        var planned = new ImageOperationResult<ImageFileProcessRequest>[requests.Count];
        var sources = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < requests.Count; i++)
        {
            if (token.IsCancellationRequested) return Canceled(requests.Count);
            try
            {
                AddOwner(sources, Canonical(requests[i].SourcePath), i);
                var identity = Identity(requests[i].SourcePath);
                if (identity is not null) AddOwner(sources, identity, i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { planned[i] = ImageOperationResult<ImageFileProcessRequest>.Failed(ImageFailureKind.FileAccessConflict, "无法确认输入文件路径。", ex); }
        }

        // The UI can also protect selected inputs that failed earlier path planning.
        if (protectedSources is not null)
        {
            foreach (var source in protectedSources)
            {
                if (token.IsCancellationRequested) return Canceled(requests.Count);
                try
                {
                    var key = Canonical(source);
                    if (sources.ContainsKey(key)) continue;
                    AddOwner(sources, key, -1);
                    var identity = Identity(source);
                    if (identity is not null) AddOwner(sources, identity, -1);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return Enumerable.Range(0, requests.Count).Select(_ => ImageOperationResult<ImageFileProcessRequest>.Failed(
                        ImageFailureKind.FileAccessConflict, "无法确认某张原图是否会被覆盖，请先移除不可读取的输入。", ex)).ToArray();
                }
            }
        }

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < requests.Count; i++)
        {
            if (token.IsCancellationRequested) return Canceled(requests.Count);
            if (planned[i] is not null) continue;
            try
            {
                var request = requests[i];
                var format = request.Encoding.OutputFormat switch
                {
                    ImageOutputFormat.Jpeg => ImageFormat.Jpeg, ImageOutputFormat.Png => ImageFormat.Png,
                    ImageOutputFormat.Webp => ImageFormat.Webp, ImageOutputFormat.Gif => ImageFormat.Gif,
                    ImageOutputFormat.Bmp => ImageFormat.Bmp, ImageOutputFormat.Tiff => ImageFormat.Tiff,
                    ImageOutputFormat.Ico => ImageFormat.Ico, _ => ImageFormat.Unknown
                };
                if (request.Encoding.OutputFormat == ImageOutputFormat.Original)
                {
                    using var file = File.OpenRead(PortablePath.ForFileSystem(request.SourcePath));
                    var header = new byte[32]; var read = file.Read(header, 0, header.Length);
                    if (read < header.Length) Array.Resize(ref header, read);
                    var detected = ImageFileSignatureInspector.Detect(header);
                    if (!detected.IsSuccess) { planned[i] = new ImageOperationResult<ImageFileProcessRequest>(default, detected.Failure); continue; }
                    format = detected.Value;
                }
                if (format == ImageFormat.Unknown)
                {
                    planned[i] = ImageOperationResult<ImageFileProcessRequest>.Failed(ImageFailureKind.InvalidConfiguration, "无法规划所选输出格式。");
                    continue;
                }
                var destination = Canonical(ImageFileProcessingService.ApplyOutputExtension(request, format));
                bool OtherSource(string path) => sources.TryGetValue(path, out var owners) && owners.Any(owner => owner != i) ||
                    Identity(path) is { } identity && sources.TryGetValue(identity, out owners) && owners.Any(owner => owner != i);
                if (request.ConflictPolicy == OutputConflictPolicy.AutoRename)
                {
                    var original = destination;
                    for (var number = 0; File.Exists(PortablePath.ForFileSystem(destination)) || Directory.Exists(PortablePath.ForFileSystem(destination)) ||
                         reserved.Contains(destination) || OtherSource(destination);)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++number > 10000) throw new IOException("无法规划不冲突的输出文件名。");
                        destination = Path.Combine(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(original) + "_" + number + Path.GetExtension(original));
                    }
                }
                else if (request.ConflictPolicy == OutputConflictPolicy.Skip && File.Exists(PortablePath.ForFileSystem(destination)))
                {
                    // A skipped existing file is not a write. Preserve the user's
                    // skip policy even when that file is another protected input.
                }
                else if (OtherSource(destination))
                {
                    planned[i] = ImageOperationResult<ImageFileProcessRequest>.Failed(ImageFailureKind.FileAccessConflict,
                        "输出会覆盖本批次另一张原图，已阻止处理。请另存到其他文件夹，或选择自动重命名。");
                    continue;
                }
                else if (reserved.Contains(destination) && request.ConflictPolicy != OutputConflictPolicy.Skip)
                {
                    planned[i] = ImageOperationResult<ImageFileProcessRequest>.Failed(ImageFailureKind.FileAccessConflict,
                        "本批次有多张图片使用同一个输出文件名，已阻止覆盖。请选择自动重命名或使用序号命名。");
                    continue;
                }
                reserved.Add(destination);
                planned[i] = ImageOperationResult<ImageFileProcessRequest>.Success(request with { DestinationPath = destination });
            }
            catch (OperationCanceledException) { return Canceled(requests.Count); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { planned[i] = ImageOperationResult<ImageFileProcessRequest>.Failed(ImageFailureKind.FileAccessConflict, "无法安全规划输出文件。", ex); }
        }
        return planned;
    }

    private static ImageOperationResult<ImageFileProcessRequest>[] Canceled(int count) => Enumerable.Range(0, count).Select(_ =>
        ImageOperationResult<ImageFileProcessRequest>.Failed(ImageFailureKind.TaskCanceled, "批处理已取消，任务未开始。")).ToArray();
    private static void AddOwner(Dictionary<string, HashSet<int>> owners, string key, int index)
    { if (!owners.TryGetValue(key, out var set)) owners.Add(key, set = new HashSet<int>()); set.Add(index); }
    private static string Canonical(string path) => PortablePath.ForDisplay(Path.GetFullPath(PortablePath.ForFileSystem(PortablePath.ForDisplay(path))));

    private static string? Identity(string path) => ImageFileIdentity.Get(path);
}
