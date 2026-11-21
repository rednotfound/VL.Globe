using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace VL.Globe.Engine;

// A MapLibre style (the subset in MapStyle.cs) drawn with SkiaSharp, the renderer of the draped
// tile overlays. A viewport says where longitude/latitude land in pixels: the whole canvas of a
// MapLibre render test (ROADMAP 4b) or one tile's square (4c), so the render tests measure the code
// that drapes. Pure: tested without a GPU.

/// <summary>One feature of a source: geometry in longitude/latitude, and its properties.</summary>
internal sealed class StyleFeature
{
    public enum Shape { Point, Line, Polygon }
    public Shape Kind;
    // Point: each part one coordinate. Line: each part one line. Polygon: each part one polygon, its rings.
    public List<List<(double Lon, double Lat)[]>> Parts = new();
    public Dictionary<string, JsonElement> Properties = new();
    public string GeometryType => Kind switch { Shape.Point => "Point", Shape.Line => "LineString", _ => "Polygon" };
}

/// <summary>Where the map lies on a canvas: a Web Mercator view with MapLibre's 512 px world at zoom 0.</summary>
internal readonly record struct MapViewport(double CenterLon, double CenterLat, double Zoom, int Width, int Height, double PixelRatio)
{
    public SKPoint Project(double lon, double lat)
    {
        double world = 512 * Math.Pow(2, Zoom);
        double cx = (CenterLon + 180) / 360 * world, cy = TileGeometry.MercatorY(CenterLat) * world;
        double x = (lon + 180) / 360 * world - cx + Width / 2.0;
        double y = TileGeometry.MercatorY(lat) * world - cy + Height / 2.0;
        return new SKPoint((float)(x * PixelRatio), (float)(y * PixelRatio));
    }
}

/// <summary>A parsed style: its layers, its inline sources, the camera of a render test.</summary>
internal sealed class MapStyleDocument
{
    public sealed record Layer(string Id, string Type, string? Source, JsonElement Paint, JsonElement Layout, double MinZoom, double MaxZoom);

    public List<Layer> Layers = new();
    public Dictionary<string, List<StyleFeature>> Sources = new();
    public Dictionary<string, JsonElement> GlobalState = new();
    public double CenterLon, CenterLat, Zoom;

    static readonly HashSet<string> RootKeys = new() { "version", "name", "metadata", "center", "zoom", "bearing", "pitch", "sources", "layers", "state", "sprite", "glyphs", "transition" };
    static readonly HashSet<string> LayerKeys = new() { "id", "type", "source", "source-layer", "paint", "layout", "minzoom", "maxzoom", "metadata" };
    static readonly Dictionary<string, HashSet<string>> PaintKeys = new()
    {
        ["background"] = new() { "background-color", "background-opacity" },
        ["fill"] = new() { "fill-color", "fill-opacity", "fill-outline-color", "fill-antialias" },
        ["line"] = new() { "line-color", "line-opacity", "line-width" },
    };
    static readonly Dictionary<string, HashSet<string>> LayoutKeys = new()
    {
        ["background"] = new() { "visibility" },
        ["fill"] = new() { "visibility" },
        ["line"] = new() { "visibility", "line-cap", "line-join", "line-miter-limit", "line-round-limit" },
    };

