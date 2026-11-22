using System;
using Stride.Core.Mathematics;

namespace VL.Globe;

// The /// text is the tooltip; the reasoning is here.
//
// A geographic camera state (vl-overworld GLOBE-SCOPE-PROPOSAL.md, section 9): the point looked at,
// the height above it, a heading. Never a free 3D pose, so the camera cannot reach the ground by
// construction: Height is clamped to Min/Max Height on every change. Mutable and handed between
// nodes, exactly as VL.Mapsui hands its Map from Drag to ZoomByWheel.
//
// All maths in doubles (a 100 m height is 1.6e-5 radii, near float's resolution at radius 1) and
// converted to Stride floats only for the view matrix. Tilt is phase 2: always 0 (looking straight
// down) until a Tilt node exists.
//
// The two gestures share one primitive, BringUnderCursor: move the camera, heading unchanged, so
// that the point currently under a pixel becomes a wanted point.
// Drag uses it with the point grabbed at press; ZoomByWheel uses it with the point that was under
// the cursor before the height changed. Both invariants ("the grabbed point stays under the
// cursor", "zoom does not move the point under the cursor") are this one rotation, tested once.

/// <summary>Where a globe camera looks from: longitude, latitude, height, heading.</summary>
public sealed class GlobeView
{
    internal GlobeView() { }

    /// <summary>Degrees east of the point straight below the camera.</summary>
    public double Longitude { get; internal set; }

    /// <summary>Degrees north of the point straight below the camera.</summary>
    public double Latitude { get; internal set; }

    /// <summary>Metres above the surface.</summary>
    public double Height { get; internal set; } = 1.5e7;

    /// <summary>Degrees clockwise from north that the top of the view points to.</summary>
    public double Heading { get; internal set; }

    // Tilt (2026-11-21, the user: "想要调整camera的俯仰角度从侧面看你放在地图上的3d模型"). With a tilt the state is
    // MapLibre's: Longitude/Latitude is the point at the CENTRE of the view, Height the distance from
    // the eye to it, Heading and Tilt orient the eye around it - so rotating and tilting turn about
    // the centre, and at Tilt 0 every number means what it meant before (the point straight below, the
    // height above it). EyeAltitude is the height the camera is actually at.
    /// <summary>Degrees the view is tilted from looking straight down: 0 is a map, larger looks toward the horizon.</summary>
    public double Tilt { get; internal set; }

    internal double MaxTilt = 60;   // MapLibre's default maxPitch
    // the tilt limited by the zoom (TiltLimit); off only in tests that study the geometry of any tilt
    internal bool TiltByZoomLimit = true;

    internal double MinHeight = 20, MaxHeight = 4 * GlobeCoordinates.EarthRadius;
    internal float Radius = 1f;
    internal double FovY = Math.PI / 4;
    internal double Width = 1280, Height_px = 720;
    // The camera's projection scale (M11, M22): projection-space x / M11 is the camera-space slope.
    // Set by GlobeCamera each frame from the projection Stride computed for the window.
    internal double ProjX = 1 / Math.Tan(Math.PI / 8) / (16.0 / 9), ProjY = 1 / Math.Tan(Math.PI / 8);

    internal double HeightScene => Height / GlobeCoordinates.EarthRadius * Radius;

    // Height is the distance to the centre point; the floor is on the EYE's altitude, which a tilt
    // lowers to about Height x cos(tilt).
    internal void SetHeight(double metres)
    {
        double floor = MinHeight / Math.Max(Math.Cos(Tilt * Math.PI / 180), 0.2);
        Height = Math.Clamp(metres, floor, Math.Max(floor, MaxHeight));
        // zoomed out, the tilt comes down with its limit (Google's maps do the same)
        double limit = TiltLimit();
        if (Tilt > limit) { Tilt = limit; Height = Math.Max(Height, MinHeight / Math.Max(Math.Cos(Tilt * Math.PI / 180), 0.2)); }
    }

