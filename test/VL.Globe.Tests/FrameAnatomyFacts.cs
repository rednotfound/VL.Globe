using System.Collections.Generic;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 4g step 4: a frame that missed its vsync, taken apart - its Update by stage, its Draw by part,
// and what is outside both.
public class FrameAnatomyFacts
{
    static FrameAnatomy Kept(double updateMs, long drawSerial)
    {
        var a = new FrameAnatomy();
        var stages = new List<(string, double)> { ("select", 1000 + 2), ("gpu vectors", 1000 + updateMs - 1) };
        a.EndOfUpdate(1000, 1000 + updateMs, stages, new double[] { 3, 0, 0, 0, 0, 0, 0, 0 }, 5 << 20, 2,
            TimeSpan.FromMilliseconds(10), 7, 1, drawSerial);
        return a;
    }

    [Fact]
    public void A_missed_frame_is_its_update_its_draw_and_the_rest()
    {
        var a = Kept(updateMs: 6, drawSerial: 41);
        var draw = new VectorRenderer.DrawTiming(42, Place: 1.5, Upload: 2, Submit: 8.5, Items: 3000);
        var line = a.Explain(31, 1031, TimeSpan.FromMilliseconds(14), 9, 1, draw)!;
        Assert.StartsWith("31.0 ms = Update 6.0 (select 2.0, gpu vectors 3.0, rest 1.0) + draw 12.0 (place 1.5 [index 0.0, placement 0.0, commit 0.0, opacities 0.0, lines 0.0], upload 2.0, submit 8.5 [vector sets 0.0, apply 0.0, draw 0.0; text sets 0.0, apply 0.0, draw 0.0], 3000 items) + outside 13.0", line);
        Assert.Contains("GC paused 4.0 ms, gen0 +2, gen2 +0", line);
        Assert.Contains("uploaded 2 tiles 5.0 MB", line);
        Assert.Contains("gpu vectors: finish+upload 3.0", line);
    }

    [Fact]
    public void A_frame_in_time_says_nothing_and_a_frame_without_its_draw_says_so()
    {
        var a = Kept(updateMs: 6, drawSerial: 41);
        Assert.Null(a.Explain(16.7, 1017, TimeSpan.Zero, 7, 1, new VectorRenderer.DrawTiming(42, 1, 1, 1, 1)));
        var line = a.Explain(40, 1040, TimeSpan.FromMilliseconds(10), 7, 1, new VectorRenderer.DrawTiming(41, 1, 1, 1, 1))!;
        Assert.Contains("+ 0 draws + outside 34.0", line);
    }

    [Fact]
    public void At_most_twenty_lines_a_second()
    {
        var a = Kept(updateMs: 6, drawSerial: 0);
        int lines = 0;
        for (int i = 0; i < 30; i++) if (a.Explain(25, 2000 + i, TimeSpan.Zero, 7, 1, default) != null) lines++;
        Assert.Equal(20, lines);
        Assert.NotNull(a.Explain(25, 3001, TimeSpan.Zero, 7, 1, default));   // the next second
    }
}
