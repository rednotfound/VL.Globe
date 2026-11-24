using Stride.Core.Mathematics;

namespace VL.Globe.Tests.Lighting;

// ROADMAP 6b, C: the shadow map's box for a camera-relative view 1500 m over Tokyo Station, looking
// straight down, the sun 31 degrees up in the south-east (2026-11-24 10:10 JST).
public class ShadowFitFacts
{
    const double Lon = 139.7671, Lat = 35.6812;

    static (Matrix view, Matrix projection, D3 eye, Vector3 up) Camera(double heightM)
    {
        var up = GlobeCoordinates.ToGlobe(Lon, Lat);
        var eyeV = GlobeCoordinates.ToGlobe(Lon, Lat, heightM);
        var eye = new D3(eyeV.X, eyeV.Y, eyeV.Z);
        // camera-relative: the view is a rotation, looking down the local vertical, north up the screen
        var view = Matrix.LookAtRH(Vector3.Zero, -up, Vector3.UnitY);
        var projection = Matrix.PerspectiveFovRH(MathUtil.DegreesToRadians(45), 16f / 9f, 1e-9f, 1f);
        return (view, projection, eye, up);
    }

    static Vector3 Sun => SunPosition.Direction(new DateTime(2026, 11, 24, 1, 10, 0, DateTimeKind.Utc));

    [Fact]
    public void The_ground_below_is_in_the_map_and_a_roof_is_nearer_the_sun()
    {
        var (view, projection, eye, up) = Camera(1500);
        var fit = ShadowPass.FitFor(view, projection, eye, 1f, Sun);
        Assert.NotNull(fit);
        float metre = (float)(1 / GlobeCoordinates.EarthRadius);
        var ground = Vector3.TransformNormal(-up * 1500 * metre, view);          // view space, the eye at the origin
        var roof = Vector3.TransformNormal(-up * (1500 - 100) * metre, view);    // 100 m above it
        var g = Vector3.TransformCoordinate(ground, fit!.Value.ViewToShadow);
        var r = Vector3.TransformCoordinate(roof, fit.Value.ViewToShadow);
        Assert.InRange(g.X, -1f, 1f); Assert.InRange(g.Y, -1f, 1f); Assert.InRange(g.Z, 0f, 1f);
        Assert.True(r.Z < g.Z, $"roof {r.Z}, ground {g.Z}");
        // the roof's shadow falls away from the sun: the ground point under the roof is shaded from a place
        // displaced from it, so the two do not share a texel unless the sun is overhead
        Assert.True(new Vector2(r.X - g.X, r.Y - g.Y).Length() > 2f / ShadowPass.Size);
    }

    [Fact]
    public void No_shadow_from_a_sun_below_the_horizon_or_from_far_up()
    {
        var (view, projection, eye, up) = Camera(1500);
        Assert.Null(ShadowPass.FitFor(view, projection, eye, 1f, -up));
        var high = Camera(50000);
        Assert.Null(ShadowPass.FitFor(high.view, high.projection, high.eye, 1f, Sun));
    }
}
