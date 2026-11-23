using System.Collections.Generic;

namespace VL.Globe.Tests.Diagnostics;

// ROADMAP 4g: the frame summary in `Globe perf`. The line it replaced said "0 over 33 ms" for a second
// whose frames missed the vsync again and again.
public class FrameStatsFacts
{
    [Fact]
    public void A_second_of_missed_vsyncs_is_counted_though_no_frame_passes_33_ms()
    {
        var stats = new FrameStats();
        for (int i = 0; i < 40; i++) stats.Add(16.7, moved: false);
        for (int i = 0; i < 10; i++) stats.Add(23, moved: i == 3);
        var s = stats.Take();
        Assert.Equal(50, s.Frames);
        Assert.Equal(10, s.MissedFrames);
        Assert.Equal(23, s.Max);
        Assert.Equal(16.7, s.P50, 3);
        Assert.Equal(23, s.P95, 3);
        Assert.Equal((40 * 16.7 + 10 * 23) / 50, s.Mean, 6);
        Assert.True(s.Moved);
        // taken: the next second starts empty
        Assert.Equal(0, stats.Take().Frames);
    }

    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        var values = new List<double>();
        for (int i = 1; i <= 100; i++) values.Add(i);
        var s = FrameStats.Summarise(values, false);
        Assert.Equal(50, s.P50);
        Assert.Equal(95, s.P95);
        Assert.Equal(99, s.P99);
        Assert.Equal(100, s.Max);
        Assert.False(s.Moved);
    }
}
