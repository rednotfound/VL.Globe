using VL.Globe;

namespace VL.Globe.Tests;

public class GlobeViewFacts
{
    static GlobeView View(double lon, double lat, double h, double heading = 0)
    {
        var v = new GlobeView { Longitude = lon, Latitude = lat, Heading = heading, Width = 800, Height_px = 600 };
        v.SetHeight(h);
        return v;
    }

    static (double lon, double lat) LonLat(D3 p) => (System.Math.Atan2(p.X, p.Z) * 180 / System.Math.PI, System.Math.Asin(p.Y) * 180 / System.Math.PI);

    static void Same(D3 a, D3 b, double metres)
        => Assert.True((a - b).Length * GlobeCoordinates.EarthRadius < metres, $"{(a - b).Length * GlobeCoordinates.EarthRadius:F2} m apart");

    [Fact]
    public void TheCentrePixelLooksStraightDown()
    {
        var v = View(139.7, 35.7, 1e6);
        var (lon, lat) = LonLat(v.Pick(400, 300)!.Value);
        Assert.InRange(lon, 139.7 - 1e-6, 139.7 + 1e-6);
        Assert.InRange(lat, 35.7 - 1e-6, 35.7 + 1e-6);
    }

    [Fact]
    public void NorthIsUpOnScreenAtHeadingZero()
    {
        var v = View(10, 20, 1e6);
        var (_, latTop) = LonLat(v.Pick(400, 100)!.Value);
        var (lonRight, _) = LonLat(v.Pick(700, 300)!.Value);
        Assert.True(latTop > 20, "the top of the screen is north");
        Assert.True(lonRight > 10, "the right of the screen is east");
    }

    [Theory]
    [InlineData(1.2e7, 35)] [InlineData(2e7, 60)] [InlineData(1.2e7, -35)]
    public void DraggingThePoleIntoViewStopsAtItWithoutShaking(double h, double lat)
    {
        // the user, 2026-11-21, at globe scale: "鼠标拖拽到画面的底部的时候，整个地球开始抖动，仿佛转不过来一样".
        // Pulling a point down brings the pole towards the centre; north stays up, so the camera's
        // latitude stops at 89.9 and the grabbed point cannot follow the cursor any further. Each
        // frame then jumped between two answers. It must stop, and stay stopped.
        var v = View(140, lat, h);
        double sign = lat > 0 ? 1 : -1;
        var grabbed = v.Pick(400, sign > 0 ? 200 : 400)!.Value;
        var poses = new List<(double lon, double lat, double heading)>();
        for (int k = 0; k <= 60; k++)
        {
            double y = sign > 0 ? 200 + 390.0 * System.Math.Min(k, 40) / 40 : 400 - 390.0 * System.Math.Min(k, 40) / 40;
            v.DragTo(grabbed, 400, y);
            poses.Add((v.Longitude, v.Latitude, v.Heading));
        }
        foreach (var (lon, la, heading) in poses)
        {
            Assert.InRange(System.Math.Abs(la), 0, 89.9 + 1e-9);
            Assert.InRange(heading, -1e-6, 1e-6);
        }
        for (int k = 1; k < poses.Count; k++)
            Assert.True(System.Math.Abs(poses[k].lon - poses[k - 1].lon) < 5, $"frame {k}: longitude jumped {poses[k - 1].lon:F2} -> {poses[k].lon:F2}");
        // held still at the bottom: not a hair of movement
        for (int k = 42; k < poses.Count; k++)
        {
            Assert.InRange(poses[k].lon - poses[41].lon, -1e-6, 1e-6);
            Assert.InRange(poses[k].lat - poses[41].lat, -1e-6, 1e-6);
        }
    }

