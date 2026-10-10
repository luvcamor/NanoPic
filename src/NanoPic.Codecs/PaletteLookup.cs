using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace NanoPic.Codecs;

// Exact weighted nearest-colour search. Unlike a reduced-colour lookup table this
// preserves Floyd-Steinberg's choices (including original palette-order ties).
internal sealed class PaletteLookup
{
    private readonly Color[] _colors;
    private readonly Node[] _nodes;
    private readonly int _root;
    private readonly uint[] _cacheKeys = new uint[65536];
    private readonly ushort[] _cacheValues = new ushort[65536];
    private int _nextNode;
    private readonly int _transparentIndex = -1;

    private struct Node
    {
        public int PaletteIndex, Axis, Left, Right;
    }

    public PaletteLookup(IReadOnlyList<Color> colors)
    {
        _colors = new Color[colors.Count];
        _nodes = new Node[colors.Count];
        var indices = new int[colors.Count];
        for (var i = 0; i < colors.Count; i++)
        {
            _colors[i] = colors[i];
            if (_transparentIndex < 0 && colors[i].A == 0) _transparentIndex = i;
            indices[i] = i;
        }
        _root = Build(indices, 0, indices.Length);
    }

    public int Find(byte r, byte g, byte b, byte a)
    {
        // Preserve the quantizer's special case: all fully transparent colours
        // are equivalent, regardless of their hidden RGB values.
        if (a == 0 && _transparentIndex >= 0) return _transparentIndex;
        var key = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        var slot = (int)(unchecked(key * 2654435761u) >> 16);
        if (_cacheValues[slot] != 0 && _cacheKeys[slot] == key)
            return _cacheValues[slot] - 1;

        var bestIndex = int.MaxValue;
        var bestDistance = int.MaxValue;
        Search(_root, r, g, b, a, ref bestIndex, ref bestDistance);
        _cacheKeys[slot] = key;
        _cacheValues[slot] = (ushort)(bestIndex + 1);
        return bestIndex;
    }

    private int Build(int[] indices, int start, int count)
    {
        if (count == 0) return -1;
        var axis = 0;
        var maxSpread = -1;
        for (var dimension = 0; dimension < 4; dimension++)
        {
            var min = 255;
            var max = 0;
            for (var i = start; i < start + count; i++)
            {
                var value = Component(_colors[indices[i]], dimension);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
            var spread = (max - min) * (max - min) * Weight(dimension);
            if (spread > maxSpread) { maxSpread = spread; axis = dimension; }
        }
        Array.Sort(indices, start, count, Comparer<int>.Create((x, y) =>
        {
            var comparison = Component(_colors[x], axis).CompareTo(Component(_colors[y], axis));
            return comparison != 0 ? comparison : x.CompareTo(y);
        }));
        var middle = start + count / 2;
        var node = _nextNode++;
        _nodes[node] = new Node
        {
            PaletteIndex = indices[middle],
            Axis = axis,
            Left = Build(indices, start, middle - start),
            Right = Build(indices, middle + 1, start + count - middle - 1)
        };
        return node;
    }

    private void Search(int nodeIndex, int r, int g, int b, int a, ref int bestIndex, ref int bestDistance)
    {
        if (nodeIndex < 0) return;
        var node = _nodes[nodeIndex];
        var color = _colors[node.PaletteIndex];
        var dr = r - color.R;
        var dg = g - color.G;
        var db = b - color.B;
        var da = a - color.A;
        var distance = dr * dr * 3 + dg * dg * 4 + db * db * 2 + da * da * 4;
        if (distance < bestDistance || (distance == bestDistance && node.PaletteIndex < bestIndex))
        {
            bestIndex = node.PaletteIndex;
            bestDistance = distance;
        }
        var delta = node.Axis == 0 ? dr : node.Axis == 1 ? dg : node.Axis == 2 ? db : da;
        Search(delta < 0 ? node.Left : node.Right, r, g, b, a, ref bestIndex, ref bestDistance);
        // Equality must also be searched to retain the first palette entry on ties.
        if (delta * delta * Weight(node.Axis) <= bestDistance)
            Search(delta < 0 ? node.Right : node.Left, r, g, b, a, ref bestIndex, ref bestDistance);
    }

    private static int Component(Color color, int axis) =>
        axis == 0 ? color.R : axis == 1 ? color.G : axis == 2 ? color.B : color.A;
    private static int Weight(int axis) => axis == 0 ? 3 : axis == 2 ? 2 : 4;
}
