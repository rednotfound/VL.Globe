using System.Text.Json;
using SkiaSharp;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// The GPU path's CPU half (docs/DESIGN-GPU-VECTOR.md): a vector tile's bucket - its vertices on the
// unit sphere, its ranges per layer - and the choice of vector tiles for the globe's tiles. What the
// shader then draws is seen in vvvv (rung 4); these are what it is given.
public class GpuVectorFacts
{
    // where the GPU puts a vertex: its place in the tile projected by the shader's arithmetic
    // (TileProjection.RelativeFloat, GlobeVector.sdsl's Relative), on the unit sphere
    static D3 At(VectorBucket b, VectorVertex v, bool extruded = false)
    {
        var r = TileProjection.RelativeFloat(b.Geo, v.Uv.X, v.Uv.Y, extruded ? v.Width : 0, flat: false);
        return new D3(b.Centre.X + r.X, b.Centre.Y + r.Y, b.Centre.Z + r.Z);
    }

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

    [Fact]
    public void A_casing_and_its_road_share_one_set_of_vertices()
    {
        // ROADMAP 4g step 2: Liberty's casings and roads were built twice, the same lines. A layer like
        // an earlier one in every byte draws its span; another cap, or a colour per feature, does not
        using var json = JsonDocument.Parse($$"""
            {
              "version": 8,
              "sources": { "berlin": { "type": "vector", "maxzoom": 14, "tiles": [{{JsonSerializer.Serialize(Path.Combine(Tiles(), "{z}-{x}-{y}.mvt"))}}] } },
              "layers": [
                { "id": "casing", "type": "line", "source": "berlin", "source-layer": "road",
                  "paint": { "line-width": 6, "line-color": "#999999" }, "layout": { "line-cap": "round", "line-join": "round" } },
                { "id": "road", "type": "line", "source": "berlin", "source-layer": "road",
                  "paint": { "line-width": 3, "line-color": "#ffffff" }, "layout": { "line-cap": "round", "line-join": "round" } },
                { "id": "butt", "type": "line", "source": "berlin", "source-layer": "road",
                  "paint": { "line-width": 3 }, "layout": { "line-cap": "butt", "line-join": "round" } },
                { "id": "by-class", "type": "line", "source": "berlin", "source-layer": "road",
                  "paint": { "line-color": ["match", ["get", "class"], "main", "#ff0000", "#880000"] }, "layout": { "line-cap": "round", "line-join": "round" } }
              ]
            }
            """);
        var doc = MapStyleDocument.Parse(json.RootElement.Clone(), lenient: true);
        doc.Load = url => File.Exists(url) ? File.ReadAllBytes(url) : null;
        var b = VectorBucketBuilder.Build(doc, (VectorSource)doc.Sources["berlin"], Layers(doc), false, new TileKey(14, 8802, 5374), true, null, out _)!;
        var r = b.Ranges.ToDictionary(x => doc.Layers[x.Layer].Id);
        Assert.Equal(r["casing"].Start, r["road"].Start);
        Assert.Equal(r["casing"].Count, r["road"].Count);
        Assert.NotEqual(r["casing"].Start, r["butt"].Start);
        Assert.NotEqual(r["casing"].Start, r["by-class"].Start);
        // three spans built, not four
        Assert.Equal(b.IndexCount, r["casing"].Count + r["butt"].Count + r["by-class"].Count);
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
        // the ranges tile the index buffer, in order - a line layer the same as an earlier one draws that
        // one's span (ShareOrKeep) - and every index names a vertex
        int at = 0;
        foreach (var r in b.Ranges.DistinctBy(r => (r.Start, r.Count)))
        {
            Assert.Equal(at, r.Start);
            Assert.Equal(0, r.Count % (r.Kind == RangeKind.Outline ? 2 : 3));
            at += r.Count;
        }
        Assert.Equal(b.IndexCount, at);
        foreach (var r in b.Ranges)
            for (int i = r.Start; i < r.Start + r.Count; i++) Assert.True(r.BaseVertex + b.Indices[i] < b.VertexCount);

        // on the unit sphere, relative to the tile's centre (float: a z14 tile is 0.0004 radii wide)
        foreach (var v in b.VertexSpan)
        {
            var p = At(b, v);
            Assert.InRange(p.Length, 1 - 1e-6, 1 + 1e-6);
            Assert.InRange(v.Uv.X, -0.2f, 1.2f);   // the tile's buffer lies a little outside its square
            Assert.InRange(v.Uv.Y, -0.2f, 1.2f);
            Assert.InRange(v.N, -1f, 1f);
        }
        // the roads' colour depends on the feature: in the vertices; their width only on the zoom: a uniform
        var roads = b.Ranges.Single(r => r.Kind == RangeKind.Line);
        var colours = Enumerable.Range(roads.Start, roads.Count).Select(i => b.VertexAt(roads, i).Color).ToHashSet();
        Assert.Contains(VectorBucketBuilder.Pack(new StyleColor(0x88 / 255.0, 0, 0, 1)), colours);
        Assert.All(Enumerable.Range(roads.Start, roads.Count), i => Assert.Equal(1f, b.VertexAt(roads, i).Width));
        // the water's fill: one colour, white (its colour is a uniform)
        var water = b.Ranges.Single(r => r.Kind == RangeKind.Fill);
        Assert.All(Enumerable.Range(water.Start, water.Count), i => Assert.Equal(uint.MaxValue, b.VertexAt(water, i).Color));
    }

