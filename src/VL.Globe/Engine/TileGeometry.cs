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

    // The poles (the user, 2026-11-23: the round holes at both poles). Web Mercator stops at 85.05°;
    // MapLibre GL JS's globe extends the top and bottom rows of tiles to the pole and stretches the
    // texture's edge row over the cap (vertical_perspective_projection.getMeshFromTileID, extendToNorthPole
    // for raster, background and hillshade). The same here: rows of the cap every 0.25° of latitude, so a
    // chord sags ~15 m, at most - one row from 85° to 90° sagged ~6 km.
    public const int CapRows = 20;
    public static bool NorthCap(TileKey tile) => tile.Y == 0;
    public static bool SouthCap(TileKey tile) => tile.Y == tile.TilesPerSide - 1;

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

    // Camera-relative rendering (ROADMAP item 1, Cesium's "RTC"): the absolute positions above are
    // ~1 in size, and float spacing there is 6e-8 radii = 0.38 m, so every vertex snapped to a 0.4 m
    // lattice. Here each vertex is stored as its offset from the tile's own centre, subtracted in
    // double: a zoom-19 tile is ~1e-5 radii across, so its offsets carry float error of ~1e-12 radii
    // (micrometres). The centre stays in double and goes to the GPU only as (centre * R - eye), also
    // subtracted in double (Translation below) - small wherever the camera is close. The price: two
    // neighbours no longer produce bit-identical shared edges (each adds its own float offset to its
    // own float translation), but the difference scales with the distance to the eye and stays
    // far below a pixel.

    /// <summary>The tile's centre on the unit sphere, in double: the midpoint of its Mercator square.</summary>
    public static D3 Centre(TileKey tile)
    {
        double n = tile.TilesPerSide;
        var (x, y, z) = OnSphere(LongitudeOf((tile.X + 0.5) / n), LatitudeOf((tile.Y + 0.5) / n));
        return new D3(x, y, z);
    }

    /// <summary>The tile's vertices as offsets from <see cref="Centre"/> on the unit sphere, their unit normals and texture coordinates.</summary>
    public static (D3 centre, Vector3[] offsets, Vector3[] normals, Vector2[] uv) RelativeVertices(TileKey tile)
    {
        int seg = SegmentsFor(tile.Zoom), side = seg + 1;
        var centre = Centre(tile);
        bool north = NorthCap(tile), south = SouthCap(tile);
        var offsets = new Vector3[side * side + ((north ? 1 : 0) + (south ? 1 : 0)) * CapRows * side];
        var normals = new Vector3[offsets.Length];
        var uv = new Vector2[offsets.Length];
        double global = (double)tile.TilesPerSide * seg;
        for (int j = 0; j < side; j++)
        {
            var lat = LatitudeOf((tile.Y * seg + j) / global);
            for (int i = 0; i < side; i++)
            {
                var lon = LongitudeOf((tile.X * seg + i) / global);
                var (x, y, z) = OnSphere(lon, lat);
                var p = new D3(x, y, z);
                offsets[j * side + i] = (p - centre).ToVector3();
                normals[j * side + i] = p.ToVector3();
                uv[j * side + i] = new Vector2((float)i / seg, (float)j / seg);
            }
        }
        // the caps after the grid, north first, each row k = 1..CapRows from the edge to the pole; the
        // texture's edge row stretched over them
        int at = side * side;
        foreach (var (cap, v) in new[] { (north, 0f), (south, 1f) })
        {
            if (!cap) continue;
            double edge = v == 0 ? MaxLatitude : -MaxLatitude, pole = v == 0 ? 90 : -90;
            for (int k = 1; k <= CapRows; k++)
            {
                double lat = edge + (pole - edge) * k / CapRows;
                for (int i = 0; i < side; i++, at++)
                {
                    var (x, y, z) = OnSphere(LongitudeOf((tile.X * seg + i) / global), lat);
                    var p = new D3(x, y, z);
                    offsets[at] = (p - centre).ToVector3();
                    normals[at] = p.ToVector3();
                    uv[at] = new Vector2((float)i / seg, v);
                }
            }
        }
        return (centre, offsets, normals, uv);
    }

    /// <summary>Where a tile entity goes, scaled by <paramref name="radius"/>: relative to <paramref name="eye"/> when given, absolute otherwise.</summary>
    public static Vector3 Translation(D3 centre, float radius, D3? eye)
        => (eye is { } e ? centre * radius - e : centre * radius).ToVector3();

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ushort[]> _indices = new();

    /// <summary>Two triangles per cell for this tile, its pole caps' rows included (<see cref="RelativeVertices"/>).</summary>
    public static ushort[] IndicesFor(TileKey tile)
        => _indices.GetOrAdd(SegmentsFor(tile.Zoom) * 4 + (NorthCap(tile) ? 1 : 0) + (SouthCap(tile) ? 2 : 0), BuildIndices);

    static ushort[] BuildIndices(int key)
    {
        int seg = key / 4, side = seg + 1;
        bool north = (key & 1) != 0, south = (key & 2) != 0;
        var idx = new System.Collections.Generic.List<ushort>(seg * (seg + 2 * CapRows) * 6);
        // the rows from north to south, by where each starts in the vertex array
        var rows = new System.Collections.Generic.List<int>();
        int capStart = side * side;
        if (north) for (int k = CapRows; k >= 1; k--) rows.Add(capStart + (k - 1) * side);
        for (int j = 0; j <= seg; j++) rows.Add(j * side);
        if (south)
        {
            int southStart = capStart + (north ? CapRows * side : 0);
            for (int k = 1; k <= CapRows; k++) rows.Add(southStart + (k - 1) * side);
        }
        for (int r = 0; r + 1 < rows.Count; r++)
            for (int i = 0; i < seg; i++)
            {
                ushort a = (ushort)(rows[r] + i), b = (ushort)(a + 1);
                ushort c = (ushort)(rows[r + 1] + i), d = (ushort)(c + 1);
                idx.Add(a); idx.Add(c); idx.Add(b);
                idx.Add(b); idx.Add(c); idx.Add(d);
            }
        return idx.ToArray();
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
        // a tile in the top or bottom row reaches its pole (the cap)
        if (NorthCap(tile)) r = Math.Max(r, Vector3.Distance(centre, new Vector3(0, 1, 0)));
        if (SouthCap(tile)) r = Math.Max(r, Vector3.Distance(centre, new Vector3(0, -1, 0)));
        // A tile wider than ~90 degrees bulges outward beyond its corners' chord; at zoom 0-1 just
        // take the whole sphere.
        if (tile.Zoom <= 1) { centre = Vector3.Zero; r = 1f; }
        return (centre, r * 1.05f);
    }
}
