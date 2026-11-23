using SkiaSharp;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 0.1 item 6: tile layers above the bottom one are drawn into the overlay at their opacity.
// No network: the source's disk cache is filled first, and only stand-in renders (fetch false) are made.
public class RasterStackFacts
{
    static readonly TileKey T = new(10, 909, 403);

    static GlobeTileSource SourceWithRed(out string folder)
    {
        var key = "https://example.invalid/" + Guid.NewGuid().ToString("N") + "/{z}/{x}/{y}.png";
        var source = TileSourceFactory.Create(key, "test", key);
        folder = source.CacheFolder;
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(SKColors.Red);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        source.Http.PersistentCache!.Add(new BruTile.TileIndex(T.X, T.Y, T.Zoom), png.ToArray());
        return source;
    }

    [Fact]
    public void ATileLayerAboveTheBottomIsDrawnAtItsOpacity()
    {
        var source = SourceWithRed(out var folder);
        try
        {
            var bytes = OverlayRenderer.Render(T, new OverlayLayer[] { new RasterOverlay(source, 0.5f) }, 256, out bool complete, fetch: false);
            Assert.True(complete);
            Assert.NotNull(bytes);
            // straight RGBA: red, half covering
            Assert.Equal(255, bytes![0]);
            Assert.InRange(bytes[3], 126, 129);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AStandInWithoutTheTileSaysItIsIncomplete()
    {
        var source = SourceWithRed(out var folder);
        try
        {
            var missing = new List<string>();
            var bytes = OverlayRenderer.Render(new TileKey(10, 0, 0), new OverlayLayer[] { new RasterOverlay(source, 1) }, 256, out bool complete, fetch: false, missing: missing);
            Assert.False(complete);
            Assert.Single(missing);
            Assert.Null(bytes);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void OpacityZeroDrawsNothing()
    {
        var source = SourceWithRed(out var folder);
        try
        {
            Assert.Null(OverlayRenderer.Render(T, new OverlayLayer[] { new RasterOverlay(source, 0) }, 256, out bool complete, fetch: false));
            Assert.True(complete);
        }
        finally { Directory.Delete(folder, true); }
    }
}
