using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe.Engine;

// Vectors DRAPED into the tiles (ROADMAP 0.1 item 3, decided by experiment on 2026-11-21). Each
// visible tile gets a transparent overlay image with the geometry that falls inside its Web Mercator
// square drawn by SkiaSharp - VL.Skia's own renderer - and the tile material lays it over the map
// (BinaryOperator.Over). It is how MapLibre puts vectors on terrain (render-to-texture) and how
// GeoLibre puts vector tiles on its Cesium globe (cesium-drape.ts). Line widths are in TEXELS of the
// overlay, which the selector shows at 0.71-1.41 screen pixels, so they read as screen pixels.
//
// Pure: geometry in, straight-alpha RGBA bytes out, tested without a GPU.
internal static class TileOverlay
{
    public readonly record struct Style(SKColor Stroke, float Width, SKColor Fill);

    /// <summary>One layer's geometry inside <paramref name="tile"/>'s square; null when nothing falls inside.</summary>
    public static byte[]? Rasterize(TileKey tile, IReadOnlyList<Geometry> geometries, Style style, int size)
        => Rasterize(tile, new[] { (geometries, style) }, size);

    /// <summary>Every layer's geometry inside <paramref name="tile"/>'s square, drawn in order on one transparent size x size image; null when nothing falls inside.</summary>
    public static byte[]? Rasterize(TileKey tile, IReadOnlyList<(IReadOnlyList<Geometry> geometries, Style style)> layers, int size)
    {
        double n = tile.TilesPerSide;
        // the tile's lon/lat box, grown by a line width so a line just outside still draws its edge
        double west = TileGeometry.LongitudeOf(tile.X / n), east = TileGeometry.LongitudeOf((tile.X + 1) / n);
        double north = TileGeometry.LatitudeOf(tile.Y / n), south = TileGeometry.LatitudeOf((tile.Y + 1) / n);

        SKPoint Pixel(Coordinate c) => new(
            (float)(((c.X + 180) / 360 * n - tile.X) * size),
            (float)((TileGeometry.MercatorY(c.Y) * n - tile.Y) * size));

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        bool any = false;
        foreach (var (geometries, style) in layers)
        {
            double pad = (east - west) * Math.Max(style.Width, 1) * 2 / size;
            var box = new Envelope(west - pad, east + pad, south - pad, north + pad);
            using var stroke = new SKPaint { Color = style.Stroke, StrokeWidth = style.Width, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            using var fill = new SKPaint { Color = style.Fill, Style = SKPaintStyle.Fill, IsAntialias = true };
            foreach (var g in geometries)
            {
                if (g is null || g.IsEmpty || !g.EnvelopeInternal.Intersects(box)) continue;
                for (int i = 0; i < g.NumGeometries; i++)
                {
                    switch (g.GetGeometryN(i))
                    {
                        case Polygon poly:
                            using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
                            {
                                AddRing(path, poly.ExteriorRing.Coordinates, Pixel);
                                foreach (var hole in poly.InteriorRings) AddRing(path, hole.Coordinates, Pixel);
                                if (style.Fill.Alpha > 0) canvas.DrawPath(path, fill);
                                if (style.Stroke.Alpha > 0 && style.Width > 0) canvas.DrawPath(path, stroke);
                            }
                            any = true;
                            break;
                        case LineString line:
                            using (var path = new SKPath())
                            {
                                var cs = line.Coordinates;
                                path.MoveTo(Pixel(cs[0]));
                                for (int k = 1; k < cs.Length; k++) path.LineTo(Pixel(cs[k]));
                                canvas.DrawPath(path, stroke);
                            }
                            any = true;
                            break;
                        case Point pt:
                            canvas.DrawCircle(Pixel(pt.Coordinate), Math.Max(2, style.Width * 2), fill.Color.Alpha > 0 ? fill : stroke);
                            any = true;
                            break;
                    }
                }
            }
        }
        if (!any) return null;
        canvas.Flush();
        return bitmap.Bytes;
    }

    static void AddRing(SKPath path, Coordinate[] ring, Func<Coordinate, SKPoint> pixel)
    {
        if (ring.Length == 0) return;
        path.MoveTo(pixel(ring[0]));
        for (int k = 1; k < ring.Length; k++) path.LineTo(pixel(ring[k]));
        path.Close();
    }
}
