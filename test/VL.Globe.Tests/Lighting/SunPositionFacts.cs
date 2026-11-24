using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Lights;

namespace VL.Globe.Tests.Lighting;

// ROADMAP 6b: SunLight's sun, against published moments - the 2024 equinox and solstices (the sun's
// declination 0 and +/-23.44 degrees) and the equation of time at its extremes (solar noon at Greenwich
// 11:43:35 UTC on 3 November, 12:14:12 UTC on 11 February) - and the light's entity read the way
// Stride's LightProcessor reads it.
public class SunPositionFacts
{
    static DateTime Utc(int y, int mo, int d, int h, int mi, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Theory]
    [InlineData(2024, 3, 20, 3, 6, 0.0)]       // March equinox
    [InlineData(2024, 6, 20, 20, 51, 23.44)]   // June solstice
    [InlineData(2024, 9, 22, 12, 44, 0.0)]     // September equinox
    [InlineData(2024, 12, 21, 9, 21, -23.44)]  // December solstice
    public void Declination_at_equinoxes_and_solstices(int y, int mo, int d, int h, int mi, double expected)
        => Assert.InRange(SunPosition.Subsolar(Utc(y, mo, d, h, mi)).latitude, expected - 0.03, expected + 0.03);

    [Theory]
    [InlineData(2024, 11, 3, 11, 43, 35)]   // the sun 16.4 min fast
    [InlineData(2024, 2, 11, 12, 14, 12)]   // the sun 14.2 min slow
    public void Over_Greenwich_at_solar_noon(int y, int mo, int d, int h, int mi, int s)
        => Assert.InRange(SunPosition.Subsolar(Utc(y, mo, d, h, mi, s)).longitude, -0.1, 0.1);

    [Fact]
    public void Moves_west_fifteen_degrees_an_hour()
    {
        var a = SunPosition.Subsolar(Utc(2024, 6, 1, 12, 0)).longitude;
        var b = SunPosition.Subsolar(Utc(2024, 6, 1, 13, 0)).longitude;
        Assert.InRange(a - b, 14.9, 15.1);
    }

    [Fact]
    public void A_local_time_is_converted()
    {
        var utc = Utc(2024, 6, 1, 12, 0);
        Assert.Equal(SunPosition.Subsolar(utc), SunPosition.Subsolar(utc.ToLocalTime()));
    }

    [Fact]
    public void Direction_is_the_subsolar_point_on_the_globe()
    {
        var t = Utc(2024, 6, 20, 20, 51);
        var (lat, lon) = SunPosition.Subsolar(t);
        var expected = GlobeCoordinates.ToGlobe(lon, lat);
        Assert.True((SunPosition.Direction(t) - expected).Length() < 1e-5f);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    [InlineData(1, 0, 0)]
    [InlineData(0.3, 0.8, -0.5)]
    public void Towards_turns_the_light_away_from_the_sun(float x, float y, float z)
    {
        var to = Vector3.Normalize(new Vector3(x, y, z));
        var travels = Vector3.Transform(-Vector3.UnitZ, SunLightNode.Towards(to));
        Assert.True((travels + to).Length() < 1e-4f, $"{travels} for {to}");
    }

    [Fact]
    public void SunLight_is_a_directional_light_shining_from_the_sun()
    {
        var node = new SunLightNode();
        var t = Utc(2024, 6, 20, 20, 51);
        var entity = node.Update(t, intensity: 2f);
        Assert.Same(entity, node.Update(t, intensity: 2f));   // one entity, kept
        var light = entity.Get<LightComponent>();
        Assert.IsType<LightDirectional>(light.Type);
        Assert.Equal(2f, light.Intensity);
        entity.Transform.UpdateWorldMatrix();
        // as Stride's LightProcessor: the way the light travels is the entity's -Z
        var direction = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, entity.Transform.WorldMatrix));
        Assert.True((direction + SunPosition.Direction(t)).Length() < 1e-4f);
    }
}