    // ROADMAP 4f step 1 (2026-11-22): the tilt allowed grows with the zoom, as Google's vector maps
    // allow it - 30 degrees to z10, 45 at z14, 60 at z15, 67.5 from z15.5, linear between - so a tilted
    // view far out does not reach the coarse tiles of the distance (no buildings below z13). MaxTilt
    // stays the ceiling. The zoom is the 256-px zoom at the centre of the view, as GlobeViewInfo's.
    static readonly (double zoom, double tilt)[] TiltByZoom = { (10, 30), (14, 45), (15, 60), (15.5, 67.5) };

    /// <summary>The most this view may be tilted at its present zoom: MaxTilt or less.</summary>
    internal double TiltLimit() => TiltByZoomLimit ? Math.Min(MaxTilt, TiltAllowedAt(ZoomOf(GroundResolution(), Latitude))) : MaxTilt;

    internal static double TiltAllowedAt(double zoom)
    {
        if (zoom <= TiltByZoom[0].zoom) return TiltByZoom[0].tilt;
        for (int i = 1; i < TiltByZoom.Length; i++)
            if (zoom <= TiltByZoom[i].zoom)
            {
                var (z0, t0) = TiltByZoom[i - 1]; var (z1, t1) = TiltByZoom[i];
                return t0 + (t1 - t0) * (zoom - z0) / (z1 - z0);
            }
        return TiltByZoom[^1].tilt;
    }

    // Ground resolution and web-map zoom (ROADMAP 0.1 item 2): GeoLibre's camera sync
    // (cesium-camera.ts groundResolution) - a 256-px tile at zoom z spans cos(lat) x 2 pi R / 2^z metres
    // of ground, so a zoom IS a resolution at a latitude, and VL.Mapsui shows the same ground at the same
    // zoom. Measured at the centre of the view, along the line of sight (tilt included).
    internal const double MetresPerPixelAtZoom0 = 2 * Math.PI * GlobeCoordinates.EarthRadius / 256;   // 156543.03

    /// <summary>Ground metres one pixel covers at the centre of the view; Height_px is the window's height.</summary>
    internal double GroundResolution() => Height * 2 * Math.Tan(FovY / 2) / Math.Max(1, Height_px);

    /// <summary>The web-map zoom whose 256-px tiles have this ground resolution at this latitude.</summary>
    internal static double ZoomOf(double metresPerPixel, double latitude)
        => Math.Log2(MetresPerPixelAtZoom0 * Math.Cos(latitude * Math.PI / 180) / Math.Max(metresPerPixel, 1e-9));

    /// <summary>The eye's height above the surface, in metres.</summary>
    internal double EyeAltitude => (Pose().eye.Length / Radius - 1) * GlobeCoordinates.EarthRadius;

    /// <summary>Turn the view about the point at its centre: heading by <paramref name="degreesHeading"/>, tilt by <paramref name="degreesTilt"/>, tilt kept within 0..TiltLimit.</summary>
    internal void RotateBy(double degreesHeading, double degreesTilt)
    {
        Heading = ((Heading + degreesHeading) % 360 + 540) % 360 - 180;
        Tilt = Math.Clamp(Tilt + degreesTilt, 0, TiltLimit());
        SetHeight(Height);   // the floor moved with the tilt
    }

    // ---------------------------------------------------------------- frame and pose

    internal static D3 Unit(double lonDeg, double latDeg)
    {
        double lon = lonDeg * Math.PI / 180, lat = latDeg * Math.PI / 180, c = Math.Cos(lat);
        return new D3(c * Math.Sin(lon), Math.Sin(lat), c * Math.Cos(lon));
    }

    /// <summary>East and north at a unit surface point (Stride Sphere axes: y north).</summary>
    internal static (D3 east, D3 north) Frame(D3 up)
    {
        var east = D3.Cross(new D3(0, 1, 0), up);
        if (east.Length < 1e-9) east = new D3(1, 0, 0);      // at a pole any east will do
        east = east.Normalized;
        return (east, D3.Cross(up, east).Normalized);
    }

