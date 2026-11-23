using System;
using System.Collections;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// A decoded tile as a few large arrays, not hundreds of thousands of small objects (ROADMAP 4g step 4,
// 2026-11-23). Building a tile takes longer than the ~50 ms between two gen0 collections, so whatever a
// decoded tile is made of survives and is copied, object by object, from gen0 to gen1 to gen2 - and the
// pause that copying costs stops vvvv's render thread. The bench promoted ~14 MB a second, about one
// decoded tile (8.6 MB at Tokyo z14) for each tile decoded; with the decoded tiles not cached at all it
// promoted twice that (each tile decoded twice). So a layer's points are one array, its rings and parts
// offsets into it, its features' tags offsets into one int array over the layer's key and value tables -
// as MVT itself stores them. A large array is not copied (the large-object heap); a small one is copied
// as one block.

/// <summary>One layer's geometry: every point, the rings as ranges of points, the parts as ranges of rings.</summary>
internal sealed class FeatureGeometry
{
    public static readonly FeatureGeometry Empty = new(Array.Empty<(double, double)>(), new[] { 0 }, new[] { 0 });

    public readonly (double X, double Y)[] Points;
    public readonly int[] RingStarts;   // ring r is Points[RingStarts[r] .. RingStarts[r + 1])
    public readonly int[] PartStarts;   // part p is rings PartStarts[p] .. PartStarts[p + 1]

    public FeatureGeometry((double X, double Y)[] points, int[] ringStarts, int[] partStarts)
    {
        Points = points; RingStarts = ringStarts; PartStarts = partStarts;
    }
}

/// <summary>Fills a <see cref="FeatureGeometry"/>: points, then EndRing; rings, then EndPart.</summary>
internal sealed class FeatureGeometryBuilder
{
    readonly List<(double X, double Y)> _points = new();
    readonly List<int> _ringStarts = new() { 0 };
    readonly List<int> _partStarts = new() { 0 };

    public int PartCount => _partStarts.Count - 1;
    public int RingCount => _ringStarts.Count - 1;

    public void Add(double x, double y) => _points.Add((x, y));
    public void EndRing() => _ringStarts.Add(_points.Count);
    public void EndPart() => _partStarts.Add(RingCount);

    /// <summary>A whole ring at once.</summary>
    public void AddRing(ReadOnlySpan<(double X, double Y)> ring)
    {
        foreach (var p in ring) _points.Add(p);
        EndRing();
    }

    public FeatureGeometry Build() => new(_points.ToArray(), _ringStarts.ToArray(), _partStarts.ToArray());
}

/// <summary>A feature's parts: for a point each part one coordinate, a line each part one line, a polygon each part its rings.</summary>
internal readonly struct FeatureParts : IReadOnlyList<FeaturePart>
{
    readonly FeatureGeometry _g;
    readonly int _first, _count;

    public FeatureParts(FeatureGeometry g, int first, int count) { _g = g; _first = first; _count = count; }

    public int Count => _count;
    public FeaturePart this[int i] => (uint)i < (uint)_count
        ? new FeaturePart(_g, _g.PartStarts[_first + i], _g.PartStarts[_first + i + 1] - _g.PartStarts[_first + i])
        : throw new ArgumentOutOfRangeException(nameof(i));

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<FeaturePart> IEnumerable<FeaturePart>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(FeatureParts parts) : IEnumerator<FeaturePart>
    {
        int _i = -1;
        public FeaturePart Current => parts[_i];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_i < parts.Count;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}

/// <summary>One part's rings.</summary>
internal readonly struct FeaturePart : IReadOnlyList<Ring>
{
    readonly FeatureGeometry _g;
    readonly int _first, _count;

    public FeaturePart(FeatureGeometry g, int first, int count) { _g = g; _first = first; _count = count; }

    public int Count => _count;
    public Ring this[int i] => (uint)i < (uint)_count
        ? new Ring(_g.Points, _g.RingStarts[_first + i], _g.RingStarts[_first + i + 1] - _g.RingStarts[_first + i])
        : throw new ArgumentOutOfRangeException(nameof(i));

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<Ring> IEnumerable<Ring>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(FeaturePart part) : IEnumerator<Ring>
    {
        int _i = -1;
        public Ring Current => part[_i];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_i < part.Count;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}

/// <summary>A ring (or line, or a point's one coordinate): a range of its layer's points.</summary>
internal readonly struct Ring : IReadOnlyList<(double X, double Y)>
{
    readonly (double X, double Y)[] _points;
    readonly int _start, _length;

    public Ring((double X, double Y)[] points, int start, int length) { _points = points; _start = start; _length = length; }

    public int Length => _length;
    public int Count => _length;
    public (double X, double Y) this[int i] => (uint)i < (uint)_length ? _points[_start + i] : throw new ArgumentOutOfRangeException(nameof(i));
    public ReadOnlySpan<(double X, double Y)> Span => _points.AsSpan(_start, _length);

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<(double X, double Y)> IEnumerable<(double X, double Y)>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(Ring ring) : IEnumerator<(double X, double Y)>
    {
        int _i = -1;
        public (double X, double Y) Current => ring._points[ring._start + _i];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_i < ring._length;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}

/// <summary>
/// An MVT feature's properties, read from its layer's tables: the feature's tags (key and value indices)
/// are a range of one int array the whole layer shares. A key given twice keeps its last value, as a
/// dictionary filled in order did.
/// </summary>
internal sealed class TagProperties : IReadOnlyDictionary<string, object?>
{
    readonly string[] _keys;
    readonly object?[] _values;
    readonly int[] _tags;   // key, value, key, value...
    readonly int _start, _count;   // in pairs

    public TagProperties(string[] keys, object?[] values, int[] tags, int start, int count)
    {
        _keys = keys; _values = values; _tags = tags; _start = start; _count = count;
    }

    public bool TryGetValue(string key, out object? value)
    {
        // last first: a repeated key's last value wins
        for (int i = _count - 1; i >= 0; i--)
        {
            int t = 2 * (_start + i);
            if (string.Equals(_keys[_tags[t]], key)) { value = _values[_tags[t + 1]]; return true; }
        }
        value = null;
        return false;
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public object? this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);

    // the distinct keys, each with its last value, in the order first given (a dictionary's order)
    IEnumerable<KeyValuePair<string, object?>> Distinct()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < _count; i++)
        {
            var key = _keys[_tags[2 * (_start + i)]];
            if (seen.Add(key) && TryGetValue(key, out var v)) yield return new(key, v);
        }
    }

    public int Count { get { int n = 0; foreach (var _ in Distinct()) n++; return n; } }
    public IEnumerable<string> Keys { get { foreach (var kv in Distinct()) yield return kv.Key; } }
    public IEnumerable<object?> Values { get { foreach (var kv in Distinct()) yield return kv.Value; } }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Distinct().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
