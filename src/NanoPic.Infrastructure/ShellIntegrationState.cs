using System;
using System.Collections.Generic;

namespace NanoPic.Infrastructure;

/// <summary>注册表中右键菜单集成的整体状态。UI 可以简化显示，但服务层必须返回完整状态。</summary>
public enum ShellIntegrationStatus
{
    /// <summary>私有元数据与目标键都不存在。</summary>
    NotInstalled = 0,
    /// <summary>11 个 Verb、CLSID、owner、schema、事务与当前 exe 路径全部一致。</summary>
    InstalledCurrent,
    /// <summary>所有权完整，但目标 exe 路径与当前不同或目标不存在。</summary>
    InstalledStale,
    /// <summary>所有权可证明属于 NanoPic，但键、值或扩展集合不完整。</summary>
    Partial,
    /// <summary>同名 Verb/CLSID 存在但所有权无法证明，禁止自动接管与删除。</summary>
    Conflict,
    /// <summary>私有元数据表明安装、修复或移除在中途终止。</summary>
    RecoveryPending
}

public enum ShellIntegrationOperationState
{
    None = 0,
    Installing,
    Installed,
    Repairing,
    Removing
}

/// <summary>单条诊断信息：定位到具体注册表位置，供“复制诊断信息”使用。</summary>
public sealed record ShellIntegrationDiagnostic(string Location, string Message, bool IsConflict);

public sealed record ShellIntegrationState(
    ShellIntegrationStatus Status,
    ShellIntegrationOperationState OperationState,
    string? TargetExePath,
    bool TargetExeExists,
    string? ProductVersion,
    string? TransactionId,
    int RegisteredExtensionCount,
    IReadOnlyList<string> MissingExtensions,
    IReadOnlyList<ShellIntegrationDiagnostic> Diagnostics)
{
    public int ExpectedExtensionCount => ShellIntegrationContract.SupportedExtensions.Count;

    /// <summary>另一份 NanoPic 副本仍然存在：禁止自动接管，必须由用户明确切换。</summary>
    public bool HasOtherLivingCopy => Status == ShellIntegrationStatus.InstalledStale && TargetExeExists;

    /// <summary>只有这两种状态允许直接切换开关。</summary>
    public bool AllowsDirectToggle =>
        Status is ShellIntegrationStatus.NotInstalled or ShellIntegrationStatus.InstalledCurrent;

    public bool IsEnabled => Status is ShellIntegrationStatus.InstalledCurrent;

    public string BuildDiagnosticReport()
    {
        var lines = new List<string>
        {
            $"状态：{Status}",
            $"操作状态：{OperationState}",
            $"已注册扩展：{RegisteredExtensionCount}/{ExpectedExtensionCount}",
            $"目标程序：{TargetExePath ?? "（无）"}（存在：{(TargetExeExists ? "是" : "否")}）",
            $"产品版本：{ProductVersion ?? "（无）"}",
            $"事务 ID：{TransactionId ?? "（无）"}",
            $"CLSID：{ShellIntegrationContract.DropTargetClsidKey}"
        };

        if (MissingExtensions.Count > 0)
        {
            lines.Add("缺失扩展：" + string.Join("、", MissingExtensions));
        }

        foreach (var diagnostic in Diagnostics)
        {
            lines.Add($"[{(diagnostic.IsConflict ? "冲突" : "提示")}] HKCU\\{diagnostic.Location}：{diagnostic.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed record ShellIntegrationOperationResult(
    bool Succeeded,
    ShellIntegrationState State,
    string? UserMessage = null);
