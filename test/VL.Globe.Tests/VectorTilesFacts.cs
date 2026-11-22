using System.Text.Json;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 4c: the VectorTiles layer's parts without a network or a GPU - the lenient parse a real
// style needs, the overlay of a globe tile drawn from a vector tile (MapLibre's Berlin fixture
// tile), the "not here yet" path, and the service reading a style and tiles from files.
public class VectorTilesFacts
{
    static string Fixtures()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VL.Globe.nuspec")))
            {
                var assets = Path.Combine(d.FullName, "test", "fixtures", "maplibre", "v6.19.0", "_assets", "tiles");
                Assert.True(Directory.Exists(assets), $"fetch the fixtures first: .\\tools\\Fetch-MapLibreFixtures.ps1 ({assets})");
                return assets;
            }
        throw new DirectoryNotFoundException("no VL.Globe.nuspec above the test assembly");
    }

    // the Berlin tile 14/8802/5374 and a style drawing its roads and water
    static string BerlinStyle(string tiles) => $$"""
        {
          "version": 8,
          "sources": { "berlin": { "type": "vector", "maxzoom": 14, "tiles": [{{JsonSerializer.Serialize(Path.Combine(tiles, "{z}-{x}-{y}.mvt"))}}] } },
          "layers": [
            { "id": "water", "type": "fill", "source": "berlin", "source-layer": "water", "paint": { "fill-color": "#0000ff" } },
            { "id": "roads", "type": "line", "source": "berlin", "source-layer": "road", "paint": { "line-width": 3, "line-color": "#ff0000" } },
            { "id": "labels", "type": "symbol", "source": "berlin", "source-layer": "road_label", "layout": { "text-field": "{name}" } },
            { "id": "dashed", "type": "line", "source": "berlin", "source-layer": "road", "paint": { "line-dasharray": [2, 1] } }
          ]
        }
        """;

    // a globe tile (256 px) inside the Berlin tile: zoom 15 shows the vector tile's quarter at 512 px of z14
    static readonly TileKey Inside = new(15, 8802 * 2, 5374 * 2);

    [Fact]
    public void ALenientParseLeavesOutWhatItCannotDrawAndSaysSo()
    {
        var tiles = Fixtures();
        using var json = JsonDocument.Parse(BerlinStyle(tiles));
        Assert.Throws<StyleUnsupportedException>(() => MapStyleDocument.Parse(json.RootElement));
        var doc = MapStyleDocument.Parse(json.RootElement, lenient: true);
        // labels are read since 4e (the GPU path draws them); the dashes are still drawn solid
        Assert.Equal(new[] { "water", "roads", "labels", "dashed" }, doc.Layers.Select(l => l.Id));
        Assert.Contains(doc.Skipped, s => s.StartsWith("dashed:") && s.Contains("line-dasharray"));
    }

    [Fact]
    public void AGlobeTileIsDrawnFromTheVectorTileAroundIt()
    {
        var tiles = Fixtures();
        using var json = JsonDocument.Parse(BerlinStyle(tiles));
        var doc = MapStyleDocument.Parse(json.RootElement, lenient: true);
        var asked = new List<string>();
        doc.Load = url => { lock (asked) asked.Add(url); return File.Exists(url) ? File.ReadAllBytes(url) : null; };
        // the vector tile's own square at its own 512 px: every feature in it, nothing from a neighbour
        var bytes = OverlayRenderer.Render(new TileKey(14, 8802, 5374), new OverlayLayer[] { new StyleOverlay(doc) }, 512, out bool complete);
        Assert.True(complete);
        Assert.NotNull(bytes);
        Assert.Equal(new[] { Path.Combine(tiles, "14-8802-5374.mvt") }, asked.Distinct());
        int red = 0, blue = 0;
        for (int i = 0; i < bytes!.Length; i += 4)
        {
            if (bytes[i + 3] < 200) continue;
            if (bytes[i] > 200 && bytes[i + 2] < 60) red++;
            if (bytes[i + 2] > 200 && bytes[i] < 60) blue++;
        }
        Assert.True(red > 2000, $"{red} road pixels");   // Berlin streets
        Assert.True(blue > 100, $"{blue} water pixels");  // the Landwehr canal and the Spree
    }

    [Fact]
    public void ATileStillOnItsWayLeavesTheOverlayIncomplete()
    {
        var tiles = Fixtures();
        using var json = JsonDocument.Parse(BerlinStyle(tiles));
        var doc = MapStyleDocument.Parse(json.RootElement, lenient: true);
        bool here = false;
        doc.Load = url => here ? File.ReadAllBytes(url) : null;
        doc.Pending = _ => !here;
        var missing = new List<string>();
        var first = OverlayRenderer.Render(Inside, new OverlayLayer[] { new StyleOverlay(doc) }, 256, out bool complete, missing: missing);
        Assert.False(complete);
        Assert.Null(first);
        // what it waits for: the globe draws it again only when this tile has come, not on every arrival
        Assert.Equal(new[] { Path.Combine(tiles, "14-8802-5374.mvt") }, missing.Distinct());
        here = true;   // it arrives; the miss was not remembered
        var second = OverlayRenderer.Render(Inside, new OverlayLayer[] { new StyleOverlay(doc) }, 256, out complete);
        Assert.True(complete);
        Assert.NotNull(second);
    }

    [Fact]
    public void AStandInTileNeverFetchesButDrawsWhatIsHere()
    {
        var tiles = Fixtures();
        using var json = JsonDocument.Parse(BerlinStyle(tiles));
        var doc = MapStyleDocument.Parse(json.RootElement, lenient: true);
        int loads = 0;
        bool cached = false;
        doc.Load = url => { Interlocked.Increment(ref loads); return null; };
        doc.Pending = _ => true;
        doc.Peek = url => cached ? File.ReadAllBytes(url) : null;
        var layers = new OverlayLayer[] { new StyleOverlay(doc) };
        Assert.Null(OverlayRenderer.Render(Inside, layers, 256, out bool complete, fetch: false));
        Assert.False(complete);
        Assert.Equal(0, loads);   // nothing fetched for a stand-in
        cached = true;            // the tile came for a wanted neighbour
        Assert.NotNull(OverlayRenderer.Render(Inside, layers, 256, out complete, fetch: false));
        Assert.True(complete);
        Assert.Equal(0, loads);
    }

    [Fact]
    public void TheServiceReadsAStyleAndItsTilesFromFiles()
    {
        var tiles = Fixtures();
        var styleFile = Path.Combine(Path.GetTempPath(), $"vl-globe-style-{Guid.NewGuid():N}.json");
        File.WriteAllText(styleFile, BerlinStyle(tiles));
        try
        {
            using var service = new VectorTileService(styleFile);
            Assert.Null(service.Document);   // nothing before Start
            service.Start();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (service.Document is null && DateTime.UtcNow < until) Thread.Sleep(20);
            Assert.True(service.Document != null, service.Status);
            Assert.StartsWith("4 layers drawn", service.Status);   // water, roads, labels (4e) and the dashed line drawn solid
            var bytes = OverlayRenderer.Render(Inside, new OverlayLayer[] { new StyleOverlay(service.Document!) }, 256, out bool complete);
            Assert.True(complete);
            Assert.NotNull(bytes);
        }
        finally { File.Delete(styleFile); }
    }

    [Fact]
    public void AttributionHtmlBecomesText()
        => Assert.Equal("© OpenMapTiles Data from OpenStreetMap",
            VectorTileService.PlainText("<a href=\"https://www.openmaptiles.org/\" target=\"_blank\">&copy; OpenMapTiles</a> Data from <a href=\"https://www.openstreetmap.org/copyright\" target=\"_blank\">OpenStreetMap</a>"));
    [Fact]
    public void ReloadReadsAStyleFileAgainAfterItWasSaved()
    {
        // the Maputnik loop: the style is a file, edited and saved beside vvvv, read again on Reload
        var file = Path.Combine(Path.GetTempPath(), $"vlglobe-reload-{Environment.ProcessId}.json");
        string Style(string colour) => $$"""{ "version": 8, "sources": {}, "layers": [ { "id": "bg", "type": "background", "paint": { "background-color": "{{colour}}" } } ] }""";
        string Colour(VectorTileService s)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (s.Document is null && DateTime.UtcNow < until) Thread.Sleep(20);
            return s.Document!.Layers[0].Paint.GetProperty("background-color").GetString()!;
        }
        try
        {
            File.WriteAllText(file, Style("#ff0000"));
            using var node = new GlobeVectorTilesNode();
            var layer = node.Update(out _, out _, file, reload: false, enabled: true);
            Assert.Equal("#ff0000", Colour(layer.Vector!));

            File.WriteAllText(file, Style("#00ff00"));
            layer = node.Update(out _, out _, file, reload: false, enabled: true);
            Assert.Equal("#ff0000", Colour(layer.Vector!));   // not read again unasked
            layer = node.Update(out _, out _, file, reload: true, enabled: true);
            Assert.Equal("#00ff00", Colour(layer.Vector!));
        }
        finally { File.Delete(file); }
    }
}