    [Fact]
    public void BuildingsAreExtrudedToTheirHeightWithWallsFacingOut()
    {
        // Liberty's buildings are a fill below z14 and fill-extrusion from 14: without it they
        // vanished as one zoomed in (the user, 2026-11-21). In 3D on the GPU path - and only in a
        // lenient parse: the harness renders MapLibre's fixtures with the drape, which has no 3D.
        var tiles = Tiles();
        using var json = JsonDocument.Parse($$"""
            {
              "version": 8,
              "sources": { "berlin": { "type": "vector", "maxzoom": 14, "tiles": [{{JsonSerializer.Serialize(Path.Combine(tiles, "{z}-{x}-{y}.mvt"))}}] } },
              "layers": [
                { "id": "building-3d", "type": "fill-extrusion", "source": "berlin", "source-layer": "building", "minzoom": 14,
                  "paint": { "fill-extrusion-color": "#ccbbaa", "fill-extrusion-height": 30, "fill-extrusion-opacity": 0.8 } }
              ]
            }
            """);
        Assert.Throws<StyleUnsupportedException>(() => MapStyleDocument.Parse(json.RootElement));
        var doc = MapStyleDocument.Parse(json.RootElement.Clone(), lenient: true);
        doc.Load = url => File.Exists(url) ? File.ReadAllBytes(url) : null;
        var layer = Assert.Single(Layers(doc));
        Assert.True(layer.IsExtrusion);
        Assert.True(layer.Uniforms(new StyleContext { Zoom = 15 }, out var color, out _, out _));
        Assert.Equal(0.8, color.A, 6);
        Assert.False(layer.Uniforms(new StyleContext { Zoom = 13 }, out _, out _, out _));   // its minzoom
        var b = VectorBucketBuilder.Build(doc, (VectorSource)doc.Sources["berlin"], Layers(doc), false, new TileKey(14, 8802, 5374), true, null, out _)!;
        var range = Assert.Single(b.Ranges);
        Assert.Equal(RangeKind.Extrusion, range.Kind);

        double up = 30 / 6378137.0;
        int roofs = 0, walls = 0;
        for (int t = range.Start; t < range.Start + range.Count; t += 3)
        {
            var vs = new[] { b.VertexAt(range, t), b.VertexAt(range, t + 1), b.VertexAt(range, t + 2) };
            D3 P(VectorVertex v) => At(b, v, extruded: true);
            if (vs.All(v => v.Extrude == default))
            {
                roofs++;
                foreach (var v in vs) Assert.InRange(P(v).Length - 1, up - 1e-7, up + 1e-7);   // raised 30 m
                continue;
            }
            walls++;
            Assert.All(vs, v => Assert.InRange(P(v).Length - 1, -1e-7, up + 1e-7));   // between the ground and the roof
        }
        Assert.True(roofs > 1000, $"{roofs} roof triangles");
        Assert.True(walls > 1000, $"{walls} wall triangles");
    }

