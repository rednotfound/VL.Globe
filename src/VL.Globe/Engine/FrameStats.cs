using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

/// <summary>
/// A second's frame intervals, summed up for the `Globe perf` line (ROADMAP 4g): frames, mean,
/// percentiles and the frames that missed a 60 Hz vsync.
/// </summary>
/// <remarks>
/// The slowest frame and the frames over 33 ms hid what the user sees: at 60 Hz a frame over
/// 16.7 ms misses its vsync and the picture judders (2026-11-22: 23 ms at rest, "0 over 33 ms").
/// </remarks>
internal sealed class FrameStats
{
    // one vsync at 60 Hz is 16.7 ms; the interval is measured between Updates, which jitter by a
    // few ms round the present, so a frame counts as missed only past 20 ms
    public const double Missed = 20;

    readonly List<double> _intervals = new();
    bool _moved;

    public int Count => _intervals.Count;

    /// <summary>One frame's interval in ms; <paramref name="moved"/>: the camera moved in it.</summary>
    public void Add(double ms, bool moved)
    {
        _intervals.Add(ms);
        _moved |= moved;
    }

    public readonly record struct Summary(int Frames, double Mean, double P50, double P95, double P99, double Max, int MissedFrames, bool Moved)
    {
        public double Fps => Mean > 0 ? 1000 / Mean : 0;

        public override string ToString()
            => $"{Frames} frames, {Fps:F0} fps, mean {Mean:F1} p50 {P50:F1} p95 {P95:F1} p99 {P99:F1} max {Max:F0} ms, {MissedFrames} over {Missed:F0} ms{(Moved ? ", moving" : ", still")}";
    }

    /// <summary>The intervals since the last call, summed up; then cleared.</summary>
    public Summary Take()
    {
        var s = Summarise(_intervals, _moved);
        _intervals.Clear();
        _moved = false;
        return s;
    }

    internal static Summary Summarise(List<double> intervals, bool moved)
    {
        if (intervals.Count == 0) return new Summary(0, 0, 0, 0, 0, 0, 0, moved);
        var sorted = new List<double>(intervals);
        sorted.Sort();
        double sum = 0;
        int missed = 0;
        foreach (var v in sorted) { sum += v; if (v > Missed) missed++; }
        // nearest rank: the smallest value with at least p of the frames at or below it
        double Rank(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        return new Summary(sorted.Count, sum / sorted.Count, Rank(0.5), Rank(0.95), Rank(0.99), sorted[^1], missed, moved);
    }
}
