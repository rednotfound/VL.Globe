using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace VL.Globe.Engine.Camera;

// ROADMAP 6d (docs/DESIGN-PICK.md): what a VectorTiles layer draws under the cursor, as MapLibre GL JS's
// queryRenderedFeatures. The style's layers top-first; each layer's own test in Web Mercator world units
// (conformal: near the point a world unit is 2 pi R cos(lat) metres in every direction); fill-extrusion by the
// cursor's ray, the nearest building first. The tiles are those already on disk at the zoom drawn there - a
// pick never fetches - decoded once and kept for the next few picks (the cursor stays in a tile most frames).
internal sealed class VectorPicker
{
    /// <summary>What a pick found: the feature, the style layer and source layer it was drawn by, and where.</summary>
    internal sealed record Hit(StyleFeature Feature, string LayerId, string? SourceLayer, TileKey Tile);

    /// <summary>Where the cursor is: the ground point under it, the eye above the ground, the pixel there in metres.</summary>
    internal readonly record struct Query(double Longitude, double Latitude, double EyeLongitude, double EyeLatitude, double EyeHeight,
        double MetresPerPixel, double TolerancePx, double Zoom);

    const int KeepTiles = 8;
    const double EarthRadius = GlobeCoordinates.EarthRadius;
    readonly LinkedList<(object source, TileKey key, Dictionary<string, FeatureTable>? tables)> _tiles = new();

    /// <summary>The topmost feature of <paramref name="style"/>'s layers at the query, or null.</summary>
    public Hit? Pick(MapStyleDocument style, Query q)
    {
        for (int i = style.Layers.Count - 1; i >= 0; i--)
        {
            var layer = style.Layers[i];
            if (layer.Type is not ("fill" or "line" or "circle" or "fill-extrusion")) continue;
            if (style.Lenient && style.IsDropped(layer.Id)) continue;
            if (layer.Source is not { } sourceId || !style.Sources.TryGetValue(sourceId, out var source)) continue;
            var ctx = new StyleContext { Zoom = q.Zoom, GlobalState = style.GlobalState, Document = style };
            // drawn at the zoom: its range and visibility, as GpuStyleLayer.Uniforms
            if ((layer.MinZoom > 0 && q.Zoom < layer.MinZoom) || q.Zoom >= layer.MaxZoom) continue;
            if (Expression.String(Get(layer.Layout, "visibility"), ctx, "visible") == "none") continue;
            var hit = layer.Type == "fill-extrusion" ? PickExtrusion(style, layer, source, q, ctx) : Pick2D(style, layer, source, q, ctx);
            if (hit is not null) return hit;
        }
        return null;
    }

    // fill, line, circle: the ground point against the features of the tile it lies in
    Hit? Pick2D(MapStyleDocument style, MapStyleDocument.Layer layer, StyleSource source, Query q, StyleContext ctx)
    {
        var (wx, wy) = World(q.Longitude, q.Latitude);
        double worldPerMetre = 1 / (2 * Math.PI * EarthRadius * Math.Max(1e-6, Math.Cos(q.Latitude * Math.PI / 180)));
        double worldPerPixel = q.MetresPerPixel * worldPerMetre;
        if (TileAt(style, source, wx, wy, q.Zoom) is not { } tile || Features(style, source, tile, layer) is not { } features) return null;
        var paint = layer.Paint;
        StyleFeature? found = null;
        foreach (var f in features)
        {
            ctx.Zoom = tile.Zoom;   // GL JS filters at the tile's zoom
            ctx.Use(f);
            if (layer.Filter is { } filter && !Expression.FilterFeature(filter, ctx, layer.Id)) continue;
            ctx.Zoom = q.Zoom;      // paint at the camera's
            bool hit = layer.Type switch
            {
                "fill" => f.Kind == StyleFeature.Shape.Polygon && Contains(f, wx, wy),
                "line" => f.Kind != StyleFeature.Shape.Point
                    && Near(f, wx, wy, (Expression.Number(Get(paint, "line-width"), ctx, 1) / 2 + q.TolerancePx) * worldPerPixel, rings: f.Kind == StyleFeature.Shape.Polygon),
                "circle" => f.Kind == StyleFeature.Shape.Point
                    && NearPoint(f, wx, wy, (Expression.Number(Get(paint, "circle-radius"), ctx, 5) + Expression.Number(Get(paint, "circle-stroke-width"), ctx, 0) + q.TolerancePx) * worldPerPixel),
                _ => false,
            };
            if (hit) found = f.Index >= 0 ? features.Get(f.Index) : f;   // the last drawn is on top: keep looking
        }
        return found is null ? null : new Hit(found, layer.Id, layer.SourceLayer, tile);
    }

