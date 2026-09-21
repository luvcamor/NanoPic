using System;

namespace NanoPic.Core;

/// <summary>
/// Produces a path-free, stable diagnostic summary that users can copy into bug reports.
/// Raw exception messages are intentionally excluded because they commonly contain private paths.
/// </summary>
public static class ImageFailureDiagnostics
{
    private const string SafeDiagnosticKey = "NanoPic.SafeDiagnostic";
    private const int MaxSafeDetailLength = 512;

    public static string Format(ImageOperationFailure failure)
    {
        if (failure is null) throw new ArgumentNullException(nameof(failure));

        var category = failure.Kind.ToString();
        var categoryToken = GetCategoryToken(failure.Kind);
        var stage = GetStage(failure.Kind);
        var diagnostic = $"诊断信息：阶段={stage}；分类={category}";
        var identifier = $"NP-{categoryToken}";

        if (failure.Exception is not null)
        {
            var exception = GetInnermostException(failure.Exception);
            var exceptionName = exception.GetType().Name;
            var hresult = unchecked((uint)exception.HResult).ToString("X8");
            diagnostic += $"；异常={exceptionName}；HRESULT=0x{hresult}";
            identifier += $"-{exceptionName.ToUpperInvariant()}-{hresult}";
        }

        diagnostic += $"；标识={identifier}";

        if (failure.Exception?.Data[SafeDiagnosticKey] is string safeDetail &&
            !string.IsNullOrWhiteSpace(safeDetail))
        {
            diagnostic += $"；详情={NormalizeSafeDetail(safeDetail)}";
        }

        return $"{failure.UserMessage}{Environment.NewLine}{diagnostic}";
    }

    private static Exception GetInnermostException(Exception exception)
    {
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            exception = aggregate.Flatten().InnerExceptions[0];
        }

        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private static string NormalizeSafeDetail(string safeDetail)
    {
        var normalized = safeDetail
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= MaxSafeDetailLength
            ? normalized
            : normalized.Substring(0, MaxSafeDetailLength) + "…";
    }

    private static string GetStage(ImageFailureKind kind) => kind switch
    {
        ImageFailureKind.UnsupportedFormat => "格式识别",
        ImageFailureKind.DecodeFailed => "图像解码",
        ImageFailureKind.PixelBudgetExceeded => "安全检查",
        ImageFailureKind.TargetSizeUnreachable => "目标大小压缩",
        ImageFailureKind.EncodeFailed => "图像编码",
        ImageFailureKind.OutputVerificationFailed => "输出验证",
        ImageFailureKind.FileAccessConflict => "文件读写",
        ImageFailureKind.TaskCanceled => "任务取消",
        ImageFailureKind.AccelerationUnavailable => "加速能力检查",
        ImageFailureKind.LegacyUnimplemented => "功能可用性检查",
        ImageFailureKind.InvalidConfiguration => "参数校验",
        ImageFailureKind.Unknown => "未知处理阶段",
        _ => "未指定"
    };

    private static string GetCategoryToken(ImageFailureKind kind) => kind switch
    {
        ImageFailureKind.UnsupportedFormat => "UNSUPPORTED_FORMAT",
        ImageFailureKind.DecodeFailed => "DECODE_FAILED",
        ImageFailureKind.PixelBudgetExceeded => "PIXEL_BUDGET_EXCEEDED",
        ImageFailureKind.TargetSizeUnreachable => "TARGET_SIZE_UNREACHABLE",
        ImageFailureKind.EncodeFailed => "ENCODE_FAILED",
        ImageFailureKind.OutputVerificationFailed => "OUTPUT_VERIFICATION_FAILED",
        ImageFailureKind.FileAccessConflict => "FILE_ACCESS_CONFLICT",
        ImageFailureKind.TaskCanceled => "TASK_CANCELED",
        ImageFailureKind.AccelerationUnavailable => "ACCELERATION_UNAVAILABLE",
        ImageFailureKind.LegacyUnimplemented => "LEGACY_UNIMPLEMENTED",
        ImageFailureKind.InvalidConfiguration => "INVALID_CONFIGURATION",
        ImageFailureKind.Unknown => "UNKNOWN",
        _ => "NONE"
    };
}
