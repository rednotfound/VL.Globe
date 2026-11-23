
namespace VL.Globe.Tests.Tiles;

// GeoJSON cut to each tile before it is built (TileClip): a vertex holds its place only from -0.5 to 1.5
// tiles, and a far end clamped there turned a line's segment the wrong way.
public class TileClipFacts
{
    [Fact]
    public void ALineAcrossTheTileIsCutAtTheWidenedSquareAndKeepsItsDirection()
    {
        // the unit square widened by the margin: -0.25..1.25
        var line = new (double X, double Y)[] { (-10, -0.5), (10, 1.5) };   // a slope of 0.1, through the middle
        var pieces = TileClip.ClipLine(line, -0.25, -0.25, 1.25, 1.25);
        var piece = Assert.Single(pieces);
        Assert.Equal(-0.25, piece[0].X, 12);
        Assert.Equal(1.25, piece[^1].X, 12);
        double slope = (piece[^1].Y - piece[0].Y) / (piece[^1].X - piece[0].X);
        Assert.Equal(0.1, slope, 12);
    }

    [Fact]
    public void ALineLeavingAndComingBackIsTwoPieces()
    {
        var line = new (double X, double Y)[] { (0.5, 0.5), (5, 0.5), (5, 0.7), (0.5, 0.7) };
        Assert.Equal(2, TileClip.ClipLine(line, -0.25, -0.25, 1.25, 1.25).Count);
    }

    [Fact]
    public void ABigSquareBecomesTheWidenedTile()
    {
        var ring = new (double X, double Y)[] { (-5, -5), (5, -5), (5, 5), (-5, 5), (-5, -5) };
        var cut = TileClip.ClipRing(ring, -0.25, -0.25, 1.25, 1.25)!;
        Assert.Equal(cut[0], cut[^1]);   // still closed
        Assert.All(cut, p => Assert.True(p.X >= -0.25 - 1e-12 && p.X <= 1.25 + 1e-12 && p.Y >= -0.25 - 1e-12 && p.Y <= 1.25 + 1e-12));
        Assert.Equal(5, cut.Length);
    }

    [Fact]
    public void ARingOutsideIsGone() =>
        Assert.Null(TileClip.ClipRing(new (double X, double Y)[] { (3, 3), (4, 3), (4, 4), (3, 3) }, -0.25, -0.25, 1.25, 1.25));
}
