using System;

namespace VL.Globe.Engine.Diagnostics;

// TEMPORARY (ROADMAP 4g step 4, 2026-11-23): what each stage of a symbol layer's per-feature loop
// allocates, read by AllocationProbe. Off unless the probe turns it on. To be removed once the stages
// are known.
internal static class AllocMeter
{
    public static bool On;
    public static readonly double[] Mb = new double[16];
    public static readonly string?[] Names = new string?[16];
    public static readonly long[] Calls = new long[16];
    public static long Glyphs;
    [ThreadStatic] static long t_at;

    public static void Start() { if (On) t_at = GC.GetAllocatedBytesForCurrentThread(); }

    public static void Lap(int stage, string name)
    {
        if (!On) return;
        long now = GC.GetAllocatedBytesForCurrentThread();
        Mb[stage] += (now - t_at) / 1048576.0;
        Calls[stage]++;
        Names[stage] = name;
        t_at = now;
    }

    public static void Reset() { Array.Clear(Mb); Array.Clear(Names); Array.Clear(Calls); Glyphs = 0; }
}
