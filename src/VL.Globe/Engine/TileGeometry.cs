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
    // Cells per tile side. Fine tiles get 8; coarse ones more, so every level has the same angular
    // resolution (360/64 = 5.6 degrees) and a facet never sags more than 1 - cos(2.8 deg) = 0.0012 of
    // the radius below the sphere. With 8 at every level, a zoom-1 tile's 22.5-degree facets sagged
    // 0.019 and a background sphere at 0.995 showed through them as dark blotches (first far-view
    // picture, 2026-11-09). Zooms 0..3 all land on one 64-cell global grid, so their shared edges
    // stay bit-identical too.
    public const int Segments = 8;
    public static int SegmentsFor(int zoom) => Math.Max(Segments, 64 >> Math.Min(zoom, 6));
    public const int VertsPerSide = Segments + 1;    // for zoom 3 and finer
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
        int seg = SegmentsFor(tile.Zoom), side = seg + 1;
        var positions = new Vector3[side * side];
        var uv = new Vector2[positions.Length];
        double global = (double)tile.TilesPerSide * seg;
        for (int j = 0; j < side; j++)
        {
            var lat = LatitudeOf((tile.Y * seg + j) / global);
            for (int i = 0; i < side; i++)
            {
                var lon = LongitudeOf((tile.X * seg + i) / global);
                var (x, y, z) = OnSphere(lon, lat);
                positions[j * side + i] = new Vector3((float)x, (float)y, (float)z);
                uv[j * side + i] = new Vector2((float)i / seg, (float)j / seg);
            }
        }
        return (positions, uv);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ushort[]> _indices = new();

    /// <summary>Two triangles per cell for a tile at this zoom.</summary>
    public static ushort[] IndicesFor(int zoom) => _indices.GetOrAdd(SegmentsFor(zoom), BuildIndices);

    static ushort[] BuildIndices(int seg)
    {
        int side = seg + 1;
        var idx = new ushort[seg * seg * 6];
        int k = 0;
        for (int j = 0; j < seg; j++)
            for (int i = 0; i < seg; i++)
            {
                ushort a = (ushort)(j * side + i), b = (ushort)(a + 1);
                ushort c = (ushort)(a + side), d = (ushort)(c + 1);
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
