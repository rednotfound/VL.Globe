namespace VL.Globe.Tests.Tiles;

// The raster layers' mip chain (TileTextures, straightAlpha): straight alpha, no dark halo.
public class StraightAlphaMipFacts
{
    [Fact]
    public void AStraightAlphaMipKeepsTheLineColourNotBlack()
    {
        // one opaque white pixel among three transparent black ones: the coarser level is white at a
        // quarter alpha, not a grey that would ring every line with a dark halo
        var p = new byte[] { 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var one = MipChain.Build(p, 2, 2, srgbColour: true, straightAlpha: true)[1].pixels;
        Assert.Equal(255, one[0]);
        Assert.InRange(one[3], 63, 64);
        var plain = MipChain.Build(p, 2, 2, srgbColour: true, straightAlpha: false)[1].pixels;
        Assert.True(plain[0] < 200);    // the halo this avoids
    }
}