    /// <summary>Eye, forward (unit), up (unit, the top of the screen), in scene units.</summary>
    internal (D3 eye, D3 forward, D3 up) Pose()
    {
        var t = Unit(Longitude, Latitude);
        var dir = Ahead(t);
        double p = Tilt * Math.PI / 180;
        var fwd = -t * Math.Cos(p) + dir * Math.Sin(p);   // tilt 0: straight down
        var up = dir * Math.Cos(p) + t * Math.Sin(p);     // tilt 0: the heading direction
        return (t * Radius - fwd * HeightScene, fwd, up);
    }

    /// <summary>The horizontal direction the heading points to at a unit surface point.</summary>
    D3 Ahead(D3 t)
    {
        var (east, north) = Frame(t);
        double hd = Heading * Math.PI / 180;
        return north * Math.Cos(hd) + east * Math.Sin(hd);
    }

    internal Matrix ViewMatrix()
    {
        var (eye, fwd, up) = Pose();
        return Matrix.LookAtRH(eye.ToVector3(), (eye + fwd).ToVector3(), up.ToVector3());
    }

    // Camera-relative rendering (ROADMAP item 1): the view with the eye at the origin - rotation only,
    // no large numbers. Everything drawn with it must be placed at (its position - eye), the
    // subtraction done in double; GlobeCamera hands that eye to Globe through GlobeCameraFrame.
    /// <summary>The view matrix of a camera at the origin with this pose's orientation.</summary>
    internal static Matrix ViewRotation(D3 forward, D3 up)
        => Matrix.LookAtRH(Vector3.Zero, forward.ToVector3(), up.ToVector3());

    /// <summary>Near and far planes that follow the height: near a tenth of the way down, far past the horizon.</summary>
    internal (float near, float far) ClipPlanes()
    {
        var h = Math.Max(Pose().eye.Length - Radius, 1e-12);   // the eye's altitude: a tilt brings the ground nearer than Height
        var horizon = Math.Sqrt((Radius + h) * (Radius + h) - Radius * (double)Radius);
        return ((float)Math.Max(h * 0.1, 1e-9), (float)(horizon * 1.1 + Radius * 0.01));
    }

    // ---------------------------------------------------------------- picking

    /// <summary>The world-space ray through a pixel (origin top-left).</summary>
    internal (D3 origin, D3 dir) Ray(double x, double y)
    {
        var (eye, fwd, up) = Pose();
        var right = D3.Cross(fwd, up).Normalized;
        double t = Math.Tan(FovY / 2), aspect = Width / Math.Max(1, Height_px);
        double nx = 2 * x / Math.Max(1, Width) - 1, ny = 1 - 2 * y / Math.Max(1, Height_px);
        return (eye, (fwd + right * (nx * t * aspect) + up * (ny * t)).Normalized);
    }

    // A cursor is kept in CAMERA space (right, up, forward components of its ray), because that is
    // what stays fixed under the cursor while the rig rotates: re-picking the same camera-space
    // direction after a rotation is "the same pixel". vvvv's Mouse [Stride.Input] gives a world ray;
    // CameraDirection turns it into this form against the current pose.

    /// <summary>A world ray direction as (right, up, forward) components of the current pose.</summary>
    internal D3 CameraDirection(D3 worldDir)
    {
        var (_, fwd, up) = Pose();
        var right = D3.Cross(fwd, up).Normalized;
        var d = worldDir.Normalized;
        return new D3(D3.Dot(d, right), D3.Dot(d, up), D3.Dot(d, fwd));
    }

    internal D3 CameraDirection(double x, double y) => CameraDirection(Ray(x, y).dir);

