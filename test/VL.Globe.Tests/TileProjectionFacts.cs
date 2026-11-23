using Stride.Core.Mathematics;

namespace VL.Globe.Tests;

// ROADMAP 4g step 2: vector vertices keep their place in the tile and GlobeVector.sdsl projects them.
// TileProjection.RelativeFloat is the shader's arithmetic in floats; the double reference is what the
// builder computed per vertex before.
public class TileProjectionFacts
{
    const double EarthMetres = 6378137.0;

    static IEnumerable<(TileKey key, double u, double v)> Samples()
    {
        // Tokyo, Kyoto, the equator, the far north and south, the antimeridian; every zoom from 0
        foreach (var (lon, lat) in new[] { (139.767, 35.681), (135.759, 34.985), (0.1, 0.1), (-70.0, -45.0), (25.0, 78.0), (179.9, -60.0) })
            for (int z = 0; z <= 18; z++)
            {
                int n = 1 << z;
                int x = Math.Clamp((int)((lon + 180) / 360 * n), 0, n - 1);
                int y = Math.Clamp((int)(TileGeometry.MercatorY(lat) * n), 0, n - 1);
                // across the square and its buffer (-0.5 to just under 1.5 is what a vertex can hold)
                foreach (var u in new[] { -0.5, -0.1, 0, 0.25, 0.5, 0.77, 1, 1.2, 1.49 })
                    foreach (var v in new[] { -0.5, 0, 0.33, 0.5, 1, 1.49 })
                        yield return (new TileKey(z, x, y), u, v);
            }
    }

    [Fact]
    public void The_shader_s_arithmetic_puts_a_street_level_vertex_within_half_a_millimetre()
    {
        double worstStreet = 0, worstRelative = 0;
        foreach (var (key, u, v) in Samples())
        {
            var g = TileProjection.Of(key);
            var exact = TileProjection.RelativeExact(key, u, v, 0, flat: false);
            var f = TileProjection.RelativeFloat(g, (float)u, (float)v, 0, flat: false);
            double error = (new D3(f.X, f.Y, f.Z) - exact).Length;
            // to the size of what is drawn: the offset itself, or the tile for a vertex at its centre
            double size = Math.Max(exact.Length, 2 * Math.PI / key.TilesPerSide);
            worstRelative = Math.Max(worstRelative, error / size);
            if (key.Zoom >= 14) worstStreet = Math.Max(worstStreet, error * EarthMetres);
        }
        // measured 2026-11-23: 0.22 mm at z14 (TileProjectionProbe); z12's 1.3 mm is the same 1e-7 of an 11-km offset
        Assert.True(worstStreet < 0.0005, $"{worstStreet * 1000:F3} mm at z14-z18");
        // float's own rounding, a few times over, at every zoom
        Assert.True(worstRelative < 1e-6, $"{worstRelative:E2} of the offset");
    }

    [Fact]
    public void A_raised_vertex_stands_its_height_above_the_ground()
    {
        foreach (var key in new[] { new TileKey(14, 14552, 6451), new TileKey(16, 58211, 25806) })
        {
            var g = TileProjection.Of(key);
            foreach (var h in new[] { 3.0, 30.0, 250.0, 634.0 })
            {
                var exact = TileProjection.RelativeExact(key, 0.3, 0.8, h, flat: false);
                var f = TileProjection.RelativeFloat(g, 0.3f, 0.8f, (float)h, flat: false);
                Assert.True((new D3(f.X, f.Y, f.Z) - exact).Length * EarthMetres < 0.001, $"{h} m at {key}");
            }
        }
    }

    [Fact]
    public void On_the_plane_the_place_is_the_offset()
    {
        var key = new TileKey(3, 5, 2);
        var g = TileProjection.Of(key);
        foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 1.0), (0.3, 0.9), (-0.4, 1.4) })
        {
            var exact = TileProjection.RelativeExact(key, u, v, 120, flat: true);
            var f = TileProjection.RelativeFloat(g, (float)u, (float)v, 120, flat: true);
            Assert.True((new D3(f.X, f.Y, f.Z) - exact).Length < 1e-8, $"({u}, {v})");   // 0.3 itself rounded to float
        }
    }

    [Fact]
    public void A_point_minus_the_centre_in_floats_would_not_do()
    {
        // why the difference is formed from small quantities: the sphere point in floats, minus the
        // centre in floats, at Tokyo z16 - the same vertex the shader's arithmetic puts within 1 mm
        var key = new TileKey(16, 58211, 25806);
        var exact = TileProjection.RelativeExact(key, 0.9, 0.1, 0, flat: false);
        var c = TileGeometry.Centre(key);
        var p = c + exact;
        var naive = new Vector3((float)p.X, (float)p.Y, (float)p.Z) - new Vector3((float)c.X, (float)c.Y, (float)c.Z);
        double naiveError = (new D3(naive.X, naive.Y, naive.Z) - exact).Length * EarthMetres;
        Assert.True(naiveError > 0.05, $"{naiveError:F3} m");
    }

    [Fact]
    public void MVT_s_grid_and_the_tile_s_edges_are_held_exactly()
    {
        // 4096 to a tile is 8 of our 32768 units: no MVT coordinate is moved by the packing, from -0.5
        // up to (not including) 1.5
        for (int k = -2047; k < 4096 + 2048; k += 37)
        {
            var vertex = new VectorVertex();
            vertex.SetUv(k / 4096.0, (4096 - k) / 4096.0);
            Assert.Equal(k / 4096.0, vertex.Uv.X, 9);
            Assert.Equal((4096 - k) / 4096.0, vertex.Uv.Y, 9);
        }
        var edge = new VectorVertex();
        edge.SetUv(0, 1);
        Assert.Equal(new Vector2(0, 1), edge.Uv);
    }
}