    // fill-extrusion: along the cursor's ray. Near the point the ground is a plane: the ray at the height h meets it
    // over P(h) = G + (N - G) h / H (G the ground point, N under the eye, H the eye's height); a building of base b
    // and height h is hit where the segment P(h)..P(b) meets its footprint, nearest where it enters highest.
    Hit? PickExtrusion(MapStyleDocument style, MapStyleDocument.Layer layer, StyleSource source, Query q, StyleContext ctx)
    {
        if (q.EyeHeight <= 0) return null;
        var g = World(q.Longitude, q.Latitude);
        var n = World(q.EyeLongitude, q.EyeLatitude);
        (double X, double Y) P(double h) { double t = Math.Clamp(h / q.EyeHeight, 0, 1); return (g.X + (n.X - g.X) * t, g.Y + (n.Y - g.Y) * t); }
        // the tiles the segment from the ground point to 600 m up crosses (taller buildings are few)
        var top = P(Math.Min(600, q.EyeHeight));
        var tiles = new HashSet<TileKey>();
        foreach (var (x, y) in new[] { g, top, ((g.X + top.X) / 2, (g.Y + top.Y) / 2) })
            if (TileAt(style, source, x, y, q.Zoom) is { } t) tiles.Add(t);
        Hit? best = null;
        double bestEntry = double.NegativeInfinity;   // the height the ray enters at: higher is nearer the eye
        var paint = layer.Paint;
        foreach (var tile in tiles)
        {
            if (Features(style, source, tile, layer) is not { } features) continue;
            foreach (var f in features)
            {
                if (f.Kind != StyleFeature.Shape.Polygon) continue;
                ctx.Zoom = tile.Zoom;
                ctx.Use(f);
                if (layer.Filter is { } filter && !Expression.FilterFeature(filter, ctx, layer.Id)) continue;
                ctx.Zoom = q.Zoom;
                double h = Math.Max(0, Expression.Number(Get(paint, "fill-extrusion-height"), ctx, 0));
                double b = Math.Clamp(Expression.Number(Get(paint, "fill-extrusion-base"), ctx, 0), 0, h);
                if (h <= 0 || h <= bestEntry) continue;
                var a = P(h); var c = P(b);
                if (Entry(f, a, c) is not { } t) continue;   // 0 at the roof's height, 1 at the base's
                double entry = h - t * (h - b);
                if (entry > bestEntry) { bestEntry = entry; best = new Hit(f.Index >= 0 ? features.Get(f.Index) : f, layer.Id, layer.SourceLayer, tile); }
            }
        }
        return best;
    }

    // the source's tile drawn round a world point: GL JS's floored zoom for its tile size, within its range, the finest
    // already here at or above it (a pick never fetches)
    TileKey? TileAt(MapStyleDocument style, StyleSource source, double wx, double wy, double zoom)
    {
        var (minZoom, maxZoom, tileSize) = source switch
        {
            VectorSource v => (v.MinZoom, v.MaxZoom, v.TileSize),
            GeoJsonSource => (0, 18, 512),
            _ => (-1, -1, -1),
        };
        if (minZoom < 0) return null;
        int z = Math.Clamp((int)Math.Floor(zoom + Math.Log2(512.0 / tileSize)), minZoom, maxZoom);
        if (source is GeoJsonSource) return Key(z, wx, wy);   // its data is all here
        for (; z >= minZoom; z--)
        {
            var key = Key(z, wx, wy);
            if (Tables(style, source, key) is not null) return key;
        }
        return null;
    }

    static TileKey Key(int z, double wx, double wy)
    {
        int n = 1 << z;
        return new TileKey(z, Math.Clamp((int)Math.Floor(wx * n), 0, n - 1), Math.Clamp((int)Math.Floor(wy * n), 0, n - 1));
    }

    FeatureTable? Features(MapStyleDocument style, StyleSource source, TileKey tile, MapStyleDocument.Layer layer)
    {
        if (source is GeoJsonSource geo)
        {
            bool complete = true;
            return style.Features(geo, ref complete);
        }
        return Tables(style, source, tile) is { } tables && layer.SourceLayer is { } sl && tables.TryGetValue(sl, out var t) ? t : null;
    }

    // decoded tiles, the last few
    Dictionary<string, FeatureTable>? Tables(MapStyleDocument style, StyleSource source, TileKey key)
    {
        if (source is not VectorSource vector) return null;
        for (var node = _tiles.First; node != null; node = node.Next)
            if (ReferenceEquals(node.Value.source, source) && node.Value.key == key)
            {
                _tiles.Remove(node); _tiles.AddFirst(node);
                return node.Value.tables;
            }
        bool complete = true;
        var tables = style.Tile(vector, key, ref complete, fetch: false);
        if (tables is null && !complete) return null;   // not on disk: not kept, asked again when it may be
        _tiles.AddFirst((source, key, tables));
        while (_tiles.Count > KeepTiles) _tiles.RemoveLast();
        return tables;
    }

    /// <summary>The feature as an NTS feature in degrees: its properties, and $id, $layer, $sourceLayer.</summary>
    public static Feature ToFeature(Hit hit)
    {
        var f = hit.Feature;
        var attributes = new AttributesTable();
        foreach (var (k, v) in f.Properties) attributes.Add(k, v is JsonElement e ? e.ToString() : v);
        attributes.Add("$id", f.Id);
        attributes.Add("$layer", hit.LayerId);
        attributes.Add("$sourceLayer", hit.SourceLayer);
        return new Feature(ToGeometry(f), attributes);
    }

