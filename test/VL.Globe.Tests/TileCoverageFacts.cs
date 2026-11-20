using Stride.Core.Mathematics;
using VL.Globe.Engine;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// Every place on screen must be drawn by a tile fine enough for where it is, not only the tile
// straight below. The user, 2026-11-20, over Central Asia: the south was zoom 5, the north one stretched
// zoom-2/3 tile, "一部分已经明显加载到了，另外一部分还是没有判定到需要加载"; the log said "25 wanted, zoom 2..4,
// 0 loading" - the selector asked for nothing finer.
public class TileCoverageFacts(ITestOutputHelper output)
{
    const double FovY = Math.PI / 4;
    const double W = 1087, H = 808;

    // For each sampled pixel: the selected tile under it and how many screen pixels one of its texels
    // covers there, measured by projecting the texel's two neighbours.
    List<(double px, double py, TileKey tile, double pixels)> Survey(double lon, double lat, double h)
    {
        var v = new GlobeView { MinHeight = 1, Longitude = lon, Latitude = lat, Width = W, Height_px = H, FovY = FovY };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        v.SetHeight(h);
        var (eye, _, _) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = v.ViewMatrix();
        var vp = view * Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);
        output.WriteLine($"{tiles.Count} tiles, zoom {tiles.Min(t => t.Zoom)}..{tiles.Max(t => t.Zoom)}");

        var result = new List<(double, double, TileKey, double)>();
        for (double py = 20; py < H; py += 60)
            for (double px = 20; px < W; px += 60)
            {
                if (v.Pick(px, py) is not { } p) continue;
                double plon = Math.Atan2(p.X, p.Z) * 180 / Math.PI, plat = Math.Asin(p.Y) * 180 / Math.PI;
                double mx = (plon + 180) / 360, my = TileGeometry.MercatorY(plat);
                var t = tiles.FirstOrDefault(k => k.X == (int)(mx * k.TilesPerSide) && k.Y == (int)(my * k.TilesPerSide));
                if (t.Zoom == 0 && tiles.All(k => k.Zoom != 0)) { result.Add((px, py, default, double.PositiveInfinity)); continue; }
                double step = 1.0 / (t.TilesPerSide * 256.0);   // one texel in Mercator units
                var a = Screen(v, view, mx - step / 2, my); var b = Screen(v, view, mx + step / 2, my);
                var c = Screen(v, view, mx, my - step / 2); var d = Screen(v, view, mx, my + step / 2);
                result.Add((px, py, t, Math.Max((a - b).Length(), (c - d).Length())));
            }
        return result;
    }

    static Vector2 Screen(GlobeView v, Matrix view, double mx, double my)
    {
        var (x, y, z) = TileGeometry.OnSphere(TileGeometry.LongitudeOf(mx), TileGeometry.LatitudeOf(my));
        var (eye, _, _) = v.Pose();
        // camera space in double
        var cam = Vector3.TransformCoordinate(new Vector3((float)(x - eye.X), (float)(y - eye.Y), (float)(z - eye.Z)), GlobeView.ViewRotation(v.Pose().forward, v.Pose().up));
        double f = (v.Height_px / 2) / Math.Tan(v.FovY / 2);
        return new Vector2((float)(cam.X / -cam.Z * f), (float)(cam.Y / -cam.Z * f));
    }

    [Theory]
    [InlineData(60, 42, 3_000_000)]
    [InlineData(60, 42, 5_000_000)]
    [InlineData(60, 42, 1_500_000)]
    [InlineData(139.7, 35.7, 500_000)]
    [InlineData(10, 60, 4_000_000)]
    [InlineData(115, 45, 3_570_000)]   // the user's second view, 2026-11-20: a seam at ~41 N
    [InlineData(-70, -45, 2_000_000)]
    public void EveryPixelIsDrawnFineEnough(double lon, double lat, double h)
    {
        var s = Survey(lon, lat, h);
        var worst = s.OrderByDescending(r => r.pixels).Take(5).ToList();
        foreach (var r in worst) output.WriteLine($"pixel {r.px},{r.py}: tile {r.tile} -> {r.pixels:F2} px per texel");
        foreach (var g in s.GroupBy(r => r.tile.Zoom).OrderBy(g => g.Key))
            output.WriteLine($"zoom {g.Key}: {g.Count()} samples, {g.Min(r => r.pixels):F2}-{g.Max(r => r.pixels):F2} px per texel");
        Assert.All(s, r => Assert.True(r.pixels <= 1.5,   // 1.41 asked; the selector estimates at the tile's nearest point, a sampled pixel can read ~2 % more
             $"pixel {r.px},{r.py} drawn by {r.tile} at {r.pixels:F2} px per texel"));
    }
}
