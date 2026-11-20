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
// The two gestures share one primitive, BringUnderCursor: rotate the whole camera rig about the
// globe's centre by the rotation that takes the point currently under a pixel to a wanted point.
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

    internal double MinHeight = 100, MaxHeight = 4 * GlobeCoordinates.EarthRadius;
    internal float Radius = 1f;
    internal double FovY = Math.PI / 4;
    internal double Width = 1280, Height_px = 720;
    // The camera's projection scale (M11, M22): projection-space x / M11 is the camera-space slope.
    // Set by GlobeCamera each frame from the projection Stride computed for the window.
    internal double ProjX = 1 / Math.Tan(Math.PI / 8) / (16.0 / 9), ProjY = 1 / Math.Tan(Math.PI / 8);

    internal double HeightScene => Height / GlobeCoordinates.EarthRadius * Radius;

    internal void SetHeight(double metres) => Height = Math.Clamp(metres, MinHeight, Math.Max(MinHeight, MaxHeight));

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
        var (east, north) = Frame(t);
        double hd = Heading * Math.PI / 180;
        var top = north * Math.Cos(hd) + east * Math.Sin(hd);
        return (t * (Radius + HeightScene), -t, top);
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
        var h = HeightScene;
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

    /// <summary>Rotate the rig about the centre so that the point now along <paramref name="camDir"/> becomes <paramref name="wanted"/>.</summary>
    /// <returns>False when the direction misses the globe; the view is then unchanged.</returns>
    internal bool BringUnderCursor(D3 wanted, D3 camDir)
    {
        if (PickDir(camDir) is not { } now) return false;
        var axis = D3.Cross(now, wanted);
        double sin = axis.Length, cos = Math.Clamp(D3.Dot(now, wanted), -1, 1);
        if (sin < 1e-15) return true;                          // already there
        axis = axis * (1 / sin);
        double angle = Math.Atan2(sin, cos);

        var (_, fwd, up) = Pose();
        var t = Rotate(-fwd, axis, angle);
        var top = Rotate(up, axis, angle);
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
