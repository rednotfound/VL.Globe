using System;
using System.Buffers;
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

// The GPU path's data (docs/DESIGN-GPU-VECTOR.md): one vector tile's fills and lines, built once on a
// worker thread, drawn every frame by VectorRenderer. GL JS's buckets: a vertex holds its place in the
// tile, and the shader projects it - onto the sphere, or the Mercator plane (ROADMAP 4g step 2).

/// <summary>One vertex; 16 bytes, as GlobeVector.sdsl reads it.</summary>
// Packed as GL JS packs its buckets. 2026-11-21: 40 bytes, and 3D buildings doubled a street-level
// tile to 22 MB; then 28, a position on the unit sphere in three floats beside its place in the tile.
// 2026-11-23 (ROADMAP 4g: GL JS 0 frames over 20 ms panning, ours 3.5%, the misses in the seconds that
// upload tiles): the place alone, as GL JS keeps it, the shader projecting it (VectorProjection.cs).
// The place is in 1/32768 of the tile from -0.5 to 1.5: MVT's 4096 grid and the tile's edges exactly,
// 6 cm in a z14 tile at Tokyo. The extrusion is GL JS's own: two signed bytes k, e = k / 63, exact for
// a line (line_bucket stores exactly these); a wall's normal and a round cap's slice to 1/63. (As
// 16-bit e / 4 it was within 6e-5, enough to move a bevel's flat edge across a row of pixel centres:
// line-miter-limit/default, 2026-11-22.) N and Width are half floats; Width is a line's width scale,
// or for fill-extrusion the metres the vertex is raised by (its roof or its floor).
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct VectorVertex
{
    ushort _u, _v;             // (uv + 0.5) x 32768
    sbyte _ex, _ey;            // the extrusion x 63, rounded as GL JS rounds it
    short _spare;              // Stride wants every element a multiple of 4 bytes: R8G8B8A8_SInt, .xy read
    System.Half _n, _width;
    public uint Color;         // the feature's colour, straight sRGB bytes R G B A, where it depends on the feature; else white

    public const int Size = 16;
    public const float UvUnits = 32768;
    const float ExtrudeScale = 63, UvLow = -0.5f;

    /// <summary>GL JS's extrusion in units of the outset, in the tile's plane (x east, y south); a wall's outward normal.</summary>
    public Vector2 Extrude
    {
        readonly get => new(_ex / ExtrudeScale, _ey / ExtrudeScale);
        set { _ex = Byte(value.X); _ey = Byte(value.Y); }
    }

    /// <summary>The vertex's place in the tile's square, 0..1 inside it (-0.5..1.5 kept): the shader projects it and clips to the square.</summary>
    public Vector2 Uv
    {
        readonly get => new(_u / UvUnits + UvLow, _v / UvUnits + UvLow);
        set { _u = Units(value.X); _v = Units(value.Y); }
    }

    /// <summary>-1..1 across a line, 0..1 out from a round cap's centre, 0 for a fill; a wall's vertical gradient.</summary>
    public float N { readonly get => (float)_n; set => _n = (System.Half)value; }

    /// <summary>The feature's line width scale where it depends on the feature, else 1; for fill-extrusion the metres the vertex is raised by.</summary>
    public float Width { readonly get => (float)_width; set => _width = (System.Half)value; }

    static sbyte Byte(float x) => (sbyte)Math.Clamp((int)Math.Floor(x * ExtrudeScale + 0.5f), -128, 127);
    static ushort Units(double x) => (ushort)Math.Clamp(Math.Round((x - UvLow) * UvUnits), 0, ushort.MaxValue);

    /// <summary>The place set from the tile's own units without going through float: GL JS's integer grid stays exact.</summary>
    public void SetUv(double u, double v) { _u = Units(u); _v = Units(v); }

    public static readonly VertexDeclaration Layout = new(
        new VertexElement("TEXCOORD", 1, PixelFormat.R16G16_UInt, 0),
        new VertexElement("TEXCOORD", 0, PixelFormat.R8G8B8A8_SInt, 4),
        new VertexElement("TEXCOORD", 2, PixelFormat.R16G16_Float, 8),
        new VertexElement("COLOR", 0, PixelFormat.R8G8B8A8_UNorm, 12));
}

// Outline is a line list (GL JS's gl.LINES, one device pixel); the rest are triangle lists. Extrusion
// is fill-extrusion: roofs and walls in 3D, drawn in two passes (depth, then colour). Text is a
// symbol layer's glyph quads, in the bucket's symbol arrays (SymbolLayout.cs)
internal enum RangeKind { Fill, Outline, Line, Background, Extrusion, Text, Icon, Circle }

