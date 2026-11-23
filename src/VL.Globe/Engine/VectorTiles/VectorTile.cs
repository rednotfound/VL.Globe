using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VL.Globe.Engine.VectorTiles;

// A Mapbox Vector Tile (https://github.com/mapbox/vector-tile-spec, v2) decoded into StyleFeatures
// in Web Mercator world coordinates (0..1, y down). Hand-written rather than
// NetTopologySuite.IO.VectorTiles: that one needs protobuf-net, a dependency vvvv does not ship and
// another package could load in another version, and it converts to longitude/latitude, which the
// painter would only convert back. MapLibre reads tiles with its own small pbf reader too.
internal static class VectorTile
{
    /// <summary>The layers of one tile (gzip or raw), by name; geometry placed in tile <paramref name="tile"/>'s square of the world.</summary>
    public static Dictionary<string, FeatureTable> Decode(byte[] data, TileKey tile)
    {
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        var layers = new Dictionary<string, FeatureTable>();
        var r = new Pbf(data, 0, data.Length);
        while (r.Next(out int field, out int wire))
        {
            if (field == 3 && wire == 2) { var (start, end) = r.Sub(); ReadLayer(new Pbf(data, start, end), tile, layers); }
            else r.Skip(wire);
        }
        return layers;
    }

    // One layer: its key and value tables, then its features - their geometry into one FeatureGeometry
    // and their tags into one int array the layer shares (FeatureGeometry.cs; 4g step 4: a decoded tile
    // of small objects was what the collections copied while tiles were built)
    static void ReadLayer(Pbf r, TileKey tile, Dictionary<string, FeatureTable> into)
    {
        string name = "";
        uint extent = 4096;
        var keyList = new List<string>();
        var valueList = new List<object?>();
        var valueStarts = new List<int>(); var valueLengths = new List<int>();
        var features = new List<(int start, int end)>();
        var data = r.Data;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2: name = r.String(); break;
                case 2 when wire == 2: features.Add(r.Sub()); break;
                case 3 when wire == 2: keyList.Add(Key(r.String())); break;
                case 4 when wire == 2:
                {
                    var (s, e) = r.Sub();
                    valueList.Add(ReadValue(new Pbf(data, s, e), out int textStart, out int textLength));
                    valueStarts.Add(textStart); valueLengths.Add(textLength);
                    break;
                }
                case 5 when wire == 0: extent = (uint)r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        double scale = 1.0 / (extent * (double)tile.TilesPerSide);
        double ox = (double)tile.X / tile.TilesPerSide, oy = (double)tile.Y / tile.TilesPerSide;
        var keys = keyList.ToArray();
        var values = new ValueTable(valueList.ToArray(), valueStarts.ToArray(), valueLengths.ToArray(), data);
        var geometry = new FeatureGeometryBuilder();
        var tags = new List<int>();
        // a feature is a row of these arrays, not an object (FeatureTable)
        var kinds = new List<StyleFeature.Shape>(features.Count);
        var tagStarts = new List<int>(features.Count); var tagCounts = new List<int>(features.Count);
        var firstParts = new List<int>(features.Count); var partCounts = new List<int>(features.Count);
        var ids = new List<double>(features.Count);
        bool anyId = false;
        foreach (var (s, e) in features)
        {
            int tagStart = tags.Count / 2, firstPart = geometry.PartCount;
            var kind = ReadFeature(new Pbf(data, s, e), keys.Length, values.Count, ox, oy, scale, geometry, tags, out var id);
            if (kind is null) continue;
            kinds.Add(kind.Value);
            tagStarts.Add(tagStart); tagCounts.Add(tags.Count / 2 - tagStart);
            firstParts.Add(firstPart); partCounts.Add(geometry.PartCount - firstPart);
            ids.Add(id ?? double.NaN); anyId |= id != null;
        }
        into[name] = new FeatureTable(kinds.ToArray(), tagStarts.ToArray(), tagCounts.ToArray(), firstParts.ToArray(), partCounts.ToArray(),
            anyId ? ids.ToArray() : null, keys, values, tags.ToArray(), geometry.Build());
    }

    // A layer's keys, one string each across tiles: a style's schema is a few hundred keys, every tile
    // repeated them (4g, 2026-11-23). Up to a bound, past it (a tile of arbitrary keys) as they come.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Keys = new();
    static string Key(string key)
    {
        if (Keys.TryGetValue(key, out var known)) return known;
        if (Keys.Count < 8192) Keys.TryAdd(key, key);
        return key;
    }

