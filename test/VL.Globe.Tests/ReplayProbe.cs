using VL.Globe.Tests.MapLibre;
using Stride.Core.Mathematics;
using VL.Globe.Engine;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// By hand (VLGLOBE_PROBE=1, VLGLOBE_VIEWS_FILE=<a file of "Globe view: #..." hashes, one a line>): a
// session's views replayed in order on one style and one GPU tile engine, as vvvv went through them, and
// what the lenient style skipped or dropped on the way. 2026-11-23: the user's city names went away "from
// some time on" - a layer that throws while one tile is built is dropped for every tile after.
public class ReplayProbe(ITestOutputHelper output)
{
    [Fact]
    public void A_session_s_views_in_order()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1") return;
        var file = Environment.GetEnvironmentVariable("VLGLOBE_VIEWS_FILE");
        if (file is null || !File.Exists(file)) return;
        var hashes = File.ReadAllLines(file).Where(l => l.StartsWith('#')).ToList();
        const double FovY = Math.PI / 4, W = 1265, H = 808, R = 6378137;
        using var service = new VectorTileService(Environment.GetEnvironmentVariable("VLGLOBE_STYLE") ?? "https://tiles.openfreemap.org/styles/liberty");
        service.Start();
        using var gpu = new GpuVectorTiles();
        int frame = 0, skippedBefore = 0;
        (GlobeView v, D3 eye, double height)? last = null;
        var until = DateTime.UtcNow.AddMinutes(15);
        foreach (var hash in hashes)
        {
            var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
            v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
            if (!v.TryApplyHash(hash)) continue;
            var (eye, _, _) = v.Pose();
            var (near, far) = v.ClipPlanes();
            var vp = v.ViewMatrix() * Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
            var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);
            double height = eye.Length - 1;
            double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / H * R, v.Latitude) - 1;
            var wanted = tiles.ToHashSet();
            int still = 0, lastBuilt = -1;
            // VLGLOBE_REPLAY_FRAMES=n: n updates a view, as a hand moving on (the last view settles)
            int quick = int.TryParse(Environment.GetEnvironmentVariable("VLGLOBE_REPLAY_FRAMES"), out var q) ? q : 0;
            bool lastView = hash == hashes[^1];
            int updates = 0;
            while (DateTime.UtcNow < until && still < 8 && (quick == 0 || lastView || updates++ < quick))
            {
                GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, new[] { service }, 1f, eye, zoom, ++frame, height); return 0; });
                if (service.Document != null && gpu.Building == 0 && gpu.TilesBuilt == lastBuilt) still++; else still = 0;
                lastBuilt = gpu.TilesBuilt;
                Thread.Sleep(50);
            }
            var skipped = service.Document?.Skipped.ToList() ?? new List<string>();
            var labels = gpu.Frame?.Labels.Sum(l => l.Tiles.Sum(t => t.tile.Labels.Count)) ?? 0;
            output.WriteLine($"{hash}: {gpu.TilesBuilt} built, {labels} point labels in the frame{(skipped.Count > skippedBefore ? ", NEW: " + string.Join(" | ", skipped.Skip(skippedBefore)) : "")}");
            skippedBefore = skipped.Count;
            last = (v, eye, height);
        }
        // the last view drawn, as the session left it (2026-11-23: the globe's background showed through
        // a style's background after zooming in, where one view alone drew it whole)
        if (last is var (lv, le, lh) && gpu.Frame is { } f)
        {
            var (_, forward, up) = lv.Pose();
            var projection = Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), (float)(lh * 0.01), 10f);
            var px = GpuFixtureRenderer.RenderGlobe(f, GlobeView.ViewRotation(forward, up), projection, (int)W, (int)H, false);
            var bmp = Path.Combine(Path.GetTempPath(), "vlglobe-replay.bmp");
            using var w = new BinaryWriter(File.Create(bmp));
            int size = 54 + (int)W * (int)H * 4;
            w.Write((byte)'B'); w.Write((byte)'M'); w.Write(size); w.Write(0); w.Write(54);
            w.Write(40); w.Write((int)W); w.Write(-(int)H); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write((int)W * (int)H * 4); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
            for (int i = 0; i < px.Length; i += 4) { w.Write(px[i + 2]); w.Write(px[i + 1]); w.Write(px[i]); w.Write((byte)255); }
            output.WriteLine("wrote " + bmp);
        }
    }
}
