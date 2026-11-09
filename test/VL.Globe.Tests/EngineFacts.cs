using Stride.Core.Mathematics;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

public class TileGeometryFacts
{
    const int N = TileGeometry.VertsPerSide;

    [Fact]
    public void NeighboursShareTheirEdgeExactly()
    {
        var (a, _) = TileGeometry.Vertices(new TileKey(3, 2, 3));
        var (b, _) = TileGeometry.Vertices(new TileKey(3, 3, 3));   // east neighbour
        var (c, _) = TileGeometry.Vertices(new TileKey(3, 2, 4));   // south neighbour
        for (int k = 0; k < N; k++)
        {
            Assert.Equal(a[k * N + (N - 1)], b[k * N]);             // a's east column == b's west column, bit for bit
            Assert.Equal(a[(N - 1) * N + k], c[k]);                 // a's south row == c's north row
        }
    }

    [Fact]
    public void TheDateLineCloses()
    {
        var (east, _) = TileGeometry.Vertices(new TileKey(2, 3, 1));
        var (west, _) = TileGeometry.Vertices(new TileKey(2, 0, 1));
        for (int k = 0; k < N; k++)
            Assert.True(Vector3.Distance(east[k * N + N - 1], west[k * N]) < 1e-6f);
    }

    [Fact]
    public void EveryVertexIsOnTheUnitSphere()
    {
        foreach (var t in new[] { new TileKey(0, 0, 0), new TileKey(5, 17, 9), new TileKey(12, 3640, 1613) })
            foreach (var p in TileGeometry.Vertices(t).positions)
                Assert.InRange(p.Length(), 1 - 1e-6f, 1 + 1e-6f);
    }

    [Fact]
    public void TextureCoordinatesSpanTheTile()
    {
        var (_, uv) = TileGeometry.Vertices(new TileKey(4, 5, 6));
        Assert.Equal(new Vector2(0, 0), uv[0]);
        Assert.Equal(new Vector2(1, 1), uv[^1]);
    }

    [Fact]
    public void WorldTileReachesTheMercatorLimit()
    {
        var (p, _) = TileGeometry.Vertices(new TileKey(0, 0, 0));
        Assert.InRange(System.Math.Asin(p[0].Y) * 180 / System.Math.PI, 85.0, 85.06);
    }

    [Fact]
    public void MercatorRoundTrips()
    {
        foreach (var lat in new[] { -80.0, -35.6, 0.0, 12.3, 84.9 })
            Assert.InRange(TileGeometry.LatitudeOf(TileGeometry.MercatorY(lat)), lat - 1e-9, lat + 1e-9);
    }

    [Fact]
    public void ParentsAndChildren()
    {
        var t = new TileKey(5, 17, 9);
        Assert.Equal(new TileKey(4, 8, 4), t.Parent);
        Assert.Equal(new TileKey(2, 2, 1), t.AncestorAt(2));
        Assert.True(new TileKey(2, 2, 1).IsAncestorOf(t));
        for (int i = 0; i < 4; i++) Assert.Equal(t, t.Child(i).Parent);
    }
}

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

public class DrawSetFacts
{
    static readonly TileKey P = new(1, 0, 0);
    static readonly TileKey[] Kids = Enumerable.Range(0, 4).Select(P.Child).ToArray();

    [Fact]
    public void AMissingChildIsStoodInForByItsParent()
        => Assert.Equal(new[] { P }, DrawSet.Resolve(Kids, t => t == P));

    [Fact]
    public void AllChildrenArrivedReplaceTheParent()
        => Assert.Equal(Kids.OrderBy(k => k.Y).ThenBy(k => k.X), DrawSet.Resolve(Kids, t => t == P || Kids.Contains(t)).OrderBy(k => k.Y).ThenBy(k => k.X));

    [Fact]
    public void ThreeOfFourStillShowTheParentAloneNeverBoth()
    {
        var drawn = DrawSet.Resolve(Kids, t => t == P || t == Kids[0] || t == Kids[1] || t == Kids[2]);
        Assert.Equal(new[] { P }, drawn);   // a parent and its child at one place would z-fight
    }

    [Fact]
    public void NothingArrivedDrawsNothing() => Assert.Empty(DrawSet.Resolve(Kids, _ => false));

    [Fact]
    public void RequestsAreCoarseFirst()
    {
        var wanted = new[] { new TileKey(9, 270, 254), new TileKey(9, 271, 254) };
        var req = DrawSet.Requests(wanted, _ => false);
        Assert.Equal(new TileKey(2, 2, 1), req[0]);
        Assert.Equal(3, req.Count);
        Assert.Empty(DrawSet.Requests(wanted, _ => true));
    }
}
