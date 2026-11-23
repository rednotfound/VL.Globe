using NetTopologySuite.Geometries;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 0.1 item 5: the navigation nodes' arithmetic (Engine/Navigation.cs).
public class NavigationFacts
{
    static GlobeView View(double lon, double lat, double h, double heading = 0, double tilt = 0)
    {
        var v = new GlobeView { Longitude = lon, Latitude = lat, Heading = heading, Tilt = tilt, Width = 1280, Height_px = 720, MinHeight = 20 };
        v.SetHeight(h);
        return v;
    }

    [Theory]
    [InlineData(3, 0)]   // zoom 0 is ~136,000 km up: past MaxHeight, clamped
    [InlineData(12, 35.68)]
    [InlineData(18.5, -33.9)]
    public void ZoomToLevelArrivesAtGlobeViewInfosZoom(double zoom, double lat)
    {
        var v = View(10, lat, 1e6);
        v.SetHeight(Navigation.HeightForZoom(v, zoom, lat));
        Assert.Equal(zoom, GlobeView.ZoomOf(v.GroundResolution(), v.Latitude), 9);
    }

    [Fact]
    public void AFlightBeginsAndEndsExactlyWhereAsked()
    {
        var from = new ViewState(139.7671, 35.6812, 2000, 30, 40);
        var to = new ViewState(-0.1276, 51.5072, 5000, -60, 10);
        var f = new Flight(from, to, Math.PI / 4);
        Assert.Equal(from, f.At(0));
        Assert.Equal(to, f.At(f.Duration));
        Assert.Equal(to, f.At(f.Duration + 5));
        Assert.InRange(f.Duration, 1, 10);
    }

    [Fact]
    public void AFarFlightRisesAndComesDownAlongTheGreatCircle()
    {
        // Tokyo to London, street level both ends: up high in the middle, the ground steadily covered
        var from = new ViewState(139.7671, 35.6812, 2000, 0, 0);
        var to = new ViewState(-0.1276, 51.5072, 2000, 0, 0);
        var f = new Flight(from, to, Math.PI / 4, 8);
        var a = GlobeView.Unit(from.Longitude, from.Latitude);
        var b = GlobeView.Unit(to.Longitude, to.Latitude);
        var normal = D3.Cross(a, b).Normalized;
        double peak = 0, lastAlong = -1;
        int peakAt = 0;
        for (int i = 0; i <= 80; i++)
        {
            var s = f.At(8.0 * i / 80);
            if (s.Height > peak) { peak = s.Height; peakAt = i; }
            var p = GlobeView.Unit(s.Longitude, s.Latitude);
            Assert.True(Math.Abs(D3.Dot(p, normal)) < 1e-9, $"off the great circle at step {i}");
            double along = Math.Atan2(D3.Cross(a, p).Length, D3.Dot(a, p));   // acos is coarse near 0
            Assert.True(along >= lastAlong - 1e-12, $"went back at step {i}");
            lastAlong = along;
        }
        Assert.True(peak > 1e6, $"peak only {peak:F0} m");
        // highest on the way, not at either end (eased: half the time is ~80% of the path, already lower)
        Assert.InRange(peakAt, 8, 72);
    }

    [Fact]
    public void AFlightOnTheSpotOnlyChangesTheHeight()
    {
        var from = new ViewState(10, 20, 1e6, 0, 0);
        var to = new ViewState(10, 20, 1e3, 0, 0);
        var f = new Flight(from, to, Math.PI / 4);
        Assert.True(f.Duration > 0);
        double last = double.MaxValue;
        for (int i = 0; i <= 20; i++)
        {
            var s = f.At(f.Duration * i / 20);
            Assert.Equal(10, s.Longitude, 9);
            Assert.Equal(20, s.Latitude, 9);
            Assert.True(s.Height <= last * (1 + 1e-12));
            last = s.Height;
        }
    }

    [Fact]
    public void TheHeadingTurnsTheShortWay()
    {
        var f = new Flight(new ViewState(0, 0, 1e5, 170, 0), new ViewState(0.01, 0, 1e5, -170, 0), Math.PI / 4, 2);
        var mid = f.At(1);
        Assert.True(Math.Abs(Math.Abs(mid.Heading) - 180) < 15, $"heading {mid.Heading} at the middle");
    }

    [Fact]
    public void AViewMovedByAnotherNodeIsSeen()
    {
        var v = View(139.7, 35.7, 1e4);
        var s = ViewState.Of(v);
        Assert.True(s.Holds(v));
        v.ZoomAt(0, 0, 0.9);
        Assert.False(s.Holds(v));
    }

    [Fact]
    public void ZoomToBoundsShowsEveryPointInsideTheMargin()
    {
        // Tokyo's 23 wards' box, and one across a third of Europe
        foreach (var (x0, y0, x1, y1) in new[] { (139.56, 35.52, 139.92, 35.82), (-10.0, 36.0, 30.0, 60.0) })
        {
            var g = new GeometryFactory().ToGeometry(new Envelope(x0, x1, y0, y1));
            var v = View(0, 0, 2e7);
            var fit = Navigation.Fit(g.Coordinates.Select(c => (c.X, c.Y)), v, 0.1)!.Value;
            new ViewState(fit.Longitude, fit.Latitude, fit.Height, v.Heading, 0).ApplyTo(v);
            double most = 0;
            foreach (var c in g.Coordinates)
            {
                GlobeViewNodes.GlobeToScreen(v, out var p, out var visible, c.X, c.Y);
                Assert.True(visible, $"{c} hidden");
                most = Math.Max(most, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
            }
            // inside the window, and filling it but for the margin (the narrower axis is the one that fits)
            Assert.InRange(most, 0.9 - 1e-3, 0.9 + 1e-3);
        }
    }

    [Fact]
    public void EaseIsCssEase()
    {
        Assert.Equal(0, Navigation.Ease(0));
        Assert.Equal(1, Navigation.Ease(1));
        Assert.Equal(0.8024, Navigation.Ease(0.5), 3);   // cubic-bezier(0.25, 0.1, 0.25, 1) at x 0.5
        double last = 0;
        for (int i = 1; i <= 100; i++) { var e = Navigation.Ease(i / 100.0); Assert.True(e >= last); last = e; }
    }
}