    // The cursor as vvvv's Mouse gives it in projection space (-1..1, y up), turned into a camera-space
    // direction in doubles. The Mouse node's world Ray was unprojected in float from near/far points
    // close to an eye of magnitude ~1: 100 m above the ground the near plane is 10 m away and float
    // spacing at 1.0 is ~0.4 m, so the ray direction carried a few percent of noise - metres on the
    // ground, tens of pixels, a violent jitter when dragging low (the user, 2026-11-09). Projection
    // space has no such cancellation.
    /// <summary>A camera-space direction from a projection-space cursor position.</summary>
    internal D3 CameraDirectionFromProjection(double px, double py) => new D3(px / ProjX, py / ProjY, 1).Normalized;

    /// <summary>The unit surface point along a camera-space direction, or null when it misses the globe.</summary>
    internal D3? PickDir(D3 camDir)
    {
        var (o, fwd, up) = Pose();
        var right = D3.Cross(fwd, up).Normalized;
        var d = (right * camDir.X + up * camDir.Y + fwd * camDir.Z).Normalized;
        double b = D3.Dot(o, d), c = D3.Dot(o, o) - Radius * (double)Radius;
        double disc = b * b - c;
        if (disc < 0) return null;
        double s = -b - Math.Sqrt(disc);
        if (s <= 0) return null;
        return (o + d * s).Normalized;
    }

    // ---------------------------------------------------------------- the one primitive

    /// <summary>The unit surface point under a pixel, or null when the pixel is off the globe.</summary>
    internal D3? Pick(double x, double y) => PickDir(CameraDirection(x, y));

    // North stays up (the user, 2026-11-20: "拖动一下就是会拖歪掉，歪了也很难摆正"). The rigid rotation
    // about now x wanted is the shortest move, but along a curved path those rotations add up to a
    // turn of the heading - 31 degrees for one circle dragged at globe scale, 50 for ten wheel notches
    // toward a corner (GlobeViewFacts) - and no gesture turns it back. Cesium constrains its globe
    // rotation to the polar axis by default and MapLibre's globe pans without touching the bearing,
    // so: the rigid rotation only as a first guess, then Heading restored and Longitude/Latitude
    // refined by Gauss-Newton until the wanted point is under the cursor again.
    /// <summary>Move the camera, keeping its heading, so that the point now along <paramref name="camDir"/> becomes <paramref name="wanted"/>.</summary>
    /// <returns>False when the direction misses the globe; the view is then unchanged.</returns>
    //
    // From where the camera is first: one frame's move is small, so Newton converges there and never
    // crosses a pole. The rigid rotation is only the fallback guess for a move too large for that - it
    // used to be the first guess, and dragging the pole into view (the camera's latitude at its 89.9
    // limit) put that guess over the pole, 180 degrees of longitude away; each frame then jumped
    // between the two sides and the globe shook (the user, 2026-11-21, "仿佛转不过来一样").
    // Of two exact answers the one on this side of the pole is kept: over it the same point is under
    // the cursor again with the camera 180 degrees round, which is the jump this replaced. A large
    // move's first Newton step can also overshoot the limit when the answer lies well inside it, so
    // "at the pole" is believed only when the rigid guess finds nothing nearer.
    internal bool BringUnderCursor(D3 wanted, D3 camDir)
    {
        double lon0 = Longitude, lat0 = Latitude, heading = Heading;
        var near = Refine(wanted, camDir);
        if (near == Fit.Exact) return true;
        double lonNear = Longitude, latNear = Latitude;

        Longitude = lon0; Latitude = lat0;
        bool rig = RotateRig(wanted, camDir);
        Heading = heading;
        if (rig)
        {
            Latitude = Math.Clamp(Latitude, -MaxLatitude, MaxLatitude);
            if (Refine(wanted, camDir) == Fit.Exact && Math.Abs(Wrap(Longitude - lon0)) < 90) return true;
        }
        if (near == Fit.AtPole) { Longitude = lonNear; Latitude = latNear; return true; }
        Longitude = lon0; Latitude = lat0;
        return false;

        static double Wrap(double deg) => deg - 360 * Math.Round(deg / 360);
    }

