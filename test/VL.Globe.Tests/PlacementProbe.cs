using System.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// Not a fact: a measurement, run by hand (VLGLOBE_PROBE=1). Where label placement's time goes on
// Liberty at Kyoto Station, z16, tilted: placement spiked to 20-38 ms in vvvv as tiles came (2026-11-22).
public class PlacementProbe(ITestOutputHelper output)
{
    [Fact]
    public void Kyoto_station_z16_tilted_on_liberty()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1") return;
        using var service = new VectorTileService("https://tiles.openfreemap.org/styles/liberty");
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(120);
        while (service.Document is null && DateTime.UtcNow < until) Thread.Sleep(100);
        var doc = service.Document ?? throw new InvalidOperationException(service.Status);
        var (sourceId, source) = doc.Sources.First(s => s.Value is VectorSource);
        var all = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null && l.Layer.Source == sourceId).Select(l => l!).ToList();
        var symbols = all.Where(l => l.IsSymbol).ToList();

        using var device = GraphicsDevice.New(DeviceCreationFlags.None, GraphicsProfile.Level_11_0);
        var tiles = new List<GpuTile>();
        double zoom = 16, cx = (135.7588 + 180) / 360, cy = TileGeometry.MercatorY(34.9858);
        var buildClock = Stopwatch.StartNew();
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                var key = new TileKey(14, 14371 + dx, 6491 + dy);
                VectorBucket? b = null;
                while (DateTime.UtcNow < until)
                {
                    b = VectorBucketBuilder.Build(doc, source, symbols, false, key, true, null, out bool complete, VectorProjection.Mercator, overscale: 4);
                    if (b != null && complete) break;
                    Thread.Sleep(200);
                }
                if (b != null) tiles.Add(GpuTile.Upload(device, b));
            }
        output.WriteLine($"{tiles.Count} tiles, {tiles.Sum(t => t.Labels.Count)} point labels, {tiles.Sum(t => t.LineLabels.Count)} line labels, built in {buildClock.ElapsedMilliseconds} ms");

        // the eye over the centre at z16 for 1200 x 800 CSS px, pitched 60 degrees
        float width = 1200, height = 800;
        double worldSize = 512 * Math.Pow(2, zoom), fov = 0.6435011087932844;
        double distance = height / 2.0 / Math.Tan(fov / 2) / worldSize;
        var view = Matrix.RotationX(MathUtil.DegreesToRadians(60));
        var projection = Matrix.PerspectiveFovRH((float)fov, width / height, (float)(distance * 0.01), (float)(distance * 100));
        var ctx = new StyleContext { Zoom = zoom, GlobalState = doc.GlobalState };
        Matrix World(GpuTile t, double ox) => Matrix.Translation((float)(t.Bucket.Centre.X - cx - ox), (float)(t.Bucket.Centre.Y + cy), (float)(t.Bucket.Centre.Z - distance));
        List<LabelPlacement.Layer> Layers(double ox)
        {
            var list = new List<LabelPlacement.Layer>();
            foreach (var layer in symbols)
            {
                if (!layer.SymbolUniforms(ctx, out var paint)) continue;
                list.Insert(0, new LabelPlacement.Layer(layer.Index, tiles.Select(t => (t, World(t, ox))).ToList(), (float)paint!.Size));
            }
            return list;
        }
        var placement = new LabelPlacement();
        var layers = Layers(0);
        output.WriteLine($"{layers.Count} symbol layers shown, {layers.Sum(l => l.Tiles.Count)} label tiles");
        // the cross-tile IDs given and the fade states written, as in a frame (fades off: placed whole)
        new LabelFades { FadeDuration = 0 }.Step(layers, view, projection, width, height, (float)distance, flat: true, eye: null, now: 0);
        Time("Place (first)", () => placement.Place(layers, view, projection, width, height, (float)distance, flat: true), 1);
        Time("Place", () => placement.Place(layers, view, projection, width, height, (float)distance, flat: true), 20);
        Time("UpdateLines", () => placement.UpdateLines(layers, view, projection, width, height, (float)distance, flat: true), 20);
        // which layers cost: placed one at a time, in order
        var costs = new List<(string, double, int, int)>();
        foreach (var layer in layers)
        {
            var one = new List<LabelPlacement.Layer> { layer };
            var c = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) placement.Place(one, view, projection, width, height, (float)distance, flat: true);
            int points = 0, lines = 0;
            foreach (var (t, _) in layer.Tiles) { points += t.LabelRange(layer.Index).Count; lines += t.LineLabelRange(layer.Index).Count; }
            costs.Add((doc.Layers[layer.Index].Id, c.Elapsed.TotalMilliseconds / 5, points, lines));
        }
        foreach (var (id, ms, p, l) in costs.OrderByDescending(c => c.Item2).Take(12))
            output.WriteLine($"  {id,-28} {ms,6:F2} ms  {p,6} point  {l,6} line");
        foreach (var t in tiles) t.Dispose();

        void Time(string what, Action a, int n)
        {
            var c = Stopwatch.StartNew();
            double max = 0;
            for (int i = 0; i < n; i++) { var s = c.Elapsed.TotalMilliseconds; a(); max = Math.Max(max, c.Elapsed.TotalMilliseconds - s); }
            output.WriteLine($"{what}: {c.Elapsed.TotalMilliseconds / n:F2} ms mean, {max:F2} max over {n}");
        }
    }
}
