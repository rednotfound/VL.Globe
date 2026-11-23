using System.Text.Json;
using Xunit.Abstractions;

namespace VL.Globe.Tests.Probes;

// By hand (VLGLOBE_PROBE=1), 2026-11-23: the user sees no city or region names. Liberty's place layers,
// and what the lenient parse and the builds skipped or dropped, at zooms where cities are labelled.
public class PlaceLabelProbe(ITestOutputHelper output)
{
    [Fact]
    public void Liberty_place_layers()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1") return;
        using var service = new VectorTileService("https://tiles.openfreemap.org/styles/liberty");
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (service.Document is null && DateTime.UtcNow < until) Thread.Sleep(100);
        var doc = service.Document ?? throw new InvalidOperationException(service.Status);
        output.WriteLine("after parsing, skipped:");
        foreach (var s in doc.Skipped) output.WriteLine("  " + s);

        output.WriteLine("place / boundary layers:");
        foreach (var l in doc.Layers.Where(l => l.SourceLayer is "place" or "boundary" or "water_name" or "mountain_peak"))
        {
            string field = l.Layout is { } lay && lay.TryGetProperty("text-field", out var tf) ? tf.GetRawText() : "-";
            output.WriteLine($"  {l.Id,-28} {l.Type,-8} {l.SourceLayer,-10} z{l.MinZoom}-{l.MaxZoom} text-field {(field.Length > 90 ? field[..90] + "..." : field)}");
        }

        var (sourceId, source) = doc.Sources.First(s => s.Value is VectorSource);
        var all = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null && l.Layer.Source == sourceId).Select(l => l!).ToList();
        // Kyoto at z6, z8, z10, z12: cities and towns, prefectures
        foreach (var (z, lon, lat) in new[] { (6, 135.76, 35.0), (8, 135.76, 35.0), (10, 135.76, 35.0), (12, 135.76, 35.0) })
        {
            int n = 1 << z;
            var key = new TileKey(z, (int)((lon + 180) / 360 * n), (int)(TileGeometry.MercatorY(lat) * n));
            VectorBucket? b = null;
            while (DateTime.UtcNow < until.AddSeconds(60))
            {
                b = VectorBucketBuilder.Build(doc, source, all, false, key, true, null, out bool complete);
                if (b != null && complete) break;
                Thread.Sleep(200);
            }
            bool here = true;
            var data = doc.Tile((VectorSource)source, key, ref here, false, null);
            int places = data != null && data.TryGetValue("place", out var p) ? p.Count : 0;
            var placeLayers = all.Where(l => l.Layer.SourceLayer == "place").Select(l => l.Index).ToHashSet();
            int labels = b!.Labels.Count(x => placeLayers.Contains(x.Layer));
            output.WriteLine($"{key}: {places} place features, {labels} point labels from place layers");
        }
        output.WriteLine("after building, skipped or dropped:");
        foreach (var s in doc.Skipped) output.WriteLine("  " + s);
    }
}
