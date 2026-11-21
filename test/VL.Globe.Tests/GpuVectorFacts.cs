using System.Text.Json;
using SkiaSharp;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// The GPU path's CPU half (docs/DESIGN-GPU-VECTOR.md): a vector tile's bucket - its vertices on the
// unit sphere, its ranges per layer - and the choice of vector tiles for the globe's tiles. What the
// shader then draws is seen in vvvv (rung 4); these are what it is given.
public class GpuVectorFacts
{
    static string Tiles()
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

    static MapStyleDocument Berlin(string tiles)
    {
        using var json = JsonDocument.Parse($$"""
            {
              "version": 8,
              "sources": { "berlin": { "type": "vector", "maxzoom": 14, "tiles": [{{JsonSerializer.Serialize(Path.Combine(tiles, "{z}-{x}-{y}.mvt"))}}] } },
              "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#eeeeee" } },
                { "id": "water", "type": "fill", "source": "berlin", "source-layer": "water", "paint": { "fill-color": "#0000ff" } },
                { "id": "roads", "type": "line", "source": "berlin", "source-layer": "road",
                  "paint": { "line-width": ["interpolate", ["linear"], ["zoom"], 10, 1, 18, 8], "line-color": ["match", ["get", "class"], "main", "#ff0000", "#880000"] },
                  "layout": { "line-cap": "round", "line-join": "round" } }
              ]
            }
            """);
        var doc = MapStyleDocument.Parse(json.RootElement.Clone(), lenient: true);
        doc.Load = url => File.Exists(url) ? File.ReadAllBytes(url) : null;
        return doc;
    }