    /// <summary>Reads a style JSON; throws StyleUnsupportedException for anything outside the subset.</summary>
    public static MapStyleDocument Parse(JsonElement root)
    {
        var doc = new MapStyleDocument();
        foreach (var p in root.EnumerateObject())
            if (!RootKeys.Contains(p.Name)) throw new StyleUnsupportedException($"style.{p.Name}");
        if (root.TryGetProperty("bearing", out var bearing) && bearing.GetDouble() != 0) throw new StyleUnsupportedException("bearing");
        if (root.TryGetProperty("pitch", out var pitch) && pitch.GetDouble() != 0) throw new StyleUnsupportedException("pitch");
        if (root.TryGetProperty("center", out var c)) { doc.CenterLon = c[0].GetDouble(); doc.CenterLat = c[1].GetDouble(); }
        if (root.TryGetProperty("zoom", out var z)) doc.Zoom = z.GetDouble();
        if (root.TryGetProperty("state", out var state))
            foreach (var s in state.EnumerateObject())
                if (s.Value.TryGetProperty("default", out var d)) doc.GlobalState[s.Name] = d.Clone();

        if (root.TryGetProperty("sources", out var sources))
            foreach (var s in sources.EnumerateObject())
                doc.Sources[s.Name] = ReadSource(s.Name, s.Value);

        foreach (var l in root.GetProperty("layers").EnumerateArray())
        {
            foreach (var p in l.EnumerateObject())
                if (!LayerKeys.Contains(p.Name)) throw new StyleUnsupportedException($"layer.{p.Name}");
            var type = l.GetProperty("type").GetString()!;
            if (!PaintKeys.ContainsKey(type)) throw new StyleUnsupportedException($"{type} layer");
            var paint = l.TryGetProperty("paint", out var pp) ? pp.Clone() : Empty;
            var layout = l.TryGetProperty("layout", out var ll) ? ll.Clone() : Empty;
            foreach (var p in paint.EnumerateObject())
                if (!PaintKeys[type].Contains(p.Name) && !p.Name.EndsWith("-transition")) throw new StyleUnsupportedException(p.Name);
            foreach (var p in layout.EnumerateObject())
                if (!LayoutKeys[type].Contains(p.Name)) throw new StyleUnsupportedException(p.Name);
            string? source = l.TryGetProperty("source", out var src) ? src.GetString() : null;
            if (type != "background" && (source is null || !doc.Sources.ContainsKey(source))) throw new StyleUnsupportedException($"source '{source}'");
            doc.Layers.Add(new Layer(l.GetProperty("id").GetString()!, type, source, paint, layout,
                l.TryGetProperty("minzoom", out var mn) ? mn.GetDouble() : 0,
                l.TryGetProperty("maxzoom", out var mx) ? mx.GetDouble() : 24));
        }
        return doc;
    }

    static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    static List<StyleFeature> ReadSource(string name, JsonElement s)
    {
        var type = s.GetProperty("type").GetString();
        if (type != "geojson") throw new StyleUnsupportedException($"{type} source");
        foreach (var p in s.EnumerateObject())
            if (p.Name is not ("type" or "data" or "maxzoom" or "tolerance" or "buffer")) throw new StyleUnsupportedException($"geojson source {p.Name}");
        var data = s.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object) throw new StyleUnsupportedException("geojson data by URL");
        var features = new List<StyleFeature>();
        GeoJson.Read(data, features);
        return features;
    }
}

