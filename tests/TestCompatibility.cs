using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class TestCompatibility
{
    static TestCompatibility()
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", isEnabled: false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", isEnabled: false);
    }

    public static DirectoryInfo CreateTempSubdirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        return Directory.CreateDirectory(path);
    }

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllText(path, contents);
        return Task.CompletedTask;
    }

    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.ReadAllText(path));
    }

    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllBytes(path, bytes);
        return Task.CompletedTask;
    }

    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.ReadAllBytes(path));
    }

    public static string ToHexString(byte[] bytes, int offset = 0, int? count = null)
    {
        return BitConverter.ToString(bytes, offset, count ?? bytes.Length - offset).Replace("-", string.Empty);
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    public static bool IsWindows() => Environment.OSVersion.Platform == PlatformID.Win32NT;
}