    // a string value is left in the bytes (ValueTable): its place out, null returned
    static object? ReadValue(Pbf r, out int textStart, out int textLength)
    {
        object? v = null;
        textStart = 0; textLength = -1;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: { var (s, e) = r.Sub(); (textStart, textLength, v) = (s, e - s, null); break; }
                case 2: v = (double)BitConverter.Int32BitsToSingle((int)r.Fixed32()); break;
                case 3: v = BitConverter.Int64BitsToDouble((long)r.Fixed64()); break;
                case 4: v = (double)(long)r.Varint(); break;
                case 5: v = (double)r.Varint(); break;
                case 6: { ulong z = r.Varint(); v = (double)((long)(z >> 1) ^ -(long)(z & 1)); break; }
                case 7: v = r.Varint() != 0; break;
                default: r.Skip(wire); break;
            }
        }
        return v;
    }

    // a feature's own points and rings, read before it is known to be kept: reused, one set a thread
    [ThreadStatic] static List<(double X, double Y)>? t_points;
    [ThreadStatic] static List<int>? t_ringEnds;
    [ThreadStatic] static List<int>? t_tags;

    // A feature into the layer's geometry and tags; null (and nothing added) for a type that is not one
    static StyleFeature.Shape? ReadFeature(Pbf r, int keyCount, int valueCount, double ox, double oy, double scale,
        FeatureGeometryBuilder into, List<int> layerTags, out double? featureId)
    {
        featureId = null;
        var points = t_points ??= new();
        var ringEnds = t_ringEnds ??= new();
        var tags = t_tags ??= new();
        points.Clear(); ringEnds.Clear(); tags.Clear();
        int type = 0;
        (int s, int e) geometry = (0, 0);
        double? id = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: id = r.Varint(); break;   // kept unboxed (StyleFeature.SetNumericId)
                case 2 when wire == 2:
                {
                    var (s, e) = r.Sub();
                    var t = new Pbf(r.Data, s, e);
                    while (t.Position < e)
                    {
                        int k = (int)t.Varint(), v = (int)t.Varint();
                        if (k < keyCount && v < valueCount) { tags.Add(k); tags.Add(v); }
                    }
                    break;
                }
                case 3 when wire == 0: type = (int)r.Varint(); break;
                case 4 when wire == 2: geometry = r.Sub(); break;
                default: r.Skip(wire); break;
            }
        }
        if (type is < 1 or > 3) return null;

        // command integers: id & 7 (1 MoveTo, 2 LineTo, 7 ClosePath), count >> 3; zigzag parameters
        int ringStart = 0;
        var g = new Pbf(r.Data, geometry.s, geometry.e);
        long x = 0, y = 0;
        while (g.Position < geometry.e)
        {
            uint cmd = (uint)g.Varint();
            int op = (int)(cmd & 7), count = (int)(cmd >> 3);
            if (op == 7)
            {
                if (points.Count > ringStart) points.Add(points[ringStart]);
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                x += ZigZag(g.Varint());
                y += ZigZag(g.Varint());
                if (op == 1 && points.Count > ringStart && type != 1) { ringEnds.Add(points.Count); ringStart = points.Count; }
                points.Add((ox + x * scale, oy + y * scale));
            }
        }
        if (points.Count > ringStart) ringEnds.Add(points.Count);

        featureId = id;
        var kind = StyleFeature.Shape.Point;   // type is 1 to 3 here
        var all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points);
        switch (type)
        {
            case 1:
                // every point its own part
                kind = StyleFeature.Shape.Point;
                foreach (var p in all) { into.Add(p.X, p.Y); into.EndRing(); into.EndPart(); }
                break;
            case 2:
                kind = StyleFeature.Shape.Line;
                for (int i = 0, from = 0; i < ringEnds.Count; from = ringEnds[i++])
                {
                    into.AddRing(all[from..ringEnds[i]]);
                    into.EndPart();
                }
                break;
            case 3:
                kind = StyleFeature.Shape.Polygon;
                ClassifyRings(all, ringEnds, into);
                break;
        }
        layerTags.AddRange(tags);
        return kind;
    }

    // MapLibre's classifyRings: a ring wound like the first one starts a polygon, the others are its holes
    static void ClassifyRings(ReadOnlySpan<(double X, double Y)> points, List<int> ringEnds, FeatureGeometryBuilder into)
    {
        bool? ccw = null;
        bool open = false;   // a polygon begun, its part not yet ended
        for (int r = 0, from = 0; r < ringEnds.Count; from = ringEnds[r++])
        {
            var ring = points[from..ringEnds[r]];
            double area = 0;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                area += (ring[j].X - ring[i].X) * (ring[i].Y + ring[j].Y);
            if (area == 0) continue;
            ccw ??= area < 0;
            if (ccw == area < 0)
            {
                if (open) into.EndPart();
                into.AddRing(ring);
                open = true;
            }
            else if (open) into.AddRing(ring);
        }
        if (open) into.EndPart();
    }

    static long ZigZag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);
}