/// <summary>Draws a style's layers in order.</summary>
internal static class MapStylePainter
{
    /// <summary>Draws <paramref name="style"/> on <paramref name="canvas"/> (Premul) at the viewport's zoom.</summary>
    public static void Draw(SKCanvas canvas, MapStyleDocument style, MapViewport view)
    {
        var ctx = new StyleContext { Zoom = view.Zoom, GlobalState = style.GlobalState };
        float pr = (float)view.PixelRatio;
        foreach (var layer in style.Layers)
        {
            ctx.Properties = null; ctx.GeometryType = null;
            if (view.Zoom < layer.MinZoom || view.Zoom >= layer.MaxZoom) continue;
            if (Expression.String(Get(layer.Layout, "visibility"), ctx, "visible") == "none") continue;
            switch (layer.Type)
            {
                case "background":
                {
                    var color = Expression.Color(Get(layer.Paint, "background-color"), ctx, StyleColor.Black)
                        .WithOpacity(Expression.Number(Get(layer.Paint, "background-opacity"), ctx, 1));
                    using var paint = new SKPaint { ColorF = ToSk(color), BlendMode = SKBlendMode.SrcOver };
                    canvas.DrawPaint(paint);
                    break;
                }
                case "fill":
                    foreach (var f in style.Sources[layer.Source!])
                    {
                        if (f.Kind != StyleFeature.Shape.Polygon) continue;
                        ctx.Properties = f.Properties; ctx.GeometryType = f.GeometryType;
                        double opacity = Expression.Number(Get(layer.Paint, "fill-opacity"), ctx, 1);
                        var fill = Expression.Color(Get(layer.Paint, "fill-color"), ctx, StyleColor.Black);
                        bool aa = Get(layer.Paint, "fill-antialias") is not { ValueKind: JsonValueKind.False };
                        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                        foreach (var polygon in f.Parts)
                            foreach (var ring in polygon) AddRing(path, ring, view, close: true);
                        using (var paint = new SKPaint { ColorF = ToSk(fill.WithOpacity(opacity)), Style = SKPaintStyle.Fill, IsAntialias = aa })
                            canvas.DrawPath(path, paint);
                        if (aa)
                        {
                            // MapLibre's antialiasing pass: a one-device-pixel outline in fill-outline-color, which defaults to fill-color
                            var outline = Expression.Color(Get(layer.Paint, "fill-outline-color"), ctx, fill);
                            using var paint = new SKPaint { ColorF = ToSk(outline.WithOpacity(opacity)), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                            canvas.DrawPath(path, paint);
                        }
                    }
                    break;
                case "line":
                    foreach (var f in style.Sources[layer.Source!])
                    {
                        if (f.Kind == StyleFeature.Shape.Point) continue;
                        ctx.Properties = f.Properties; ctx.GeometryType = f.GeometryType;
                        double width = Expression.Number(Get(layer.Paint, "line-width"), ctx, 1);
                        if (width <= 0) continue;   // Skia's width 0 is a hairline; MapLibre's draws nothing
                        var color = Expression.Color(Get(layer.Paint, "line-color"), ctx, StyleColor.Black)
                            .WithOpacity(Expression.Number(Get(layer.Paint, "line-opacity"), ctx, 1));
                        string cap = Expression.String(Get(layer.Layout, "line-cap"), ctx, "butt");
                        string join = Expression.String(Get(layer.Layout, "line-join"), ctx, "miter");
                        double miterLimit = Expression.Number(Get(layer.Layout, "line-miter-limit"), ctx, 2);
                        double roundLimit = Expression.Number(Get(layer.Layout, "line-round-limit"), ctx, 1.05);
                        // GL JS's 15 px, in the tile's own pixels, shown 2^(zoom - tile zoom) larger
                        double sharpCorner = 15 * Math.Pow(2, view.Zoom - Math.Floor(view.Zoom)) * pr;
                        double outset = LineTessellator.Outset(width * pr / 2);
                        var triangles = new List<LineVertex>();
                        foreach (var part in f.Parts)
                            foreach (var line in part)
                                LineTessellator.Tessellate(line.Select(c => view.Project(c.Lon, c.Lat)).ToList(),
                                    f.Kind == StyleFeature.Shape.Polygon, join, cap, miterLimit, roundLimit, outset, sharpCorner, triangles);
                        if (triangles.Count == 0) continue;
                        var positions = new List<SKPoint>(triangles.Count * 2);
                        var colors = new List<SKColor>(triangles.Count * 2);
                        LineTessellator.Shade(triangles, outset, ToSk(color), positions, colors);
                        // every triangle blends on its own, so overlaps blend twice, as in MapLibre
                        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions.ToArray(), null, colors.ToArray());
                        using var paint = new SKPaint();
                        canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
                    }
                    break;
            }
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
            Draw(canvas, style, new MapViewport(style.CenterLon, style.CenterLat, style.Zoom, width, height, pixelRatio));
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

    static JsonElement? Get(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) ? v : null;

    static SKColorF ToSk(StyleColor c) => new((float)c.R, (float)c.G, (float)c.B, (float)c.A);

    static void AddRing(SKPath path, (double Lon, double Lat)[] ring, MapViewport view, bool close)
    {
        if (ring.Length == 0) return;
        path.MoveTo(view.Project(ring[0].Lon, ring[0].Lat));
        for (int k = 1; k < ring.Length; k++) path.LineTo(view.Project(ring[k].Lon, ring[k].Lat));
        if (close) path.Close();
    }
}

/// <summary>GeoJSON (Geometry, Feature, FeatureCollection) into StyleFeatures.</summary>
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
                var props = new Dictionary<string, JsonElement>();
                if (e.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var kv in p.EnumerateObject()) props[kv.Name] = kv.Value.Clone();
                if (e.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object) Geometry(g, props, into);
                break;
            }
            default:
                Geometry(e, new Dictionary<string, JsonElement>(), into);
                break;
        }
    }

    static void Geometry(JsonElement g, Dictionary<string, JsonElement> props, List<StyleFeature> into)
    {
        var type = g.GetProperty("type").GetString();
        if (type == "GeometryCollection")
        {
            foreach (var child in g.GetProperty("geometries").EnumerateArray()) Geometry(child, props, into);
            return;
        }
        var c = g.GetProperty("coordinates");
        var f = new StyleFeature { Properties = props };
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

    static (double, double) Pos(JsonElement p) => (p[0].GetDouble(), p[1].GetDouble());
    static (double, double)[] Line(JsonElement l) => l.EnumerateArray().Select(Pos).ToArray();
}
