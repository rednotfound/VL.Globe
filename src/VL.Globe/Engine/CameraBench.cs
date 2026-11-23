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

    // VLGLOBE_BENCH=zoom (2026-11-23: the user's zooming showed label index spikes of 10-44 ms a pan never
    // made): each run zooms out 5 levels at one a second, then back in, about the view's centre
    readonly bool _zoom;
    double _h0, _runStart;

    CameraBench(string? hash, double wait, bool zoom) { _hash = hash; _wait = wait; _zoom = zoom; }

    public static CameraBench? FromEnvironment()
    {
        var mode = Environment.GetEnvironmentVariable("VLGLOBE_BENCH");
        if (mode is not ("pan" or "zoom")) return null;
        double wait = double.TryParse(Environment.GetEnvironmentVariable("VLGLOBE_BENCH_WAIT"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 25;
        return new CameraBench(Environment.GetEnvironmentVariable("VLGLOBE_VIEW"), wait, mode == "zoom");
    }

    /// <summary>Moves the view for this frame: the hash on the first, then the pans.</summary>
    public void Step(GlobeView view, ILogger logger)
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
            bool applied = _hash != null && view.TryApplyHash(_hash);
            logger.LogInformation("Globe bench: {Hash} {Applied}, waiting {Wait} s, then {Runs} {Kind} of {Secs} s",
                _hash ?? "(no VLGLOBE_VIEW)", applied ? "applied" : "NOT applied", _wait, Runs,
                _zoom ? "zooms (5 levels out and back at 1 a second)" : $"pans at {Speed} px/s", PanSeconds);
            return;
        }
        double t = _clock.Elapsed.TotalSeconds, dt = _last < 0 ? 0 : t - _last;
        _last = t;
        int phase = Phase(t, out bool panning);
        if (phase != _logged)
        {
            _logged = phase;
            // where the centre is at each edge, and how far a pan took it: a pan that did not move the
            // view is not a measurement (GL JS's first run did not move, 2026-11-22)
            if (phase >= 0 && phase < 2 * Runs && phase % 2 == 0)
            {
                _from = (view.Latitude, view.Longitude);
                _h0 = view.Height; _runStart = t;
                logger.LogInformation("Globe bench: pan {Run} begins, centre {Lat:F5}, {Lon:F5}", phase / 2 + 1, view.Latitude, view.Longitude);
            }
            else if (phase >= 0 && phase < 2 * Runs)
                logger.LogInformation("Globe bench: pan {Run} ends, centre {Lat:F5}, {Lon:F5}, moved {Km:F2} km", phase / 2 + 1,
                    view.Latitude, view.Longitude, Kilometres(_from, (view.Latitude, view.Longitude)));
            else if (phase == 2 * Runs) logger.LogInformation("Globe bench: done");
        }
        if (!panning || dt <= 0 || view.ProjX <= 0) return;
        if (_zoom)
        {
            // out for the first half, back in for the second: 2^levels the height
            double u = Math.Clamp(t - _runStart, 0, PanSeconds);
            double levels = u < PanSeconds / 2 ? u : PanSeconds - u;
            view.SetHeight(_h0 * Math.Pow(2, levels));
            return;
        }
        // the view's width in pixels from its height and the projection's aspect
        double width = view.Height_px * view.ProjY / view.ProjX;
        double px = Speed * dt;
        if (view.PickDir(view.CameraDirectionFromProjection(2 * px / width, 0)) is { } grabbed)
            view.DragTo(grabbed, view.CameraDirectionFromProjection(0, 0));
    }

    (double Lat, double Lon) _from;

    // great-circle distance on the 6371-km sphere (GL JS's LngLat.distanceTo uses 6371008.8 m)
    static double Kilometres((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        double r = Math.PI / 180, dLat = (b.Lat - a.Lat) * r, dLon = (b.Lon - a.Lon) * r;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(a.Lat * r) * Math.Cos(b.Lat * r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * 6371.0088 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
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
