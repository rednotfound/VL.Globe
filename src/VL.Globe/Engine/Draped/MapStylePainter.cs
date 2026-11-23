using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace VL.Globe.Engine.Draped;

// A MapLibre style (the subset in MapStyle.cs) drawn with SkiaSharp, the renderer of the draped
// tile overlays. A viewport says where the world lands in pixels: the whole canvas of a MapLibre
// render test (ROADMAP 4b) or one globe tile's square (4c), so the render tests measure the code
// that drapes. Geometry is kept in Web Mercator world coordinates (0..1, y down) - what a vector
// tile holds, and linear on the canvas. Pure: tested without a GPU.

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
        if (layer.Type == "fill-extrusion") return;   // the GPU path draws it; the drape does not
        if (layer.Type == "circle")
        {
            // circles are the GPU path's: the drape has none, and says so to the render tests
            if (!style.Lenient) throw new StyleUnsupportedException("circle layer");
            return;
        }
        if (layer.Type == "symbol")
        {
            // labels are the GPU path's: the drape has none, and says so to the render tests
            if (!style.Lenient) throw new StyleUnsupportedException("symbol layer");
            return;
        }
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

    static void DrawFeatures(SKCanvas canvas, MapStyleDocument.Layer layer, FeatureTable features, StyleContext ctx, MapViewport view, double sharpCorner)
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
            ctx.Use(f);
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

    static void AddRing(SKPath path, Ring ring, MapViewport view)
    {
        if (ring.Length == 0) return;
        path.MoveTo(view.Project(ring[0].X, ring[0].Y));
        for (int k = 1; k < ring.Length; k++) path.LineTo(view.Project(ring[k].X, ring[k].Y));
        path.Close();
    }
}
