using Xunit.Abstractions;

namespace VL.Globe.Tests.Camera;

// The user, 2026-11-23: "视野在南北极附近拖拽的时候还是会抖动，甚至会闪动到北极去". Their log: tilted 30°,
// heading -8, ~10,000 km up near 83°N; then 89.90 (the limit) with longitude -1505, then 76.7.
public class PoleDragFacts(ITestOutputHelper output)
{
    static D3 Centre(GlobeView v)
    {
        double lon = v.Longitude * System.Math.PI / 180, lat = v.Latitude * System.Math.PI / 180;
        return new D3(System.Math.Cos(lat) * System.Math.Sin(lon), System.Math.Sin(lat), System.Math.Cos(lat) * System.Math.Cos(lon));
    }

    [Theory]
    [InlineData("#-0.15/82.93352/148.63938/-8.1/30.0", 0, -1)]    // pull down: the pole comes towards the eye
    [InlineData("#-0.15/82.93352/148.63938/-8.1/30.0", 0, 1)]
    [InlineData("#-0.15/82.93352/148.63938/-8.1/30.0", 1, 0)]
    [InlineData("#-0.15/82.93352/148.63938/-8.1/30.0", 1, 1)]
    [InlineData("#-0.15/82.93352/148.63938/-8.1/30.0", -1, -1)]
    [InlineData("#-1.79/-88.86357/-121.28099/-8.1/30.0", 0, 1)]
    [InlineData("#-1.79/-88.86357/-121.28099/-8.1/30.0", 1, 0)]
    [InlineData("#3/86/20/0/0", 0, -1)]
    [InlineData("#3/86/20/0/0", 1, 0)]
    [InlineData("#6/84.5/20/30/45", -1, 0)]
    public void DraggingNearAPoleNeverJumps(string hash, int dx, int dy)
    {
        var v = new GlobeView { Width = 1250, Height_px = 800 };
        v.ProjX = 1 / System.Math.Tan(v.FovY / 2) / (v.Width / v.Height_px); v.ProjY = 1 / System.Math.Tan(v.FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        double x = 625, y = 400;
        var drag = new GlobeView.DragSession(v, x, y);
        var last = Centre(v);
        double worst = 0; int at = -1;
        for (int k = 1; k <= 120; k++)
        {
            x += 5 * dx; y += 5 * dy;   // 5 px a frame, 600 px in all
            drag.Move(v, x, y);
            var c = Centre(v);
            double moved = System.Math.Acos(System.Math.Clamp(D3.Dot(c, last), -1, 1)) * 180 / System.Math.PI;
            if (moved > worst) { worst = moved; at = k; }
            last = c;
            Assert.InRange(v.Longitude, -540, 540);
        }
        output.WriteLine($"{hash} drag ({dx},{dy}) {(drag.GlobeControls ? "globe" : "grab")}: worst frame moved the centre {worst:F3}° (frame {at}); ends {v.Hash()}");
        // 5 px at ~10,000 km up is ~0.3° of arc at most
        Assert.True(worst < 1.5, $"the centre jumped {worst:F2}° in one frame (frame {at})");
    }

    static GlobeView View(string hash)
    {
        var v = new GlobeView { Width = 1250, Height_px = 800 };
        v.ProjX = 1 / System.Math.Tan(v.FovY / 2) / (v.Width / v.Height_px); v.ProjY = 1 / System.Math.Tan(v.FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        return v;
    }

    [Fact]
    public void NearTheGroundTheGrabbedPlaceStaysUnderTheCursor()
    {
        // GL JS's Mercator controls above MapLibre zoom 12: the grab, exact
        var v = View("#15/35.68/139.76/20/40");
        var grabbed = v.Pick(500, 300)!.Value;
        var drag = new GlobeView.DragSession(v, 500, 300);
        Assert.False(drag.GlobeControls);
        drag.Move(v, 700, 450);
        Assert.True((v.Pick(700, 450)!.Value - grabbed).Length * GlobeCoordinates.EarthRadius < 0.01);
    }

    [Fact]
    public void ZoomedOutTheMapFollowsTheCursor()
    {
        // GL JS's globe controls: dragged right, the centre goes west; dragged down, north. Turned by the bearing
        var v = View("#2/20/30/0/0");
        var drag = new GlobeView.DragSession(v, 600, 400);
        Assert.True(drag.GlobeControls);
        drag.Move(v, 700, 400);
        Assert.True(v.Longitude < 30 && System.Math.Abs(v.Latitude - 20) < 1e-9);
        drag.Move(v, 700, 500);
        Assert.True(v.Latitude > 20);
        var turned = View("#2/20/30/90/0");   // east up: a drag right moves the centre north
        var d2 = new GlobeView.DragSession(turned, 600, 400);
        d2.Move(turned, 700, 400);
        Assert.True(turned.Latitude > 20 && System.Math.Abs(turned.Longitude - 30) < 1e-6);
    }
}
