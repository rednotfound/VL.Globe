using System;
using System.Collections.Generic;

namespace VL.Globe.Engine.Camera;

// ROADMAP 0.1 item 5, 2026-11-23: the navigation nodes' arithmetic, apart from the nodes so it can be
// tested without vvvv (the nodes are in GlobeNavigateNodes.cs).

/// <summary>A view's place: the point at its centre, the distance to it, and how the eye is turned.</summary>
internal readonly record struct ViewState(double Longitude, double Latitude, double Height, double Heading, double Tilt)
{
    public static ViewState Of(GlobeView v) => new(v.Longitude, v.Latitude, v.Height, v.Heading, v.Tilt);

    /// <summary>Moves <paramref name="v"/> here, within its limits (height floor, tilt by zoom).</summary>
    public void ApplyTo(GlobeView v)
    {
        v.Longitude = Navigation.WrapLongitude(Longitude);
        v.Latitude = Math.Clamp(Latitude, -89.9, 89.9);
        v.Heading = Navigation.WrapHeading(Heading);
        v.Tilt = Math.Clamp(Tilt, 0, v.MaxTilt);
        v.SetHeight(Height);
    }

    /// <summary>True when <paramref name="v"/> is still where this put it - nothing else moved it.</summary>
    public bool Holds(GlobeView v) =>
        Math.Abs(Navigation.WrapLongitude(v.Longitude - Longitude)) < 1e-9 && Math.Abs(v.Latitude - Latitude) < 1e-9 &&
        Math.Abs(v.Height - Height) <= 1e-6 * Math.Max(1, Height) &&
        Math.Abs(Navigation.WrapHeading(v.Heading - Heading)) < 1e-9 && Math.Abs(v.Tilt - Tilt) < 1e-9;
}

internal static class Navigation
{
    public static double WrapLongitude(double lon) => ((lon + 180) % 360 + 360) % 360 - 180;
    public static double WrapHeading(double h) => ((h % 360) + 540) % 360 - 180;

    /// <summary>The height at which <paramref name="v"/> shows the web-map zoom <paramref name="zoom"/> (256-px tiles) at <paramref name="latitude"/>: GlobeViewInfo's and VL.Mapsui's zoom.</summary>
    public static double HeightForZoom(GlobeView v, double zoom, double latitude)
    {
        double metresPerPixel = GlobeView.MetresPerPixelAtZoom0 * Math.Cos(latitude * Math.PI / 180) / Math.Pow(2, zoom);
        return metresPerPixel * Math.Max(1, v.Height_px) / (2 * Math.Tan(v.FovY / 2));
    }

