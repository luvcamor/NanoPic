using System;
using System.Collections.Generic;
using System.Linq;
using NanoPic.Infrastructure;

namespace NanoPic.IntegrationTests;

/// <summary>测试用注册表：只在内存中模拟 HKCU 下的键值，业务测试绝不写真实 Shell 位置。</summary>
public sealed class InMemoryShellRegistryStore : IShellRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    public int WriteCount { get; private set; }

    public IReadOnlyDictionary<string, Dictionary<string, object>> Keys => _keys;

    public string Snapshot()
    {
        var lines = _keys
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "|" + string.Join(
                ",",
                pair.Value.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(value => value.Key + "=" + Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture))));
        return string.Join("\n", lines);
    }

    public bool KeyExists(string path) =>
        _keys.ContainsKey(path) ||
        _keys.Keys.Any(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var prefix = path + "\\";
        return _keys.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key.Substring(prefix.Length).Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetValueNames(string path) =>
        _keys.TryGetValue(path, out var values) ? values.Keys.ToArray() : Array.Empty<string>();

    public string? GetStringValue(string path, string? valueName) =>
        _keys.TryGetValue(path, out var values) && values.TryGetValue(valueName ?? string.Empty, out var value)
            ? value as string
            : null;

    public int? GetInt32Value(string path, string? valueName) =>
        _keys.TryGetValue(path, out var values) && values.TryGetValue(valueName ?? string.Empty, out var value) && value is int number
            ? number
            : null;

    public void CreateKey(string path)
    {
        if (!_keys.ContainsKey(path))
        {
            _keys[path] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            WriteCount++;
        }
    }

    public void SetStringValue(string path, string? valueName, string value)
    {
        CreateKey(path);
        _keys[path][valueName ?? string.Empty] = value;
        WriteCount++;
    }

    public void SetInt32Value(string path, string? valueName, int value)
    {
        CreateKey(path);
        _keys[path][valueName ?? string.Empty] = value;
        WriteCount++;
    }

    public void DeleteKeyTree(string path)
    {
        var removals = _keys.Keys
            .Where(key => string.Equals(key, path, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in removals)
        {
            _keys.Remove(key);
            WriteCount++;
        }
    }
}
