using NetTopologySuite.Geometries;
using Stride.Core.Mathematics;
using VL;

namespace VL.Globe.Tests;

public class GlobeCoordinatesFacts
{
    const float Eps = 1e-6f;

    static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(actual.X, expected.X - Eps, expected.X + Eps);
        Assert.InRange(actual.Y, expected.Y - Eps, expected.Y + Eps);
        Assert.InRange(actual.Z, expected.Z - Eps, expected.Z + Eps);
    }

    // The axes are Stride's Sphere's: these four are the convention, written down as a test.
    [Fact] public void NullIslandIsPlusZ() => Near(new Vector3(0, 0, 1), GlobeCoordinates.ToGlobe(0, 0));
    [Fact] public void NinetyEastIsPlusX() => Near(new Vector3(1, 0, 0), GlobeCoordinates.ToGlobe(90, 0));
    [Fact] public void NorthPoleIsPlusY() => Near(new Vector3(0, 1, 0), GlobeCoordinates.ToGlobe(0, 90));
    [Fact] public void DateLineIsMinusZ() => Near(new Vector3(0, 0, -1), GlobeCoordinates.ToGlobe(180, 0));

    [Fact]
    public void RadiusScales() => Near(new Vector3(0, 0, 2.5f), GlobeCoordinates.ToGlobe(0, 0, 0, 2.5f));

    [Fact]
    public void HeightIsMetresOnTheEarthsScale()
    {
        // One earth radius up doubles the distance from the centre.
        Near(new Vector3(0, 0, 2), GlobeCoordinates.ToGlobe(0, 0, GlobeCoordinates.EarthRadius));
    }

    [Theory]
    [InlineData(139.6917, 35.6895, 0)]       // Tokyo
    [InlineData(-74.006, 40.7128, 120)]      // New York, 120 m up
    [InlineData(-179.9, -85.0, 0)]
    [InlineData(10.0, -1.5, 5000)]
    public void RoundTrip(double lon, double lat, double h)
    {
        var p = GlobeCoordinates.ToGlobe(lon, lat, h);
        GlobeCoordinates.FromGlobe(p, out var lon2, out var lat2, out var h2);
        // Float32 positions: ~1e-5 degrees and a few metres is what a float on a unit sphere holds.
        Assert.InRange(lon2, lon - 1e-4, lon + 1e-4);
        Assert.InRange(lat2, lat - 1e-4, lat + 1e-4);
        Assert.InRange(h2, h - 5, h + 5);
    }

    [Fact]
    public void CoordinateIsLonLatHeight()
    {
        Near(GlobeCoordinates.ToGlobe(12, 34, 56, 2), GlobeCoordinates.ToGlobeCoordinate(new CoordinateZ(12, 34, 56), 2));
        Near(GlobeCoordinates.ToGlobe(12, 34, 0, 2), GlobeCoordinates.ToGlobeCoordinate(new Coordinate(12, 34), 2)); // no Z = 0, not NaN
    }

    [Fact]
    public void NullCoordinateIsTheCentreNotAnException() => Assert.Equal(Vector3.Zero, GlobeCoordinates.ToGlobeCoordinate(null!));

    [Fact]
    public void CentreHasNoDirection()
    {
        GlobeCoordinates.FromGlobe(Vector3.Zero, out var lon, out var lat, out var h);
        Assert.Equal(0, lon); Assert.Equal(0, lat); Assert.Equal(-GlobeCoordinates.EarthRadius, h);
    }
}
