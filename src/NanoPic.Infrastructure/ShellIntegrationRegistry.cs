using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace NanoPic.Infrastructure;

/// <summary>
/// HKCU 注册表边界。业务逻辑只依赖该抽象，测试用内存实现，永不写真实 Shell 位置。
/// 所有路径都相对于 HKEY_CURRENT_USER。
/// </summary>
public interface IShellRegistryStore
{
    bool KeyExists(string path);
    IReadOnlyList<string> GetSubKeyNames(string path);
    IReadOnlyList<string> GetValueNames(string path);
    string? GetStringValue(string path, string? valueName);
    int? GetInt32Value(string path, string? valueName);
    void CreateKey(string path);
    void SetStringValue(string path, string? valueName, string value);
    void SetInt32Value(string path, string? valueName, int value);
    void DeleteKeyTree(string path);
}

public sealed class WindowsShellRegistryStore : IShellRegistryStore
{
    public bool KeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key is not null;
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetValueNames(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public string? GetStringValue(string path, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key?.GetValue(valueName ?? string.Empty) as string;
    }

    public int? GetInt32Value(string path, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        var value = key?.GetValue(valueName ?? string.Empty);
        return value switch
        {
            int number => number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    public void CreateKey(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException($"无法创建注册表键 HKCU\\{path}。");
        }
    }

    public void SetStringValue(string path, string? valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"无法写入注册表键 HKCU\\{path}。");
        key.SetValue(valueName ?? string.Empty, value, RegistryValueKind.String);
    }

    public void SetInt32Value(string path, string? valueName, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"无法写入注册表键 HKCU\\{path}。");
        key.SetValue(valueName ?? string.Empty, value, RegistryValueKind.DWord);
    }

    public void DeleteKeyTree(string path)
    {
        var separator = path.LastIndexOf('\\');
        if (separator <= 0)
        {
            throw new ArgumentException("拒绝删除注册表根级键。", nameof(path));
        }

        var parentPath = path.Substring(0, separator);
        var childName = path.Substring(separator + 1);
        using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
        if (parent is null)
        {
            return;
        }

        if (parent.GetSubKeyNames().Any(name => string.Equals(name, childName, StringComparison.OrdinalIgnoreCase)))
        {
            parent.DeleteSubKeyTree(childName, throwOnMissingSubKey: false);
        }
    }
}