    static List<GpuStyleLayer> Layers(MapStyleDocument doc)
        => Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null).Select(l => l!).ToList();

    [Fact]
    public void ABucketHoldsEveryLayerAsRangesOfVerticesOnTheSphere()
    {
        var doc = Berlin(Tiles());
        var layers = Layers(doc);
        var key = new TileKey(14, 8802, 5374);
        var bucket = VectorBucketBuilder.Build(doc, (VectorSource)doc.Sources["berlin"], layers, true, key, true, null, out bool complete);
        Assert.True(complete);
        Assert.NotNull(bucket);
        var b = bucket!;

        Assert.Contains(b.Ranges, r => r.Kind == RangeKind.Background && r.Layer == -1);
        Assert.Contains(b.Ranges, r => r.Kind == RangeKind.Fill && r.Layer == 1);
        Assert.Contains(b.Ranges, r => r.Kind == RangeKind.Outline && r.Layer == 1);
        Assert.Contains(b.Ranges, r => r.Kind == RangeKind.Line && r.Layer == 2);
        // the ranges tile the index buffer, in order, and every index names a vertex
        int at = 0;
        foreach (var r in b.Ranges) { Assert.Equal(at, r.Start); Assert.Equal(0, r.Count % 3); at += r.Count; }
        Assert.Equal(b.Indices.Length, at);
        Assert.All(b.Indices, i => Assert.True(i < b.Vertices.Length));

        // on the unit sphere, relative to the tile's centre (float: a z14 tile is 0.0004 radii wide)
        foreach (var v in b.Vertices)
        {
            var p = new D3(b.Centre.X + v.Position.X, b.Centre.Y + v.Position.Y, b.Centre.Z + v.Position.Z);
            Assert.InRange(p.Length, 1 - 1e-6, 1 + 1e-6);
            Assert.InRange(v.Uv.X, -0.2f, 1.2f);   // the tile's buffer lies a little outside its square
            Assert.InRange(v.Uv.Y, -0.2f, 1.2f);
            Assert.InRange(v.N, -1f, 1f);
        }
        // the roads' colour depends on the feature: in the vertices; their width only on the zoom: a uniform
        var roads = b.Ranges.Single(r => r.Kind == RangeKind.Line);
        var colours = Enumerable.Range(roads.Start, roads.Count).Select(i => b.Vertices[b.Indices[i]].Color).ToHashSet();
        Assert.Contains(VectorBucketBuilder.Pack(new StyleColor(0x88 / 255.0, 0, 0, 1)), colours);
        Assert.All(Enumerable.Range(roads.Start, roads.Count), i => Assert.Equal(1f, b.Vertices[b.Indices[i]].Width));
        // the water's fill: one colour, white (its colour is a uniform)
        var water = b.Ranges.Single(r => r.Kind == RangeKind.Fill);
        Assert.All(Enumerable.Range(water.Start, water.Count), i => Assert.Equal(uint.MaxValue, b.Vertices[b.Indices[i]].Color));
    }

    [Fact]
    public void ATileNotHereYetBuildsNothingAndNamesWhatItWaitsFor()
    {
        var tiles = Tiles();
        var doc = Berlin(tiles);
        doc.Load = _ => null;
        doc.Pending = _ => true;
        var missing = new List<string>();
        var bucket = VectorBucketBuilder.Build(doc, (VectorSource)doc.Sources["berlin"], Layers(doc), false, new TileKey(14, 8802, 5374), true, missing, out bool complete);
        Assert.Null(bucket);
        Assert.False(complete);
        Assert.Equal(new[] { Path.Combine(tiles, "14-8802-5374.mvt") }, missing);
    }

    [Fact]
    public void TheUniformsAreThePaintAtTheCamerasZoom()
    {
        var doc = Berlin(Tiles());
        var roads = Layers(doc).Single(l => l.Layer.Id == "roads");
        Assert.True(roads.ColorPerFeature);
        Assert.False(roads.WidthPerFeature);
        Assert.True(roads.Uniforms(new StyleContext { Zoom = 14 }, out var color, out double width, out _));
        Assert.Equal(4.5, width, 6);              // halfway from 1 at z10 to 8 at z18
        Assert.Equal(new StyleColor(1, 1, 1, 1), color);   // per feature: white here, the colour in the vertices
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(14)]
    public void TheBackgroundGridBendsWithTheSphereAtEveryZoom(int zoom)
    {
        // a chequerboard from afar (the user, 2026-11-21): 8 cells at zoom 1 sank 1.8% of the radius
        // under the globe in their middles; cells of 2 degrees at most sag 0.00015
        var b = VectorBucketBuilder.BackgroundBucket(new TileKey(zoom, 0, (1 << zoom) / 2));
        double worst = 0;
        for (int t = 0; t < b.Indices.Length; t += 3)
        {
            D3 P(int k) { var v = b.Vertices[b.Indices[t + k]].Position; return new D3(b.Centre.X + v.X, b.Centre.Y + v.Y, b.Centre.Z + v.Z); }
            var mid = (P(0) + P(1) + P(2)) * (1.0 / 3);
            worst = Math.Max(worst, 1 - mid.Length);
        }
        Assert.InRange(worst, 0, 0.0003);   // under the 0.0015 the shader lifts by, seen from two radii
    }

    [Theory]
    [InlineData(15, 14)]   // a 256-px globe tile at zoom 15 is MapLibre's zoom 14
    [InlineData(19, 14)]   // beyond the source's maxzoom: overscaled
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void TheVectorTileOfAGlobeTileIsTheOneMapLibreWouldPick(int globeZoom, int vectorZoom)
    {
        var source = new VectorSource { MaxZoom = 14, TileSize = 512 };
        var g = new TileKey(globeZoom, (1 << globeZoom) - 1, 0);
        var vk = GpuVectorTiles.VectorKey(source, g)!.Value;
        Assert.Equal(vectorZoom, vk.Zoom);
        Assert.Equal(g.X >> (globeZoom - vectorZoom), vk.X);
    }

    [Fact]
    public void AStandInIsClippedToTheSquareItStandsFor()
    {
        var rect = GpuVectorTiles.Rect(new TileKey(13, 4401, 2687), new TileKey(14, 8803, 5374));
        Assert.Equal(new global::Stride.Core.Mathematics.Vector4(0.5f, 0, 1, 0.5f), rect);
    }

    [Fact]
    public void TheUnextrudedLineIsTheSameLineExtrudedLater()
    {
        // the CPU path's triangles are the GPU path's vertices extruded by the outset: one tessellator
        var line = new List<SKPoint> { new(10, 10), new(60, 12), new(65, 50), new(20, 80) };
        double outset = LineTessellator.Outset(3);
        var cpu = new List<LineVertex>();
        LineTessellator.Tessellate(line, false, "miter", "butt", 2, 1.05, outset, 0, cpu);
        var gpu = new List<ExtrudedVertex>();
        LineTessellator.TessellateExtruded(line.Select(p => ((double)p.X, (double)p.Y)).ToList(), false, "miter", "butt", 2, 1.05, outset, 0, gpu);
        Assert.Equal(cpu.Count, gpu.Count);
        for (int i = 0; i < cpu.Count; i++)
        {
            Assert.Equal(cpu[i].P.X, (float)(gpu[i].X + gpu[i].QX * outset));
            Assert.Equal(cpu[i].P.Y, (float)(gpu[i].Y + gpu[i].QY * outset));
            Assert.Equal(cpu[i].N, gpu[i].N);
        }
    }
}
