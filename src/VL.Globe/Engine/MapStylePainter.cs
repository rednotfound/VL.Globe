using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace VL.Globe.Engine;

// A MapLibre style (the subset in MapStyle.cs) drawn with SkiaSharp, the renderer of the draped
// tile overlays. A viewport says where the world lands in pixels: the whole canvas of a MapLibre
// render test (ROADMAP 4b) or one globe tile's square (4c), so the render tests measure the code
// that drapes. Geometry is kept in Web Mercator world coordinates (0..1, y down) - what a vector
// tile holds, and linear on the canvas. Pure: tested without a GPU.

/// <summary>One feature of a source: geometry in Web Mercator world coordinates (0..1, y down), and its properties.</summary>
internal sealed class StyleFeature
{
    public enum Shape { Point, Line, Polygon }
    public Shape Kind;
    // Point: each part one coordinate. Line: each part one line. Polygon: each part one polygon, its rings.
    public List<List<(double X, double Y)[]>> Parts = new();
    public Dictionary<string, object?> Properties = new();
    public object? Id;
    public string GeometryType => Kind switch { Shape.Point => "Point", Shape.Line => "LineString", _ => "Polygon" };

    /// <summary>The world rectangle the geometry covers, computed once (features are not changed after reading).</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds
    {
        get
        {
            if (_bounds is { } b) return b;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var part in Parts)
                foreach (var ring in part)
                    foreach (var (x, y) in ring)
                    {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
            var r = (x0, y0, x1, y1);
            _bounds = r;
            return r;
        }
    }
    (double, double, double, double)? _bounds;
}

/// <summary>Where the map lies on a canvas: a Web Mercator view with MapLibre's 512 px world at zoom 0.</summary>
internal readonly record struct MapViewport(double CenterX, double CenterY, double Zoom, int Width, int Height, double PixelRatio)
{
    /// <summary>When above 0, world coordinates are rounded to this step before projecting, as GL JS's tiling does.</summary>
    public double Grid { get; init; }

    public static MapViewport FromLonLat(double lon, double lat, double zoom, int width, int height, double pixelRatio)
        => new((lon + 180) / 360, TileGeometry.MercatorY(lat), zoom, width, height, pixelRatio);

    /// <summary>A tile's square filling a size x size canvas.</summary>
    public static MapViewport OfTile(TileKey tile, int size)
    {
        double n = tile.TilesPerSide;
        return new((tile.X + 0.5) / n, (tile.Y + 0.5) / n, tile.Zoom + Math.Log2(size / 512.0), size, size, 1);
    }

    double WorldPixels => 512 * Math.Pow(2, Zoom);

    public SKPoint Project(double x, double y)
    {
        if (Grid > 0) { x = Math.Floor(x / Grid + 0.5) * Grid; y = Math.Floor(y / Grid + 0.5) * Grid; }
        return new((float)(((x - CenterX) * WorldPixels + Width / 2.0) * PixelRatio),
                   (float)(((y - CenterY) * WorldPixels + Height / 2.0) * PixelRatio));
    }

    /// <summary>The world rectangle the canvas shows.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds
    {
        get
        {
            double hw = Width / 2.0 / WorldPixels, hh = Height / 2.0 / WorldPixels;
            return (CenterX - hw, CenterY - hh, CenterX + hw, CenterY + hh);
        }
    }
}

/// <summary>A source of a style: inline GeoJSON, or vector tiles fetched by URL.</summary>
internal abstract class StyleSource { }

internal sealed class GeoJsonSource : StyleSource
{
    public List<StyleFeature>? Features;   // inline, or once loaded from Url
    public string? Url;
}

internal sealed class VectorSource : StyleSource
{
    public string[] Tiles = Array.Empty<string>();
    public int MinZoom, MaxZoom = 22, TileSize = 512;
    public bool Tms;
    public string Attribution = "";

