using System.Security.Cryptography;

namespace NanoPic.Core;

/// <summary>A content version, not a path-only lock. Atomic replace races retain the displaced version.</summary>
internal sealed class ImageFileVersion
{
    private readonly long _length;
    private readonly DateTime _writtenUtc;
    private readonly byte[] _hash;
    private ImageFileVersion(long length, DateTime writtenUtc, byte[] hash)
    { _length = length; _writtenUtc = writtenUtc; _hash = hash; }

    public static ImageFileVersion Capture(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(PortablePath.ForFileSystem(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        return Capture(stream, path, token);
    }

    public static ImageFileVersion Capture(FileStream stream, string path, CancellationToken token = default)
    {
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            return new ImageFileVersion(stream.Length, File.GetLastWriteTimeUtc(PortablePath.ForFileSystem(path)), Hash(stream, token));
        }
        finally { stream.Position = position; }
    }

    public bool Matches(FileStream stream, string path, CancellationToken token)
    {
        if (stream.Length != _length || File.GetLastWriteTimeUtc(PortablePath.ForFileSystem(path)) != _writtenUtc) return false;
        return MatchesContent(stream, token);
    }

    private bool MatchesContent(FileStream stream, CancellationToken token = default)
    {
        if (stream.Length != _length) return false;
        stream.Position = 0;
        return _hash.SequenceEqual(Hash(stream, token));
    }

    private static byte[] Hash(Stream stream, CancellationToken token)
    {
        using var hash = SHA256.Create();
        var buffer = new byte[65_536]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.TransformBlock(buffer, 0, count, buffer, 0);
        }
        token.ThrowIfCancellationRequested();
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hash.Hash!;
    }

    public void Replace(string temporaryPath, string destinationPath, CancellationToken token, Action? beforeAtomicReplace = null)
    {
        var target = PortablePath.ForFileSystem(destinationPath);
        // Deny in-place writers. Delete-sharing is required by File.Replace. Another
        // atomic renamer can still race; the displaced file is captured atomically below.
        using var guard = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!Matches(guard, target, token))
            throw new ImageFileVersionConflictException("文件在处理期间已被其他程序修改，已取消保存，未覆盖新版本。请重新导入后重试。");
        var backup = Path.Combine(Path.GetDirectoryName(target)!, ".nanopic-" + Guid.NewGuid().ToString("N") + ".nanopic-recovery");
        token.ThrowIfCancellationRequested();
        beforeAtomicReplace?.Invoke(); // deterministic race injection for local tests
        File.Replace(PortablePath.ForFileSystem(temporaryPath), target, backup, ignoreMetadataErrors: true);
        bool unchanged;
        try
        {
            using var displaced = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
            unchanged = MatchesContent(displaced);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ImageFileVersionConflictException("提交后无法验证被替换版本，已保留恢复文件，请检查：" + PortablePath.ForDisplay(backup));
        }
        if (!unchanged)
        {
            // Never roll back over another possible new edit. Keep the newer displaced
            // bytes under a unique recovery name and report the exact recovery location.
            throw new ImageFileVersionConflictException("文件在提交瞬间被其他程序更新。该版本已保留，请检查结果并恢复此文件：" + PortablePath.ForDisplay(backup));
        }
        try { File.Delete(backup); }
        catch (IOException) { /* A retained original is safer than failing a completed save. */ }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ImageFileVersionConflictException : IOException
{
    public ImageFileVersionConflictException(string message) : base(message) { }
}