    [Theory]
    [InlineData(1e7)] [InlineData(1e5)] [InlineData(500)]
    public void AGrabbedPointStaysUnderTheCursor(double h)
    {
        var v = View(10, 45, h);
        var grabbed = v.Pick(300, 200)!.Value;
        foreach (var (x, y) in new[] { (320.0, 210.0), (500.0, 400.0), (250.0, 420.0), (480.0, 180.0) })   // all on the globe at every height tested
        {
            v.DragTo(grabbed, x, y);
            Same(grabbed, v.Pick(x, y)!.Value, System.Math.Max(0.5, h * 1e-6));
        }
    }

    [Fact]
    public void DragSpeedFollowsHeight()
    {
        // the same 100-pixel drag moves the view far from high up and little from low down
        double Moved(double h)
        {
            var v = View(0, 0, h);
            var before = GlobeView.Unit(v.Longitude, v.Latitude);
            v.DragTo(v.Pick(400, 300)!.Value, 500, 300);
            return (GlobeView.Unit(v.Longitude, v.Latitude) - before).Length * GlobeCoordinates.EarthRadius;
        }
        Assert.True(Moved(1e6) > 100 * Moved(1e3));
    }

    [Theory]
    [InlineData(1e7, 0.5)] [InlineData(1e6, 2)] [InlineData(1e4, 0.5)]
    public void ZoomKeepsThePointUnderTheCursor(double h, double factor)
    {
        var v = View(-74, 40.7, h);
        var before = v.Pick(600, 150)!.Value;
        v.ZoomAt(600, 150, factor);
        Same(before, v.Pick(600, 150)!.Value, System.Math.Max(0.5, h * 1e-6));
        Assert.Equal(h * factor, v.Height, 6);
    }

    [Fact]
    public void NoInputTakesTheCameraBelowMinOrAboveMaxHeight()
    {
        var v = View(0, 0, 1e5);
        for (int i = 0; i < 100; i++) v.ZoomAt(400, 300, 0.3);
        Assert.Equal(v.MinHeight, v.Height);
        for (int i = 0; i < 100; i++) v.ZoomAt(400, 300, 3);
        Assert.Equal(v.MaxHeight, v.Height);
    }

    // Until 2026-11-23 a cursor off the globe left the camera where it was; the user chose MapLibre's
    // globe instead: the cursor in space holds the horizon's nearest point, and the drag goes on.
    [Fact]
    public void ACursorOffTheGlobeHoldsTheNearestPointOfTheHorizon()
    {
        var v = View(10, 20, 3 * GlobeCoordinates.EarthRadius);
        Assert.Null(v.Pick(5, 5));                     // a corner of the window shows space
        var camDir = v.CameraDirection(5, 5);
        var p = v.PickDir(camDir, horizon: true)!.Value;
        var (eye, fwd, up) = v.Pose();
        // on the silhouette: the sight line touches the sphere there
        Assert.True(Math.Abs(D3.Dot(p * v.Radius - eye, p)) < 1e-12, "not a tangent point");
        // in the plane of the cursor's direction and the centre's
        var right = D3.Cross(fwd, up).Normalized;
        var d = (right * camDir.X + up * camDir.Y + fwd * camDir.Z).Normalized;
        var normal = D3.Cross(d, -eye).Normalized;
        Assert.True(Math.Abs(D3.Dot(p * v.Radius - eye, normal)) < 1e-9, "not toward the cursor");
        // a point on the globe is still the point on the globe
        Assert.Equal(v.Pick(400, 300)!.Value, v.PickDir(v.CameraDirection(400, 300), horizon: true)!.Value);
    }

    [Fact]
    public void ADragGoesOnPastTheEdgeAndComesBackExactly()
    {
        var v = View(0, 0, 3 * GlobeCoordinates.EarthRadius);
        var grabbed = v.Pick(400, 300)!.Value;
        double lastLon = v.Longitude;
        // out to the window's left edge, past the globe's rim (the globe is ~200 px across here)
        for (int x = 400; x >= 5; x -= 15)
        {
            v.DragTo(grabbed, x, 300);
            Assert.True(double.IsFinite(v.Longitude) && double.IsFinite(v.Latitude));
            Assert.True(v.Longitude >= lastLon - 1e-9, $"turned back at x {x}");
            lastLon = v.Longitude;
        }
        Assert.Null(v.Pick(5, 300));
        Assert.True(v.Longitude > 60, $"stopped at the edge: longitude {v.Longitude:F1}");
        // back onto the globe: the grabbed point under the cursor again
        v.DragTo(grabbed, 380, 300);
        Same(v.Pick(380, 300)!.Value, grabbed, 1);
    }

