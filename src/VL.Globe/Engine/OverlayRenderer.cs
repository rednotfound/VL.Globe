using System.Collections.Generic;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe.Engine;

// One tile's overlay: every layer of Globe's Layers drawn in order into one image - FeatureLayer
// and Geometry by TileOverlay, VectorTiles by the MapLibre painter in the tile's own viewport. Runs
// on a worker thread (the globe renders overlays off the main thread since vector tiles came:
// drawing a street-level tile takes milliseconds that a frame does not have).

/// <summary>A layer as the overlay renderer draws it.</summary>
internal abstract record OverlayLayer;

internal sealed record ShapesOverlay(IReadOnlyList<Geometry> Geometries, TileOverlay.Style Style) : OverlayLayer;

internal sealed record StyleOverlay(MapStyleDocument Document) : OverlayLayer;

internal static class OverlayRenderer
{
    /// <summary>The tile's overlay as straight RGBA bytes, or null when it is empty.</summary>
    /// <param name="tile">The globe tile whose square is drawn.</param>
    /// <param name="layers">Globe's Layers, bottom first.</param>
    /// <param name="size">The overlay's width and height in pixels.</param>
    /// <param name="complete">False when a vector tile it needs was still on its way.</param>
    /// <param name="fetch">False for a tile the globe shows only while its children load: vector tiles are drawn if already here, never fetched.</param>
    /// <param name="missing">Collects the URLs of the vector tiles that were not here.</param>
    public static byte[]? Render(TileKey tile, IReadOnlyList<OverlayLayer> layers, int size, out bool complete, bool fetch = true, ICollection<string>? missing = null)
    {
        complete = true;
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            foreach (var layer in layers)
            {
                switch (layer)
                {
                    case ShapesOverlay g:
                        TileOverlay.DrawOn(canvas, tile, g.Geometries, g.Style, size);
                        break;
                    case StyleOverlay s:
                        complete &= MapStylePainter.Draw(canvas, s.Document, MapViewport.OfTile(tile, size), fetch, missing);
                        break;
                }
            }
            canvas.Flush();
        }
        var premul = bitmap.Bytes;
        bool any = false;
        for (int i = 3; i < premul.Length; i += 4) if (premul[i] != 0) { any = true; break; }
        if (!any) return null;

        // the mip chain and the material take straight alpha
        var straightInfo = info.WithAlphaType(SKAlphaType.Unpremul);
        using var straight = new SKBitmap(straightInfo);
        bitmap.PeekPixels().ReadPixels(straightInfo, straight.GetPixels(), straightInfo.RowBytes);
        return straight.Bytes;
    }
}
