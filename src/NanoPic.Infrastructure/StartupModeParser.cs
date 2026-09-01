using System;
using System.Collections.Generic;

namespace NanoPic.Infrastructure;

public enum NanoPicStartupMode
{
    /// <summary>普通交互启动：创建并显示主窗口。</summary>
    Normal = 0,
    /// <summary><c>--smoke-test &lt;input&gt; &lt;output&gt;</c>：无 UI，保留既有退出码语义。</summary>
    SmokeTest = 1,
    /// <summary>Windows 附加 <c>-Embedding</c> 激活 COM LocalServer32：注册 class factory，不显示空窗口。</summary>
    ComEmbedding = 2,
    /// <summary>未知开关或已知模式的错误参数组合：绝不退化成普通窗口。</summary>
    Invalid = 3
}

public sealed record StartupModeDecision(
    NanoPicStartupMode Mode,
    string? SmokeTestInput = null,
    string? SmokeTestOutput = null,
    string? Diagnostic = null,
    int ExitCode = 0);

/// <summary>
/// 启动参数先解析为互斥模式再执行，避免“未知开关静默打开主窗口”这类误入。
/// </summary>
public static class StartupModeParser
{
    public const string SmokeTestSwitch = "--smoke-test";

    /// <summary>命令行用法错误的退出码，与既有 smoke-test 协议保持一致。</summary>
    public const int UsageExitCode = 64;

    private static readonly string[] EmbeddingSwitches = { "-Embedding", "/Embedding", "--Embedding" };

    public static StartupModeDecision Parse(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new StartupModeDecision(NanoPicStartupMode.Normal);
        }

        if (string.Equals(arguments[0], SmokeTestSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return arguments.Count == 3
                ? new StartupModeDecision(NanoPicStartupMode.SmokeTest, arguments[1], arguments[2])
                : new StartupModeDecision(
                    NanoPicStartupMode.Invalid,
                    Diagnostic: "--smoke-test 需要恰好两个路径参数。",
                    ExitCode: UsageExitCode);
        }

        var hasEmbedding = false;
        var unexpectedSwitch = (string?)null;
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (IsEmbeddingSwitch(argument))
            {
                hasEmbedding = true;
                continue;
            }

            if (IsSwitch(argument))
            {
                unexpectedSwitch ??= argument;
            }
        }

        if (unexpectedSwitch is not null)
        {
            return new StartupModeDecision(
                NanoPicStartupMode.Invalid,
                Diagnostic: $"无法识别的启动参数：{unexpectedSwitch}。",
                ExitCode: UsageExitCode);
        }

        return hasEmbedding
            ? new StartupModeDecision(NanoPicStartupMode.ComEmbedding)
            : new StartupModeDecision(NanoPicStartupMode.Normal);
    }

    private static bool IsEmbeddingSwitch(string argument)
    {
        foreach (var candidate in EmbeddingSwitches)
        {
            if (string.Equals(argument, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSwitch(string argument) =>
        argument.Length > 1 && (argument[0] == '-' || (argument[0] == '/' && argument.IndexOf('\\') < 0 && argument.IndexOf('/', 1) < 0));
}
