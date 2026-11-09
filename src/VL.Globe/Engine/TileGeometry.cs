using System;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine;

// The geometry of one tile on the sphere, in doubles, with no GPU type in sight so that it can be
// tested without one. GlobeNode turns it into Stride buffers.
//
// Layout is MapLibre's (the lab's experiment 6, moved from a vertex shader to the CPU): vertices
// linear in Mercator x and y inside the tile, so the tile's own image maps onto them with linear
// texture coordinates and no distortion; each vertex is put on the sphere by inverse Mercator.
//
// Seams: a vertex is computed from its GLOBAL grid column and row (tile * Segments + i) divided by
// the global count, so the edge a tile shares with its neighbour is computed from identical
// integers and lands on identical doubles - the seam is exact, not merely close.
internal static class TileGeometry
{
    public const int Segments = 8;                 // 8 x 8 cells, 9 x 9 vertices per tile
    public const int VertsPerSide = Segments + 1;
    public const double MaxLatitude = 85.05112877980659;

    /// <summary>Latitude in degrees of a Mercator y in 0..1 (0 = north edge of the world).</summary>
    public static double LatitudeOf(double mercatorY)
        => (2.0 * Math.Atan(Math.Exp(Math.PI * (1.0 - 2.0 * mercatorY))) - Math.PI / 2) * 180.0 / Math.PI;

    /// <summary>Mercator y in 0..1 of a latitude in degrees, clamped to the Web Mercator square.</summary>
    public static double MercatorY(double latitude)
    {
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180.0;
        return 0.5 - Math.Log(Math.Tan(Math.PI / 4 + lat / 2)) / (2 * Math.PI);
    }

    public static double LongitudeOf(double mercatorX) => mercatorX * 360.0 - 180.0;

    /// <summary>A unit-sphere point (Stride Sphere axes: y north, lon 0 lat 0 = +z) from degrees.</summary>
    public static (double X, double Y, double Z) OnSphere(double longitude, double latitude)
    {
        var lon = longitude * Math.PI / 180.0;
        var lat = latitude * Math.PI / 180.0;
        var c = Math.Cos(lat);
        return (c * Math.Sin(lon), Math.Sin(lat), c * Math.Cos(lon));
    }

    /// <summary>Positions on the unit sphere and texture coordinates, row by row from the tile's north edge.</summary>
    public static (Vector3[] positions, Vector2[] uv) Vertices(TileKey tile)
    {
        var positions = new Vector3[VertsPerSide * VertsPerSide];
        var uv = new Vector2[positions.Length];
        double global = (double)tile.TilesPerSide * Segments;
        for (int j = 0; j < VertsPerSide; j++)
        {
            var lat = LatitudeOf((tile.Y * Segments + j) / global);
            for (int i = 0; i < VertsPerSide; i++)
            {
                var lon = LongitudeOf((tile.X * Segments + i) / global);
                var (x, y, z) = OnSphere(lon, lat);
                positions[j * VertsPerSide + i] = new Vector3((float)x, (float)y, (float)z);
                uv[j * VertsPerSide + i] = new Vector2((float)i / Segments, (float)j / Segments);
            }
        }
        return (positions, uv);
    }

    /// <summary>Two triangles per cell, wound counter-clockwise seen from outside the sphere.</summary>
    public static ushort[] Indices { get; } = BuildIndices();

    static ushort[] BuildIndices()
    {
        var idx = new ushort[Segments * Segments * 6];
        int k = 0;
        for (int j = 0; j < Segments; j++)
            for (int i = 0; i < Segments; i++)
            {
                ushort a = (ushort)(j * VertsPerSide + i), b = (ushort)(a + 1);
                ushort c = (ushort)(a + VertsPerSide), d = (ushort)(c + 1);
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        return idx;
    }

    /// <summary>The tile's centre on the unit sphere and the radius of a sphere around its corners.</summary>
    public static (Vector3 centre, float radius) Bounds(TileKey tile)
    {
        double n = tile.TilesPerSide;
        var (cx, cy, cz) = OnSphere(LongitudeOf((tile.X + 0.5) / n), LatitudeOf((tile.Y + 0.5) / n));
        var centre = new Vector3((float)cx, (float)cy, (float)cz);
        float r = 0;
        foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (0.5, 0.0), (0.5, 1.0), (0.0, 0.5), (1.0, 0.5) })
        {
            var (x, y, z) = OnSphere(LongitudeOf((tile.X + u) / n), LatitudeOf((tile.Y + v) / n));
            r = Math.Max(r, Vector3.Distance(centre, new Vector3((float)x, (float)y, (float)z)));
        }
        // A tile wider than ~90 degrees bulges outward beyond its corners' chord; at zoom 0-1 just
        // take the whole sphere.
        if (tile.Zoom <= 1) { centre = Vector3.Zero; r = 1f; }
        return (centre, r * 1.05f);
    }
}
