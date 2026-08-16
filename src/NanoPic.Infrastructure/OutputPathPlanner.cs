using NanoPic.Core;

namespace NanoPic.Infrastructure;

public enum OutputDirectoryMode
{
    SourceDirectory = 0,
    SeparateDirectory,
    PreserveDirectoryStructure
}

public sealed record OutputPathPlanRequest(
    string SourcePath,
    string? InputRootDirectory,
    string? OutputRootDirectory,
    OutputDirectoryMode Mode,
    string FilenameTemplate,
    ImageFormat OutputFormat,
    int Index);

public static class OutputPathPlanner
{
    public static ImageOperationResult<string> Plan(OutputPathPlanRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (!File.Exists(request.SourcePath))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.FileAccessConflict, "输入文件不存在，无法规划输出路径。");
        }

        var fileName = OutputNameTemplate.Render(request.FilenameTemplate, request.SourcePath, request.OutputFormat, request.Index);
        if (!fileName.IsSuccess || fileName.Value is null)
        {
            return new ImageOperationResult<string>(default, fileName.Failure);
        }

        var sourceDirectory = Path.GetDirectoryName(request.SourcePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "输入文件路径缺少父目录。");
        }

        return request.Mode switch
        {
            OutputDirectoryMode.SourceDirectory => ImageOperationResult<string>.Success(Path.Combine(sourceDirectory, fileName.Value)),
            OutputDirectoryMode.SeparateDirectory => PlanSeparateDirectory(request.OutputRootDirectory, fileName.Value),
            OutputDirectoryMode.PreserveDirectoryStructure => PlanPreservedStructure(request, fileName.Value),
            _ => ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "未知的输出目录策略。")
        };
    }

    private static ImageOperationResult<string> PlanSeparateDirectory(string? outputRootDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(outputRootDirectory))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "独立输出目录不能为空。");
        }

        return ImageOperationResult<string>.Success(Path.Combine(outputRootDirectory, fileName));
    }

    private static ImageOperationResult<string> PlanPreservedStructure(OutputPathPlanRequest request, string fileName)
    {
        if (string.IsNullOrWhiteSpace(request.InputRootDirectory) || string.IsNullOrWhiteSpace(request.OutputRootDirectory))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "保留目录结构时必须指定输入根目录和输出根目录。");
        }

        var inputRoot = Path.GetFullPath(request.InputRootDirectory);
        var sourcePath = Path.GetFullPath(request.SourcePath);
        var relative = GetRelativePath(inputRoot, sourcePath);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative))
        {
            return ImageOperationResult<string>.Failed(ImageFailureKind.InvalidConfiguration, "输入文件不在指定的输入根目录内。");
        }

        var relativeDirectory = Path.GetDirectoryName(relative);
        var outputDirectory = string.IsNullOrEmpty(relativeDirectory)
            ? request.OutputRootDirectory
            : Path.Combine(request.OutputRootDirectory, relativeDirectory);
        return ImageOperationResult<string>.Success(Path.Combine(outputDirectory, fileName));
    }

    private static string GetRelativePath(string baseDirectory, string path)
    {
        var basePath = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var baseUri = new Uri(basePath, UriKind.Absolute);
        var pathUri = new Uri(path, UriKind.Absolute);
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }
}

public static class InputRootDirectoryResolver
{
    public static string? FindCommonDirectory(IEnumerable<string> paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));

        var directories = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.GetDirectoryName(path)
                ?? throw new ArgumentException("输入文件路径必须包含父目录。", nameof(paths))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (directories.Length == 0)
        {
            return null;
        }

        var root = directories[0];
        if (directories.Any(directory => !string.Equals(Path.GetPathRoot(directory), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        while (directories.Any(directory => !IsDirectoryWithin(directory, root)))
        {
            var parent = Directory.GetParent(root);
            if (parent is null)
            {
                return null;
            }

            root = parent.FullName;
        }

        return root;
    }

    private static bool IsDirectoryWithin(string directory, string root)
    {
        if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
