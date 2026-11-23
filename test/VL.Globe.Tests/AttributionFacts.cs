using SkiaSharp;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// The credits shown bottom right of the view, as MapLibre's AttributionControl and Mapsui's renderer
// show them (the user, 2026-11-23: "别忘了支持Attribution的信息等等的呈现")
public class AttributionFacts
{
    [Fact]
    public void EveryEnabledLayersCreditOnceInTheLayersOrder()
    {
        var osm = new OpenStreetMapNode().Update();
        var topo = new XyzTilesNode().Update("https://example.org/{z}/{x}/{y}.png", "© OpenTopoMap", 0.5f);
        var again = new XyzTilesNode().Update("https://example.org/b/{z}/{x}/{y}.png", "© OpenTopoMap");
        var off = new XyzTilesNode().Update("https://example.org/c/{z}/{x}/{y}.png", "© Hidden", enabled: false);
        var shapes = new GlobeGeometryLayerNode().Update(Array.Empty<NetTopologySuite.Geometries.Geometry>());
        var mine = new GlobeFeatureLayerNode().Update(attribution: "© My survey");
        var group = new GlobeGroupSpectralNode().Update(new[] { osm, topo, shapes, again, off, mine });
        Assert.Equal("© OpenStreetMap contributors | © OpenTopoMap | © My survey", GlobeNode.AttributionOf(group));
        Assert.Equal("", GlobeNode.AttributionOf(null));
    }

    static (SKRect box, SKBitmap bitmap) Drawn(string text, int width = 400, int height = 200)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        var box = AttributionLayer.Draw(canvas, new SKRect(0, 0, width, height), text, 1);
        canvas.Flush();
        return (box, bitmap);
    }

    [Fact]
    public void TheCreditSitsInTheBottomRightCornerOnALightBox()
    {
        var (box, bitmap) = Drawn("© OpenStreetMap contributors");
        Assert.Equal(400, box.Right);
        Assert.Equal(200, box.Bottom);
        Assert.InRange(box.Height, 14, 24);           // 12 px text, MapLibre's size
        Assert.InRange(box.Width, 120, 260);
        // the box is lighter than the view behind it; above and left of it nothing is drawn
        Assert.True(bitmap.GetPixel((int)box.Left + 1, (int)box.Bottom - 1).Red > 100);
        Assert.Equal(SKColors.Black, bitmap.GetPixel((int)box.Left - 2, (int)box.Bottom - 1));
        Assert.Equal(SKColors.Black, bitmap.GetPixel(399, (int)box.Top - 2));
    }

    [Fact]
    public void ACreditWiderThanTheViewIsCutWithAnEllipsis()
    {
        var long_ = string.Join(" | ", Enumerable.Range(0, 30).Select(i => $"© Provider {i}"));
        var (box, _) = Drawn(long_, 300, 100);
        Assert.InRange(box.Left, 0, 300);
        Assert.Equal(300, box.Right);
    }

    [Fact]
    public void AStylesGeoJsonSourceGivesItsCreditToo()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            { "version": 8, "sources": { "mine": { "type": "geojson", "attribution": "<a href='x'>© My data</a>",
              "data": { "type": "FeatureCollection", "features": [] } } }, "layers": [] }
            """);
        var doc = MapStyleDocument.Parse(json.RootElement, lenient: true);
        Assert.Equal("© My data", VectorTileService.PlainText(doc.Sources["mine"].Attribution));
    }
}
