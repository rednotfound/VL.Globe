using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VL.Globe.Engine;

// A Mapbox Vector Tile (https://github.com/mapbox/vector-tile-spec, v2) decoded into StyleFeatures
// in Web Mercator world coordinates (0..1, y down). Hand-written rather than
// NetTopologySuite.IO.VectorTiles: that one needs protobuf-net, a dependency vvvv does not ship and
// another package could load in another version, and it converts to longitude/latitude, which the
// painter would only convert back. MapLibre reads tiles with its own small pbf reader too.
internal static class VectorTile
{
    /// <summary>The layers of one tile (gzip or raw), by name; geometry placed in tile <paramref name="tile"/>'s square of the world.</summary>
    public static Dictionary<string, List<StyleFeature>> Decode(byte[] data, TileKey tile)
    {
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        var layers = new Dictionary<string, List<StyleFeature>>();
        var r = new Pbf(data, 0, data.Length);
        while (r.Next(out int field, out int wire))
        {
            if (field == 3 && wire == 2) { var (start, end) = r.Sub(); ReadLayer(new Pbf(data, start, end), tile, layers); }
            else r.Skip(wire);
        }
        return layers;
    }

    static void ReadLayer(Pbf r, TileKey tile, Dictionary<string, List<StyleFeature>> into)
    {
        string name = "";
        uint extent = 4096;
        var keys = new List<string>();
        var values = new List<object?>();
        var features = new List<(int start, int end)>();
        var data = r.Data;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2: name = r.String(); break;
                case 2 when wire == 2: features.Add(r.Sub()); break;
                case 3 when wire == 2: keys.Add(r.String()); break;
                case 4 when wire == 2: { var (s, e) = r.Sub(); values.Add(ReadValue(new Pbf(data, s, e))); break; }
                case 5 when wire == 0: extent = (uint)r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        double scale = 1.0 / (extent * (double)tile.TilesPerSide);
        double ox = (double)tile.X / tile.TilesPerSide, oy = (double)tile.Y / tile.TilesPerSide;
        var list = new List<StyleFeature>(features.Count);
        foreach (var (s, e) in features)
        {
            var f = ReadFeature(new Pbf(data, s, e), keys, values, ox, oy, scale);
            if (f != null) list.Add(f);
        }
        into[name] = list;
    }

    static object? ReadValue(Pbf r)
    {
        object? v = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: v = r.String(); break;
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

    static StyleFeature? ReadFeature(Pbf r, List<string> keys, List<object?> values, double ox, double oy, double scale)
    {
        int type = 0;
        var props = new Dictionary<string, object?>();
        (int s, int e) geometry = (0, 0);
        object? id = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: id = (double)r.Varint(); break;
                case 2 when wire == 2:
                {
                    var (s, e) = r.Sub();
                    var t = new Pbf(r.Data, s, e);
                    while (t.Position < e)
                    {
                        int k = (int)t.Varint(), v = (int)t.Varint();
                        if (k < keys.Count && v < values.Count) props[keys[k]] = values[v];
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
        var rings = new List<(double, double)[]>();
        var current = new List<(double, double)>();
        var g = new Pbf(r.Data, geometry.s, geometry.e);
        long x = 0, y = 0;
        while (g.Position < geometry.e)
        {
            uint cmd = (uint)g.Varint();
            int op = (int)(cmd & 7), count = (int)(cmd >> 3);
            if (op == 7)
            {
                if (current.Count > 0) current.Add(current[0]);
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                x += ZigZag(g.Varint());
                y += ZigZag(g.Varint());
                if (op == 1 && current.Count > 0 && type != 1) { rings.Add(current.ToArray()); current.Clear(); }
                current.Add((ox + x * scale, oy + y * scale));
            }
        }
        if (current.Count > 0) rings.Add(current.ToArray());

        var f = new StyleFeature { Properties = props, Id = id };
        switch (type)
        {
            case 1:
                f.Kind = StyleFeature.Shape.Point;
                foreach (var ring in rings) foreach (var p in ring) f.Parts.Add(new() { new[] { p } });
                break;
            case 2:
                f.Kind = StyleFeature.Shape.Line;
                foreach (var ring in rings) f.Parts.Add(new() { ring });
                break;
            case 3:
                f.Kind = StyleFeature.Shape.Polygon;
                ClassifyRings(rings, f.Parts);
                break;
        }
        return f;
    }

    // MapLibre's classifyRings: a ring wound like the first one starts a polygon, the others are its holes
    static void ClassifyRings(List<(double X, double Y)[]> rings, List<List<(double, double)[]>> into)
    {
        bool? ccw = null;
        List<(double, double)[]>? polygon = null;
        foreach (var ring in rings)
        {
            double area = 0;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                area += (ring[j].X - ring[i].X) * (ring[i].Y + ring[j].Y);
            if (area == 0) continue;
            ccw ??= area < 0;
            if (ccw == area < 0) { polygon = new() { ring }; into.Add(polygon); }
            else polygon?.Add(ring);
        }
    }

    static long ZigZag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

    // a protobuf reader over [Position, End) of Data
    sealed class Pbf
    {
        public readonly byte[] Data;
        public int Position;
        readonly int _end;
        public Pbf(byte[] data, int start, int end) { Data = data; Position = start; _end = end; }

        public bool Next(out int field, out int wire)
        {
            if (Position >= _end) { field = wire = 0; return false; }
            ulong key = Varint();
            field = (int)(key >> 3); wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (Position >= _end) throw new InvalidDataException("truncated varint");
                byte b = Data[Position++];
                v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return v;
                if (shift > 63) throw new InvalidDataException("varint too long");
            }
        }

        public (int start, int end) Sub()
        {
            int len = (int)Varint();
            int start = Position;
            Position += len;
            if (Position > _end) throw new InvalidDataException("truncated message");
            return (start, Position);
        }

        public string String() { var (s, e) = Sub(); return Encoding.UTF8.GetString(Data, s, e - s); }
        public uint Fixed32() { uint v = BitConverter.ToUInt32(Data, Position); Position += 4; return v; }
        public ulong Fixed64() { ulong v = BitConverter.ToUInt64(Data, Position); Position += 8; return v; }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Position += 8; break;
                case 2: Sub(); break;
                case 5: Position += 4; break;
                default: throw new InvalidDataException($"wire type {wire}");
            }
        }
    }
}