/// <summary>A layer's part of a bucket: <see cref="Count"/> indices from <see cref="Start"/>, each <see cref="BaseVertex"/> plus the 16-bit index.</summary>
internal readonly record struct VectorRange(int Layer, RangeKind Kind, int Start, int Count, int BaseVertex = 0);

/// <summary>Where a bucket's vertices go: on the unit sphere (the globe), or on the Web Mercator plane (MapLibre's render tests).</summary>
internal enum VectorProjection { Sphere, Mercator }

internal sealed class VectorBucket
{
    public TileKey Key;
    public int OverscaledZoom;   // Key.Zoom, plus the levels it is overscaled by (GL JS's overscaledZ)
    public D3 Centre;
    /// <summary>What GlobeVector.sdsl projects this bucket's vertices with (TileProjection).</summary>
    public TileProjection.Geo Geo => _geo ??= TileProjection.Of(Key);
    TileProjection.Geo? _geo;
    // rented from the shared pool, longer than the counts: a street-level tile's arrays are megabytes,
    // and dropped after each upload they were large-object-heap garbage, collected in gen 2 - a pause
    // on vvvv's main thread (2026-11-21, "偶尔在运动加载的时候会卡一下")
    public VectorVertex[] Vertices = Array.Empty<VectorVertex>();
    // 16-bit, each range's relative to its BaseVertex (GL JS's segments): half the bytes of 32-bit
    public ushort[] Indices = Array.Empty<ushort>();
    public int VertexCount, IndexCount;
    public List<VectorRange> Ranges = new();
    // labels: few beside the lines and fills, so plain arrays
    public SymbolVertex[] SymbolVertices = Array.Empty<SymbolVertex>();
    public uint[] SymbolIndices = Array.Empty<uint>();
    public List<LabelInfo> Labels = new();
    // circle layers' quads (CircleLayout.cs)
    public CircleVertex[] CircleVertices = Array.Empty<CircleVertex>();
    public uint[] CircleIndices = Array.Empty<uint>();
    public List<LineLabel> LineLabels = new();
    // icons, per symbol layer: an SDF or an image (the layer's first icon decides, as GL JS's
    // bucket.sdfIcons), and whether GL JS's iconsNeedLinear holds (another pixel ratio, a rotation)
    public Dictionary<int, (bool Sdf, bool NeedsLinear)> IconModes = new();
    public double Ms;

    public ReadOnlySpan<VectorVertex> VertexSpan => Vertices.AsSpan(0, VertexCount);
    public ReadOnlySpan<ushort> IndexSpan => Indices.AsSpan(0, IndexCount);

    /// <summary>The vertex the index at <paramref name="position"/> of <paramref name="range"/> names.</summary>
    public VectorVertex VertexAt(VectorRange range, int position) => Vertices[range.BaseVertex + Indices[position]];

    /// <summary>The arrays go back to the pool (once uploaded); the ranges stay.</summary>
    public void Release()
    {
        if (Vertices.Length > 0) ArrayPool<VectorVertex>.Shared.Return(Vertices);
        if (Indices.Length > 0) ArrayPool<ushort>.Shared.Return(Indices);
        Vertices = Array.Empty<VectorVertex>();
        Indices = Array.Empty<ushort>();
    }
}

/// <summary>A style layer the GPU draws, with which of its paint properties depend on the feature.</summary>
internal sealed partial class GpuStyleLayer
{
    public required int Index;           // in the style's layer order
    public required MapStyleDocument.Layer Layer;
    public JsonElement? Color, Opacity, Width, OutlineColor, Height, Base;
    public bool ColorPerFeature, OpacityPerFeature, WidthPerFeature, OutlinePerFeature, Antialias;
    public bool IsExtrusion => Layer.Type == "fill-extrusion";

    // fill-extrusion counts as a fill where only fill vs line matters (its colour and opacity keys are
    // fill-extrusion-color and -opacity, read by Of with the type as the prefix; it has no outline)
    public string Type => Layer.Type == "fill-extrusion" ? "fill" : Layer.Type;

