using Stride.Core.Mathematics;
using VL.Globe.Tests.MapLibre;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// The round holes at both poles (the user, 2026-11-23): Web Mercator stops at 85.05°. As MapLibre GL JS's
// globe, the top and bottom rows of tiles reach the pole, the texture's edge row stretched over the cap.
public class PoleCapFacts(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 5, 0)]
    [InlineData(3, 2, 7)]
    [InlineData(12, 100, 0)]
    [InlineData(12, 100, 4095)]
    public void AnEdgeRowTileReachesItsPoleInShortSteps(int z, int x, int y)
    {
        var tile = new TileKey(z, x, y);
        var (centre, offsets, normals, uv) = TileGeometry.RelativeVertices(tile);
        var indices = TileGeometry.IndicesFor(tile);
        Assert.All(indices, i => Assert.InRange(i, 0, offsets.Length - 1));
        var points = offsets.Select(o => new D3(centre.X + o.X, centre.Y + o.Y, centre.Z + o.Z)).ToArray();
        Assert.All(points, p => Assert.InRange(p.Length, 1 - 1e-6, 1 + 1e-6));
        double top = points.Max(p => p.Y), bottom = points.Min(p => p.Y);
        if (y == 0) Assert.Equal(1, top, 6); else Assert.True(top < 0.997);
        if (y == (1 << z) - 1) Assert.Equal(-1, bottom, 6); else Assert.True(bottom > -0.997);
        // the cap's edges no longer than the grid's own: its rows a quarter degree apart
        double grid = TileGeometry.SegmentsFor(z) is var seg ? 2 * Math.PI / ((1 << z) * seg) : 0;
        for (int i = 0; i < indices.Length; i += 3)
            foreach (var (a, b) in new[] { (indices[i], indices[i + 1]), (indices[i + 1], indices[i + 2]), (indices[i + 2], indices[i]) })
            {
                double angle = 2 * Math.Asin(Math.Min(1, (points[a] - points[b]).Length / 2));
                Assert.True(angle <= Math.Max(grid * 1.5, 0.26 * Math.PI / 180), $"edge {angle * 180 / Math.PI:F3}° in {tile}");
            }
        var capLats = points.Select(p => Math.Asin(Math.Clamp(p.Y, -1, 1)) * 180 / Math.PI).Where(l => Math.Abs(l) > TileGeometry.MaxLatitude + 1e-6).Distinct().Order().ToList();
        for (int i = 1; i < capLats.Count; i++) if (Math.Sign(capLats[i]) == Math.Sign(capLats[i - 1])) Assert.True(capLats[i] - capLats[i - 1] < 0.26);
        // the cap's texture coordinates are the image's edge row
        foreach (var (p, t) in points.Zip(uv))
            if (Math.Abs(p.Y) > Math.Sin(TileGeometry.MaxLatitude * Math.PI / 180) + 1e-9) Assert.Equal(p.Y > 0 ? 0f : 1f, t.Y);
        var (bc, br) = TileGeometry.Bounds(tile);
        Assert.All(points, p => Assert.True((p.ToVector3() - bc).Length() <= br * 1.0001));
    }

    [Theory]
    [InlineData(90, 2_000_000)]
    [InlineData(-90, 2_000_000)]
    [InlineData(88, 200_000)]
    [InlineData(-87, 50_000)]
    public void OverAPoleEveryPixelOfTheGlobeHasATile(double lat, double metres)
    {
        const double FovY = Math.PI / 4, W = 600, H = 400;
        var v = new GlobeView { MinHeight = 1, Longitude = 20, Latitude = lat, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        v.SetHeight(metres);
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var vp = v.ViewMatrix() * Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);
        int ground = 0, missed = 0;
        for (double py = 5; py < H; py += 10)
            for (double px = 5; px < W; px += 10)
            {
                if (v.Pick(px, py) is not { } p) continue;
                ground++;
                double plon = Math.Atan2(p.X, p.Z) * 180 / Math.PI, plat = Math.Asin(p.Y) * 180 / Math.PI;
                double mx = (plon + 180) / 360, my = TileGeometry.MercatorY(Math.Clamp(plat, -TileGeometry.MaxLatitude, TileGeometry.MaxLatitude));
                bool covered = tiles.Any(k => k.X == Math.Min(k.TilesPerSide - 1, (int)(mx * k.TilesPerSide))
                    && (Math.Abs(plat) <= TileGeometry.MaxLatitude ? k.Y == Math.Min(k.TilesPerSide - 1, (int)(my * k.TilesPerSide)) : plat > 0 ? TileGeometry.NorthCap(k) : TileGeometry.SouthCap(k)));
                if (!covered) missed++;
            }
        output.WriteLine($"lat {lat}, {metres} m: {tiles.Count} tiles, {ground} ground samples, {missed} without a tile");
        Assert.True(ground > 100);
        Assert.Equal(0, missed);
    }

    [Theory]
    [InlineData(90, 3_000_000)]
    [InlineData(89, 300_000)]
    [InlineData(89.9, 3_000_000)]
    [InlineData(89.999, 3_000_000)]
    [InlineData(-90, 1_000_000)]
    [InlineData(80, 3_000_000)]
    public void AStylesBackgroundCoversThePole(double lat, double metres)
    {
        // the vector background reaches the pole too, drawn over the globe tiles' depth
        var tiles0 = System.Text.Json.JsonSerializer.Serialize(Path.Combine(OverscaledGroundFacts.Tiles(), "{z}-{x}-{y}.mvt"));
        var style = System.Text.Encoding.UTF8.GetBytes($$"""
            { "version": 8, "sources": { "world": { "type": "vector", "maxzoom": 0, "tiles": [{{tiles0}}] } },
              "layers": [ { "id": "bg", "type": "background", "paint": { "background-color": "#00ff00" } },
                          { "id": "none", "type": "fill", "source": "world", "source-layer": "nothing" } ] }
            """);
        const double FovY = Math.PI / 4, W = 400, H = 300;
        var v = new GlobeView { MinHeight = 1, Longitude = 10, Latitude = lat, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        v.SetHeight(metres);
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        output.WriteLine($"lat {lat}: near {near} far {far} eye {eye}, forward {forward}, up {up}");
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);
        output.WriteLine(string.Join(" ", tiles));
        using var service = new VectorTileService("pole", style);
        service.Start();
        using var gpu = new GpuVectorTiles();
        double height = eye.Length - 1;
        double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / H * 6378137, 85) - 1;
        var until = DateTime.UtcNow.AddSeconds(30);
        int frame = 0, still = 0, lastBuilt = -1;
        while (DateTime.UtcNow < until && still < 10)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, tiles.ToHashSet(), new[] { service }, 1f, eye, zoom, ++frame, height); return 0; });
            if (service.Document != null && gpu.Building == 0 && gpu.TilesBuilt == lastBuilt) still++; else still = 0;
            lastBuilt = gpu.TilesBuilt;
            Thread.Sleep(30);
        }
        output.WriteLine(gpu.Status + "; items " + gpu.Frame!.Items.Count);
        var alone = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false);
        output.WriteLine("alone green: " + Enumerable.Range(0, (int)(W * H)).Count(i => alone[i * 4 + 1] > 200 && alone[i * 4] < 50));
        var px = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false, tiles);
        if (Environment.GetEnvironmentVariable("VLGLOBE_POLE_BMP") == "1") { Bmp(Path.Combine(Path.GetTempPath(), "pole-alone.bmp"), alone, (int)W, (int)H); Bmp(Path.Combine(Path.GetTempPath(), "pole-depth.bmp"), px, (int)W, (int)H); }
        int ground = 0, green = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (v.Pick(x + 0.5, y + 0.5) is not { } p || Math.Abs(p.Y) < 0.9) continue;   // well inside, away from the limb
                ground++;
                int i = (y * (int)W + x) * 4;
                if (px[i + 1] > 200 && px[i] < 50) green++;
            }
        // and multisampled, as vvvv's window draws: a clip's discard on the cap's seams dropped pixels there
        var msaa = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false, tiles, global::Stride.Graphics.MultisampleCount.X4);
        if (Environment.GetEnvironmentVariable("VLGLOBE_POLE_BMP") == "1") Bmp(Path.Combine(Path.GetTempPath(), $"pole-msaa-{lat}.bmp"), msaa, (int)W, (int)H);
        int greenMsaa = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (v.Pick(x + 0.5, y + 0.5) is not { } p || Math.Abs(p.Y) < 0.9) continue;
                int i = (y * (int)W + x) * 4;
                if (msaa[i + 1] > 250 && msaa[i] < 5) greenMsaa++;
            }
        output.WriteLine($"{tiles.Count} tiles; {green} of {ground} ground pixels north of 64° green, {greenMsaa} multisampled");
        Assert.True(ground > 1000);
        Assert.Equal(ground, green);
        Assert.Equal(ground, greenMsaa);
    }

    static void Bmp(string file, byte[] rgba, int w, int h)
    {
        using var s = new BinaryWriter(File.Create(file));
        s.Write((byte)'B'); s.Write((byte)'M'); s.Write(54 + w * h * 4); s.Write(0); s.Write(54);
        s.Write(40); s.Write(w); s.Write(-h); s.Write((short)1); s.Write((short)32); s.Write(0); s.Write(w * h * 4); s.Write(2835); s.Write(2835); s.Write(0); s.Write(0);
        for (int i = 0; i < rgba.Length; i += 4) { s.Write(rgba[i + 2]); s.Write(rgba[i + 1]); s.Write(rgba[i]); s.Write((byte)255); }
    }
}
