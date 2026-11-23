using Stride.Core.Mathematics;
using VL.Globe.Tests.MapLibre;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// A view the user reported, by its log line: down the middle of the screen, which globe tile and which
// vector tile draw each row, and how far off the ground there is. VLGLOBE_PROBE=1 VLGLOBE_VIEW=#z/lat/lng/b/p
public class ViewProbe(ITestOutputHelper output)
{
    [Fact]
    public void RowsOfTheReportedView()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1") return;
        var hash = Environment.GetEnvironmentVariable("VLGLOBE_VIEW") ?? "#13.82/35.69555/139.75982/-9.9/57.3";
        const double FovY = Math.PI / 4, W = 1265, H = 808;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        var (eye, _, _) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = v.ViewMatrix();
        var vp = view * Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);
        const double R = 6378137;
        output.WriteLine($"{hash}: eye {(eye.Length - 1) * R:F0} m up, {tiles.Count} tiles, zoom {tiles.Min(t => t.Zoom)}..{tiles.Max(t => t.Zoom)}");
        for (double py = 0; py < H; py += 25)
        {
            if (v.Pick(W / 2, py) is not { } p) { output.WriteLine($"y {py,4}: sky"); continue; }
            double plon = Math.Atan2(p.X, p.Z) * 180 / Math.PI, plat = Math.Asin(p.Y) * 180 / Math.PI;
            double mx = (plon + 180) / 360, my = TileGeometry.MercatorY(plat);
            var t = tiles.FirstOrDefault(k => k.X == (int)(mx * k.TilesPerSide) && k.Y == (int)(my * k.TilesPerSide));
            var vk = GpuVectorTiles.VectorKey(0, 14, 512, t);
            double km = (new D3(p.X, p.Y, p.Z) - eye).Length * R / 1000;
            output.WriteLine($"y {py,4}: {km,7:F2} km, globe z{t.Zoom}, vector z{vk?.Zoom}");
        }

        // the view drawn offscreen by the GPU path as vvvv draws it, on a grey ground, into a .bmp
        using var service = new VectorTileService(Environment.GetEnvironmentVariable("VLGLOBE_STYLE") ?? "https://tiles.openfreemap.org/styles/liberty");
        service.Start();
        using var gpu = new GpuVectorTiles();
        var (_, forward, up) = v.Pose();
        double height = eye.Length - 1;
        double metresPerPixel = height * 2 * Math.Tan(FovY / 2) / H * R;
        double zoom = GlobeView.ZoomOf(metresPerPixel, v.Latitude) - 1;
        var wanted = tiles.ToHashSet();
        var until = DateTime.UtcNow.AddSeconds(240);
        int frame = 0, still = 0, lastBuilt = -1;
        while (DateTime.UtcNow < until && still < 20)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, new[] { service }, 1f, eye, zoom, ++frame, height); return 0; });
            if (service.Document != null && gpu.Building == 0 && gpu.TilesBuilt == lastBuilt) still++; else still = 0;
            lastBuilt = gpu.TilesBuilt;
            Thread.Sleep(100);
        }
        output.WriteLine($"{gpu.Status} (zoom {zoom:F2})");
        foreach (var g in gpu.Frame!.Items.GroupBy(i => (i.Pass, z: (int)Math.Log2(i.Tiles))).OrderBy(g => g.Key.Pass).ThenBy(g => g.Key.z))
            output.WriteLine($"  {g.Key.Pass,-16} z{g.Key.z}: {g.Count()} items, {g.Sum(i => (long)i.Count)} indices");
        var viewRotation = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), (float)(height * 0.01), 10f);
        foreach (var i in gpu.Frame!.Items.Where(i => i.Pass == VectorRenderer.Pass.ExtrusionColor))
            output.WriteLine($"  extrusion z{Math.Log2(i.Tiles)} clip {i.Clip} {i.Count} indices");
        foreach (bool fog in new[] { false, true })
        {
            var px = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, viewRotation, projection, (int)W, (int)H, fog, tiles,
                Environment.GetEnvironmentVariable("VLGLOBE_MSAA") == "4" ? global::Stride.Graphics.MultisampleCount.X4 : global::Stride.Graphics.MultisampleCount.None);
            var file = Path.Combine(Path.GetTempPath(), $"vlglobe-view{(fog ? "-fog" : "")}.bmp");
            Bmp(file, px, (int)W, (int)H);
            output.WriteLine("wrote " + file);
        }
    }

    static void Bmp(string file, byte[] rgba, int w, int h)
    {
        using var s = new BinaryWriter(File.Create(file));
        int size = 54 + w * h * 4;
        s.Write((byte)'B'); s.Write((byte)'M'); s.Write(size); s.Write(0); s.Write(54);
        s.Write(40); s.Write(w); s.Write(-h); s.Write((short)1); s.Write((short)32); s.Write(0); s.Write(w * h * 4); s.Write(2835); s.Write(2835); s.Write(0); s.Write(0);
        for (int i = 0; i < rgba.Length; i += 4) { s.Write(rgba[i + 2]); s.Write(rgba[i + 1]); s.Write(rgba[i]); s.Write((byte)255); }
    }
}
