using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe.Tests.Draped;

// EXPERIMENT (ROADMAP 0.1 item 3): vectors draped into tile overlays.
public class TileOverlayFacts
{
    static readonly GeometryFactory F = new();
    static readonly TileOverlay.Style Red = new(new SKColor(255, 0, 0), 2, SKColors.Transparent);
    static readonly TileKey T = new(10, 909, 403);   // Tokyo

    static (double west, double east, double north, double south) Box(TileKey t)
    {
        double n = t.TilesPerSide;
        return (TileGeometry.LongitudeOf(t.X / n), TileGeometry.LongitudeOf((t.X + 1) / n),
                TileGeometry.LatitudeOf(t.Y / n), TileGeometry.LatitudeOf((t.Y + 1) / n));
    }

    static byte Alpha(byte[] px, int size, int x, int y) => px[(y * size + x) * 4 + 3];

    [Fact]
    public void ALineAcrossTheTileIsDrawnOnItsRow()
    {
        var (w, e, n, s) = Box(T);
        double lat = TileGeometry.LatitudeOf((T.Y + 0.5) / T.TilesPerSide);   // the tile's middle row in Mercator
        var line = F.CreateLineString(new[] { new Coordinate(w - 1, lat), new Coordinate(e + 1, lat) });
        var px = TileOverlay.Rasterize(T, new Geometry[] { line }, Red, 256)!;
        Assert.True(Alpha(px, 256, 128, 128) > 200);     // on the line
        Assert.Equal(0, Alpha(px, 256, 128, 40));        // well above it
        Assert.Equal(255, px[(128 * 256 + 128) * 4]);    // red
    }

    [Fact]
    public void GeometryElsewhereDrawsNothing()
    {
        var far = F.CreatePoint(new Coordinate(-74, 40.7));   // New York
        Assert.Null(TileOverlay.Rasterize(T, new Geometry[] { far }, Red, 256));
    }

    [Fact]
    public void APolygonFillsItsInside()
    {
        var (w, e, n, s) = Box(T);
        var poly = F.CreatePolygon(new[] { new Coordinate(w, s), new Coordinate(e, s), new Coordinate(e, n), new Coordinate(w, n), new Coordinate(w, s) });
        var style = new TileOverlay.Style(SKColors.Transparent, 0, new SKColor(0, 0, 255, 128));
        var px = TileOverlay.Rasterize(T, new Geometry[] { poly }, style, 64)!;
        Assert.InRange(Alpha(px, 64, 32, 32), 120, 136);   // half-transparent blue, straight alpha
        Assert.Equal(255, px[(32 * 64 + 32) * 4 + 2]);
    }

    [Fact]
    public void AStraightAlphaMipKeepsTheLineColourNotBlack()
    {
        // one opaque white pixel among three transparent black ones: the coarser level is white at a
        // quarter alpha, not a grey that would ring every line with a dark halo
        var p = new byte[] { 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var one = MipChain.Build(p, 2, 2, srgbColour: true, straightAlpha: true)[1].pixels;
        Assert.Equal(255, one[0]);
        Assert.InRange(one[3], 63, 64);
        var plain = MipChain.Build(p, 2, 2, srgbColour: true, straightAlpha: false)[1].pixels;
        Assert.True(plain[0] < 200);    // the halo this avoids
    }
}
