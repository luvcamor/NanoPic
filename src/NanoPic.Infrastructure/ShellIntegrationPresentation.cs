using System;
using System.Globalization;
using Microsoft.Win32;

namespace NanoPic.Infrastructure;

/// <summary>
/// 右键菜单集成的文案与平台提示。Windows 11 的经典菜单在“显示更多选项”里，
/// 不告诉用户这一点，最常见的反馈就是"开了但右键看不到"。
/// </summary>
public static class ShellIntegrationPresentation
{
    /// <summary>Windows 11 的第一个内部版本号。</summary>
    public const int Windows11MinimumBuild = 22000;

    public const string BaseDescription = "在图片右键菜单中显示“添加到 NanoPic”。";
    public const string Windows11EntryNote = "Windows 11 需在右键菜单中点“显示更多选项”查看。";

    private static readonly Lazy<bool> LazyIsWindows11OrLater = new(DetectWindows11OrLater);

    public static bool IsWindows11OrLater => LazyIsWindows11OrLater.Value;

    private static bool DetectWindows11OrLater()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            var build = key?.GetValue("CurrentBuildNumber") as string;
            if (build is not null &&
                int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buildNumber))
            {
                return buildNumber >= Windows11MinimumBuild;
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return Environment.OSVersion.Version.Major >= 10 &&
            Environment.OSVersion.Version.Build >= Windows11MinimumBuild;
    }

    /// <summary>设置项下方那一行浅色说明。</summary>
    public static string BuildHint(ShellIntegrationState state, bool isWindows11OrLater)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        return state.Status switch
        {
            ShellIntegrationStatus.NotInstalled => isWindows11OrLater
                ? BaseDescription + Windows11EntryNote
                : BaseDescription,
            ShellIntegrationStatus.InstalledCurrent => isWindows11OrLater
                ? "已启用。" + Windows11EntryNote
                : "已启用：选中图片后右键即可看到。",
            ShellIntegrationStatus.InstalledStale when state.HasOtherLivingCopy => "另一份 NanoPic 已集成。",
            ShellIntegrationStatus.InstalledStale => "右键菜单指向的程序路径已变化。",
            ShellIntegrationStatus.Partial or ShellIntegrationStatus.RecoveryPending => "右键菜单配置需要修复。",
            ShellIntegrationStatus.Conflict => "右键菜单配置存在冲突。",
            _ => BaseDescription
        };
    }
}
