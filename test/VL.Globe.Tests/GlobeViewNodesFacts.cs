using Stride.Core.Mathematics;

namespace VL.Globe.Tests;

// ROADMAP 0.1 item 2: ScreenToGlobe, GlobeToScreen, GlobeViewInfo - Mapsui's ScreenToWorld,
// WorldToScreen and ViewportInfo for the globe.
public class GlobeViewNodesFacts
{
    static GlobeView View(double h, double tilt = 0, double heading = 0)
    {
        var v = new GlobeView { MinHeight = 1, Longitude = 139.77, Latitude = 35.68, Heading = heading, Tilt = tilt, Height_px = 808 };
        v.SetHeight(h);
        return v;
    }

    [Theory]
    [InlineData(1e7, 0)] [InlineData(500, 0)] [InlineData(500, 50)]
    public void TheCentreOfTheScreenIsTheCentreOfTheView(double h, double tilt)
    {
        GlobeViewNodes.ScreenToGlobe(View(h, tilt, heading: 30), Vector2.Zero, out var lon, out var lat, out var ok);
        Assert.True(ok);
        Assert.Equal(139.77, lon, 6);
        Assert.Equal(35.68, lat, 6);
    }

    [Fact]
    public void LookingPastTheGlobeIsNoSuccess()
    {
        // from 20,000 km the globe fills the middle; a corner of the window is space
        GlobeViewNodes.ScreenToGlobe(View(2e7), new Vector2(0.98f, 0.98f), out _, out _, out var ok);
        Assert.False(ok);
    }

    [Theory]
    [InlineData(1e6, 0, 0.3f, -0.2f)] [InlineData(800, 0, -0.5f, 0.4f)] [InlineData(800, 55, 0.2f, 0.1f)] [InlineData(30, 40, -0.3f, -0.3f)]
    public void ScreenToGlobeAndBackIsTheSamePosition(double h, double tilt, float x, float y)
    {
        var v = View(h, tilt, heading: 20);
        GlobeViewNodes.ScreenToGlobe(v, new Vector2(x, y), out var lon, out var lat, out var ok);
        Assert.True(ok);
        GlobeViewNodes.GlobeToScreen(v, out var back, out var visible, lon, lat, 0);
        Assert.True(visible);
        Assert.True((back - new Vector2(x, y)).Length() < 1e-4f, $"{back} vs ({x}, {y})");
    }

    [Fact]
    public void TheFarSideIsNotVisible()
    {
        // Tokyo seen from 20,000 km: Sao Paulo is on the other side
        GlobeViewNodes.GlobeToScreen(View(2e7), out _, out var visible, -46.63, -23.55, 0);
        Assert.False(visible);
        GlobeViewNodes.GlobeToScreen(View(2e7), out _, out var near, 135.5, 34.7, 0);   // Osaka
        Assert.True(near);
    }

    [Fact]
    public void ZoomZeroAtTheEquatorIsOsmsResolution()
    {
        Assert.Equal(156543.034, GlobeView.MetresPerPixelAtZoom0, 3);
        Assert.Equal(0, GlobeView.ZoomOf(156543.034, 0), 6);
        Assert.Equal(10, GlobeView.ZoomOf(156543.034 / 1024, 0), 6);
        Assert.Equal(1, GlobeView.ZoomOf(156543.034 / 2 * 0.5, 60), 6);   // cos 60 = 0.5: the same zoom is finer ground
    }

    [Fact]
    public void ViewInfoReadsTheView()
    {
        var v = View(1000, tilt: 30, heading: 45);
        GlobeViewNodes.GlobeViewInfo(v, out var lon, out var lat, out var h, out var heading, out var tilt, out var res, out var zoom);
        Assert.Equal(139.77, lon, 9);
        Assert.Equal(35.68, lat, 9);
        Assert.InRange(h, 860, 870);          // 1000 m along a 30 degree line of sight: ~866 m up
        Assert.Equal(45, heading, 9);
        Assert.Equal(30, tilt, 9);
        // 1000 m away with a 45 degree field over 808 px: 2 x 1000 x tan(22.5) / 808 = 1.025 m per pixel
        Assert.Equal(1.025, res, 3);
        Assert.InRange(zoom, 16.9, 17.1);     // z17 at 35.7 N: 156543 x cos / 2^17 = 0.97 m per pixel
    }
}
