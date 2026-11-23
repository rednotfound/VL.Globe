
namespace VL.Globe.Tests.Tiles;

public class MipChainFacts
{
    static byte[] Fill(int w, int h, System.Func<int, int, (byte r, byte g, byte b, byte a)> f)
    {
        var p = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var (r, g, b, a) = f(x, y); int i = (y * w + x) * 4; p[i] = r; p[i + 1] = g; p[i + 2] = b; p[i + 3] = a; }
        return p;
    }

    [Fact]
    public void A256TileHasNineLevelsDownToOnePixel()
    {
        var levels = MipChain.Build(new byte[256 * 256 * 4], 256, 256);
        Assert.Equal(9, levels.Count);
        Assert.Equal(MipChain.LevelCount(256, 256), levels.Count);
        Assert.Equal((1, 1), (levels[^1].width, levels[^1].height));
    }

    [Fact]
    public void BlackAndWhiteAverageInLinearLight()
    {
        // A checkerboard of black and white is, from afar, half the LIGHT: sRGB 188, not 128. Averaging
        // the bytes gives 128 - too dark, and thin dark map lines and text grow bolder with every level.
        var p = Fill(2, 2, (x, y) => (x + y) % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var one = MipChain.Build(p, 2, 2)[1].pixels;
        Assert.InRange(one[0], 186, 190);
        Assert.Equal(255, one[3]);                      // alpha is linear: averaged as numbers
    }

    [Fact]
    public void AFlatColourStaysThatColour()
    {
        var levels = MipChain.Build(Fill(8, 8, (_, _) => (242, 239, 233, 255)), 8, 8);   // OSM's land
        foreach (var (px, _, _) in levels) Assert.Equal(new byte[] { 242, 239, 233, 255 }, px[..4]);
    }

    [Fact]
    public void OddSizesStillReachOnePixel()
    {
        var levels = MipChain.Build(new byte[5 * 3 * 4], 5, 3);
        Assert.Equal((1, 1), (levels[^1].width, levels[^1].height));
        Assert.Equal(MipChain.LevelCount(5, 3), levels.Count);
    }
}