    // what GL JS evaluates per feature goes into the vertices; the rest is a uniform per frame
    public static GpuStyleLayer? Of(MapStyleDocument style, int index)
    {
        var l = style.Layers[index];
        if (l.Type is not ("fill" or "line" or "background" or "fill-extrusion" or "symbol" or "circle")) return null;
        if (style.Lenient && style.IsDropped(l.Id)) return null;
        if (l.Type == "symbol") return SymbolOf(index, l);
        if (l.Type == "circle") return CircleOf(index, l);
        var g = new GpuStyleLayer { Index = index, Layer = l };
        string p = l.Type;
        g.Color = Get(l.Paint, p + "-color");
        g.Opacity = Get(l.Paint, p + "-opacity");
        g.Width = l.Type == "line" ? Get(l.Paint, "line-width") : null;
        g.OutlineColor = l.Type == "fill" ? Get(l.Paint, "fill-outline-color") : null;
        g.Height = l.Type == "fill-extrusion" ? Get(l.Paint, "fill-extrusion-height") : null;
        g.Base = l.Type == "fill-extrusion" ? Get(l.Paint, "fill-extrusion-base") : null;
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

internal static partial class VectorBucketBuilder
{
    /// <summary>The 16-bit segmenting <see cref="Builder.Finish"/> does (tests).</summary>
    internal static List<VectorRange> Segments(Span<uint> source, ushort[] target, List<VectorRange> ranges, List<VectorVertex> vertices)
        => Builder.Segments(source, target, ranges, vertices);

    /// <summary>Tile-local units per tile side: GL JS's tile seen at its own zoom, 512 px.</summary>
    public const double Extent = 512;
    const double NominalOutset = 10;          // slices round caps for lines up to ~20 px wide
    const double SharpCornerOffset = 15;      // GL JS: 15 px of the tile it is built in

    /// <summary>
    /// Builds the bucket of one vector tile for the layers of one source; null when the tile is not here
    /// (<paramref name="complete"/> false then, the URL in <paramref name="missing"/>) or has no data.
    /// </summary>
    public static VectorBucket? Build(MapStyleDocument style, StyleSource source, IReadOnlyList<GpuStyleLayer> layers, bool withBackground,
        TileKey key, bool fetch, ICollection<string>? missing, out bool complete, VectorProjection projection = VectorProjection.Sphere,
        int overscale = 1)
    {
        complete = true;
        var clock = Stopwatch.StartNew();
        Dictionary<string, List<StyleFeature>>? data = null;
        List<StyleFeature>? geo = null;
        if (source is VectorSource vector)
        {
            data = style.Tile(vector, key, ref complete, fetch, missing);
            if (data is null && !complete) return null;
        }
        else if (source is GeoJsonSource geoJson)
        {
            // the whole collection, in world coordinates; what lies outside the tile is clipped by the
            // shader. GL JS tiles GeoJSON with geojson-vt, which rounds to 8192 steps a tile: so here
            geo = style.Features(geoJson, ref complete);
            if (!complete) return null;
        }

        var b = new Builder(key, projection, grid: source is GeoJsonSource) { Overscale = Math.Max(1, overscale) };
        var ctx = new StyleContext { Zoom = key.Zoom, GlobalState = style.GlobalState, Document = style };
        if (withBackground) b.Background();
        foreach (var layer in layers)
        {
            if (layer.Type == "background") continue;
            // GL JS's worker_tile.ts skips a layer hidden at the tile's (overscaled) zoom, `zoom >= maxzoom`:
            // Liberty's flat `building` (maxzoom 14) was a quarter of every street-level tile's bytes, built
            // and uploaded, never drawn (2026-11-22). Overscaled further, the zoom only grows: still hidden.
            // Its other half, `zoom < floor(minzoom)`, is not followed: a tilted view draws the far z13
            // tiles at the camera's zoom, and their 3D buildings (minzoom 14) are wanted there (ROADMAP 4f).
            if (key.Zoom + (layer.IsSymbol ? Math.Log2(b.Overscale) : 0) >= layer.Layer.MaxZoom) continue;
            List<StyleFeature>? features = geo;
            if (features is null && (data is null || layer.Layer.SourceLayer is not { } sl || !data.TryGetValue(sl, out features))) continue;
            try
            {
                if (layer.IsSymbol)
                {
                    // GL JS lays symbols out at the overscaled tile's zoom (past the source's maxzoom)
                    ctx.Zoom = key.Zoom + Math.Log2(b.Overscale);
                    try { b.Symbol(layer, features, ctx, style, fetch, missing, ref complete); }
                    finally { ctx.Zoom = key.Zoom; }
                }
                else if (layer.IsCircle) b.Circle(layer, features, ctx);
                else b.Layer(layer, features, ctx);
            }
            catch (StyleUnsupportedException e) when (style.Lenient) { style.Drop(layer.Layer.Id, e.Message); b.Rewind(); }
        }
        var bucket = b.Finish();
        bucket.Ms = clock.Elapsed.TotalMilliseconds;
        return bucket;
    }

    /// <summary>One layer's features into a bucket of their own (tests).</summary>
    internal static VectorBucket BuildFeatures(TileKey key, GpuStyleLayer layer, List<StyleFeature> features, StyleContext ctx)
    {
        var b = new Builder(key);
        b.Layer(layer, features, ctx);
        return b.Finish();
    }

    /// <summary>The background grid alone: it needs no data, so it is drawn at once while the tile's data is on its way.</summary>
    public static VectorBucket BackgroundBucket(TileKey key, VectorProjection projection = VectorProjection.Sphere)
    {
        var b = new Builder(key, projection);
        b.Background();
        return b.Finish();
    }

    sealed partial class Builder
    {
        readonly TileKey _key;
        readonly int _n;
        readonly D3 _centre;
        readonly double _maxSegment;   // tile-local; long edges are cut so their chords follow the sphere
        // one pair of lists per worker thread, kept with their capacity: growing new ones for every
        // tile reallocated megabytes several times per build
        [ThreadStatic] static List<VectorVertex>? t_vertices;
        [ThreadStatic] static List<uint>? t_indices;
        readonly List<VectorVertex> _vertices = t_vertices ??= new();
        readonly List<uint> _indices = t_indices ??= new();
        readonly List<VectorRange> _ranges = new();
        int _layerVertexStart, _layerIndexStart, _layerRangeStart;
        readonly GeometryFactory _factory = new();
        readonly List<ExtrudedVertex> _line = new();
        // fill-extrusion: the height (in metres) every vertex is raised by, and the N a roof carries
        double _raise;
        float _fillN;
        bool _extruding;   // Width carries _raise (VectorVertex)
        readonly Dictionary<ExtrudedVertex, uint> _seen = new();

        readonly bool _flat, _grid;
        // GL JS's overscaling: the tile is shown this many times its own size (past the source's
        // maxzoom), and symbols are laid out for that - spacing and label lengths in its pixels
        public int Overscale = 1;

        public Builder(TileKey key, VectorProjection projection = VectorProjection.Sphere, bool grid = false)
        {
            _key = key;
            _flat = projection == VectorProjection.Mercator;
            _grid = grid;
            _vertices.Clear();
            _indices.Clear();
            _symbolVertices.Clear();
            _symbolIndices.Clear();
            _n = 1 << key.Zoom;
            // the plane: x east, y north (minus Mercator's y), in world units (the world is 1 wide)
            _centre = _flat ? new D3((key.X + 0.5) / _n, -(key.Y + 0.5) / _n, 0) : TileGeometry.Centre(key);
            // An edge's chord must sag less than the shader's lift (0.0015 of the distance to the eye)
            // at the distance a tile of this zoom is seen from: an eighth of a tile, as the globe's own
            // tiles are cut, and 2 degrees at most for the coarsest. Cut by 2 degrees only, a zoom-8
            // ocean square was two flat triangles sagging 1 km, seen from 300 km where the lift is
            // 450 m: discs of background in the sea (the user, 2026-11-21). From zoom 11 on a whole
            // tile sags under 2 m and nothing is cut.
            _maxSegment = key.Zoom > 10 || _flat ? double.PositiveInfinity : Math.Min(Extent / 8, 2 * Extent * _n / 360.0);
        }

        public void Rewind()
        {
            _vertices.RemoveRange(_layerVertexStart, _vertices.Count - _layerVertexStart);
            _indices.RemoveRange(_layerIndexStart, _indices.Count - _layerIndexStart);
            _ranges.RemoveRange(_layerRangeStart, _ranges.Count - _layerRangeStart);
            _symbolVertices.RemoveRange(_layerSymbolVertexStart, _symbolVertices.Count - _layerSymbolVertexStart);
            _symbolIndices.RemoveRange(_layerSymbolIndexStart, _symbolIndices.Count - _layerSymbolIndexStart);
            _labels.RemoveRange(_layerLabelStart, _labels.Count - _layerLabelStart);
            _lineLabels.RemoveRange(_layerLineLabelStart, _lineLabels.Count - _layerLineLabelStart);
            RewindCircles();
        }

        /// <summary>
        /// The 32-bit indices as 16-bit ones, each range cut into segments whose vertices lie within
        /// 65536 of their lowest - its BaseVertex (GL JS's SegmentVector; ROADMAP 4g: indices were 37%
        /// of a street-level tile). Cut between primitives (2 indices an outline segment, 3 a triangle);
        /// a span two layers share (ShareOrKeep) is cut once, and both get the same segments.
        /// </summary>
        /// <remarks>
        /// A primitive whose own vertices lie further apart (a triangle of a polygon of more than 65536
        /// vertices, possible in a coarse tile's coastline) gets copies of its vertices at the end of
        /// <paramref name="vertices"/>, a segment of its own.
        /// </remarks>
        internal static List<VectorRange> Segments(Span<uint> source, ushort[] target, List<VectorRange> ranges, List<VectorVertex> vertices)
        {
            var result = new List<VectorRange>(ranges.Count);
            var cut = new Dictionary<(int Start, int Count), List<(int Start, int Count, int Base)>>();
            foreach (var r in ranges)
            {
                // labels', icons' and circles' ranges index their own 32-bit arrays
                if (r.Kind is RangeKind.Text or RangeKind.Icon or RangeKind.Circle) { result.Add(r); continue; }
                if (!cut.TryGetValue((r.Start, r.Count), out var segments))
                {
                    segments = new List<(int, int, int)>();
                    int step = r.Kind == RangeKind.Outline ? 2 : 3;
                    int start = r.Start, end = r.Start + r.Count;
                    while (start < end)
                    {
                        uint min = uint.MaxValue, max = 0;
                        int at = start;
                        while (at < end)
                        {
                            uint lo = min, hi = max;
                            for (int k = 0; k < step && at + k < end; k++) { lo = Math.Min(lo, source[at + k]); hi = Math.Max(hi, source[at + k]); }
                            if (at > start && hi - lo > ushort.MaxValue) break;   // this primitive starts the next segment
                            if (hi - lo > ushort.MaxValue)
                            {
                                // too wide alone: its own copies, side by side
                                for (int k = 0; k < step && at + k < end; k++)
                                {
                                    vertices.Add(vertices[(int)source[at + k]]);
                                    source[at + k] = (uint)(vertices.Count - 1);
                                }
                                lo = (uint)(vertices.Count - Math.Min(step, end - at)); hi = (uint)(vertices.Count - 1);
                            }
                            min = lo; max = hi;
                            at += step;
                        }
                        at = Math.Min(at, end);
                        for (int i = start; i < at; i++) target[i] = (ushort)(source[i] - min);
                        segments.Add((start, at - start, (int)min));
                        start = at;
                    }
                    cut[(r.Start, r.Count)] = segments;
                }
                foreach (var (start, count, baseVertex) in segments) result.Add(r with { Start = start, Count = count, BaseVertex = baseVertex });
            }
            return result;
        }

        public VectorBucket Finish()
        {
            // segments first: a primitive too wide for 16 bits adds copies of its vertices
            var indices = _indices.Count == 0 ? Array.Empty<ushort>() : ArrayPool<ushort>.Shared.Rent(_indices.Count);
            var ranges = Segments(CollectionsMarshal.AsSpan(_indices), indices, _ranges, _vertices);
            var vertices = _vertices.Count == 0 ? Array.Empty<VectorVertex>() : ArrayPool<VectorVertex>.Shared.Rent(_vertices.Count);
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vertices).CopyTo(vertices);
            return new()
            {
                Key = _key, OverscaledZoom = _key.Zoom + System.Numerics.BitOperations.Log2((uint)Overscale), Centre = _centre, Vertices = vertices, Indices = indices,
                VertexCount = _vertices.Count, IndexCount = _indices.Count, Ranges = ranges,
                SymbolVertices = _symbolVertices.ToArray(), SymbolIndices = _symbolIndices.ToArray(), Labels = _labels.ToList(), LineLabels = _lineLabels.ToList(),
                IconModes = new Dictionary<int, (bool, bool)>(_iconModes),
                CircleVertices = _circleVertices.ToArray(), CircleIndices = _circleIndices.ToArray(),
            };
        }

        // the whole square, a grid so it bends with the sphere; the background layers draw it. Cells of
        // 2 degrees at most, as every other edge here: 8 cells at zoom 1 are 22 degrees, whose middles
        // sank 1.8% of the radius under the globe - a chequerboard seen from afar (the user, 2026-11-21)
        public void Background()
        {
            int start = _indices.Count;
            uint first = (uint)_vertices.Count;
            // a little past the square on every side: the clip rectangle, not the mesh's edge, decides
            // where it ends, so neighbours meet without a crack (two meshes' edges are not bit-equal)
            int cells = Math.Max(8, (int)Math.Ceiling(Extent / _maxSegment));
            int side = cells + 1;
            double pad = Extent * 0.01, span = Extent + 2 * pad;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                    _vertices.Add(Vertex(span * i / cells - pad, span * j / cells - pad, 0, 0, 0, 1, uint.MaxValue));
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
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
            _layerSymbolVertexStart = _symbolVertices.Count; _layerSymbolIndexStart = _symbolIndices.Count; _layerLabelStart = _labels.Count;
            _layerLineLabelStart = _lineLabels.Count;
            var l = layer.Layer;
            // GL JS builds a bucket at the tile's zoom: filters and layout at it. Not the layer's zoom
            // range: one bucket serves every zoom the tile is overscaled to (z14 data at z19), so
            // minzoom/maxzoom are checked per frame (GpuStyleLayer.Uniforms).
            int fillStart = _indices.Count;
            var outlines = new List<(StyleFeature f, uint color)>();
            foreach (var f in features)
            {
                if (f.Kind == StyleFeature.Shape.Point) continue;
                ctx.Use(f);
                if (l.Filter is { } filter && !Expression.FilterFeature(filter, ctx, l.Id)) continue;
                double opacity = layer.OpacityPerFeature ? Expression.Number(layer.Opacity, ctx, 1) : 1;
                if (layer.IsExtrusion)
                {
                    if (f.Kind != StyleFeature.Shape.Polygon) continue;
                    var color = layer.ColorPerFeature ? Expression.Color(layer.Color, ctx, StyleColor.Black) : new StyleColor(1, 1, 1, 1);
                    double height = Math.Max(0, Expression.Number(layer.Height, ctx, 0));
                    double floor = Math.Clamp(Expression.Number(layer.Base, ctx, 0), 0, height);
                    Extrusion(f, Pack(color.WithOpacity(opacity)), height, floor);
                    continue;
                }
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
            if (layer.IsExtrusion)
            {
                if (_indices.Count > fillStart) _ranges.Add(new VectorRange(layer.Index, RangeKind.Extrusion, fillStart, _indices.Count - fillStart));
            }
            else if (layer.Type == "fill")
            {
                if (_indices.Count > fillStart) _ranges.Add(new VectorRange(layer.Index, RangeKind.Fill, fillStart, _indices.Count - fillStart));
                int outlineStart = _indices.Count;
                // GL JS's antialiasing outline, one device pixel: a 1 px line here
                foreach (var (f, color) in outlines) Outline(f, color);
                if (_indices.Count > outlineStart) _ranges.Add(new VectorRange(layer.Index, RangeKind.Outline, outlineStart, _indices.Count - outlineStart));
            }
            else if (_indices.Count > fillStart)
            {
                _ranges.Add(new VectorRange(layer.Index, RangeKind.Line, fillStart, _indices.Count - fillStart));
                ShareOrKeep(_layerVertexStart, fillStart);
            }
        }

        // A line layer whose vertices and indices are those of an earlier one, byte for byte, draws that
        // one's: Liberty's road casings and roads are the same lines twice (2026-11-23: two pairs were
        // half of a 8.3-MB z13 tile at Kyoto). GL JS shares a bucket between layers of the same layout
        // (groupByLayout); here the result is compared, so any difference - a filter, a cap, a colour or
        // width per feature (both in the vertex) - keeps the layer its own.
        readonly List<(int VStart, int VCount, int IStart, int ICount, ulong Hash)> _lineSlices = new();

        void ShareOrKeep(int vStart, int iStart)
        {
            int vCount = _vertices.Count - vStart, iCount = _indices.Count - iStart;
            if (vCount == 0 || iCount == 0) return;
            var vertices = CollectionsMarshal.AsSpan(_vertices);
            var indices = CollectionsMarshal.AsSpan(_indices);
            ulong hash = Hash(vertices.Slice(vStart, vCount), indices.Slice(iStart, iCount), vStart);
            foreach (var s in _lineSlices)
            {
                if (s.Hash != hash || s.VCount != vCount || s.ICount != iCount) continue;
                if (!MemoryMarshal.AsBytes(vertices.Slice(s.VStart, vCount)).SequenceEqual(MemoryMarshal.AsBytes(vertices.Slice(vStart, vCount)))) continue;
                bool same = true;
                for (int i = 0; i < iCount && same; i++) same = indices[s.IStart + i] - s.VStart == indices[iStart + i] - vStart;
                if (!same) continue;
                _vertices.RemoveRange(vStart, vCount);
                _indices.RemoveRange(iStart, iCount);
                _ranges[^1] = _ranges[^1] with { Start = s.IStart };
                return;
            }
            _lineSlices.Add((vStart, vCount, iStart, iCount, hash));
        }

        // FNV-1a over the vertices' bytes and the indices relative to the first vertex
        static ulong Hash(ReadOnlySpan<VectorVertex> vertices, ReadOnlySpan<uint> indices, int vStart)
        {
            ulong h = 14695981039346656037;
            foreach (var b in MemoryMarshal.AsBytes(vertices)) h = (h ^ b) * 1099511628211;
            foreach (var i in indices) h = (h ^ (i - (uint)vStart)) * 1099511628211;
            return h;
        }

        // GL JS's fill_extrusion_bucket: the roof is the polygon raised to its height, each edge of each
        // ring a wall from base to height, facing out. The shader lights them as fill_extrusion.vertex
        // does; N carries the wall's vertical gradient, computed here because it depends only on the
        // vertex: clamp((top + base) * sqrt(height / 150), 0.84, 1) with the default light intensity.
        void Extrusion(StyleFeature f, uint color, double height, double floor)
        {
            _extruding = true;
            try { ExtrusionCore(f, color, height, floor); }
            finally { _extruding = false; _raise = 0; _fillN = 0; }
        }

        void ExtrusionCore(StyleFeature f, uint color, double height, double floor)
        {
            _raise = height; _fillN = 1;
            Fill(f, color);
            _raise = 0; _fillN = 0;
            if (height <= floor) return;
            double scale = Math.Sqrt(height / 150);
            float bottomN = (float)Math.Clamp((0 + floor) * scale, 0.84, 1), topN = (float)Math.Clamp((1 + floor) * scale, 0.84, 1);
            foreach (var polygon in f.Parts)
                for (int r = 0; r < polygon.Count; r++)
                {
                    var pts = _ring;
                    pts.Clear();
                    foreach (var (x, y) in polygon[r])
                    {
                        var c = Local(x, y);
                        if (pts.Count == 0 || pts[^1] != c) pts.Add(c);
                    }
                    if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
                    if (pts.Count < 3) continue;
                    double area = 0;
                    for (int i = 0; i < pts.Count; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Count]; area += a.X * b.Y - b.X * a.Y; }
                    // outward: right of the edge for a ring wound counter-clockwise (area > 0), left
                    // otherwise; a hole's walls face into the hole, away from the building
                    double side = (area > 0 ? 1 : -1) * (r == 0 ? 1 : -1);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                        if (len == 0) continue;
                        float nx = (float)(side * dy / len), ny = (float)(side * -dx / len);
                        uint i0 = (uint)_vertices.Count;
                        _raise = floor;
                        _vertices.Add(Vertex(a.X, a.Y, nx, ny, bottomN, (float)height, color));
                        _vertices.Add(Vertex(b.X, b.Y, nx, ny, bottomN, (float)height, color));
                        _raise = height;
                        _vertices.Add(Vertex(a.X, a.Y, nx, ny, topN, (float)height, color));
                        _vertices.Add(Vertex(b.X, b.Y, nx, ny, topN, (float)height, color));
                        _indices.Add(i0); _indices.Add(i0 + 1); _indices.Add(i0 + 2);
                        _indices.Add(i0 + 1); _indices.Add(i0 + 3); _indices.Add(i0 + 2);
                    }
                    _raise = 0;
                }
        }

        void Fill(StyleFeature f, uint color)
        {
            foreach (var polygon in f.Parts)
            {
                if (polygon.Count == 0) continue;
                // most polygons (buildings) have no hole and few corners: clipped here without NTS's
                // objects, which made buildings 85 of a tile's 118 ms (Liberty, Kyoto, 2026-11-21)
                if (polygon.Count == 1 && polygon[0].Length <= 128 && SimpleRing(polygon[0], color)) continue;
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

        readonly List<(double X, double Y)> _ring = new();
        readonly List<int> _left = new();

        // ear clipping of one ring without holes; false when it is not simple enough (left to NTS)
        bool SimpleRing(VL.Globe.Engine.Ring ring, uint color)
        {
            var pts = _ring;
            pts.Clear();
            foreach (var (x, y) in ring)
            {
                var c = Local(x, y);
                if (pts.Count == 0 || pts[^1] != c) pts.Add(c);
            }
            if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return true;   // nothing to fill
            double area = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++) area += (pts[j].X - pts[i].X) * (pts[j].Y + pts[i].Y);
            if (area == 0) return true;
            double sign = Math.Sign(area);
            var left = _left;
            left.Clear();
            for (int i = 0; i < pts.Count; i++) left.Add(i);
            var tris = new List<int>(3 * pts.Count);
            int guard = 0;
            while (left.Count > 3)
            {
                bool cut = false;
                for (int k = 0; k < left.Count; k++)
                {
                    int ia = left[(k + left.Count - 1) % left.Count], ib = left[k], ic = left[(k + 1) % left.Count];
                    var (a, b, c) = (pts[ia], pts[ib], pts[ic]);
                    double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (cross * sign <= 0) continue;   // reflex or flat: not an ear
                    bool inside = false;
                    foreach (int m in left)
                    {
                        if (m == ia || m == ib || m == ic) continue;
                        if (In(pts[m], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    left.RemoveAt(k);
                    cut = true;
                    break;
                }
                if (!cut || ++guard > 4 * pts.Count) return false;   // self-intersecting: NTS decides
            }
            tris.Add(left[0]); tris.Add(left[1]); tris.Add(left[2]);
            uint first = (uint)_vertices.Count;
            foreach (var (x, y) in pts) _vertices.Add(Vertex(x, y, 0, 0, _fillN, 1, color));
            if (pts.Count > 0 && Max(pts) > _maxSegment)
            {
                // a coarse tile's large polygon: through the splitting path, so it follows the sphere
                _vertices.RemoveRange((int)first, pts.Count);
                var seen = new Dictionary<(double, double), uint>();
                for (int t = 0; t < tris.Count; t += 3)
                    Triangle(new Coordinate(pts[tris[t]].X, pts[tris[t]].Y), new Coordinate(pts[tris[t + 1]].X, pts[tris[t + 1]].Y),
                        new Coordinate(pts[tris[t + 2]].X, pts[tris[t + 2]].Y), color, seen, 0);
                return true;
            }
            foreach (int t in tris) _indices.Add(first + (uint)t);
            return true;

            static bool In((double X, double Y) p, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
            {
                double d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
                double d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
                double d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }
            static double Max(List<(double X, double Y)> q)
            {
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                foreach (var (x, y) in q) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
                return Math.Max(x1 - x0, y1 - y0);
            }
        }

        LinearRing? Ring(VL.Globe.Engine.Ring ring)
        {
            var cs = new List<Coordinate>(ring.Length + 1);
            foreach (var (x, y) in ring)
            {
                var (cx, cy) = Local(x, y); var c = new Coordinate(cx, cy);
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
                _vertices.Add(Vertex(p.X, p.Y, 0, 0, _fillN, 1, color));
                seen[(p.X, p.Y)] = i;
                return i;
            }
            static Coordinate Mid(Coordinate p, Coordinate q) => new((p.X + q.X) / 2, (p.Y + q.Y) / 2);
        }

        // GL JS's fill outline is gl.LINES: one device pixel along each edge, no joins - a line list
        // here too, one vertex per corner. As quads it was 4 vertices an edge, 385k a street-level
        // tile; through the full line path, 42 of a tile's 56 ms of buildings (2026-11-21).
        void Outline(StyleFeature f, uint color)
        {
            foreach (var polygon in f.Parts)
                foreach (var ring in polygon)
                {
                    var pts = _ring;
                    pts.Clear();
                    for (int k = 0; k < ring.Length; k++)
                    {
                        var p = Local(ring[k].X, ring[k].Y);
                        if (pts.Count > 0)
                        {
                            var q = pts[^1];
                            double len = Math.Sqrt((p.Item1 - q.X) * (p.Item1 - q.X) + (p.Item2 - q.Y) * (p.Item2 - q.Y));
                            if (len == 0) continue;
                            int cuts = (int)Math.Min(64, Math.Ceiling(len / _maxSegment));
                            for (int c = 1; c < cuts; c++) pts.Add((q.X + (p.Item1 - q.X) * c / cuts, q.Y + (p.Item2 - q.Y) * c / cuts));
                        }
                        pts.Add(p);
                    }
                    if (pts.Count < 2) continue;
                    uint first = (uint)_vertices.Count;
                    foreach (var (x, y) in pts) _vertices.Add(Vertex(x, y, 0, 0, 0, 1, color));
                    for (int k = 1; k < pts.Count; k++) { _indices.Add(first + (uint)(k - 1)); _indices.Add(first + (uint)k); }
                }
        }

        // a line's points, one list a builder (it was a new one a feature)
        readonly List<(double X, double Y)> _linePoints = new();

        void Lines(StyleFeature f, bool isPolygon, string join, string cap, double miterLimit, double roundLimit, float width, uint color)
        {
            var points = _linePoints;
            foreach (var part in f.Parts)
                foreach (var line in part)
                {
                    points.Clear();
                    for (int k = 0; k < line.Length; k++)
                    {
                        var p = Local(line[k].X, line[k].Y);
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

        // world (Mercator 0..1) to the tile's own units, 512 a side; GeoJSON on geojson-vt's grid
        (double X, double Y) Local(double x, double y)
        {
            double lx = (x * _n - _key.X) * Extent, ly = (y * _n - _key.Y) * Extent;
            return _grid ? (Math.Round(lx * 16) / 16, Math.Round(ly * 16) / 16) : (lx, ly);
        }

        // the vertex keeps its place in the tile; GlobeVector.sdsl projects it (TileProjection). A
        // fill-extrusion vertex carries the metres it is raised by in Width (a line's width scale else)
        VectorVertex Vertex(double lx, double ly, float qx, float qy, float n, float width, uint color)
        {
            var v = new VectorVertex { Extrude = new Vector2(qx, qy), N = n, Width = _extruding ? (float)_raise : width, Color = color };
            v.SetUv(lx / Extent, ly / Extent);
            return v;
        }

        /// <summary>A point of the tile (its own units) relative to the bucket's centre, in double: a symbol's anchor, a label's line.</summary>
        Vector3 Relative(double lx, double ly) => TileProjection.RelativeExact(_key, lx / Extent, ly / Extent, 0, _flat).ToVector3();
    }

    // straight sRGB bytes, R in the lowest byte (R8G8B8A8_UNorm)
    internal static uint Pack(StyleColor c)
    {
        static uint B(double v) => (uint)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return B(c.R) | (B(c.G) << 8) | (B(c.B) << 16) | (B(c.A) << 24);
    }
}