    static Geometry ToGeometry(StyleFeature f)
    {
        var factory = new GeometryFactory();
        Coordinate C((double X, double Y) p) { var (lon, lat) = Degrees(p.X, p.Y); return new Coordinate(lon, lat); }
        var parts = new List<Geometry>();
        foreach (var part in f.Parts)
        {
            var rings = new List<Coordinate[]>();
            foreach (var ring in part)
            {
                var cs = new Coordinate[ring.Length];
                for (int i = 0; i < ring.Length; i++) cs[i] = C(ring[i]);
                rings.Add(cs);
            }
            if (rings.Count == 0) continue;
            switch (f.Kind)
            {
                case StyleFeature.Shape.Point: parts.Add(factory.CreatePoint(rings[0][0])); break;
                case StyleFeature.Shape.Line: parts.Add(factory.CreateLineString(rings[0])); break;
                default:
                    LinearRing Ring(Coordinate[] cs) => factory.CreateLinearRing(cs.Length > 0 && !cs[0].Equals2D(cs[^1]) ? cs.Append(cs[0]).ToArray() : cs);
                    if (rings[0].Length < 3) continue;
                    parts.Add(factory.CreatePolygon(Ring(rings[0]), rings.Skip(1).Where(r => r.Length >= 3).Select(Ring).ToArray()));
                    break;
            }
        }
        if (parts.Count == 1) return parts[0];
        return f.Kind switch
        {
            StyleFeature.Shape.Point => factory.CreateMultiPoint(parts.Cast<Point>().ToArray()),
            StyleFeature.Shape.Line => factory.CreateMultiLineString(parts.Cast<LineString>().ToArray()),
            _ => factory.CreateMultiPolygon(parts.Cast<Polygon>().ToArray()),
        };
    }

    internal static (double X, double Y) World(double lon, double lat)
    {
        double s = Math.Sin(Math.Clamp(lat, -85.0511, 85.0511) * Math.PI / 180);
        return ((lon + 180) / 360, 0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI));
    }

    static (double lon, double lat) Degrees(double x, double y)
        => (x * 360 - 180, Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI);

    static JsonElement? Get(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;

    // even-odd over every ring of a part: inside one part is inside the feature
    static bool Contains(StyleFeature f, double x, double y)
    {
        foreach (var part in f.Parts)
        {
            bool inside = false;
            foreach (var ring in part)
            {
                var r = ring.Span;
                for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
                    if ((r[i].Y > y) != (r[j].Y > y) && x < (r[j].X - r[i].X) * (y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X) inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }

    static bool Near(StyleFeature f, double x, double y, double distance, bool rings)
    {
        double d2 = distance * distance;
        foreach (var part in f.Parts)
            foreach (var ring in part)
            {
                var r = ring.Span;
                int n = rings ? r.Length : r.Length - 1;
                for (int i = 0; i < n; i++)
                {
                    var a = r[i]; var b = r[(i + 1) % r.Length];
                    if (SegmentDistance2(x, y, a.X, a.Y, b.X, b.Y) <= d2) return true;
                }
                if (r.Length == 1 && (r[0].X - x) * (r[0].X - x) + (r[0].Y - y) * (r[0].Y - y) <= d2) return true;
            }
        return false;
    }

    static bool NearPoint(StyleFeature f, double x, double y, double distance)
    {
        foreach (var part in f.Parts)
            foreach (var ring in part)
                foreach (var p in ring)
                    if ((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= distance * distance) return true;
        return false;
    }

    static double SegmentDistance2(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1) : 0;
        double ex = ax + t * dx - px, ey = ay + t * dy - py;
        return ex * ex + ey * ey;
    }

    // where along a..c (0 at a, 1 at c) the segment first is inside the footprint, or null
    static double? Entry(StyleFeature f, (double X, double Y) a, (double X, double Y) c)
    {
        if (Contains(f, a.X, a.Y)) return 0;
        double first = double.PositiveInfinity;
        foreach (var part in f.Parts)
            foreach (var ring in part)
            {
                var r = ring.Span;
                for (int i = 0; i < r.Length; i++)
                {
                    var p = r[i]; var q = r[(i + 1) % r.Length];
                    if (Cross(a, c, p, q) is { } t && t < first) first = t;
                }
            }
        return double.IsPositiveInfinity(first) ? null : first;
    }

    // the parameter along a..c where it crosses p..q
    static double? Cross((double X, double Y) a, (double X, double Y) c, (double X, double Y) p, (double X, double Y) q)
    {
        double rx = c.X - a.X, ry = c.Y - a.Y, sx = q.X - p.X, sy = q.Y - p.Y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-30) return null;
        double t = ((p.X - a.X) * sy - (p.Y - a.Y) * sx) / den, u = ((p.X - a.X) * ry - (p.Y - a.Y) * rx) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : null;
    }
}
