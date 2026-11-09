using System;
using NetTopologySuite.Geometries;
using Stride.Core.Mathematics;
using VL.Core.Import;

namespace VL;

// The /// text is the hover tooltip: short, user-facing. The reasoning lives in these comments.
//
// The axes are the ones Stride's own Sphere uses and the lab shaders used (lab\shaders in
// vl-overworld): y is north, longitude 0 / latitude 0 is +z, longitude 90 east is +x. Keeping the
// Sphere's convention means a texture mapped by Stride and a point placed by this node agree.
//
// Longitude and latitude are Float64 in, the position is Float32 out: Stride is float. On a unit
// sphere a float step is ~6e-8, which is ~0.4 m on the earth - fine for a globe view, and the
// reason street level will need relative-to-centre rendering (GLOBE-SCOPE-PROPOSAL.md, §3).
//
// Height is in metres above the sphere and scales with Radius as the earth would: the sphere is
// the WGS84 equatorial radius, 6,378,137 m, as Web Mercator assumes.

/// <summary>Converts between longitude/latitude and positions on a globe.</summary>
[Name("Globe")]
public static class GlobeCoordinates
{
    /// <summary>The sphere Web Mercator assumes: WGS84's equatorial radius, in metres.</summary>
    internal const double EarthRadius = 6378137.0;

    /// <summary>A longitude and latitude, in degrees, as a position on the globe.</summary>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="height">Metres above the surface.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    [return: Pin(Name = "Position")]
    public static Vector3 ToGlobe(double longitude, double latitude, double height = 0, float radius = 1f)
    {
        var lon = longitude * Math.PI / 180.0;
        var lat = latitude * Math.PI / 180.0;
        var r = radius * (1.0 + height / EarthRadius);
        var c = Math.Cos(lat);
        return new Vector3((float)(r * c * Math.Sin(lon)), (float)(r * Math.Sin(lat)), (float)(r * c * Math.Cos(lon)));
    }

    // A Coordinate is X = longitude, Y = latitude, Z = height when it has one (NaN otherwise): the
    // order GeoJSON and every NTS reader use, so a point read from a file lands where it says.

    /// <summary>A Coordinate (X longitude, Y latitude, Z metres) as a position on the globe.</summary>
    /// <param name="coordinate">X is longitude, Y latitude, Z height in metres (none is 0).</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    [Name("ToGlobe (Coordinate)")]
    [return: Pin(Name = "Position")]
    public static Vector3 ToGlobeCoordinate(Coordinate coordinate, float radius = 1f)
    {
        if (coordinate is null) return Vector3.Zero;
        var z = coordinate.Z;
        return ToGlobe(coordinate.X, coordinate.Y, double.IsNaN(z) ? 0 : z, radius);
    }

    /// <summary>A position on the globe as longitude, latitude and height.</summary>
    /// <param name="position">A position in scene units.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    /// <param name="longitude">Degrees east, -180 to 180.</param>
    /// <param name="latitude">Degrees north, -90 to 90.</param>
    /// <param name="height">Metres above the surface; negative inside.</param>
    public static void FromGlobe(Vector3 position, out double longitude, out double latitude, out double height, float radius = 1f)
    {
        double x = position.X, y = position.Y, z = position.Z;
        var r = Math.Sqrt(x * x + y * y + z * z);
        if (r == 0 || radius <= 0)
        {
            longitude = latitude = 0;
            height = -EarthRadius;
            return;
        }
        longitude = Math.Atan2(x, z) * 180.0 / Math.PI;
        latitude = Math.Asin(Math.Clamp(y / r, -1.0, 1.0)) * 180.0 / Math.PI;
        height = (r / radius - 1.0) * EarthRadius;
    }
}