    public string Url(TileKey t)
    {
        int y = Tms ? (1 << t.Zoom) - 1 - t.Y : t.Y;
        return Tiles[Math.Abs(t.X + t.Y) % Tiles.Length]
            .Replace("{z}", t.Zoom.ToString(CultureInfo.InvariantCulture))
            .Replace("{x}", t.X.ToString(CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>A parsed style: its layers, its sources, the camera of a render test.</summary>
/// <remarks>
/// Strict (the render tests): anything outside the subset throws. Lenient (a real style on the
/// globe): an unsupported source or layer is left out, an unsupported property is ignored, a layer
/// that throws while drawing is dropped - each named in <see cref="Skipped"/>. Drawing may run on
/// several threads at once: the caches are locked.
/// </remarks>
internal sealed class MapStyleDocument
{
    public sealed record Layer(string Id, string Type, string? Source, string? SourceLayer, JsonElement? Filter,
        JsonElement Paint, JsonElement Layout, double MinZoom, double MaxZoom);

    public List<Layer> Layers = new();
    public Dictionary<string, StyleSource> Sources = new();
    public Dictionary<string, JsonElement> GlobalState = new();
    public double CenterLon, CenterLat, Zoom;
    public bool Lenient;

    /// <summary>What a lenient parse or draw left out, and why: "layer id: reason".</summary>
    public IReadOnlyCollection<string> Skipped { get { lock (_skipped) return _skipped.ToList(); } }
    readonly HashSet<string> _skipped = new();

    /// <summary>Vector tiles decoded so far, by zoom level: what drawing the globe has asked for.</summary>
    public IReadOnlyDictionary<int, int> DecodedByZoom { get { lock (_decoded) return new SortedDictionary<int, int>(_decoded); } }
    readonly Dictionary<int, int> _decoded = new();
    readonly HashSet<string> _dropped = new();   // layers that threw while drawing

    /// <summary>Fetches a resource (a vector tile, GeoJSON data) by URL; null when it does not exist or is not here yet. Set by the host.</summary>
    public Func<string, byte[]?>? Load;
    /// <summary>True while a resource Load returned null for is still on its way. Set by the host.</summary>
    public Func<string, bool>? Pending;
    /// <summary>A resource's bytes only if they are already here (memory or disk), never a fetch; Load when unset. Set by the host.</summary>
    public Func<string, byte[]?>? Peek;

    // decoded tiles, the most recently used last; a miss that is pending is not remembered
    const int TileCacheSize = 256;
    readonly Dictionary<(string, TileKey), LinkedListNode<((string, TileKey) key, Dictionary<string, List<StyleFeature>>? layers)>> _tiles = new();
    readonly LinkedList<((string, TileKey) key, Dictionary<string, List<StyleFeature>>? layers)> _lru = new();

    /// <summary>A vector tile's layers, decoded once; null when it cannot be had, and <paramref name="complete"/> false when it may still come.</summary>
    public Dictionary<string, List<StyleFeature>>? Tile(VectorSource source, TileKey key, ref bool complete, bool fetch = true, ICollection<string>? missing = null)
    {
        var url = source.Url(key);
        var k = (url, key);   // keyed by tile too: a template without {z}/{x}/{y} serves one file for every tile
        if (Cached(k, out var hit)) return hit;
        // one decode per tile: the overlays of the 16 globe tiles a vector tile covers are drawn on
        // several threads at once, and each decoded it (18 decodes for 6 tiles, 2026-11-21)
        lock (_gates.GetOrAdd(k, _ => new object()))
        {
            if (Cached(k, out hit)) return hit;
            return LoadAndDecode(url, key, k, ref complete, fetch, missing);
        }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<(string, TileKey), object> _gates = new();

    bool Cached((string, TileKey) k, out Dictionary<string, List<StyleFeature>>? layers)
    {
        lock (_tiles)
        {
            if (_tiles.TryGetValue(k, out var node)) { _lru.Remove(node); _lru.AddLast(node); layers = node.Value.layers; return true; }
        }
        layers = null;
        return false;
    }

    Dictionary<string, List<StyleFeature>>? LoadAndDecode(string url, TileKey key, (string, TileKey) k, ref bool complete, bool fetch, ICollection<string>? missing)
    {
        // without fetch (a stand-in tile on the globe) only what is already here is drawn; a miss
        // leaves it incomplete, so it is drawn again once the tile has come for another reason
        var bytes = fetch || Peek is null ? Load?.Invoke(url) : Peek(url);
        if (bytes is null && (!fetch && Peek != null || Pending?.Invoke(url) == true)) { complete = false; missing?.Add(url); return null; }
        Dictionary<string, List<StyleFeature>>? layers = null;
        if (bytes != null)
        {
            lock (_decoded) _decoded[key.Zoom] = _decoded.GetValueOrDefault(key.Zoom) + 1;
            try { layers = VectorTile.Decode(bytes, key); }
            catch (Exception e) when (e is System.IO.InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                Skip($"tile {url}: {e.Message}");
            }
        }
        lock (_tiles)
        {
            if (!_tiles.ContainsKey(k))
            {
                _tiles[k] = _lru.AddLast((k, layers));
                if (_lru.Count > TileCacheSize) { _tiles.Remove(_lru.First!.Value.key); _lru.RemoveFirst(); }
            }
        }
        return layers;
    }

    /// <summary>A GeoJSON source's features, loaded once; unreadable or invalid data draws nothing, as in GL JS.</summary>
    public List<StyleFeature> Features(GeoJsonSource g, ref bool complete)
    {
        lock (g)
        {
            if (g.Features != null) return g.Features;
            var bytes = g.Url is null ? null : Load?.Invoke(g.Url);
            if (bytes is null && g.Url != null && Pending?.Invoke(g.Url) == true) { complete = false; return new List<StyleFeature>(); }
            var features = new List<StyleFeature>();
            if (bytes != null)
            {
                try
                {
                    using var json = JsonDocument.Parse(bytes);
                    GeoJson.Read(json.RootElement, features);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
                {
                    features.Clear();
                }
            }
            return g.Features = features;
        }
    }

    internal void Skip(string what) { lock (_skipped) _skipped.Add(what); }
    internal bool IsDropped(string layerId) { lock (_skipped) return _dropped.Contains(layerId); }
    internal void Drop(string layerId, string why) { lock (_skipped) { _dropped.Add(layerId); _skipped.Add($"{layerId}: {why}"); } }

    static readonly HashSet<string> RootKeys = new() { "version", "name", "metadata", "center", "zoom", "bearing", "pitch", "sources", "layers", "state", "sprite", "glyphs", "transition" };
    static readonly HashSet<string> LayerKeys = new() { "id", "type", "source", "source-layer", "filter", "paint", "layout", "minzoom", "maxzoom", "metadata" };
    static readonly Dictionary<string, HashSet<string>> PaintKeys = new()
    {
        ["background"] = new() { "background-color", "background-opacity" },
        ["fill"] = new() { "fill-color", "fill-opacity", "fill-outline-color", "fill-antialias" },
        ["line"] = new() { "line-color", "line-opacity", "line-width" },
        // lenient parses only (see Parse): drawn flat by the GPU path, its height not yet
        ["fill-extrusion"] = new() { "fill-extrusion-color", "fill-extrusion-opacity" },
    };
    static readonly Dictionary<string, HashSet<string>> LayoutKeys = new()
    {
        ["background"] = new() { "visibility" },
        ["fill"] = new() { "visibility" },
        ["fill-extrusion"] = new() { "visibility" },
        ["line"] = new() { "visibility", "line-cap", "line-join", "line-miter-limit", "line-round-limit" },
    };

    /// <summary>Reads a style JSON. Strict: throws StyleUnsupportedException for anything outside the subset.
    /// Lenient: leaves it out and names it in Skipped. <paramref name="tileJson"/> resolves a vector source's "url".</summary>
    public static MapStyleDocument Parse(JsonElement root, bool lenient = false, Func<string, JsonElement?>? tileJson = null)
    {
        var doc = new MapStyleDocument { Lenient = lenient };
        // in a lenient parse, an unsupported part is skipped; in a strict one it throws
        void Unsupported(string what) { if (lenient) doc.Skip(what); else throw new StyleUnsupportedException(what); }

        foreach (var p in root.EnumerateObject())
            if (!RootKeys.Contains(p.Name) && !lenient) throw new StyleUnsupportedException($"style.{p.Name}");
        if (!lenient)
        {
            // a render test's camera; on the globe the camera is the globe's
            if (root.TryGetProperty("bearing", out var bearing) && bearing.GetDouble() != 0) throw new StyleUnsupportedException("bearing");
            if (root.TryGetProperty("pitch", out var pitch) && pitch.GetDouble() != 0) throw new StyleUnsupportedException("pitch");
        }
        if (root.TryGetProperty("center", out var c) && c.ValueKind == JsonValueKind.Array) { doc.CenterLon = c[0].GetDouble(); doc.CenterLat = c[1].GetDouble(); }
        if (root.TryGetProperty("zoom", out var z) && z.ValueKind == JsonValueKind.Number) doc.Zoom = z.GetDouble();
        if (root.TryGetProperty("state", out var state))
            foreach (var s in state.EnumerateObject())
                if (s.Value.TryGetProperty("default", out var d)) doc.GlobalState[s.Name] = d.Clone();

        if (root.TryGetProperty("sources", out var sources))
            foreach (var s in sources.EnumerateObject())
            {
                try { doc.Sources[s.Name] = ReadSource(s.Value, lenient, tileJson); }
                catch (StyleUnsupportedException e) when (lenient) { doc.Skip($"source {s.Name}: {e.Message}"); }
            }

        foreach (var l in root.GetProperty("layers").EnumerateArray())
        {
            string id = l.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
            try
            {
                foreach (var p in l.EnumerateObject())
                    if (!LayerKeys.Contains(p.Name)) Unsupported($"{id}: layer.{p.Name}");
                var type = l.GetProperty("type").GetString()!;
                if (!PaintKeys.ContainsKey(type)) throw new StyleUnsupportedException($"{type} layer");
                // A real style hands its buildings from a flat fill to fill-extrusion (Liberty: building
                // below z14, building-3d from 14), so without it buildings vanished as one zoomed in (the
                // user, 2026-11-21). A lenient parse keeps it, drawn flat by the GPU path until 3D is
                // built; a strict one (MapLibre's fixtures, which are 3D) still refuses it.
                if (type == "fill-extrusion")
                {
                    if (!lenient) throw new StyleUnsupportedException("fill-extrusion layer");
                    doc.Skip($"{id}: fill-extrusion drawn flat");
                }
                var paint = l.TryGetProperty("paint", out var pp) ? pp.Clone() : Empty;
                var layout = l.TryGetProperty("layout", out var ll) ? ll.Clone() : Empty;
                foreach (var p in paint.EnumerateObject())
                {
                    if (PaintKeys[type].Contains(p.Name) || p.Name.EndsWith("-transition")) continue;
                    // without these the layer would be drawn as something else (a pattern fill as
                    // black, a casing's two lines as one): leave the layer out instead
                    if (p.Name is "fill-pattern" or "line-pattern" or "line-gap-width" or "line-gradient" or "line-offset" or "fill-translate" or "line-translate"
                        or "fill-extrusion-pattern" or "fill-extrusion-translate")
                        throw new StyleUnsupportedException(p.Name);
                    if (type == "fill-extrusion") continue;   // its height and base: "drawn flat" says it
                    Unsupported($"{id}: {p.Name} ignored");
                }
                foreach (var p in layout.EnumerateObject())
                    if (!LayoutKeys[type].Contains(p.Name)) Unsupported($"{id}: {p.Name} ignored");
                string? source = l.TryGetProperty("source", out var src) ? src.GetString() : null;
                if (type != "background" && (source is null || !doc.Sources.ContainsKey(source))) throw new StyleUnsupportedException($"source '{source}'");
                string? sourceLayer = l.TryGetProperty("source-layer", out var sl) ? sl.GetString() : null;
                if (source != null && doc.Sources[source] is VectorSource && sourceLayer is null) throw new StyleUnsupportedException("a vector layer without source-layer");
                doc.Layers.Add(new Layer(id, type, source, sourceLayer,
                    l.TryGetProperty("filter", out var f) ? f.Clone() : null, paint, layout,
                    l.TryGetProperty("minzoom", out var mn) ? mn.GetDouble() : 0,
                    l.TryGetProperty("maxzoom", out var mx) ? mx.GetDouble() : 24));
            }
            catch (StyleUnsupportedException e) when (lenient) { doc.Skip($"{id}: {e.Message}"); }
        }
        return doc;
    }

    static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    static StyleSource ReadSource(JsonElement s, bool lenient, Func<string, JsonElement?>? tileJson)
    {
        var type = s.GetProperty("type").GetString();
        switch (type)
        {
            case "geojson":
            {
                foreach (var p in s.EnumerateObject())
                    if (!lenient && p.Name is not ("type" or "data" or "maxzoom" or "tolerance" or "buffer" or "attribution"))
                        throw new StyleUnsupportedException($"geojson source {p.Name}");
                var data = s.GetProperty("data");
                if (data.ValueKind == JsonValueKind.String) return new GeoJsonSource { Url = data.GetString() };
                var features = new List<StyleFeature>();
                try { GeoJson.Read(data, features); }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
                {
                    features.Clear();   // invalid GeoJSON draws nothing, as in GL JS
                }
                return new GeoJsonSource { Features = features };
            }
            case "vector":
            {
                var v = new VectorSource();
                // TileJSON first, then the source's own keys over it, as GL JS merges them
                if (s.TryGetProperty("url", out var url))
                {
                    var tj = url.GetString() is { } u ? tileJson?.Invoke(u) : null;
                    if (tj is null) throw new StyleUnsupportedException($"TileJSON not available: {url.GetString()}");
                    ReadVectorKeys(tj.Value, v, lenient: true);
                }
                ReadVectorKeys(s, v, lenient);
                if (v.Tiles.Length == 0) throw new StyleUnsupportedException("vector source without tiles");
                return v;
            }
        }
        throw new StyleUnsupportedException($"{type} source");
    }

    static void ReadVectorKeys(JsonElement s, VectorSource v, bool lenient)
    {
        foreach (var p in s.EnumerateObject())
            switch (p.Name)
            {
                case "tiles": v.Tiles = p.Value.EnumerateArray().Select(t => t.GetString()!).ToArray(); break;
                case "minzoom": v.MinZoom = (int)p.Value.GetDouble(); break;
                case "maxzoom": v.MaxZoom = (int)p.Value.GetDouble(); break;
                case "tileSize": v.TileSize = (int)p.Value.GetDouble(); break;
                case "scheme": v.Tms = p.Value.GetString() == "tms"; break;
                case "attribution": v.Attribution = p.Value.GetString() ?? ""; break;
                case "type": case "bounds": case "url": break;
                default: if (!lenient) throw new StyleUnsupportedException($"vector source {p.Name}"); break;
            }
    }
}

/// <summary>Draws a style's layers in order.</summary>
internal static class MapStylePainter
{
    /// <summary>Draws <paramref name="style"/> on <paramref name="canvas"/> (Premul) at the viewport's zoom.</summary>
    /// <returns>False when a tile or data it needs is still on its way: draw again when it has come.</returns>
    /// <param name="canvas">A Premul canvas.</param>
    /// <param name="style">The style to draw.</param>
    /// <param name="view">Where the world lands on the canvas.</param>
    /// <param name="fetch">False: draw only from tiles already here, never start a fetch.</param>
    /// <param name="missing">Collects the URLs of the tiles that were not here: draw again when one of them has come.</param>
    public static bool Draw(SKCanvas canvas, MapStyleDocument style, MapViewport view, bool fetch = true, ICollection<string>? missing = null)
    {
        bool complete = true;
        var ctx = new StyleContext { Zoom = view.Zoom, GlobalState = style.GlobalState };
        foreach (var layer in style.Layers)
        {
            if (style.Lenient && style.IsDropped(layer.Id)) continue;
            int save = canvas.Save();
            try { DrawLayer(canvas, style, layer, view, ctx, ref complete, fetch, missing); }
            catch (StyleUnsupportedException e) when (style.Lenient) { style.Drop(layer.Id, e.Message); }
            finally { canvas.RestoreToCount(save); }
        }
        return complete;
    }

    static void DrawLayer(SKCanvas canvas, MapStyleDocument style, MapStyleDocument.Layer layer, MapViewport view, StyleContext ctx, ref bool complete, bool fetch, ICollection<string>? missing)
    {
        ctx.Properties = null; ctx.GeometryType = null; ctx.Id = null;
        // GL JS: `if (this.minzoom && zoom < this.minzoom)` - an unset minzoom hides nothing, not even below zoom 0
        if ((layer.MinZoom > 0 && view.Zoom < layer.MinZoom) || view.Zoom >= layer.MaxZoom) return;
        if (Expression.String(Get(layer.Layout, "visibility"), ctx, "visible") == "none") return;
        if (layer.Type == "fill-extrusion") return;   // the GPU path draws it (flat); the drape does not
        if (layer.Type == "background")
        {
            var color = Expression.Color(Get(layer.Paint, "background-color"), ctx, StyleColor.Black)
                .WithOpacity(Expression.Number(Get(layer.Paint, "background-opacity"), ctx, 1));
            using var paint = new SKPaint { ColorF = ToSk(color), BlendMode = SKBlendMode.SrcOver };
            canvas.DrawPaint(paint);
            return;
        }
        switch (style.Sources[layer.Source!])
        {
            case GeoJsonSource g:
            {
                // GL JS tiles GeoJSON at the floored zoom with geojson-vt (maxzoom 18), which rounds
                // coordinates to 8192 steps per tile
                double dataZoom = Math.Max(0, Math.Min(Math.Floor(view.Zoom), 18));
                var grid = view with { Grid = 1 / (8192 * Math.Pow(2, dataZoom)) };
                DrawFeatures(canvas, layer, style.Features(g, ref complete), ctx, grid, SharpCorner(view, Math.Max(0, Math.Floor(view.Zoom)), dataZoom));
                break;
            }
            case VectorSource v:
            {
                // the tiles GL JS would pick: the floored zoom for the source's tile size, clamped
                // to its zoom range (beyond maxzoom the last level is overscaled)
                int ideal = Math.Max(0, (int)Math.Floor(view.Zoom + Math.Log2(512.0 / v.TileSize)));
                if (ideal < v.MinZoom) break;
                int z = Math.Min(ideal, v.MaxZoom);
                int n = 1 << z;
                var (minX, minY, maxX, maxY) = view.Bounds;
                int y0 = Math.Max(0, (int)Math.Floor(minY * n)), y1 = Math.Min(n - 1, (int)Math.Floor(maxY * n - 1e-9));
                int x0 = (int)Math.Floor(minX * n), x1 = (int)Math.Floor(maxX * n - 1e-9);
                double sharp = SharpCorner(view, ideal, z);
                for (int ty = y0; ty <= y1; ty++)
                    for (int tx = x0; tx <= x1; tx++)
                    {
                        var key = new TileKey(z, ((tx % n) + n) % n, ty);
                        var layers = style.Tile(v, key, ref complete, fetch, missing);
                        if (layers is null || !layers.TryGetValue(layer.SourceLayer!, out var features)) continue;
                        // GL JS clips each tile's drawing to its square with the stencil buffer;
                        // a wrapped copy of the world is shifted by whole worlds
                        double shift = Math.Floor((double)tx / n);
                        var a = view.Project((double)tx / n, (double)ty / n);
                        var b = view.Project((double)(tx + 1) / n, (double)(ty + 1) / n);
                        canvas.Save();
                        canvas.ClipRect(new SKRect(a.X, a.Y, b.X, b.Y), antialias: false);
                        var shifted = view with { CenterX = view.CenterX - shift };
                        DrawFeatures(canvas, layer, features, ctx, shifted, sharp);
                        canvas.Restore();
                    }
                break;
            }
        }
    }

    // GL JS shortens the legs of a sharp corner by 15 px of the tile it is built in, shown
    // 2^(zoom - tile zoom) larger; not at all past 16x overscaling
    static double SharpCorner(MapViewport view, double idealZoom, double dataZoom)
        => Math.Pow(2, idealZoom - dataZoom) > 16 ? 0 : 15 * Math.Pow(2, view.Zoom - idealZoom) * view.PixelRatio;

    static void DrawFeatures(SKCanvas canvas, MapStyleDocument.Layer layer, List<StyleFeature> features, StyleContext ctx, MapViewport view, double sharpCorner)
    {
        float pr = (float)view.PixelRatio;
        // a vector tile covers 16 globe tiles at street level: what lies off this canvas is skipped
        // before its filter and its triangles (it cost 16x the work, 172 ms an overlay, 2026-11-21);
        // the margin covers the widest lines' half width
        var (vx0, vy0, vx1, vy1) = view.Bounds;
        double margin = 64 / (512 * Math.Pow(2, view.Zoom));
        vx0 -= margin; vy0 -= margin; vx1 += margin; vy1 += margin;
        // a line layer's triangles go out in ONE draw, as GL JS draws a bucket: each triangle still
        // blends on its own, so overlaps blend twice as before
        var linePositions = new List<SKPoint>();
        var lineColors = new List<SKColor>();
        var triangles = new List<LineVertex>();
        foreach (var f in features)
        {
            // GL JS's fill bucket classifies any geometry's rings, so a line is filled as a ring
            if (f.Kind == StyleFeature.Shape.Point) continue;
            var (fx0, fy0, fx1, fy1) = f.Bounds;
            if (fx1 < vx0 || fx0 > vx1 || fy1 < vy0 || fy0 > vy1) continue;
            ctx.Properties = f.Properties; ctx.GeometryType = f.GeometryType; ctx.Id = f.Id;
            if (layer.Filter is { } filter && !Expression.Filter(filter, ctx)) continue;
            if (layer.Type == "fill")
            {
                double opacity = Expression.Number(Get(layer.Paint, "fill-opacity"), ctx, 1);
                var fill = Expression.Color(Get(layer.Paint, "fill-color"), ctx, StyleColor.Black);
                bool aa = Get(layer.Paint, "fill-antialias") is not { ValueKind: JsonValueKind.False };
                using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var polygon in f.Parts)   // a line's part is one ring
                    foreach (var ring in polygon) AddRing(path, ring, view);
                // GL fills triangles without antialiasing: pixel centres inside are covered
                using (var paint = new SKPaint { ColorF = ToSk(fill.WithOpacity(opacity)), Style = SKPaintStyle.Fill, IsAntialias = false })
                    canvas.DrawPath(path, paint);
                if (aa)
                {
                    // MapLibre's antialiasing pass: a one-device-pixel outline in fill-outline-color, which defaults to fill-color
                    var outline = Expression.Color(Get(layer.Paint, "fill-outline-color"), ctx, fill).WithOpacity(opacity);
                    var positions = new List<SKPoint>();
                    var colors = new List<SKColor>();
                    foreach (var polygon in f.Parts)
                        foreach (var ring in polygon)
                            for (int k = 0; k + 1 < ring.Length; k++)
                                OutlineSegment(view.Project(ring[k].X, ring[k].Y), view.Project(ring[k + 1].X, ring[k + 1].Y), outline, positions, colors);
                    if (positions.Count > 0)
                    {
                        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions.ToArray(), null, colors.ToArray());
                        using var vpaint = new SKPaint();
                        canvas.DrawVertices(vertices, SKBlendMode.Modulate, vpaint);
                    }
                }
            }
            else
            {
                double width = Expression.Number(Get(layer.Paint, "line-width"), ctx, 1);
                if (width <= 0) continue;   // Skia's width 0 is a hairline; MapLibre's draws nothing
                var color = Expression.Color(Get(layer.Paint, "line-color"), ctx, StyleColor.Black)
                    .WithOpacity(Expression.Number(Get(layer.Paint, "line-opacity"), ctx, 1));
                if (color.A <= 0) continue;
                string cap = Expression.String(Get(layer.Layout, "line-cap"), ctx, "butt");
                string join = Expression.String(Get(layer.Layout, "line-join"), ctx, "miter");
                double miterLimit = Expression.Number(Get(layer.Layout, "line-miter-limit"), ctx, 2);
                double roundLimit = Expression.Number(Get(layer.Layout, "line-round-limit"), ctx, 1.05);
                double outset = LineTessellator.Outset(width * pr / 2);
                triangles.Clear();
                foreach (var part in f.Parts)
                    foreach (var line in part)
                        LineTessellator.Tessellate(line.Select(c => view.Project(c.X, c.Y)).ToList(),
                            f.Kind == StyleFeature.Shape.Polygon, join, cap, miterLimit, roundLimit, outset, sharpCorner, triangles);
                if (triangles.Count == 0) continue;
                LineTessellator.Shade(triangles, outset, ToSk(color), linePositions, lineColors);
            }
        }
        if (linePositions.Count > 0)
        {
            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, linePositions.ToArray(), null, lineColors.ToArray());
            using var vpaint = new SKPaint();
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, vpaint);
        }
    }

    /// <summary>Renders a render test's style as MapLibre's harness stores it: straight RGBA, width x height x pixelRatio.</summary>
    public static byte[] RenderStraight(MapStyleDocument style, int width, int height, double pixelRatio)
    {
        int w = (int)Math.Floor(width * pixelRatio), h = (int)Math.Floor(height * pixelRatio);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            Draw(canvas, style, MapViewport.FromLonLat(style.CenterLon, style.CenterLat, style.Zoom, width, height, pixelRatio));
            canvas.Flush();
        }
        var px = bitmap.Bytes;
        // render.test.ts un-premultiplies the read-back framebuffer by division into a Uint8Array,
        // which truncates; do the same so equal renderings give equal bytes
        for (int i = 0; i < px.Length; i += 4)
        {
            double a = px[i + 3] / 255.0;
            if (a == 0) continue;
            for (int k = 0; k < 3; k++) px[i + k] = (byte)((int)(px[i + k] / a) & 0xFF);
        }
        return px;
    }

    // GL JS's fill outline: gl.LINES, one pixel per step along the major axis (half-open, so a
    // shared vertex is not drawn twice), alpha = 1 - smoothstep(0, 1, distance from the pixel's
    // centre to the line) (fill_outline.fragment.glsl). Each pixel goes out as two triangles.
    static void OutlineSegment(SKPoint a, SKPoint b, StyleColor color, List<SKPoint> positions, List<SKColor> colors)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        bool xMajor = Math.Abs(dx) >= Math.Abs(dy);
        double a0 = xMajor ? a.X : a.Y, a1 = xMajor ? b.X : b.Y;
        if (a0 == a1) return;   // a zero-length segment
        double b0 = xMajor ? a.Y : a.X, slope = xMajor ? dy / dx : dx / dy;
        int step = a1 > a0 ? 1 : -1;
        // pixel centres c + 0.5 from the start (inclusive) toward the end (exclusive)
        int first = step > 0 ? (int)Math.Ceiling(a0 - 0.5) : (int)Math.Floor(a0 - 0.5);
        int last = step > 0 ? (int)Math.Ceiling(a1 - 0.5) - 1 : (int)Math.Floor(a1 - 0.5) + 1;
        for (int c = first; step > 0 ? c <= last : c >= last; c += step)
        {
            double along = c + 0.5;
            double across = b0 + (along - a0) * slope;
            int row = (int)Math.Floor(across);
            double d = Math.Abs(across - (row + 0.5));
            double alpha = 1 - d * d * (3 - 2 * d);
            int px = xMajor ? c : row, py = xMajor ? row : c;
            var col = (SKColor)new SKColorF((float)color.R, (float)color.G, (float)color.B, (float)(color.A * alpha));
            SKPoint p00 = new(px, py), p10 = new(px + 1, py), p01 = new(px, py + 1), p11 = new(px + 1, py + 1);
            positions.Add(p00); positions.Add(p10); positions.Add(p11);
            positions.Add(p00); positions.Add(p11); positions.Add(p01);
            for (int i = 0; i < 6; i++) colors.Add(col);
        }
    }

