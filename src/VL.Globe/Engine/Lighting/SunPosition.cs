using System;

namespace VL.Globe.Engine.Lighting;

// Where the sun stands over the Earth at a moment: the point it is straight above (the subsolar point).
// The Astronomical Almanac's low-precision formulas (sun's position to about 0.01 degrees, 1950-2050):
// the mean longitude and anomaly from the days since J2000.0, the ecliptic longitude, the obliquity,
// right ascension and declination; the subsolar longitude is the right ascension less Greenwich mean
// sidereal time. The same family of formulas as suncalc (deck.gl's SunLight) - a light on a map needs
// no more (Cesium's Simon 1994 is finer than a pixel of shade can show).
internal static class SunPosition
{
    /// <summary>The subsolar point at a moment: degrees north and east.</summary>
    public static (double latitude, double longitude) Subsolar(DateTime time)
    {
        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;   // Unspecified is taken as UTC
        double n = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        double L = 280.460 + 0.9856474 * n;                // mean longitude
        double g = Rad(357.528 + 0.9856003 * n);           // mean anomaly
        double lambda = Rad(L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g));   // ecliptic longitude
        double epsilon = Rad(23.439 - 0.0000004 * n);      // obliquity of the ecliptic
        double ra = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda));
        double dec = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));
        double gmst = 280.46061837 + 360.98564736629 * n;  // Greenwich mean sidereal time, degrees
        double lon = Wrap(Deg(ra) - gmst);
        return (Deg(dec), lon);
    }

    /// <summary>The unit vector from the globe's centre towards the sun, in the globe's axes (y north, lon 0 / lat 0 = +z, 90°E = +x).</summary>
    public static global::Stride.Core.Mathematics.Vector3 Direction(DateTime time)
    {
        var (lat, lon) = Subsolar(time);
        double la = Rad(lat), lo = Rad(lon);
        return new((float)(Math.Cos(la) * Math.Sin(lo)), (float)Math.Sin(la), (float)(Math.Cos(la) * Math.Cos(lo)));
    }

    static double Rad(double d) => d * Math.PI / 180;
    static double Deg(double r) => r * 180 / Math.PI;
    static double Wrap(double d) { d %= 360; if (d > 180) d -= 360; if (d <= -180) d += 360; return d; }
}
