using Stride.Core.Mathematics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests;

// The fog towards the horizon (ROADMAP 4f step 2, shaders\GlobeFog.sdsl), drawn offscreen on a D3D11
// device of the test's own from GlobeView's camera and read back: alpha is how much fog a pixel got.
public class FogFacts
{
    const int W = 64, H = 48;

    static byte[] Fogged(double height, double tilt, out GlobeView v)
    {
        v = new GlobeView { MinHeight = 1, Longitude = 135.7588, Latitude = 34.9858, Width = W, Height_px = H, TiltByZoomLimit = false, MaxTilt = 89, Tilt = tilt };
        v.SetHeight(height);
        var (eye, forward, up) = v.Pose();
        var view = GlobeView.ViewRotation(forward, up);
        float altitude = (float)(eye.Length - v.Radius);
        var projection = Matrix.PerspectiveFovRH((float)v.FovY, (float)W / H, altitude * 0.01f, 10f);
        return GpuFixtureRenderer.RenderFog(view, projection, (-eye).ToVector3(), v.Radius, Fog.For(altitude), W, H);
    }

    static int Alpha(byte[] px, int x, int y) => px[(y * W + x) * 4 + 3];

    [Fact]
    public void TheFogGoesByTheEyeItselfNotTheTileSelection()
    {
        // the Globe off: no tile selection, CameraToCenter left at 1e-9 - the fog then covered the whole
        // window in vvvv (2026-11-22). The eye, 1,500 m up, is what it goes by.
        double up = 1 + 1500 / GlobeCoordinates.EarthRadius;
        var frame = new VectorRenderer.Frame { Radius = 1, CameraToCenter = 1e-9f, Eye = new D3(0, 0, up) };
        var fog = VectorRenderer.FogOf(frame)!.Value;
        Assert.Equal(Fog.StartFactor * 1500 / GlobeCoordinates.EarthRadius, fog.Start, 9);
        Assert.Null(VectorRenderer.FogOf(new VectorRenderer.Frame { Radius = 1 }));
    }

    [Theory]
    [InlineData(300)] [InlineData(1.5e7)]
    public void LookingStraightDownNothingIsFogged(double height)
    {
        // street level, and the whole globe from afar with space round it
        var px = Fogged(height, 0, out _);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                Assert.True(Alpha(px, x, y) <= 2, $"{x},{y}: alpha {Alpha(px, x, y)}");
    }

    [Fact]
    public void TiltedAtStreetLevelTheFogThickensTowardsTheHorizon()
    {
        var px = Fogged(300, 60, out _);
        Assert.True(Alpha(px, W / 2, H - 1) <= 2, $"the near ground: alpha {Alpha(px, W / 2, H - 1)}");
        Assert.True(Alpha(px, W / 2, 0) >= 40, $"the far ground: alpha {Alpha(px, W / 2, 0)}");
        for (int y = 1; y < H; y++)
            Assert.True(Alpha(px, W / 2, y) <= Alpha(px, W / 2, y - 1) + 1, $"row {y} foggier than the row above it");
    }

    [Fact]
    public void TheSkyIsFoggedJustAboveTheLimbAndNotHigher()
    {
        // tilted so far the horizon is in view: the ground at it is whole fog, and so is the sky just over
        // it - no hard edge against space - while the sky at the top of the view is clear
        var px = Fogged(300, 85, out _);
        var column = string.Join(" ", Enumerable.Range(0, H).Select(y => Alpha(px, W / 2, y)));
        int limb = -1;
        for (int y = 0; y < H && limb < 0; y++) if (Alpha(px, W / 2, y) >= 250) limb = y;
        Assert.True(limb > 0, "no whole fog in the centre column: " + column);
        Assert.True(Alpha(px, W / 2, 0) <= 2, $"the top of the sky: alpha {Alpha(px, W / 2, 0)}");
        Assert.True(Alpha(px, W / 2, limb - 1) > 0, "a hard edge at the limb: " + column);
    }
}
