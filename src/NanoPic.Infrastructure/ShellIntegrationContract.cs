using System;
using System.Collections.Generic;
using System.Globalization;

namespace NanoPic.Infrastructure;

/// <summary>
/// Shell 集成的固定契约：CLSID、受支持扩展、注册表位置与所有权标记。
/// 这些值在版本之间必须保持稳定，卸载与修复都依赖它们证明“这一项属于 NanoPic”。
/// </summary>
public static class ShellIntegrationContract
{
    /// <summary>进程外 DropTarget 的固定 CLSID：源码常量，不在安装时重新生成。</summary>
    public const string DropTargetClsidText = "8F3A6B21-5D74-4C2E-9E61-1A9C0D5B7E43";

    public static Guid DropTargetClsid { get; } = new(DropTargetClsidText);

    /// <summary>注册表中 CLSID 的书写形式（含花括号）。</summary>
    public static string DropTargetClsidKey { get; } = DropTargetClsid.ToString("B").ToUpperInvariant();

    public const string VerbKeyName = "NanoPic.Add";
    public const string VerbDisplayName = "添加到 NanoPic";
    public const string ClsidDisplayName = "NanoPic File Drop Target";
    public const string OwnerId = "NanoPic.ShellIntegration";
    public const int SchemaVersion = 2;

    /// <summary>经典 Verb 的多选模型：整个选择集只激活一次谓词。</summary>
    public const string MultiSelectModel = "Player";

    public const string OwnerValueName = "NanoPic.OwnerId";
    public const string SchemaValueName = "NanoPic.SchemaVersion";
    public const string TransactionValueName = "NanoPic.TransactionId";

    public const string PrivateMetadataKeyPath = @"Software\NanoPic\ShellIntegration";
    public const string ClassesKeyPath = @"Software\Classes";

    /// <summary>只注册到明确受支持的扩展，不使用 <c>image</c> 感知类型或 <c>*</c>。</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        ".jpg", ".jpeg", ".jpe", ".jfif",
        ".png", ".webp", ".gif", ".bmp",
        ".tif", ".tiff", ".ico"
    };

    public static string ClsidKeyPath { get; } = string.Format(
        CultureInfo.InvariantCulture,
        @"{0}\CLSID\{1}",
        ClassesKeyPath,
        DropTargetClsidKey);

    public static string LocalServerKeyPath { get; } = ClsidKeyPath + @"\LocalServer32";

    public static string VerbKeyPath(string extension) => string.Format(
        CultureInfo.InvariantCulture,
        @"{0}\SystemFileAssociations\{1}\shell\{2}",
        ClassesKeyPath,
        extension,
        VerbKeyName);

    public static string DropTargetKeyPath(string extension) => VerbKeyPath(extension) + @"\DropTarget";

    public static string CommandKeyPath(string extension) => VerbKeyPath(extension) + @"\command";
}