    [Fact]
    public void AWallFacesAwayFromItsBuildingWhicheverWayTheRingWinds()
    {
        // a square building in the middle of a tile, wound both ways: each wall's normal points out
        foreach (bool clockwise in new[] { false, true })
        {
            // 5 tile units a side: uv is packed to 1/32768 of two tiles (0.016 units of 512)
            var ring = new (double, double)[] { (0.5, 0.5), (0.51, 0.5), (0.51, 0.51), (0.5, 0.51), (0.5, 0.5) };
            if (clockwise) ring = ring.Reverse().ToArray();
            var f = new StyleFeature { Kind = StyleFeature.Shape.Polygon };
            f.SetParts(new[] { new[] { ring } });
            using var json = JsonDocument.Parse("""
                { "version": 8, "sources": { "s": { "type": "geojson", "data": { "type": "FeatureCollection", "features": [] } } },
                  "layers": [ { "id": "b", "type": "fill-extrusion", "source": "s", "paint": { "fill-extrusion-height": 10 } } ] }
                """);
            var doc = MapStyleDocument.Parse(json.RootElement.Clone(), lenient: true);
            var b = VectorBucketBuilder.BuildFeatures(new TileKey(0, 0, 0), Layers(doc)[0], new FeatureTable(new[] { f }), new StyleContext { Zoom = 0 });
            var centre = (0.505 * 512, 0.505 * 512);
            int checkedWalls = 0;
            foreach (var v in b.VertexSpan)
            {
                if (v.Extrude == default) continue;
                double px = v.Uv.X * 512, py = v.Uv.Y * 512;
                // a wall vertex sits on an edge; its normal points from the centre towards that edge
                double dot = (px - centre.Item1) * v.Extrude.X + (py - centre.Item2) * v.Extrude.Y;
                Assert.True(dot > 0, $"wound {(clockwise ? "clockwise" : "counter-clockwise")}: a wall faces in");
                checkedWalls++;
            }
            Assert.Equal(16, checkedWalls);
        }
    }

    [Fact]
    public void AVertexKeepsWhatTheShaderNeedsIn28Bytes()
    {
        Assert.Equal(VectorVertex.Size, System.Runtime.InteropServices.Marshal.SizeOf<VectorVertex>());
        Assert.Equal(VectorVertex.Size, VectorVertex.Layout.VertexStride);
        // GL JS's extrusion bytes, exactly (they wrap at 127/63); a unit vector to 1/126
        foreach (var e in new[] { new global::Stride.Core.Mathematics.Vector2(127 / 63f, -128 / 63f), new(5 / 63f, -40 / 63f), new(0, 0) })
            Assert.Equal(e, new VectorVertex { Extrude = e }.Extrude);
        var unit = new VectorVertex { Extrude = new(0.6f, -0.8f) }.Extrude;
        Assert.InRange(unit.X - 0.6f, -1 / 126f, 1 / 126f);
        Assert.InRange(unit.Y + 0.8f, -1 / 126f, 1 / 126f);
        // uv from the buffer left of the square to the buffer right of it, to 1/32768 of two tiles
        foreach (var u in new[] { -0.2f, 0f, 0.5f, 1f, 1.2f })
        {
            var v = new VectorVertex { Uv = new(u, u) };
            Assert.InRange(v.Uv.X - u, -3.1e-5f, 3.1e-5f);
        }
        Assert.Equal(new VectorVertex().Extrude, default);   // a roof: no extrusion, exactly
    }

