using Xunit.Abstractions;

namespace VL.Globe.Tests;

// By hand (VLGLOBE_PROBE=1): the shader arithmetic's worst error by zoom, in mm and relative to the offset.
public class TileProjectionProbe
{
    readonly ITestOutputHelper _out;
    public TileProjectionProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Worst_error_by_zoom()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1") return;
        foreach (var (lon, lat) in new[] { (139.767, 35.681), (25.0, 78.0), (179.9, -60.0) })
            for (int z = 0; z <= 18; z += 2)
            {
                int n = 1 << z;
                var key = new TileKey(z, Math.Clamp((int)((lon + 180) / 360 * n), 0, n - 1), Math.Clamp((int)(TileGeometry.MercatorY(lat) * n), 0, n - 1));
                var g = TileProjection.Of(key);
                double worst = 0, rel = 0, size = 0;
                foreach (var u in new[] { -0.5, 0, 0.5, 1, 1.49 })
                    foreach (var v in new[] { -0.5, 0, 0.5, 1, 1.49 })
                    {
                        var e = TileProjection.RelativeExact(key, u, v, 0, false);
                        var f = TileProjection.RelativeFloat(g, (float)u, (float)v, 0, false);
                        double err = (new D3(f.X, f.Y, f.Z) - e).Length;
                        worst = Math.Max(worst, err);
                        if (e.Length > 0) rel = Math.Max(rel, err / e.Length);
                        size = Math.Max(size, e.Length);
                    }
                _out.WriteLine($"lat {lat,6} z{z,2}: worst {worst * 6378137e3,10:F3} mm, {rel:E1} of the offset (offset up to {size * 6378137 / 1000:F1} km)");
            }
    }
}
