using Stride.Core.Mathematics;

namespace VL.Globe.Tests.Tiles;

public class TileSelectorFacts
{
    static (Vector3 eye, BoundingFrustum frustum) Camera(double lon, double lat, float distance)
    {
        var (x, y, z) = TileGeometry.OnSphere(lon, lat);
        var eye = new Vector3((float)x, (float)y, (float)z) * distance;
        var up = System.Math.Abs(lat) > 80 ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix.LookAtRH(eye, Vector3.Zero, up);
        var proj = Matrix.PerspectiveFovRH(MathUtil.DegreesToRadians(60), 1f, 0.00001f, 100f); // near below the lowest camera height tested
        var vp = view * proj;
        return (eye, new BoundingFrustum(ref vp));
    }

    static readonly TileSelector Selector = new() { MaxZoom = 18 };
    static List<TileKey> Pick(double lon, double lat, float d)
    {
        var (eye, f) = Camera(lon, lat, d);
        return Selector.Select(eye, f, MathUtil.DegreesToRadians(60), 1000, 1f);
    }

    static TileKey Under(List<TileKey> tiles, double lon, double lat)
    {
        var mx = (lon + 180) / 360; var my = TileGeometry.MercatorY(lat);
        return tiles.Single(t => mx >= (double)t.X / t.TilesPerSide && mx < (t.X + 1.0) / t.TilesPerSide
                              && my >= (double)t.Y / t.TilesPerSide && my < (t.Y + 1.0) / t.TilesPerSide);
    }

    [Fact]
    public void TheFarSideIsNotRequested()
    {
        var tiles = Pick(0, 0, 3);
        Assert.NotEmpty(tiles);
        foreach (var t in tiles.Where(t => t.Zoom >= 2))
        {
            var lon = TileGeometry.LongitudeOf((t.X + 0.5) / t.TilesPerSide);
            Assert.True(System.Math.Abs(lon) < 135, $"{t} at {lon:F0}E is behind the globe");
        }
    }

    [Fact]
    public void CloserMeansSharperUnderTheCamera()
    {
        int last = -1;
        foreach (var d in new[] { 3f, 1.5f, 1.1f, 1.01f, 1.001f })
        {
            var z = Under(Pick(10.6, -1.5, d), 10.6, -1.5).Zoom;
            Assert.True(z > last, $"distance {d}: zoom {z} not above {last}");
            last = z;
        }
        Assert.True(last >= 10, $"1 permille above the surface should be street-ish, got zoom {last}");
    }

    [Fact]
    public void TheTileUnderTheCameraIsTheSharpest()
    {
        var tiles = Pick(139.7, 35.7, 1.02f);
        Assert.Equal(tiles.Max(t => t.Zoom), Under(tiles, 139.7, 35.7).Zoom);
    }

    [Fact]
    public void TheTileCountStaysBounded()
    {
        foreach (var d in new[] { 3f, 1.2f, 1.01f, 1.0005f })
            Assert.InRange(Pick(-74, 40.7, d).Count, 1, 400);
    }

    [Fact]
    public void InsideTheGlobeNothingIsSelected() => Assert.Empty(Selector.Select(new Vector3(0, 0, 0.5f), null, 1, 1000, 1));

    [Fact]
    public void SelectedTilesDoNotOverlap()
    {
        var tiles = Pick(10, 50, 1.05f);
        foreach (var a in tiles) Assert.DoesNotContain(tiles, b => a.IsAncestorOf(b));
    }
}
