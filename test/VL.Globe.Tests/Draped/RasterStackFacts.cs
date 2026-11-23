using SkiaSharp;

namespace VL.Globe.Tests.Draped;

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
            var bytes = OverlayRenderer.Render(new TileKey(10, 0, 0), new OverlayLayer[] { new RasterOverlay(source, 1) }, 256, out bool complete, fetch: false);
            Assert.False(complete);
            Assert.Null(bytes);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnOpaqueBottomTileLayerIsTheImagery()
    {
        var osm = new OpenStreetMapNode().Update();
        var topo = new XyzTilesNode().Update("https://example.invalid/{z}/{x}/{y}.png", "t", opacity: 0.5f);
        var (imagery, over) = GlobeNode.SplitLayers(new GlobeGroupSpectralNode().Update(new[] { osm, topo }));
        Assert.Same(osm.Raster, imagery);
        Assert.Equal(new[] { topo }, over);
    }

    // the user, 2026-11-23: the bottom layer's Opacity too - "用户知道自己再做什么"
    [Fact]
    public void ATranslucentBottomTileLayerGoesOverThePlainTiles()
    {
        var osm = new OpenStreetMapNode().Update(opacity: 0.4f);
        var topo = new XyzTilesNode().Update("https://example.invalid/{z}/{x}/{y}.png", "t", opacity: 0.5f);
        var (imagery, over) = GlobeNode.SplitLayers(new GlobeGroupSpectralNode().Update(new[] { osm, topo }));
        Assert.Null(imagery);
        Assert.Equal(new[] { osm, topo }, over);
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
