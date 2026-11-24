using Stride.Core.Mathematics;
using Xunit.Abstractions;

namespace VL.Globe.Tests.Tiles;

// How large a map texel appears on screen for the tile straight below the camera - the size the
// map's text is read at. The user, 2026-11-20: "地图上的文字永远很小，再拉近一点就加载了下一级更小的字".
// A raster map is drawn for 1 texel = 1 pixel; a 2D web map shows each level between 1x and 2x.
public class TileScaleFacts(ITestOutputHelper output)
{
    const double FovY = Math.PI / 4;

    // Pixels per texel at the screen centre for the tile Select chose there, looking straight down.
    static (int zoom, double pxPerTexel) Centre(double heightMetres, double selectorScreenHeight, double realScreenHeight, double sse = 1.41)
    {
        var v = new GlobeView { MinHeight = 1, Longitude = 139.7671, Latitude = 35.6812 };
        v.SetHeight(heightMetres);
        var (eye, _, _) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var vp = v.ViewMatrix() * Matrix.PerspectiveFovRH((float)FovY, 1f, near, far);
        var tiles = new TileSelector { MaxZoom = 22, ScreenSpaceError = sse }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, selectorScreenHeight, 1f);
        int n0 = 1;
        var mx = (v.Longitude + 180) / 360; var my = TileGeometry.MercatorY(v.Latitude);
        var t = tiles.First(k => { n0 = k.TilesPerSide; return k.X == (int)(mx * n0) && k.Y == (int)(my * n0); });
        var texel = 2 * Math.PI * Math.Cos(v.Latitude * Math.PI / 180) / t.TilesPerSide / TileSelector.TilePixels;  // radians
        var h = heightMetres / GlobeCoordinates.EarthRadius;
        return (t.Zoom, texel / h * (realScreenHeight / (2 * Math.Tan(FovY / 2))));
    }

    [Theory]
    [InlineData(50)] [InlineData(100)] [InlineData(141)] [InlineData(200)] [InlineData(300)] [InlineData(500)]
    [InlineData(1000)] [InlineData(5000)] [InlineData(20000)] [InlineData(1e6)]
    public void TheTileBelowIsReadAtOneToTwoTimesItsSize(double h)
    {
        // Measured 2026-11-20 before the fix, at the real 808 px: 0.76-1.18 px per texel, about one
        // level too fine (distance taken to the bounding sphere's near side, not to the ground).
        var (zoom, px) = Centre(h, 808, 808);
        output.WriteLine($"{h} m: z{zoom}, {px:F2} px per texel");
        // Rounded, as MapLibre and Cesium choose imagery levels: 0.71-1.41x, and a little under 0.71
        // below a coarse tile, which is judged by its widest texel row (TileSelector, 2026-11-20).
        Assert.InRange(px, 0.6, 1.42);
    }

    [Fact]
    public void AScreenHeightTooLargeMakesTheTextSmall()
    {
        // The contrast: Globe told 1080 (its height before the first draw) while the window is 808,
        // the text is drawn below its size again.
        var pairs = new[] { 200.0, 500, 1000, 5000, 20000, 100000 }.Select(h => (h, wrong: Centre(h, 1080, 808).pxPerTexel, right: Centre(h, 808, 808).pxPerTexel)).ToList();
        foreach (var (h, wrong, right) in pairs) output.WriteLine($"{h} m: told 1080 -> {wrong:F2}, told 808 -> {right:F2}");
        Assert.All(pairs, p => Assert.True(p.wrong <= p.right + 1e-9));
        Assert.Contains(pairs, p => p.wrong < p.right * 0.6);
    }
}
