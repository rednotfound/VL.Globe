namespace VL.Globe.Tests.MapLibre;

// pixelmatch 8.0.0's own test/test.js, the cases that do not need the ignore mask or the window,
// with the expected counts copied from there. The images are vendored from its test/fixtures.
public class PixelmatchFacts
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "MapLibre", "pixelmatch-fixtures", name + ".png");

    static int Diff(string a, string b, Pixelmatch.Options options)
    {
        var (img1, w, h) = Png.Read(Fixture(a));
        var (img2, w2, h2) = Png.Read(Fixture(b));
        Assert.Equal((w, h), (w2, h2));
        return Pixelmatch.Count(img1, img2, new byte[img1.Length], w, h, options);
    }

    [Theory]
    [InlineData("1a", "1b", 0.05, 152)]
    [InlineData("1a", "1b", 0.1, 121)]     // the default threshold
    [InlineData("3a", "3b", 0.05, 220)]
    [InlineData("8a", "5b", 0.05, 32896)]
    public void CountsAsPixelmatchDoes(string a, string b, double threshold, int expected)
        => Assert.Equal(expected, Diff(a, b, new(Threshold: threshold)));

    [Fact]
    public void Test7CountsAsPixelmatchDoes() => Assert.Equal(2440, Diff("7a", "7b", new()));

    [Fact]
    public void IdenticalImagesDifferNowhere() => Assert.Equal(0, Diff("1a", "1a", new(Threshold: 0)));

    static int Pair(byte[] p1, byte[] p2, Pixelmatch.Options? o = null) => Pixelmatch.Count(p1, p2, null, 1, 1, o);
    static byte[] Px(int r, int g, int b, int a = 255) => new[] { (byte)r, (byte)g, (byte)b, (byte)a };

    [Fact]
    public void NearBlackUsesTheToeCorrection()
    {
        Assert.Equal(0, Pair(Px(0, 0, 0), Px(13, 13, 13)));
        Assert.Equal(1, Pair(Px(0, 0, 0), Px(23, 23, 23)));
        Assert.Equal(0, Pair(Px(0, 0, 0), Px(0, 1, 0), new(Threshold: 0.05)));
        Assert.Equal(0, Pair(Px(0, 0, 0), Px(1, 0, 0), new(Threshold: 0.05)));
        Assert.Equal(0, Pair(Px(0, 0, 0), Px(0, 0, 1), new(Threshold: 0.05)));
        Assert.Equal(1, Pair(Px(0, 0, 0), Px(32, 20, 20), new(Threshold: 0.05)));
    }

    [Fact]
    public void OklabSeparatesIssue127Pairs()
    {
        Assert.Equal(1, Pair(Px(41, 56, 157), Px(41, 56, 0)));
        Assert.Equal(1, Pair(Px(39, 44, 92), Px(39, 44, 14)));
        Assert.Equal(0, Pair(Px(0, 254, 252), Px(94, 254, 252)));
        Assert.Equal(0, Pair(Px(0, 254, 252), Px(47, 254, 252)));
    }

    [Fact]
    public void TransparencyBlendsOnTheCheckerboardOrWhite()
    {
        Assert.Equal(0, Pair(Px(0, 0, 0, 128), Px(127, 127, 127), new(Checkerboard: false)));
        Assert.Equal(1, Pair(Px(0, 0, 0, 128), Px(127, 127, 127)));
    }
}