    const double MaxLatitude = 89.9;

    enum Fit { Exact, AtPole, Failed }

    /// <summary>Newton steps on (Longitude, Latitude) with the heading fixed.</summary>
    // Exact: the point is under the cursor. AtPole: the latitude reached its limit before that, and only
    // the longitude was fitted - the cross-screen part; the grabbed point then stays below the cursor
    // and the camera stops, as a map stops at its edge. Failed: the cursor left the globe, or no
    // convergence from here.
    Fit Refine(D3 wanted, D3 camDir)
    {
        var (east, north) = Frame(wanted);
        (double, double)? Residual()
        {
            if (PickDir(camDir) is not { } p) return null;
            var r = p - wanted;
            return (D3.Dot(r, east), D3.Dot(r, north));
        }
        // A step of 1e-7 degrees is 1.1 cm on the ground: small against any frame's move, large
        // against double's 1e-16 - the difference quotient keeps ~9 digits, and the residual itself
        // is computed exactly, so the iteration converges to the true point regardless.
        const double d = 1e-7;
        const double close = 1e-28;   // squared, 1e-14 radii: 0.06 mm
        bool atPole = false;
        for (int it = 0; it < 12; it++)
        {
            if (Residual() is not var (rx, ry)) return Fit.Failed;
            if (atPole ? rx * rx < close : rx * rx + ry * ry < close) return atPole ? Fit.AtPole : Fit.Exact;
            double lon = Longitude, lat = Latitude;
            Longitude = lon + d; var ex = Residual(); Longitude = lon;
            if (ex is not var (ax, ay)) return Fit.Failed;
            double j11 = (ax - rx) / d, j21 = (ay - ry) / d;
            if (!atPole)
            {
                Latitude = lat + d; var ey = Residual(); Latitude = lat;
                if (ey is not var (bx, by)) return Fit.Failed;
                double j12 = (bx - rx) / d, j22 = (by - ry) / d;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 1e-30) return Fit.Failed;
                double nextLat = lat - (-j21 * rx + j11 * ry) / det;
                if (Math.Abs(nextLat) <= MaxLatitude)
                {
                    Longitude = lon - (j22 * rx - j12 * ry) / det;
                    Latitude = nextLat;
                    continue;
                }
                // the point would need the camera past the pole: stop there, fit the rest across
                atPole = true;
                Latitude = Math.Clamp(nextLat, -MaxLatitude, MaxLatitude);
                continue;
            }
            if (Math.Abs(j11) < 1e-30) return Fit.AtPole;
            Longitude = lon - rx / j11;
        }
        if (Residual() is not var (fx, fy)) return Fit.Failed;
        if (atPole) return fx * fx < 1e-20 ? Fit.AtPole : Fit.Failed;
        return fx * fx + fy * fy < 1e-20 ? Fit.Exact : Fit.Failed;
    }

    /// <summary>The shortest move: rotate the whole rig about the centre by the rotation taking the point under the cursor to the wanted one.</summary>
    bool RotateRig(D3 wanted, D3 camDir)
    {
        if (PickDir(camDir) is not { } now) return false;
        var axis = D3.Cross(now, wanted);
        double sin = axis.Length, cos = Math.Clamp(D3.Dot(now, wanted), -1, 1);
        if (sin < 1e-15) return true;                          // already there
        axis = axis * (1 / sin);
        double angle = Math.Atan2(sin, cos);

        // the centre point and the heading direction travel with the rotation (at tilt 0 these are
        // -forward and up, the rig's own axes, as before)
        var t0 = Unit(Longitude, Latitude);
        var t = Rotate(t0, axis, angle);
        var top = Rotate(Ahead(t0), axis, angle);
        Latitude = Math.Asin(Math.Clamp(t.Y, -1, 1)) * 180 / Math.PI;
        Longitude = Math.Atan2(t.X, t.Z) * 180 / Math.PI;
        var (east, north) = Frame(t);
        Heading = Math.Atan2(D3.Dot(top, east), D3.Dot(top, north)) * 180 / Math.PI;
        return true;
    }

    static D3 Rotate(D3 v, D3 k, double a)   // Rodrigues
        => v * Math.Cos(a) + D3.Cross(k, v) * Math.Sin(a) + k * (D3.Dot(k, v) * (1 - Math.Cos(a)));

    // ---------------------------------------------------------------- gestures

    /// <summary>One frame of a drag: the grabbed point is brought back under the cursor.</summary>
    internal void DragTo(D3 grabbed, D3 camDir) => BringUnderCursor(grabbed, camDir);
    internal void DragTo(D3 grabbed, double x, double y) => DragTo(grabbed, CameraDirection(x, y));

    /// <summary>Multiply the height by <paramref name="factor"/> keeping the point under the cursor where it is.</summary>
    internal void ZoomAt(D3 camDir, double factor)
    {
        var before = PickDir(camDir);
        SetHeight(Height * factor);
        if (before is { } p) BringUnderCursor(p, camDir);
    }
    internal void ZoomAt(double x, double y, double factor) => ZoomAt(CameraDirection(x, y), factor);

    // The view as MapLibre's URL hash, #zoom/lat/lng/bearing/pitch (maplibre-gl-js src/ui/hash.ts):
    // the user can name the view they saw a fault in (the camera nodes log it after every gesture),
    // a test can load it, and the same text in a MapLibre page shows GL JS's picture of that place.
    // MapLibre's zoom is in 512-px tiles, one below ours; its bearing is our heading, its pitch our tilt.
    /// <summary>The view as MapLibre writes it in a page's address: #zoom/latitude/longitude/bearing/pitch.</summary>
    internal string Hash()
    {
        double zoom = ZoomOf(GroundResolution(), Latitude) - 1;
        return FormattableString.Invariant($"#{zoom:F2}/{Latitude:F5}/{Longitude:F5}/{Heading:F1}/{Tilt:F1}");
    }

    /// <summary>Moves the view to a MapLibre hash (#zoom/lat/lng[/bearing[/pitch]]); false if it is not one.</summary>
    internal bool TryApplyHash(string hash)
    {
        var parts = (hash ?? "").Trim().TrimStart('#').Split('/');
        if (parts.Length < 3) return false;
        var n = new double[5];
        for (int i = 0; i < parts.Length && i < 5; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return false;
        Latitude = Math.Clamp(n[1], -85.0511, 85.0511);
        Longitude = ((n[2] + 180) % 360 + 360) % 360 - 180;
        Heading = ((n[3] % 360) + 540) % 360 - 180;
        Tilt = Math.Clamp(n[4], 0, MaxTilt);
        // the height whose ground resolution at the centre is that of MapLibre's zoom (ours is one more)
        double metresPerPixel = MetresPerPixelAtZoom0 * Math.Cos(Latitude * Math.PI / 180) / Math.Pow(2, n[0] + 1);
        SetHeight(metresPerPixel * Math.Max(1, Height_px) / (2 * Math.Tan(FovY / 2)));
        return true;
    }

    public override string ToString() => $"{Longitude:F4}, {Latitude:F4} at {Height:F0} m";
}

/// <summary>A double-precision 3-vector, internal to the camera maths.</summary>
internal readonly record struct D3(double X, double Y, double Z)
{
    public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);
    public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(D3 a, D3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static D3 Cross(D3 a, D3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(Dot(this, this));
    public D3 Normalized => this * (1 / Length);
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

    // Written out by hand. The compiler's record ToString prints every public property, and
    // Normalized is a D3: printing it printed its Normalized, and so on - a StackOverflowException,
    // uncatchable, that closed vvvv the first time Drag logged a grabbed point (NOTES.md 2026-11-09).
    public override string ToString() => FormattableString.Invariant($"({X}, {Y}, {Z})");
}