    [Fact]
    public void HeadingSurvivesADrag()
    {
        var v = View(30, 10, 1e5, heading: 40);
        v.DragTo(v.Pick(400, 300)!.Value, 450, 320);
        Assert.InRange(v.Heading, 39.9, 40.1);
    }

    [Theory]
    [InlineData(1e7)] [InlineData(1e5)] [InlineData(20)]
    public void ALoopedDragLeavesNorthUp(double h)
    {
        // The user, 2026-11-20: "拖动一下就是会拖歪掉，歪了也很难摆正". A drag that rotated the rig about
        // grabbed x cursor turned the heading a little on every curved path (a sphere's holonomy) and
        // nothing turned it back. Drag a circle with the button held: north must still be up.
        var v = View(10, 45, h);
        var grabbed = v.Pick(400, 300)!.Value;
        for (int i = 1; i <= 72; i++)
        {
            double a = i * System.Math.PI / 36;
            double x = 400 + 150 * System.Math.Sin(a), y = 300 - 150 * (1 - System.Math.Cos(a));
            v.DragTo(grabbed, x, y);
            Same(grabbed, v.Pick(x, y)!.Value, System.Math.Max(0.01, h * 1e-6));
        }
        Assert.InRange(v.Heading, -1e-6, 1e-6);
    }

    [Fact]
    public void ZoomLeavesNorthUp()
    {
        var v = View(10, 45, 1e7, heading: 0);
        for (int i = 0; i < 10; i++) v.ZoomAt(700, 100, 0.7);
        Assert.InRange(v.Heading, -1e-6, 1e-6);
    }

    // Tilt (2026-11-21): the state is MapLibre's - the centre point, the distance to it, heading, tilt.
    static GlobeView Tilted(double h, double tilt, double heading = 0)
    {
        // the geometry of any tilt: the limit by zoom has facts of its own below
        var v = View(139.77, 35.68, h, heading);
        v.TiltByZoomLimit = false;
        v.Tilt = tilt;
        v.SetHeight(h);
        return v;
    }

    [Theory]
    [InlineData(1e6, 45)] [InlineData(2000, 60)] [InlineData(30, 30)]
    public void ATiltedViewStillLooksAtItsCentrePoint(double h, double tilt)
    {
        var v = Tilted(h, tilt, heading: 25);
        Same(GlobeView.Unit(139.77, 35.68), v.PickDir(new D3(0, 0, 1))!.Value, System.Math.Max(0.01, h * 1e-7));
    }

    [Fact]
    public void RotatingAndTiltingTurnAboutTheCentre()
    {
        var v = Tilted(5000, 0);
        var centre = v.PickDir(new D3(0, 0, 1))!.Value;
        v.RotateBy(30, 40);
        Assert.Equal(30, v.Heading, 9);
        Assert.Equal(40, v.Tilt, 9);
        Same(centre, v.PickDir(new D3(0, 0, 1))!.Value, 0.01);
    }

    [Fact]
    public void TiltStaysBetweenStraightDownAndTheLimit()
    {
        var v = Tilted(5000, 0);
        v.TiltByZoomLimit = true;
        v.RotateBy(0, 500);
        Assert.Equal(v.TiltLimit(), v.Tilt);
        Assert.True(v.Tilt <= v.MaxTilt);
        v.RotateBy(0, -500);
        Assert.Equal(0, v.Tilt);
    }

