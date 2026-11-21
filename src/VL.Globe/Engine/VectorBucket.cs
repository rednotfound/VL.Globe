using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate.Polygon;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Globe.Engine;

// The GPU path's data (docs/DESIGN-GPU-VECTOR.md): one vector tile's fills and lines as vertices on
// the unit sphere, built once on a worker thread, drawn every frame by VectorRenderer. GL JS's
// buckets, with the globe's projection done here per vertex in double instead of in the shader.

/// <summary>One vertex; 40 bytes, as GlobeVector.sdsl reads it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VectorVertex
{
    public Vector3 Position;   // on the unit sphere, relative to the bucket's centre
    public Vector2 Extrude;    // GL JS's extrusion in units of the outset, in the tile's plane (x east, y south)
    public Vector2 Uv;         // in the tile's square, 0..1: the shader clips to it
    public float N;            // -1..1 across a line, 0..1 out from a round cap's centre, 0 for a fill
    public float Width;        // the feature's line width in px where it depends on the feature, else 1
    public uint Color;         // the feature's colour, straight sRGB bytes R G B A, where it depends on the feature; else white

    public const int Size = 40;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector3>(),
        new VertexElement("TEXCOORD", 0, PixelFormat.R32G32_Float, 12),
        new VertexElement("TEXCOORD", 1, PixelFormat.R32G32_Float, 20),
        new VertexElement("TEXCOORD", 2, PixelFormat.R32G32_Float, 28),
        new VertexElement("COLOR", 0, PixelFormat.R8G8B8A8_UNorm, 36));
}

internal enum RangeKind { Fill, Outline, Line, Background }

/// <summary>A layer's part of a bucket: <see cref="Count"/> indices from <see cref="Start"/>.</summary>
internal readonly record struct VectorRange(int Layer, RangeKind Kind, int Start, int Count);

internal sealed class VectorBucket
{
    public TileKey Key;
    public D3 Centre;
    public VectorVertex[] Vertices = Array.Empty<VectorVertex>();
    public uint[] Indices = Array.Empty<uint>();
    public List<VectorRange> Ranges = new();
    public double Ms;
}

/// <summary>A style layer the GPU draws, with which of its paint properties depend on the feature.</summary>
internal sealed class GpuStyleLayer
{
    public required int Index;           // in the style's layer order
    public required MapStyleDocument.Layer Layer;
    public JsonElement? Color, Opacity, Width, OutlineColor;
    public bool ColorPerFeature, OpacityPerFeature, WidthPerFeature, OutlinePerFeature, Antialias;

    public string Type => Layer.Type;

    // what GL JS evaluates per feature goes into the vertices; the rest is a uniform per frame
    public static GpuStyleLayer? Of(MapStyleDocument style, int index)
    {
        var l = style.Layers[index];
        if (l.Type is not ("fill" or "line" or "background")) return null;
        if (style.Lenient && style.IsDropped(l.Id)) return null;
        var g = new GpuStyleLayer { Index = index, Layer = l };
        string p = l.Type;
        g.Color = Get(l.Paint, p + "-color");
        g.Opacity = Get(l.Paint, p + "-opacity");
        g.Width = l.Type == "line" ? Get(l.Paint, "line-width") : null;
        g.OutlineColor = l.Type == "fill" ? Get(l.Paint, "fill-outline-color") : null;
        g.Antialias = l.Type == "fill" && Get(l.Paint, "fill-antialias") is not { ValueKind: JsonValueKind.False };
        g.ColorPerFeature = g.Color is { } c && Expression.IsFeatureDependent(c);
        g.OpacityPerFeature = g.Opacity is { } o && Expression.IsFeatureDependent(o);
        g.WidthPerFeature = g.Width is { } w && Expression.IsFeatureDependent(w);
        g.OutlinePerFeature = g.OutlineColor is { } oc ? Expression.IsFeatureDependent(oc) : g.ColorPerFeature;
        return g;
    }

    /// <summary>This frame's uniforms at the camera's zoom; false when the layer is not drawn at it.</summary>
    public bool Uniforms(StyleContext ctx, out StyleColor color, out double width, out StyleColor outline)
    {
        color = outline = StyleColor.Black; width = 0;
        ctx.Properties = null; ctx.GeometryType = null; ctx.Id = null;
        var l = Layer;
        if ((l.MinZoom > 0 && ctx.Zoom < l.MinZoom) || ctx.Zoom >= l.MaxZoom) return false;
        if (Expression.String(Get(l.Layout, "visibility"), ctx, "visible") == "none") return false;
        double opacity = OpacityPerFeature ? 1 : Expression.Number(Opacity, ctx, 1);
        var white = new StyleColor(1, 1, 1, 1);
        color = (ColorPerFeature ? white : Expression.Color(Color, ctx, StyleColor.Black)).WithOpacity(opacity);
        outline = (OutlinePerFeature ? white : Expression.Color(OutlineColor ?? Color, ctx, StyleColor.Black)).WithOpacity(opacity);
        width = Type == "line" ? (WidthPerFeature ? 1 : Expression.Number(Width, ctx, 1)) : 0;
        return color.A > 0 || (Type == "fill" && Antialias && outline.A > 0);
    }

