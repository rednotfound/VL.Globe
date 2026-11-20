using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine;

// Which tiles the camera can see, at which zoom. Pure: a camera pose in, a tile list out, so the
// whole policy is tested without a GPU or a network.
//
// A quadtree walk from zoom 0. A tile is dropped when it is outside the frustum or entirely behind
// the horizon; it is refined while one of its texels would cover more than ScreenSpaceError pixels
// on screen - Cesium's screen-space error, applied to imagery: the texel's size on the ground,
// divided by its distance, scaled to pixels by the screen height and the field of view.
internal sealed class TileSelector
{
    public const int TilePixels = 256;

    public int MaxZoom { get; init; } = 18;
    // 1.41 = sqrt 2: a level is chosen by ROUNDING the ideal zoom, so tiles show at 0.71-1.41x - what
    // MapLibre (raster roundZoom) and Cesium (imagery level Math.round) do. 2.0 floored it: 1-2x, and a
    // one-level seam read as 2x-stretched text beside 1x (the user over northern China, 2026-11-20).
    public double ScreenSpaceError { get; init; } = 1.41;
    public int MaxTiles { get; init; } = 400;

    /// <param name="camera">Camera position in scene units.</param>
    /// <param name="frustum">The camera's view frustum, or null to skip frustum culling.</param>
    /// <param name="fovY">Vertical field of view in radians.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    public List<TileKey> Select(Vector3 camera, BoundingFrustum? frustum, double fovY, double screenHeight, float radius)
    {
        var result = new List<TileKey>();
        if (radius <= 0) return result;
        var cn = camera / radius;                      // camera in globe radii
        var camDist = cn.Length();
        if (camDist <= 1.0f) return result;             // inside the globe: nothing is honest
        var pxPerRadian = screenHeight / (2 * Math.Tan(fovY / 2));
        var stack = new Stack<TileKey>();
        stack.Push(new TileKey(0, 0, 0));
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            var (centre, r) = TileGeometry.Bounds(t);

            // horizon: a point p on the unit sphere is in view when dot(p, C) > 1. The tile's bounding
            // sphere was too generous (a zoom-2 tile wholly behind the limb passed and was fetched),
            // so sample the tile itself on a 5 x 5 grid, with a little slack for what lies between.
            if (t.Zoom >= 2 && !AboveHorizon(t, cn)) continue;
            if (frustum is { } f)
            {
                // Stride 4.2's frustum tests boxes only: the box around the tile's bounding sphere.
                var c = centre * radius; var e = new Vector3(r * radius);
                var box = new BoundingBoxExt(c - e, c + e);
                if (!f.Contains(ref box)) continue;
            }

            if (t.Zoom < MaxZoom && result.Count + stack.Count < MaxTiles)
            {
                var d = Math.Max(DistanceToTile(t, cn), 1e-12);
                // A texel's width shrinks with cos(latitude) inside one tile. Taken at the tile's middle,
                // a tile spanning 66-41 N was judged by cos 56 while its near edge at 41 N is 1.34x wider:
                // drawn at 2.55 px per texel, its text visibly stretched beside a sharp neighbour
                // (TileCoverageFacts; the user over Central Asia, 2026-11-20). Use the widest row.
                double north = TileGeometry.LatitudeOf((double)t.Y / t.TilesPerSide), south = TileGeometry.LatitudeOf((t.Y + 1.0) / t.TilesPerSide);
                var widest = north >= 0 && south <= 0 ? 0 : Math.Min(Math.Abs(north), Math.Abs(south)) * Math.PI / 180;
                var texel = 2 * Math.PI * Math.Cos(widest) / t.TilesPerSide / TilePixels;   // radians of globe per texel
                var pixels = texel / d * pxPerRadian;
                if (pixels > ScreenSpaceError)
                {
                    for (int i = 3; i >= 0; i--) stack.Push(t.Child(i));
                    continue;
                }
            }
            result.Add(t);
        }
        return result;
    }

    // The distance from the camera to the nearest point ON the tile, not to its bounding sphere. The
    // sphere's near side floats above the ground by up to its radius, so for the tile below a low
    // camera the distance came out far too short, and every tile was refined about one level too
    // early: map text was read at 0.6-1.2x its drawn size instead of a web map's 1-2x (TileScaleFacts;
    // the user, 2026-11-20). The camera's longitude and latitude clamped into the tile's ranges give
    // the tile's nearest point for a camera above the globe.
    internal static double DistanceToTile(TileKey t, Vector3 cn)
    {
        double n = t.TilesPerSide;
        double camLon = Math.Atan2(cn.X, cn.Z) * 180 / Math.PI;
        double camLat = Math.Asin(Math.Clamp(cn.Y / cn.Length(), -1, 1)) * 180 / Math.PI;
        double west = TileGeometry.LongitudeOf(t.X / n), east = TileGeometry.LongitudeOf((t.X + 1) / n);
        double north = TileGeometry.LatitudeOf(t.Y / n), south = TileGeometry.LatitudeOf((t.Y + 1) / n);
        // longitude relative to the tile's middle, wrapped, so a tile at the antimeridian clamps right
        double mid = (west + east) / 2, half = (east - west) / 2;
        double rel = camLon - mid; rel -= 360 * Math.Round(rel / 360);
        double lon = mid + Math.Clamp(rel, -half, half);
        double lat = Math.Clamp(camLat, south, north);
        var (x, y, z) = TileGeometry.OnSphere(lon, lat);
        double dx = cn.X - x, dy = cn.Y - y, dz = cn.Z - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    static bool AboveHorizon(TileKey t, Vector3 cn)
    {
        double n = t.TilesPerSide;
        const int k = 4;
        // spacing between samples, in radians of arc, bounds how far an unsampled point can rise
        var slack = 1.0 - Math.Cos(Math.PI / n / k * 1.5);
        for (int j = 0; j <= k; j++)
        {
            var lat = TileGeometry.LatitudeOf((t.Y + (double)j / k) / n);
            for (int i = 0; i <= k; i++)
            {
                var (x, y, z) = TileGeometry.OnSphere(TileGeometry.LongitudeOf((t.X + (double)i / k) / n), lat);
                if (x * cn.X + y * cn.Y + z * cn.Z > 1.0 - slack * cn.Length()) return true;
            }
        }
        return false;
    }
}