    [Fact]
    public void ALayerHiddenAtTheTilesZoomIsNotBuilt()
    {
        // GL JS's worker_tile.ts: `zoom >= maxzoom` skips the layer. Liberty's flat building (maxzoom 14)
        // was a quarter of a street-level tile's bytes, uploaded and never drawn.
        var tiles = Tiles();
        using var json = JsonDocument.Parse($$"""
            {
              "version": 8,
              "sources": { "berlin": { "type": "vector", "maxzoom": 14, "tiles": [{{JsonSerializer.Serialize(Path.Combine(tiles, "{z}-{x}-{y}.mvt"))}}] } },
              "layers": [
                { "id": "below-14", "type": "fill", "source": "berlin", "source-layer": "water", "maxzoom": 14, "paint": { "fill-color": "#0000ff" } },
                { "id": "below-15", "type": "fill", "source": "berlin", "source-layer": "water", "maxzoom": 15, "paint": { "fill-color": "#0000ff" } }
              ]
            }
            """);
        var doc = MapStyleDocument.Parse(json.RootElement.Clone(), lenient: true);
        doc.Load = url => File.Exists(url) ? File.ReadAllBytes(url) : null;
        var b = VectorBucketBuilder.Build(doc, (VectorSource)doc.Sources["berlin"], Layers(doc), false, new TileKey(14, 8802, 5374), true, null, out _)!;
        Assert.NotEmpty(b.Ranges);
        Assert.All(b.Ranges, r => Assert.Equal(1, r.Layer));
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
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    public void TheBackgroundGridBendsWithTheSphereAtEveryZoom(int zoom)
    {
        // a chequerboard from afar (the user, 2026-11-21): 8 cells at zoom 1 sank 1.8% of the radius
        // under the globe in their middles. Then discs at zoom 8: a cell must sag less than the
        // shader's lift, 0.0015 of the distance, from where a tile of that zoom is seen - about its
        // own size (a tile 256 px tall in a 45-degree view is ~ a fifth of the distance), so a
        // thousandth of a tile here
        var key = new TileKey(zoom, 0, (1 << zoom) / 2);
        var b = VectorBucketBuilder.BackgroundBucket(key);
        double tile = 2 * Math.PI / (1 << zoom);
        double worst = 0;
        var cells = Assert.Single(b.Ranges);
        for (int t = 0; t < b.IndexCount; t += 3)
        {
            D3 P(int k) => At(b, b.VertexAt(cells, t + k));
            var mid = (P(0) + P(1) + P(2)) * (1.0 / 3);
            worst = Math.Max(worst, 1 - mid.Length);
        }
        Assert.InRange(worst, 0, Math.Min(0.0003, tile * 0.001));
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
    public void InsideTheFogsReachNoVectorTileIsCoarserThanTheCamerasZoom()
    {
        // ROADMAP 4f step 3: the user's tilted view over Tokyo drew z13 tiles 6 km off, whose buildings
        // have no heights - the 3D buildings ended in a line. The floor (14) replaces them there.
        var g14 = new TileKey(14, 14552, 6451);
        Assert.Equal(new[] { new TileKey(13, 7276, 3225) }, GpuVectorTiles.VectorKeys(0, 14, 512, g14, -1));   // no floor: GL JS's choice
        Assert.Equal(new[] { new TileKey(14, 14552, 6451) }, GpuVectorTiles.VectorKeys(0, 14, 512, g14, 14));  // the floor at g's own zoom
        var g12 = new TileKey(12, 3638, 1612);
        var parts = GpuVectorTiles.VectorKeys(0, 14, 512, g12, 14).ToList();                                    // g coarser: its parts
        Assert.Equal(16, parts.Count);
        Assert.All(parts, k => Assert.Equal((14, 3638, 1612), (k.Zoom, k.X >> 2, k.Y >> 2)));
        Assert.Equal(64, GpuVectorTiles.VectorKeys(0, 14, 512, new TileKey(9, 454, 201), 14).Count());          // at most 3 levels down
        Assert.Single(GpuVectorTiles.VectorKeys(0, 14, 512, g14, 12));                                            // a floor below the choice changes nothing
    }

    [Theory]
    [InlineData(15, 1)]    // MapLibre's zoom 14 = the source's maxzoom: laid out at its own size
    [InlineData(16, 2)]    // past the maxzoom: GL JS's overscaledZ 15, overscaling 2
    [InlineData(18, 8)]    // street level, zoom 17: 8 - symbol spacing in the shown pixels
    public void SymbolsPastTheMaxzoomAreLaidOutForTheZoomTheyAreSeenAt(int globeZoom, int overscale)
    {
        var source = new VectorSource { MaxZoom = 14, TileSize = 512 };
        var g = new TileKey(globeZoom, 0, 0);
        var vk = GpuVectorTiles.VectorKey(source, g)!.Value;
        Assert.Equal(overscale, GpuVectorTiles.Overscale(source.TileSize, vk, g));
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