    internal static JsonElement? Get(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;
}

internal static class VectorBucketBuilder
{
    /// <summary>Tile-local units per tile side: GL JS's tile seen at its own zoom, 512 px.</summary>
    public const double Extent = 512;
    const double NominalOutset = 10;          // slices round caps for lines up to ~20 px wide
    const double SharpCornerOffset = 15;      // GL JS: 15 px of the tile it is built in
    const int BackgroundCells = 8;

    /// <summary>
    /// Builds the bucket of one vector tile for the layers of one source; null when the tile is not here
    /// (<paramref name="complete"/> false then, the URL in <paramref name="missing"/>) or has no data.
    /// </summary>
    public static VectorBucket? Build(MapStyleDocument style, VectorSource source, IReadOnlyList<GpuStyleLayer> layers, bool withBackground,
        TileKey key, bool fetch, ICollection<string>? missing, out bool complete)
    {
        complete = true;
        var clock = Stopwatch.StartNew();
        var data = style.Tile(source, key, ref complete, fetch, missing);
        if (data is null && !complete) return null;

        var b = new Builder(key);
        var ctx = new StyleContext { Zoom = key.Zoom, GlobalState = style.GlobalState };
        if (withBackground) b.Background();
        foreach (var layer in layers)
        {
            if (layer.Type == "background") continue;
            if (data is null || layer.Layer.SourceLayer is not { } sl || !data.TryGetValue(sl, out var features)) continue;
            try { b.Layer(layer, features, ctx); }
            catch (StyleUnsupportedException e) when (style.Lenient) { style.Drop(layer.Layer.Id, e.Message); b.Rewind(); }
        }
        var bucket = b.Finish();
        bucket.Ms = clock.Elapsed.TotalMilliseconds;
        return bucket;
    }

    sealed class Builder
    {
        readonly TileKey _key;
        readonly int _n;
        readonly D3 _centre;
        readonly double _maxSegment;   // tile-local; long edges are cut so their chords follow the sphere
        readonly List<VectorVertex> _vertices = new();
        readonly List<uint> _indices = new();
        readonly List<VectorRange> _ranges = new();
        int _layerVertexStart, _layerIndexStart, _layerRangeStart;
        readonly GeometryFactory _factory = new();
        readonly List<ExtrudedVertex> _line = new();
        readonly Dictionary<ExtrudedVertex, uint> _seen = new();

        public Builder(TileKey key)
        {
            _key = key;
            _n = 1 << key.Zoom;
            _centre = TileGeometry.Centre(key);
            // 2 degrees of longitude in tile units: nothing is cut from zoom 8 on (a tile is 1.4 degrees)
            _maxSegment = 2 * Extent * _n / 360.0;
        }

        public void Rewind()
        {
            _vertices.RemoveRange(_layerVertexStart, _vertices.Count - _layerVertexStart);
            _indices.RemoveRange(_layerIndexStart, _indices.Count - _layerIndexStart);
            _ranges.RemoveRange(_layerRangeStart, _ranges.Count - _layerRangeStart);
        }

        public VectorBucket Finish() => new()
        {
            Key = _key, Centre = _centre, Vertices = _vertices.ToArray(), Indices = _indices.ToArray(), Ranges = _ranges.ToList(),
        };

        // the whole square, a grid so it bends with the sphere; the background layers draw it
        public void Background()
        {
            int start = _indices.Count;
            uint first = (uint)_vertices.Count;
            int side = BackgroundCells + 1;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                    _vertices.Add(Vertex(Extent * i / BackgroundCells, Extent * j / BackgroundCells, 0, 0, 0, 1, uint.MaxValue));
            for (int j = 0; j < BackgroundCells; j++)
                for (int i = 0; i < BackgroundCells; i++)
                {
                    uint a = first + (uint)(j * side + i), c = a + (uint)side;
                    _indices.Add(a); _indices.Add(a + 1); _indices.Add(c);
                    _indices.Add(a + 1); _indices.Add(c + 1); _indices.Add(c);
                }
            _ranges.Add(new VectorRange(-1, RangeKind.Background, start, _indices.Count - start));
        }