    // ZoomToBounds: the smallest cap round the points, and the distance from which it fills the view.
    // The cap's axis is the normalized sum of the points' directions (exact for a symmetric set, a
    // little off-centre otherwise - the radius then takes the farthest point, so all of them show);
    // seen from distance d along the axis, a point theta from it is at angle beta from the line of sight
    // with tan(beta) = R sin(theta) / (d - R cos(theta)), so d = R cos(theta) + R sin(theta) / tan(alpha)
    // for the view's narrower half-angle alpha. Past ~80 degrees the cap is the globe: as far as allowed.
    /// <summary>The view centred on the points (degrees, x longitude, y latitude) from the height that shows them all, <paramref name="margin"/> of the view left round them; null without points.</summary>
    public static (double Longitude, double Latitude, double Height)? Fit(IEnumerable<(double Lon, double Lat)> points, GlobeView v, double margin)
    {
        D3 sum = default;
        var list = new List<D3>();
        foreach (var (lon, lat) in points)
        {
            if (!double.IsFinite(lon) || !double.IsFinite(lat)) continue;
            var u = GlobeView.Unit(lon, Math.Clamp(lat, -90, 90));
            list.Add(u);
            sum += u;
        }
        if (list.Count == 0) return null;
        var axis = sum.Length > 1e-12 ? sum * (1 / sum.Length) : list[0];
        double cosTheta = 1;
        foreach (var u in list) cosTheta = Math.Min(cosTheta, Math.Clamp(D3.Dot(u, axis), -1, 1));
        double theta = Math.Acos(cosTheta);
        double lat0 = Math.Asin(Math.Clamp(axis.Y, -1, 1)) * 180 / Math.PI;
        double lon0 = Math.Atan2(axis.X, axis.Z) * 180 / Math.PI;   // lon 0 is +z, 90 E is +x (Stride's sphere)
        const double R = GlobeCoordinates.EarthRadius;
        if (theta > 80 * Math.PI / 180) return (lon0, lat0, v.MaxHeight);
        // The cap is round, the window not: a box's corners on the cap's rim land at 1/sqrt 2 of the
        // window (0.64 for a 10% margin, the first test). So the height is found on the screen itself:
        // the farthest point (|x| or |y| in projection space) brought to 1 - margin, by bisection on the
        // log of the height - farther is smaller until the horizon hides a point (then: too low).
        double target = Math.Clamp(1 - margin, 0.05, 1);
        var probe = new GlobeView
        {
            Radius = v.Radius, FovY = v.FovY, ProjX = v.ProjX, ProjY = v.ProjY, Width = v.Width, Height_px = v.Height_px,
            MinHeight = 0.01, MaxHeight = v.MaxHeight, Longitude = lon0, Latitude = lat0, Heading = v.Heading, TiltByZoomLimit = false,
        };
        double Most(double h)
        {
            probe.Height = h;
            double most = 0;
            var (eye, fwd, up) = probe.Pose();
            var right = D3.Cross(fwd, up).Normalized;
            foreach (var u in list)
            {
                var d = u * probe.Radius - eye;
                double z = D3.Dot(d, fwd);
                // behind the camera, or past the horizon (the sight line meets the sphere first)
                if (z <= 0 || D3.Dot(u * probe.Radius, d) > 0) return double.PositiveInfinity;
                most = Math.Max(most, Math.Max(Math.Abs(D3.Dot(d, right) / z * probe.ProjX), Math.Abs(D3.Dot(d, up) / z * probe.ProjY)));
            }
            return most;
        }
        double tanAlpha = Math.Min(1 / v.ProjX, 1 / v.ProjY) * target;
        double guess = Math.Max(R * Math.Cos(theta) + R * Math.Sin(theta) / tanAlpha - R, 0.01);
        double lo = Math.Log(guess / 4), hi = Math.Log(Math.Min(guess * 4, Math.Max(v.MaxHeight, guess)));
        if (Most(Math.Exp(hi)) > target) return (lon0, lat0, Math.Exp(hi));
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Most(Math.Exp(mid)) > target) lo = mid; else hi = mid;
        }
        return (lon0, lat0, Math.Exp(hi));
    }

    // MapLibre's easing for flyTo and the zoom buttons: CSS "ease", cubic-bezier(0.25, 0.1, 0.25, 1)
    // (maplibre-gl-js src/util/util.ts defaultEasing).
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t == 0 || t == 1) return t;
        // x(s) = t solved by Newton, then y(s)
        const double x1 = 0.25, y1 = 0.1, x2 = 0.25, y2 = 1;
        static double B(double s, double p1, double p2) => 3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;
        static double dB(double s, double p1, double p2) => 3 * (1 - s) * (1 - s) * p1 + 6 * (1 - s) * s * (p2 - p1) + 3 * s * s * (1 - p2);
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double dx = B(u, x1, x2) - t, slope = dB(u, x1, x2);
            if (Math.Abs(dx) < 1e-9) break;
            if (Math.Abs(slope) < 1e-9) break;
            u = Math.Clamp(u - dx / slope, 0, 1);
        }
        return B(u, y1, y2);
    }
}

/// <summary>
/// MapLibre's flyTo on the globe: the ground followed along the great circle, the height rising and
/// falling by van Wijk and Nuij's optimal path ("Smooth and efficient zooming and panning", 2003,
/// rho = 1.42 as MapLibre), heading and tilt eased with the time.
/// </summary>
// maplibre-gl-js src/ui/camera.ts flyTo: w is the width of the view in ground units (here the height
// times the view's vertical extent at depth 1, which only scales w against u), u the ground distance
// (here the great-circle arc in metres). The time is eased by defaultEasing, as MapLibre's; the duration
// by default S / 1.2 seconds (MapLibre's speed 1.2 "screenfuls" a second), at most 10 s.
internal sealed class Flight
{
    const double Rho = 1.42, Rho2 = Rho * Rho;
    readonly ViewState _from, _to;
    readonly D3 _a, _b;
    readonly double _omega;        // the angle between the two centres, radians
    readonly double _w0, _u1, _r0, _S, _sign;
    readonly bool _straight;       // no ground to cover: only the height changes

