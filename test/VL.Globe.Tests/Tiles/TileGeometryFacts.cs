using Stride.Core.Mathematics;

namespace VL.Globe.Tests.Tiles;

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

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(9)]
    public void NoFacetSagsDeeperThanTheBackgroundSphere(int zoom)
    {
        // The midpoint of a facet's diagonal lies inside the sphere by its sag; a background sphere at
        // 0.995 showed through zoom-1 facets that sagged 0.019.
        var t = new TileKey(zoom, (1 << zoom) / 2, (1 << zoom) / 2 - (zoom > 0 ? 1 : 0));
        var (p, _) = TileGeometry.Vertices(t);
        int side = TileGeometry.SegmentsFor(zoom) + 1;
        for (int j = 0; j < side - 1; j++)
            for (int i = 0; i < side - 1; i++)
                Assert.True(((p[j * side + i] + p[(j + 1) * side + i + 1]) / 2).Length() > 0.997f, $"zoom {zoom} cell {i},{j}");   // a cell's diagonal sags 1 - cos(4 deg) = 0.0024
    }

    [Fact]
    public void CoarseLevelsShareOneGrid()
    {
        // a zoom-2 tile's east edge and its zoom-3 neighbour's west edge, vertex for vertex
        var (a, _) = TileGeometry.Vertices(new TileKey(2, 1, 1));
        var (b, _) = TileGeometry.Vertices(new TileKey(3, 4, 2));
        int sa = TileGeometry.SegmentsFor(2) + 1, sb = TileGeometry.SegmentsFor(3) + 1;
        for (int k = 0; k < sb; k++) Assert.Equal(a[k * sa + sa - 1], b[k * sb]);
    }

    [Fact]
    public void TheDateLineCloses()
    {
        var (east, _) = TileGeometry.Vertices(new TileKey(2, 3, 1));
        var (west, _) = TileGeometry.Vertices(new TileKey(2, 0, 1));
        int s = TileGeometry.SegmentsFor(2) + 1;
        for (int k = 0; k < s; k++)
            Assert.True(Vector3.Distance(east[k * s + s - 1], west[k * s]) < 1e-6f);
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