        public void Layer(GpuStyleLayer layer, List<StyleFeature> features, StyleContext ctx)
        {
            _layerVertexStart = _vertices.Count; _layerIndexStart = _indices.Count; _layerRangeStart = _ranges.Count;
            var l = layer.Layer;
            // GL JS builds a bucket at the tile's zoom: filters and layout at it. Not the layer's zoom
            // range: one bucket serves every zoom the tile is overscaled to (z14 data at z19), so
            // minzoom/maxzoom are checked per frame (GpuStyleLayer.Uniforms).
            int fillStart = _indices.Count;
            var outlines = new List<(StyleFeature f, uint color)>();
            foreach (var f in features)
            {
                if (f.Kind == StyleFeature.Shape.Point) continue;
                ctx.Properties = f.Properties; ctx.GeometryType = f.GeometryType; ctx.Id = f.Id;
                if (l.Filter is { } filter && !Expression.Filter(filter, ctx)) continue;
                double opacity = layer.OpacityPerFeature ? Expression.Number(layer.Opacity, ctx, 1) : 1;
                if (layer.Type == "fill")
                {
                    if (f.Kind != StyleFeature.Shape.Polygon) continue;
                    var color = layer.ColorPerFeature ? Expression.Color(layer.Color, ctx, StyleColor.Black) : new StyleColor(1, 1, 1, 1);
                    Fill(f, Pack(color.WithOpacity(opacity)));
                    if (layer.Antialias)
                    {
                        var outline = layer.OutlinePerFeature ? Expression.Color(layer.OutlineColor ?? layer.Color, ctx, StyleColor.Black) : new StyleColor(1, 1, 1, 1);
                        outlines.Add((f, Pack(outline.WithOpacity(opacity))));
                    }
                }
                else
                {
                    double width = layer.WidthPerFeature ? Expression.Number(layer.Width, ctx, 1) : 1;
                    if (width <= 0) continue;
                    var color = layer.ColorPerFeature ? Expression.Color(layer.Color, ctx, StyleColor.Black) : new StyleColor(1, 1, 1, 1);
                    string cap = Expression.String(GpuStyleLayer.Get(l.Layout, "line-cap"), ctx, "butt");
                    string join = Expression.String(GpuStyleLayer.Get(l.Layout, "line-join"), ctx, "miter");
                    double miterLimit = Expression.Number(GpuStyleLayer.Get(l.Layout, "line-miter-limit"), ctx, 2);
                    double roundLimit = Expression.Number(GpuStyleLayer.Get(l.Layout, "line-round-limit"), ctx, 1.05);
                    Lines(f, f.Kind == StyleFeature.Shape.Polygon, join, cap, miterLimit, roundLimit, (float)width, Pack(color.WithOpacity(opacity)));
                }
            }
            if (layer.Type == "fill")
            {
                if (_indices.Count > fillStart) _ranges.Add(new VectorRange(layer.Index, RangeKind.Fill, fillStart, _indices.Count - fillStart));
                int outlineStart = _indices.Count;
                // GL JS's antialiasing outline, one device pixel: a 1 px line here
                foreach (var (f, color) in outlines) Lines(f, true, "miter", "butt", 2, 1.05, 1, color);
                if (_indices.Count > outlineStart) _ranges.Add(new VectorRange(layer.Index, RangeKind.Outline, outlineStart, _indices.Count - outlineStart));
            }
            else if (_indices.Count > fillStart) _ranges.Add(new VectorRange(layer.Index, RangeKind.Line, fillStart, _indices.Count - fillStart));
        }

        void Fill(StyleFeature f, uint color)
        {
            foreach (var polygon in f.Parts)
            {
                if (polygon.Count == 0) continue;
                Polygon geometry;
                try
                {
                    var shell = Ring(polygon[0]);
                    if (shell is null) continue;
                    var holes = polygon.Skip(1).Select(Ring).Where(r => r is not null).Select(r => r!).ToArray();
                    geometry = _factory.CreatePolygon(shell, holes);
                }
                catch (ArgumentException) { continue; }
                IList<NetTopologySuite.Triangulate.Tri.Tri> tris;
                // an invalid ring (self-touching, as clipping leaves some) is skipped, not fatal
                try { tris = new PolygonTriangulator(geometry).GetTriangles(); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException or NetTopologySuite.Geometries.TopologyException) { continue; }
                var seen = new Dictionary<(double, double), uint>();
                foreach (var t in tris) Triangle(t.GetCoordinate(0), t.GetCoordinate(1), t.GetCoordinate(2), color, seen, 0);
            }
        }

