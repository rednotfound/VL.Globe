using Xunit.Abstractions;

namespace VL.Globe.Tests.Probes;

// Not a fact: a measurement, run by hand (set VLGLOBE_PROBE=1). What one of Liberty's street-level
// vector tiles is made of on the GPU path, layer by layer - single tiles of 20-30 MB were uploaded in
// one frame while tilting over Kyoto (2026-11-22). Fetches the style and the tile (or reads the cache).
public class TileSizeProbe(ITestOutputHelper output)
{
    [Probe]
    public void Kyoto_station_z14_on_liberty() => Measure(new TileKey(14, 14371, 6491));

    // the far tiles of the user's tilted view over Tokyo (2026-11-22): do z13 tiles hold buildings?
    [Probe]
    public void Kanda_z13_on_liberty() => Measure(new TileKey(13, 7276, 3225));

    void Measure(TileKey key)
    {
        using var service = new VectorTileService("https://tiles.openfreemap.org/styles/liberty");
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (service.Document is null && DateTime.UtcNow < until) Thread.Sleep(100);
        var doc = service.Document ?? throw new InvalidOperationException(service.Status);
        var (sourceId, source) = doc.Sources.First(s => s.Value is VectorSource);
        var layers = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null && l.Layer.Source == sourceId && !l.IsSymbol).Select(l => l!).ToList();

        VectorBucket? b = null;
        while (DateTime.UtcNow < until)
        {
            b = VectorBucketBuilder.Build(doc, source, layers, false, key, true, null, out bool complete);
            if (b != null && complete) break;
            Thread.Sleep(200);
        }
        Assert.NotNull(b);
        bool here = true;
        if (doc.Tile((VectorSource)source, key, ref here, false, null) is { } data && data.TryGetValue("building", out var buildings))
        {
            var heights = Enumerable.Range(0, buildings.Count).Select(i => buildings.Get(i).Properties.GetValueOrDefault("render_height")).ToList();
            output.WriteLine($"{buildings.Count} buildings, {heights.Count(h => h is null)} without render_height; first: {string.Join(" ", heights.Take(12))}");
        }
        output.WriteLine($"{key}: {b!.VertexCount} vertices x {VectorVertex.Size} B + {b.IndexCount} indices x 2 B = {((long)b.VertexCount * VectorVertex.Size + b.IndexCount * 2L) / 1e6:F1} MB, built in {b.Ms:F0} ms");
        foreach (var g in b.Ranges.GroupBy(r => (r.Layer, r.Kind)).OrderByDescending(g => g.Sum(r => r.Count)))
        {
            int min = int.MaxValue, max = -1, count = 0;
            foreach (var r in g)
            {
                count += r.Count;
                for (int i = r.Start; i < r.Start + r.Count; i++) { min = Math.Min(min, r.BaseVertex + b.Indices[i]); max = Math.Max(max, r.BaseVertex + b.Indices[i]); }
            }
            int vertices = max < 0 ? 0 : max - min + 1;
            output.WriteLine($"  {doc.Layers[g.Key.Layer].Id,-32} {g.Key.Kind,-10} {count,9} indices {vertices,8} vertices  {(vertices * (long)VectorVertex.Size + count * 2L) / 1e6,6:F2} MB");
        }
    }
}