    [Theory]
    [InlineData(5, 30)] [InlineData(10, 30)] [InlineData(12, 37.5)] [InlineData(14, 45)] [InlineData(14.5, 52.5)]
    [InlineData(15, 60)] [InlineData(15.5, 67.5)] [InlineData(19, 67.5)]
    public void TheTiltAllowedGrowsWithTheZoomAsGooglesMaps(double zoom, double tilt)
        => Assert.Equal(tilt, GlobeView.TiltAllowedAt(zoom), 9);

    [Fact]
    public void ZoomingOutBringsTheTiltDownWithItsLimit()
    {
        var v = Tilted(300, 0);   // street level: MaxTilt (60) is the limit
        v.TiltByZoomLimit = true;
        v.RotateBy(0, 90);
        Assert.Equal(60, v.Tilt, 9);
        for (int i = 0; i < 40 && v.Height < 2e5; i++) v.SetHeight(v.Height * 1.5);
        double zoom = GlobeView.ZoomOf(v.GroundResolution(), v.Latitude);
        Assert.True(zoom < 10, $"zoom {zoom}");
        Assert.Equal(30, v.Tilt, 9);   // as allowed at z10 and below
        v.RotateBy(0, 20);
        Assert.Equal(30, v.Tilt, 9);
    }

    [Fact]
    public void AViewWrittenAsMapLibresHashComesBackTheSame()
    {
        // the user names a view by the line the camera nodes log after a gesture; a test loads it
        var v = Tilted(800, 40, heading: -23.9);
        var hash = v.Hash();
        Assert.Matches(@"^#\d+\.\d\d/35\.68000/139\.77000/-23\.9/40\.0$", hash);
        var w = View(0, 0, 1e7);
        w.TiltByZoomLimit = false;
        Assert.True(w.TryApplyHash(hash));
        Assert.Equal(v.Longitude, w.Longitude, 5);
        Assert.Equal(v.Latitude, w.Latitude, 5);
        Assert.Equal(v.Heading, w.Heading, 1);
        Assert.Equal(v.Tilt, w.Tilt, 1);
        Assert.Equal(v.Height, w.Height, v.Height * 0.01);   // the zoom is written to 2 decimals
        Assert.False(w.TryApplyHash("Kyoto"));
    }

    [Fact]
    public void AMapLibreZoomIsOneBelowOurs()
    {
        // MapLibre counts 512-px tiles: its z16 shows the ground our z17 does
        var w = View(0, 0, 1e7);
        Assert.True(w.TryApplyHash("#16/34.9858/135.7588"));
        Assert.Equal(17, GlobeView.ZoomOf(w.GroundResolution(), w.Latitude), 6);
        Assert.Equal(0, w.Heading);
        Assert.Equal(0, w.Tilt);
    }

    [Fact]
    public void TiltingAtTheFloorDoesNotPutTheEyeBelowIt()
    {
        var v = Tilted(20, 0);                 // at the 20 m floor
        v.RotateBy(0, 60);
        Assert.True(v.EyeAltitude >= 19.9, $"{v.EyeAltitude:F2} m");
    }

    [Theory]
    [InlineData(1e5, 45)] [InlineData(500, 50)]
    public void ATiltedDragKeepsTheGrabbedPointAndTheHeading(double h, double tilt)
    {
        var v = Tilted(h, tilt, heading: 15);
        var grabbed = v.Pick(400, 350)!.Value;
        foreach (var (x, y) in new[] { (420.0, 360.0), (480.0, 420.0), (350.0, 380.0) })
        {
            v.DragTo(grabbed, x, y);
            Same(grabbed, v.Pick(x, y)!.Value, System.Math.Max(0.01, h * 1e-6));
        }
        Assert.Equal(15, v.Heading, 6);
        Assert.Equal(tilt, v.Tilt, 9);
    }