        LinearRing? Ring((double X, double Y)[] ring)
        {
            var cs = new List<Coordinate>(ring.Length + 1);
            foreach (var (x, y) in ring)
            {
                var c = new Coordinate((x * _n - _key.X) * Extent, (y * _n - _key.Y) * Extent);
                if (cs.Count == 0 || !cs[^1].Equals2D(c)) cs.Add(c);
            }
            if (cs.Count > 1 && cs[0].Equals2D(cs[^1])) cs.RemoveAt(cs.Count - 1);
            if (cs.Count < 3) return null;
            cs.Add(cs[0].Copy());
            return _factory.CreateLinearRing(cs.ToArray());
        }

        // a triangle whose longest edge is too long for the sphere is cut in two at that edge's middle
        void Triangle(Coordinate a, Coordinate b, Coordinate c, uint color, Dictionary<(double, double), uint> seen, int depth)
        {
            double ab = a.Distance(b), bc = b.Distance(c), ca = c.Distance(a);
            double longest = Math.Max(ab, Math.Max(bc, ca));
            if (longest > _maxSegment && depth < 12)
            {
                if (longest == ab) { var m = Mid(a, b); Triangle(a, m, c, color, seen, depth + 1); Triangle(m, b, c, color, seen, depth + 1); }
                else if (longest == bc) { var m = Mid(b, c); Triangle(a, b, m, color, seen, depth + 1); Triangle(a, m, c, color, seen, depth + 1); }
                else { var m = Mid(c, a); Triangle(a, b, m, color, seen, depth + 1); Triangle(m, b, c, color, seen, depth + 1); }
                return;
            }
            _indices.Add(Index(a)); _indices.Add(Index(b)); _indices.Add(Index(c));

            uint Index(Coordinate p)
            {
                if (seen.TryGetValue((p.X, p.Y), out var i)) return i;
                i = (uint)_vertices.Count;
                _vertices.Add(Vertex(p.X, p.Y, 0, 0, 0, 1, color));
                seen[(p.X, p.Y)] = i;
                return i;
            }
            static Coordinate Mid(Coordinate p, Coordinate q) => new((p.X + q.X) / 2, (p.Y + q.Y) / 2);
        }

        void Lines(StyleFeature f, bool isPolygon, string join, string cap, double miterLimit, double roundLimit, float width, uint color)
        {
            var points = new List<(double X, double Y)>();
            foreach (var part in f.Parts)
                foreach (var line in part)
                {
                    points.Clear();
                    for (int k = 0; k < line.Length; k++)
                    {
                        var p = ((line[k].X * _n - _key.X) * Extent, (line[k].Y * _n - _key.Y) * Extent);
                        if (k > 0)
                        {
                            // long segments get points between, so the line bends with the sphere
                            var q = points[^1];
                            double len = Math.Sqrt((p.Item1 - q.X) * (p.Item1 - q.X) + (p.Item2 - q.Y) * (p.Item2 - q.Y));
                            int cuts = (int)Math.Min(64, Math.Ceiling(len / _maxSegment));
                            for (int c = 1; c < cuts; c++) points.Add((q.X + (p.Item1 - q.X) * c / cuts, q.Y + (p.Item2 - q.Y) * c / cuts));
                        }
                        points.Add(p);
                    }
                    _line.Clear();
                    LineTessellator.TessellateExtruded(points, isPolygon, join, cap, miterLimit, roundLimit, NominalOutset, SharpCornerOffset, _line);
                    _seen.Clear();
                    foreach (var v in _line)
                    {
                        if (!_seen.TryGetValue(v, out var i))
                        {
                            i = (uint)_vertices.Count;
                            _vertices.Add(Vertex(v.X, v.Y, (float)v.QX, (float)v.QY, v.N, width, color));
                            _seen[v] = i;
                        }
                        _indices.Add(i);
                    }
                }
        }

        VectorVertex Vertex(double lx, double ly, float qx, float qy, float n, float width, uint color)
        {
            double wx = (_key.X + lx / Extent) / _n, wy = (_key.Y + ly / Extent) / _n;
            var (x, y, z) = TileGeometry.OnSphere(TileGeometry.LongitudeOf(wx), TileGeometry.LatitudeOf(Math.Clamp(wy, 0, 1)));
            return new VectorVertex
            {
                Position = (new D3(x, y, z) - _centre).ToVector3(),
                Extrude = new Vector2(qx, qy),
                Uv = new Vector2((float)(lx / Extent), (float)(ly / Extent)),
                N = n,
                Width = width,
                Color = color,
            };
        }
    }

    // straight sRGB bytes, R in the lowest byte (R8G8B8A8_UNorm)
    internal static uint Pack(StyleColor c)
    {
        static uint B(double v) => (uint)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return B(c.R) | (B(c.G) << 8) | (B(c.B) << 16) | (B(c.A) << 24);
    }
}
