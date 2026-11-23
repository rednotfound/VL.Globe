using System;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine.Tiles;

/// <summary>
/// A vector vertex's place in its tile (uv, 0..1 across the square, v down) to its position relative to
/// the tile's centre: on the unit sphere, or on the Mercator plane - what GlobeVector.sdsl does per
/// vertex (ROADMAP 4g step 2: GL JS keeps tile coordinates and projects in the shader).
/// </summary>
/// <remarks>
/// The shader has floats only. A point on the sphere minus the tile's centre, each about 1, cancels to
/// float's 1e-7 of the radius - 0.6 m, a street-level fault (the camera-relative work, 2026-11-20). So
/// the difference is formed from small quantities alone: the longitude step dλ = du·2π/n, the latitude
/// step from Mercator's exact identity gd(a) - gd(b) = 2·atan(sinh((a-b)/2) / cosh((a+b)/2)), and the
/// sphere's differences by half-angle and sum-to-product identities - each term a small sine times a
/// cosine, its error relative to the result. Exact at every zoom (no series), so z0 and z14 alike.
/// <see cref="RelativeFloat"/> is the shader's arithmetic in C# floats; <see cref="RelativeExact"/>
/// the double reference the builder used before.
/// </remarks>
internal static class TileProjection
{
    public const double EarthRadius = 6378137.0;

    /// <summary>What the shader needs of a tile, computed in double: the centre's latitude and longitude as sines and cosines, its Mercator y (radians), and tiles per side.</summary>
    public readonly record struct Geo(float SinLat, float CosLat, float SinLon, float CosLon, float MercatorY, float Tiles)
    {
        public Vector4 Trig => new(SinLat, CosLat, SinLon, CosLon);
    }

    public static Geo Of(TileKey key)
    {
        double n = key.TilesPerSide;
        double y = Math.PI * (1 - 2 * (key.Y + 0.5) / n);
        double lat = 2 * Math.Atan(Math.Exp(y)) - Math.PI / 2;
        double lon = (key.X + 0.5) / n * 2 * Math.PI - Math.PI;
        return new Geo((float)Math.Sin(lat), (float)Math.Cos(lat), (float)Math.Sin(lon), (float)Math.Cos(lon), (float)y, (float)n);
    }

    /// <summary>The reference, in double: the point on the sphere (or plane) minus the tile's centre, raised by metres.</summary>
    public static D3 RelativeExact(TileKey key, double u, double v, double raise, bool flat)
    {
        double n = key.TilesPerSide;
        double wx = (key.X + u) / n, wy = (key.Y + v) / n;
        double latitude = TileGeometry.LatitudeOf(Math.Clamp(wy, 0, 1));
        if (flat)
        {
            var centre = new D3((key.X + 0.5) / n, -(key.Y + 0.5) / n, 0);
            // a metre is 1 / (2 pi R cos(latitude)) of the Mercator world, as GL JS raises extrusions
            return new D3(wx, -wy, raise / (2 * Math.PI * EarthRadius * Math.Cos(latitude * Math.PI / 180))) - centre;
        }
        var (x, y, z) = TileGeometry.OnSphere(TileGeometry.LongitudeOf(wx), latitude);
        return new D3(x, y, z) * (1 + raise / EarthRadius) - TileGeometry.Centre(key);
    }

    // Small arguments by polynomial, not the GPU's sin / atan: those are exact to about 1e-6 ABSOLUTE,
    // which for a z14 tile's 4e-4 radians is a quarter of a percent - metres on the ground. A
    // polynomial is adds and multiplies, exact to float's rounding on any GPU. Past 0.25 (tiles
    // coarser than about z5, seen from thousands of kilometres) the built-in functions are kept.
    const float Small = 0.25f;
    internal static float Sin(float x) { float q = x * x; return MathF.Abs(x) < Small ? x * (1 - q / 6 * (1 - q / 20 * (1 - q / 42 * (1 - q / 72)))) : MathF.Sin(x); }
    internal static float Cos(float x) { float q = x * x; return MathF.Abs(x) < Small ? 1 - q / 2 * (1 - q / 12 * (1 - q / 30 * (1 - q / 56))) : MathF.Cos(x); }
    internal static float Sinh(float x) { float q = x * x; return MathF.Abs(x) < Small ? x * (1 + q / 6 * (1 + q / 20 * (1 + q / 42 * (1 + q / 72)))) : MathF.Sinh(x); }
    internal static float Atan(float t)
    {
        float q = t * t;
        return MathF.Abs(t) < Small ? t * (1 - q * (1 / 3f - q * (1 / 5f - q * (1 / 7f - q * (1 / 9f - q * (1 / 11f - q / 13f)))))) : MathF.Atan(t);
    }

    /// <summary>GlobeVector.sdsl's Relative, line for line, in floats.</summary>
    public static Vector3 RelativeFloat(Geo g, float u, float v, float raise, bool flat)
    {
        float delta = 2 * MathF.PI / g.Tiles;
        float du = u - 0.5f, dv = v - 0.5f;
        float d = -dv * delta;   // Mercator y grows north, v south
        float y = g.MercatorY + d;
        // a buffer past the Mercator square (z0-z2) held at its edge, as the builder clamped it; only
        // then, so the small d is never formed again as a difference of large values
        if (!flat && MathF.Abs(y) > MathF.PI) { y = MathF.CopySign(MathF.PI, y); d = y - g.MercatorY; }
        if (flat)
            return new Vector3(du / g.Tiles, -dv / g.Tiles, raise * MathF.Cosh(y) / (2 * MathF.PI * (float)EarthRadius));
        float dLat = 2 * Atan(Sinh(d / 2) / MathF.Cosh(g.MercatorY + d / 2));
        float sh = Sin(dLat / 2), ch = Cos(dLat / 2);
        float sinMid = g.SinLat * ch + g.CosLat * sh, cosMid = g.CosLat * ch - g.SinLat * sh;
        float cosLat = g.CosLat * Cos(dLat) - g.SinLat * Sin(dLat);
        float dSinLat = 2 * cosMid * sh, dCosLat = -2 * sinMid * sh;
        float dLon = du * delta;
        float s = Sin(dLon / 2), oneMinusCos = 2 * s * s, sinDLon = Sin(dLon);
        float a = dCosLat - cosLat * oneMinusCos;   // cos(lat) cos(dLon) - cos(latC)
        var rel = new Vector3(g.SinLon * a + cosLat * g.CosLon * sinDLon, dSinLat, g.CosLon * a - cosLat * g.SinLon * sinDLon);
        if (raise != 0)
        {
            var centre = new Vector3(g.CosLat * g.SinLon, g.SinLat, g.CosLat * g.CosLon);
            rel += (centre + rel) * (raise / (float)EarthRadius);
        }
        return rel;
    }
}