    [Fact]
    public void ATiltedZoomKeepsThePointUnderTheCursor()
    {
        var v = Tilted(1e5, 45);
        var before = v.Pick(500, 400)!.Value;
        v.ZoomAt(500, 400, 0.5);
        Same(before, v.Pick(500, 400)!.Value, 0.5);
    }

    [Fact]
    public void DraggingRightTurnsTheViewToLookRight()
    {
        // MapLibre, Mapbox, deck.gl, Cesium: drag right raises the heading (the user found the opposite
        // "体感上面是反的" once tilted, 2026-11-21)
        var (heading, tilt) = GlobeDragRotateNode.Turn(new global::Stride.Core.Mathematics.Vector2(0.5f, 0), 180);
        Assert.True(heading > 0);
        Assert.Equal(45, heading, 6);     // a quarter of the width: 180 x 0.25
        Assert.Equal(0, tilt);
    }

    [Fact]
    public void DraggingUpTiltsTowardTheHorizon()
    {
        var (heading, tilt) = GlobeDragRotateNode.Turn(new global::Stride.Core.Mathematics.Vector2(0, 0.5f), 180);
        Assert.Equal(0, heading);
        Assert.Equal(22.5, tilt, 6);      // tilting half as fast as turning
    }

    [Fact]
    public void ClipPlanesFollowTheHeight()
    {
        var (n1, f1) = View(0, 0, 1e6).ClipPlanes();
        var (n2, _) = View(0, 0, 1e3).ClipPlanes();
        Assert.True(n2 < n1 / 100, "a camera 1000x lower gets a near plane 1000x closer");
        Assert.True(f1 > n1 * 10);
    }
}

public class D3Facts
{
    // A record struct prints every public property in ToString. D3's Normalized was a D3 property, so
    // printing a D3 printed its Normalized, which printed its Normalized... - a StackOverflowException
    // that killed vvvv the first time Drag logged a grabbed point (2026-11-09). A stack overflow cannot
    // be caught, so this test runs the formatting on a thread with a small stack: the old D3 takes the
    // whole test host down, the fixed one prints three numbers.
    [Fact]
    public void ToStringTerminates()
    {
        string? s = null;
        var t = new System.Threading.Thread(() => s = new D3(1, 2, 3).ToString(), 256 * 1024);
        t.Start(); t.Join();
        Assert.Equal("(1, 2, 3)", s);
    }
}

public class ProjectionInputFacts
{
    [Fact]
    public void ProjectionSpaceAgreesWithPixels()
    {
        // 800 x 600, 45 degrees: M22 = 1 / tan(22.5), M11 = M22 / aspect - what Stride's projection holds
        var v = new GlobeView { Width = 800, Height_px = 600 };
        v.ProjY = 1 / System.Math.Tan(System.Math.PI / 8); v.ProjX = v.ProjY / (800.0 / 600);
        v.SetHeight(1e5);
        foreach (var (x, y) in new[] { (400.0, 300.0), (100.0, 50.0), (700.0, 550.0) })
        {
            var a = v.CameraDirection(x, y);
            var b = v.CameraDirectionFromProjection(2 * x / 800 - 1, 1 - 2 * y / 600);
            Assert.True((a - b).Length < 1e-12, $"{x},{y}: {(a - b).Length}");
        }
    }

    [Fact]
    public void LowDragIsSteadyToACentimetre()
    {
        // 100 m up, a drag of 0.2 in projection space: the grabbed point stays put to 1 cm, frame after frame
        var v = new GlobeView { Width = 800, Height_px = 600 };
        v.SetHeight(100);
        var grabbed = v.PickDir(v.CameraDirectionFromProjection(0, 0))!.Value;
        for (int i = 1; i <= 20; i++)
        {
            var d = v.CameraDirectionFromProjection(0.01 * i, 0.005 * i);
            v.DragTo(grabbed, d);
            Assert.True((v.PickDir(d)!.Value - grabbed).Length * GlobeCoordinates.EarthRadius < 0.01);
        }
    }
}
