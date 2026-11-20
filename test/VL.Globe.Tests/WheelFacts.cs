namespace VL.Globe.Tests;

// The wheel played out over time (the user, 2026-11-20: one notch zoomed "很多", not smooth like
// Google Earth or MapLibre).
public class WheelFacts
{
    [Fact]
    public void ANotchPlaysOutOverSeveralFramesAndArrivesExactly()
    {
        var s = new WheelSmoother();
        s.Add(0.25);
        double first = s.Step(1 / 60.0, 0.15), total = first;
        Assert.InRange(first, 0.01, 0.2);               // a part, not the whole notch, in the first frame
        int frames = 1;
        while (s.Pending != 0 && frames < 600) { total += s.Step(1 / 60.0, 0.15); frames++; }
        Assert.Equal(0.25, total, 12);                  // nothing lost, nothing added
        Assert.InRange(frames, 5, 30);                  // done within half a second at 60 fps
    }

    [Fact]
    public void NotchesInQuickSuccessionAddUp()
    {
        var s = new WheelSmoother();
        double total = 0;
        for (int i = 0; i < 4; i++) { s.Add(0.25); total += s.Step(1 / 60.0, 0.15); }
        while (s.Pending != 0) total += s.Step(1 / 60.0, 0.15);
        Assert.Equal(1.0, total, 12);
    }

    [Fact]
    public void ZeroSmoothingIsAtOnce()
    {
        var s = new WheelSmoother();
        s.Add(0.5);
        Assert.Equal(0.5, s.Step(1 / 60.0, 0));
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void AFrameRateDoesNotChangeTheDuration()
    {
        double Time(double dt) { var s = new WheelSmoother(); s.Add(1); double t = 0; while (System.Math.Abs(s.Pending) > 0.05) { s.Step(dt, 0.15); t += dt; } return t; }
        Assert.InRange(System.Math.Abs(Time(1 / 30.0) - Time(1 / 144.0)), 0, 0.04);
    }
}
