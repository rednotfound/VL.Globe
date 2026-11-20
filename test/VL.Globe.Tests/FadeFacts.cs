using Stride.Core.Mathematics;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

public class FadeFacts
{
    [Fact]
    public void AChildSamplesItsQuarterOfTheParent()
    {
        // 5/21/11 is the south-east child of 4/10/5: x odd, y odd -> the lower-right quarter
        var (scale, offset) = Fade.AncestorTransform(new TileKey(5, 21, 11), new TileKey(4, 10, 5));
        Assert.Equal(new Vector2(0.5f), scale);
        Assert.Equal(new Vector2(0.5f, 0.5f), offset);
    }

    [Fact]
    public void AGrandchildSamplesItsSixteenth()
    {
        // 6/42/23 under 4/10/5: 42 - 40 = 2, 23 - 20 = 3 -> columns 2, row 3 of 4
        var (scale, offset) = Fade.AncestorTransform(new TileKey(6, 42, 23), new TileKey(4, 10, 5));
        Assert.Equal(new Vector2(0.25f), scale);
        Assert.Equal(new Vector2(0.5f, 0.75f), offset);
    }

    [Fact]
    public void TheChildsCornersLandOnTheParentsInnerPoints()
    {
        // uv (0,0) and (1,1) of the child map to its sub-square of the parent, as the shader samples
        // uv * scale + offset (ComputeColorTextureScaledOffsetDynamicSampler.sdsl)
        var (s, o) = Fade.AncestorTransform(new TileKey(3, 4, 6), new TileKey(2, 2, 3));
        Assert.Equal(new Vector2(0, 0), Vector2.Zero * s + o);
        Assert.Equal(new Vector2(0.5f, 0.5f), Vector2.One * s + o);
    }

    [Fact]
    public void ProgressRunsZeroToOneOverTheDuration()
    {
        Assert.Equal(0f, Fade.Progress(10, 10, 0.3));
        Assert.Equal(0.5f, Fade.Progress(10, 10.15, 0.3), 5);
        Assert.Equal(1f, Fade.Progress(10, 11, 0.3));
        Assert.Equal(1f, Fade.Progress(10, 10, 0));      // 0: no fade
    }

    [Fact]
    public void NotAnAncestorIsRefused()
        => Assert.Throws<ArgumentException>(() => Fade.AncestorTransform(new TileKey(2, 1, 1), new TileKey(3, 2, 2)));
}
