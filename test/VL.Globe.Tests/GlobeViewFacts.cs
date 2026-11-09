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

    [Fact]
    public void ACursorOffTheGlobeDoesNotMoveTheCamera()
    {
        var v = View(0, 0, 3 * GlobeCoordinates.EarthRadius);
        Assert.Null(v.Pick(5, 5));                     // a corner of the window shows space
        var grabbed = v.Pick(400, 300)!.Value;
        var (lon, lat) = (v.Longitude, v.Latitude);
        v.DragTo(grabbed, 5, 5);
        Assert.Equal(lon, v.Longitude); Assert.Equal(lat, v.Latitude);
    }

    [Fact]
    public void HeadingSurvivesADrag()
    {
        var v = View(30, 10, 1e5, heading: 40);
        v.DragTo(v.Pick(400, 300)!.Value, 450, 320);
        Assert.InRange(v.Heading, 39.9, 40.1);
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
