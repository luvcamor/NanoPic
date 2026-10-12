using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NanoPic.Core;

/// <summary>Handle identity for source protection, shared with batch planning.</summary>
internal static class ImageFileIdentity
{
    public static string? Get(string path)
    {
        if (!File.Exists(PortablePath.ForFileSystem(path)) || Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
        using var stream = new FileStream(PortablePath.ForFileSystem(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Get(stream);
    }

    public static string Get(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("无法确认文件身份，已停止覆盖检查。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return "file-id:" + info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
    }

    public static bool SameFile(string left, string right)
    {
        if (string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase)) return true;
        var identity = Get(left);
        // Conservatively apply original-content protection to hardlinks as well.
        // Never redirect File.Replace to another name: only the requested entry is replaced.
        return identity is not null && identity == Get(right);
    }

    public static bool SameFile(FileStream source, string sourcePath, string destinationPath) =>
        string.Equals(Canonical(sourcePath), Canonical(destinationPath), StringComparison.OrdinalIgnoreCase) ||
        Get(source) == Get(destinationPath);

    private static string Canonical(string path) => PortablePath.ForDisplay(Path.GetFullPath(PortablePath.ForFileSystem(path)));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
