using System.Collections.Generic;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe.Engine.Draped;

// One tile's overlay: the draped layers of Globe's Layers drawn in order into one image - FeatureLayer
// and Geometry by TileOverlay, tile layers above the bottom one as images. Runs on a worker thread.
// VectorTiles were drawn here too by a MapLibre painter until 2026-11-24; the GPU draws them now.

/// <summary>A layer as the overlay renderer draws it.</summary>
internal abstract record OverlayLayer;

internal sealed record ShapesOverlay(IReadOnlyList<Geometry> Geometries, TileOverlay.Style Style) : OverlayLayer;

// A tile layer above the bottom one (ROADMAP item 6, 2026-11-23): the same XYZ square as the globe tile,
// drawn at its opacity. The bottom tile layer is the globe's own imagery (mipmaps, fades, stand-ins);
// the ones above it come into the overlay, in their place among the draped layers.
internal sealed record RasterOverlay(GlobeTileSource Source, float Opacity) : OverlayLayer;

internal static class OverlayRenderer
{
    /// <summary>The tile's overlay as straight RGBA bytes, or null when it is empty.</summary>
    /// <param name="tile">The globe tile whose square is drawn.</param>
    /// <param name="layers">Globe's Layers, bottom first.</param>
    /// <param name="size">The overlay's width and height in pixels.</param>
    /// <param name="complete">False when a raster tile it needs was not fetched.</param>
    /// <param name="fetch">False for a tile the globe shows only while its children load: raster tiles are drawn if cached, never fetched.</param>
    // The tile from the source's disk cache, or (a wanted tile) fetched - on this worker thread.
    // A stand-in only draws what is already cached, and says it is incomplete.
    static bool DrawRaster(SKCanvas canvas, TileKey tile, RasterOverlay r, int size, bool fetch)
    {
        if (r.Opacity <= 0) return true;
        var index = new BruTile.TileIndex(tile.X, tile.Y, tile.Zoom);
        byte[]? bytes = r.Source.Http.PersistentCache?.Find(index);
        if (bytes is null && fetch)
        {
            try { bytes = r.Source.Http.GetTileAsync(new BruTile.TileInfo { Index = index }).GetAwaiter().GetResult(); }
            catch (System.Exception) { bytes = null; }   // no tile there: the layer is left out of this square
        }
        if (bytes is null)
        {
            if (!fetch) return false;
            return true;
        }
        using var image = SKBitmap.Decode(bytes);
        if (image is null) return true;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)System.Math.Round(255 * System.Math.Clamp(r.Opacity, 0, 1))), FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(image, new SKRect(0, 0, size, size), paint);
        return true;
    }

    public static byte[]? Render(TileKey tile, IReadOnlyList<OverlayLayer> layers, int size, out bool complete, bool fetch = true)
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
                    case RasterOverlay r:
                        complete &= DrawRaster(canvas, tile, r, size, fetch);
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