    public double Duration { get; }

    public Flight(ViewState from, ViewState to, double fovY, double durationSeconds = 0)
    {
        _from = from; _to = to;
        _a = GlobeView.Unit(from.Longitude, from.Latitude);
        _b = GlobeView.Unit(to.Longitude, to.Latitude);
        _omega = Math.Acos(Math.Clamp(D3.Dot(_a, _b), -1, 1));
        double extent = 2 * Math.Tan(fovY / 2);
        _w0 = Math.Max(from.Height, 1e-3) * extent;
        double w1 = Math.Max(to.Height, 1e-3) * extent;
        _u1 = _omega * GlobeCoordinates.EarthRadius;
        if (_u1 < 1e-3 * Math.Min(_w0, w1))
        {
            _straight = true;
            _sign = w1 < _w0 ? -1 : 1;
            _S = Math.Abs(Math.Log(w1 / _w0)) / Rho;
        }
        else
        {
            double b0 = (w1 * w1 - _w0 * _w0 + Rho2 * Rho2 * _u1 * _u1) / (2 * _w0 * Rho2 * _u1);
            double b1 = (w1 * w1 - _w0 * _w0 - Rho2 * Rho2 * _u1 * _u1) / (2 * w1 * Rho2 * _u1);
            _r0 = Math.Log(Math.Sqrt(b0 * b0 + 1) - b0);
            double r1 = Math.Log(Math.Sqrt(b1 * b1 + 1) - b1);
            _S = (r1 - _r0) / Rho;
        }
        if (!double.IsFinite(_S)) _S = 0;
        Duration = durationSeconds > 0 ? durationSeconds : Math.Min(10, _S / 1.2);
    }

    /// <summary>Where the flight is <paramref name="seconds"/> after it began; the end itself from Duration on.</summary>
    public ViewState At(double seconds)
    {
        if (Duration <= 0 || seconds >= Duration) return _to;
        if (seconds <= 0) return _from;
        double k = Navigation.Ease(Math.Max(0, seconds) / Duration);
        double s = k * _S;
        double scale, ground;   // the height as a share of the first, the ground covered as a share of all of it
        if (_straight || _S == 0)
        {
            scale = _S == 0 ? 1 + k * (_to.Height / Math.Max(_from.Height, 1e-3) - 1) : Math.Exp(_sign * Rho * s);
            ground = k;
        }
        else
        {
            scale = Math.Cosh(_r0) / Math.Cosh(Rho * s + _r0);
            double u = _w0 * (Math.Cosh(_r0) * Math.Tanh(Rho * s + _r0) - Math.Sinh(_r0)) / Rho2;
            ground = Math.Clamp(u / _u1, 0, 1);
        }
        var p = Slerp(_a, _b, _omega, ground);
        double lat = Math.Asin(Math.Clamp(p.Y, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
        double heading = _from.Heading + Navigation.WrapHeading(_to.Heading - _from.Heading) * k;
        double tilt = _from.Tilt + (_to.Tilt - _from.Tilt) * k;
        return new ViewState(lon, lat, _from.Height * scale, heading, tilt);
    }

    static D3 Slerp(D3 a, D3 b, double omega, double t)
    {
        if (omega < 1e-12) return a;
        double s = Math.Sin(omega);
        if (s < 1e-9)
        {
            // opposite points: any great circle - the one through the pole nearer a's latitude's side
            var axis = D3.Cross(a, Math.Abs(a.Y) < 0.9 ? new D3(0, 1, 0) : new D3(1, 0, 0));
            axis = axis * (1 / axis.Length);
            var c = D3.Cross(axis, a);
            double ang = omega * t;
            return a * Math.Cos(ang) + c * Math.Sin(ang);
        }
        double wa = Math.Sin((1 - t) * omega) / s, wb = Math.Sin(t * omega) / s;
        var r = a * wa + b * wb;
        return r * (1 / r.Length);
    }
}
