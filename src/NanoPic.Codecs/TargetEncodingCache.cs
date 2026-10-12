using System;
using System.Collections.Generic;
using System.IO;

namespace NanoPic.Codecs;

/// <summary>One size/fallback attempt owns all files; nothing is reused after its pixels change.</summary>
internal sealed class TargetEncodingCache : IDisposable
{
    private readonly Dictionary<int, string> _candidates = new();
    private readonly List<string> _owned = new();
    private string? _preparedInput;

    public long Measure(int quality, Action<string> encode)
    {
        if (!_candidates.TryGetValue(quality, out var path))
        {
            path = NewPath();
            encode(path);
            _candidates.Add(quality, path);
        }
        return new FileInfo(path).Length;
    }

    public string PreparedInput(Action<string> prepare)
    {
        if (_preparedInput is null)
        {
            var path = NewPath();
            prepare(path);
            _preparedInput = path;
        }
        return _preparedInput;
    }

    public void Select(int quality, string destination) => File.Copy(_candidates[quality], destination, overwrite: true);

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "nanopic-candidate-" + Guid.NewGuid().ToString("N") + ".tmp");
        _owned.Add(path);
        return path;
    }
    public void Dispose()
    {
        foreach (var path in _owned)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
