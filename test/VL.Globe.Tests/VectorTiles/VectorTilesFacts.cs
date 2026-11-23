using System.Text.Json;

namespace VL.Globe.Tests.VectorTiles;

// ROADMAP 4c: the VectorTiles layer's parts without a network or a GPU - the lenient parse a real
// style needs, and the service reading a style and its tiles (MapLibre's Berlin fixture tile) from files.
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
            var url = ((VectorSource)service.Document!.Sources["berlin"]).Url(new TileKey(14, 8802, 5374));
            Assert.NotNull(service.Load(url));   // a file source: read at once
        }
        finally { File.Delete(styleFile); }
    }

    [Fact]
    public void AttributionHtmlBecomesText()
        => Assert.Equal("© OpenMapTiles Data from OpenStreetMap",
            VectorTileService.PlainText("<a href=\"https://www.openmaptiles.org/\" target=\"_blank\">&copy; OpenMapTiles</a> Data from <a href=\"https://www.openstreetmap.org/copyright\" target=\"_blank\">OpenStreetMap</a>"));
    [Fact]
    public void AStyleUrlIsOneValueUntilTheUrlChanges()
    {
        // VL compares by identity: the same value every frame, or VectorTiles would start over each frame
        var node = new MapLibreStyleUrlNode();
        var liberty = node.Update();
        Assert.Same(liberty, node.Update());
        Assert.Equal("https://tiles.openfreemap.org/styles/liberty", liberty!.ToString());
        Assert.NotSame(liberty, node.Update("https://tiles.openfreemap.org/styles/bright"));
        Assert.Null(node.Update(" "));

        // an empty Path IOBox gives the document's folder: no style from a folder
        Assert.Null(new MapLibreStyleFileNode().Update(new VL.Lib.IO.Path(Path.GetTempPath())));
    }

    [Fact]
    public void ReadGivesANewStyleThatVectorTilesReadsAgain()
    {
        // the Maputnik loop: the style is a file, edited and saved beside vvvv, read again on Read
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
            var path = new VL.Lib.IO.Path(file);
            var reader = new MapLibreStyleFileNode();
            using var node = new GlobeVectorTilesNode();
            var style = reader.Update(path, read: false);
            var layer = node.Update(out _, out _, style, enabled: true);
            Assert.Equal("#ff0000", Colour(layer.Vector!));

            File.WriteAllText(file, Style("#00ff00"));
            Assert.Same(style, reader.Update(path, read: false));   // the same value: not read again unasked
            layer = node.Update(out _, out _, style, enabled: true);
            Assert.Equal("#ff0000", Colour(layer.Vector!));
            var again = reader.Update(path, read: true);
            Assert.NotSame(style, again);
            Assert.Same(again, reader.Update(path, read: true));    // a held Read reads once
            layer = node.Update(out _, out _, again, enabled: true);
            Assert.Equal("#00ff00", Colour(layer.Vector!));
        }
        finally { File.Delete(file); }
    }
}
