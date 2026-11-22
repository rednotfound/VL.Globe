using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VL.Globe.Engine;

/// <summary>
/// ROADMAP 4g's bench: the motion tools/GLJS-Yardstick.html makes in GL JS, made by GlobeCamera, so
/// the two `perf` logs can be set side by side. Only when vvvv was started with VLGLOBE_BENCH=pan.
/// </summary>
/// <remarks>
/// VLGLOBE_VIEW=#zoom/lat/lng/bearing/pitch (MapLibre's hash, a `Globe view` log line) is applied on
/// the first frame; then, after VLGLOBE_BENCH_WAIT seconds (25) for loading, the view pans east at
/// 300 px/s by the clock - GL JS's panBy each frame - for 10 s, twice, 5 s apart. A pan is a drag:
/// the point 300 x dt px right of the centre is grabbed and brought to the centre.
/// </remarks>
internal sealed class CameraBench
{
    const double Speed = 300, PanSeconds = 10, GapSeconds = 5;
    const int Runs = 2;

    readonly string? _hash;
    readonly double _wait;
    readonly Stopwatch _clock = new();
    double _last = -1;
    int _logged = -1;

    CameraBench(string? hash, double wait) { _hash = hash; _wait = wait; }

    public static CameraBench? FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_BENCH") != "pan") return null;
        double wait = double.TryParse(Environment.GetEnvironmentVariable("VLGLOBE_BENCH_WAIT"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 25;
        return new CameraBench(Environment.GetEnvironmentVariable("VLGLOBE_VIEW"), wait);
    }

    /// <summary>Moves the view for this frame: the hash on the first, then the pans.</summary>
    public void Step(GlobeView view, ILogger logger)
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
            bool applied = _hash != null && view.TryApplyHash(_hash);
            logger.LogInformation("Globe bench: {Hash} {Applied}, waiting {Wait} s, then {Runs} pans of {Secs} s at {Speed} px/s",
                _hash ?? "(no VLGLOBE_VIEW)", applied ? "applied" : "NOT applied", _wait, Runs, PanSeconds, Speed);
            return;
        }
        double t = _clock.Elapsed.TotalSeconds, dt = _last < 0 ? 0 : t - _last;
        _last = t;
        int phase = Phase(t, out bool panning);
        if (phase != _logged)
        {
            _logged = phase;
            if (phase >= 0 && phase < 2 * Runs)
                logger.LogInformation("Globe bench: pan {Run} {Edge}", phase / 2 + 1, phase % 2 == 0 ? "begins" : "ends");
            else if (phase == 2 * Runs) logger.LogInformation("Globe bench: done");
        }
        if (!panning || dt <= 0 || view.ProjX <= 0) return;
        // the view's width in pixels from its height and the projection's aspect
        double width = view.Height_px * view.ProjY / view.ProjX;
        double px = Speed * dt;
        if (view.PickDir(view.CameraDirectionFromProjection(2 * px / width, 0)) is { } grabbed)
            view.DragTo(grabbed, view.CameraDirectionFromProjection(0, 0));
    }

    // -1 waiting; 2r while pan r runs, 2r + 1 after it; 2 x Runs when done
    int Phase(double t, out bool panning)
    {
        panning = false;
        if (t < _wait) return -1;
        for (int r = 0; r < Runs; r++)
        {
            double from = _wait + r * (PanSeconds + GapSeconds);
            if (t < from + PanSeconds) { panning = t >= from; return t >= from ? 2 * r : 2 * r - 1; }
            if (t < from + PanSeconds + GapSeconds) return 2 * r + 1;
        }
        return 2 * Runs;
    }
}
