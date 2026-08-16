namespace NanoPic.Core;

public static class PortablePath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string UncPrefix = @"\\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static string ForFileSystem(string path, bool forceExtendedPath = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
            path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ||
            (!forceExtendedPath && path.Length < 240))
        {
            return path;
        }

        var absolutePath = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        return absolutePath.StartsWith(UncPrefix, StringComparison.Ordinal)
            ? ExtendedUncPrefix + absolutePath.Substring(UncPrefix.Length)
            : ExtendedPrefix + absolutePath;
    }

    public static string ForDisplay(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UncPrefix + path.Substring(ExtendedUncPrefix.Length);
        }

        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? path.Substring(ExtendedPrefix.Length)
            : path;
    }
}