    static JsonElement? Get(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) ? v : null;

    static SKColorF ToSk(StyleColor c) => new((float)c.R, (float)c.G, (float)c.B, (float)c.A);

    static void AddRing(SKPath path, (double X, double Y)[] ring, MapViewport view)
    {
        if (ring.Length == 0) return;
        path.MoveTo(view.Project(ring[0].X, ring[0].Y));
        for (int k = 1; k < ring.Length; k++) path.LineTo(view.Project(ring[k].X, ring[k].Y));
        path.Close();
    }
}

/// <summary>GeoJSON (Geometry, Feature, FeatureCollection) into StyleFeatures in world coordinates.</summary>
internal static class GeoJson
{
    public static void Read(JsonElement e, List<StyleFeature> into)
    {
        switch (e.GetProperty("type").GetString())
        {
            case "FeatureCollection":
                foreach (var f in e.GetProperty("features").EnumerateArray()) Read(f, into);
                break;
            case "Feature":
            {
                var props = new Dictionary<string, object?>();
                if (e.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var kv in p.EnumerateObject()) props[kv.Name] = Expression.FromJson(kv.Value.Clone(), Expression.Kind.Any);
                object? id = e.TryGetProperty("id", out var i) ? Expression.FromJson(i, Expression.Kind.Any) : null;
                if (e.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object) Geometry(g, props, id, into);
                break;
            }
            default:
                Geometry(e, new Dictionary<string, object?>(), null, into);
                break;
        }
    }

    static void Geometry(JsonElement g, Dictionary<string, object?> props, object? id, List<StyleFeature> into)
    {
        var type = g.GetProperty("type").GetString();
        if (type == "GeometryCollection")
        {
            foreach (var child in g.GetProperty("geometries").EnumerateArray()) Geometry(child, props, id, into);
            return;
        }
        // a geometry without coordinates is skipped, as GL JS does
        if (!g.TryGetProperty("coordinates", out var c) || c.ValueKind != JsonValueKind.Array) return;
        var f = new StyleFeature { Properties = props, Id = id };
        switch (type)
        {
            case "Point": f.Kind = StyleFeature.Shape.Point; f.Parts.Add(new() { new[] { Pos(c) } }); break;
            case "MultiPoint": f.Kind = StyleFeature.Shape.Point; foreach (var p in c.EnumerateArray()) f.Parts.Add(new() { new[] { Pos(p) } }); break;
            case "LineString": f.Kind = StyleFeature.Shape.Line; f.Parts.Add(new() { Line(c) }); break;
            case "MultiLineString": f.Kind = StyleFeature.Shape.Line; foreach (var l in c.EnumerateArray()) f.Parts.Add(new() { Line(l) }); break;
            case "Polygon": f.Kind = StyleFeature.Shape.Polygon; f.Parts.Add(c.EnumerateArray().Select(Line).ToList()); break;
            case "MultiPolygon": f.Kind = StyleFeature.Shape.Polygon; foreach (var p in c.EnumerateArray()) f.Parts.Add(p.EnumerateArray().Select(Line).ToList()); break;
            default: throw new StyleUnsupportedException($"GeoJSON {type}");
        }
        into.Add(f);
    }

    static (double, double) Pos(JsonElement p) => ((p[0].GetDouble() + 180) / 360, TileGeometry.MercatorY(p[1].GetDouble()));
    static (double, double)[] Line(JsonElement l) => l.EnumerateArray().Select(Pos).ToArray();
}
