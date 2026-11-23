using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace VL.Globe.Engine.Camera;

// ROADMAP 0.1 item 4, Pick (2026-11-23): what is under a point of the screen, VL.Mapsui's Pick on the
// globe. The cursor's ray meets the globe; the tolerance in pixels becomes metres at that point (the eye's
// distance times one pixel's angle); each geometry is moved into metres round that point (longitude
// scaled by its cosine - exact enough over a few pixels anywhere but the poles) and NTS says whether it
// contains the point or comes within the tolerance. The top layer first, the last feature of a layer first:
// what is drawn on top is what is picked.
internal static class Picking
{
    const double MetresPerDegreeLat = 110574.0, MetresPerDegreeLonAtEquator = 111319.49;

    /// <summary>The topmost geometry within <paramref name="tolerancePx"/> of the screen point, as its layer and index; null if none, or past the globe.</summary>
    public static (GlobeLayer layer, int index, double longitude, double latitude)? Pick(GlobeView view, GlobeLayer? input, double px, double py, double tolerancePx)
    {
        if (input is null) return null;
        if (view.PickDir(view.CameraDirectionFromProjection(px, py)) is not { } dir) return null;
        double lat = Math.Asin(Math.Clamp(dir.Y, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(dir.X, dir.Z) * 180 / Math.PI;
        var (eye, _, _) = view.Pose();
        double distance = (dir * view.Radius - eye).Length / view.Radius * GlobeCoordinates.EarthRadius;
        double tolerance = Math.Max(0, tolerancePx) * distance * 2 * Math.Tan(view.FovY / 2) / Math.Max(1, view.Height_px);
        return Pick(input, lon, lat, tolerance) is { } hit ? (hit.layer, hit.index, lon, lat) : null;
    }

    /// <summary>The topmost geometry within <paramref name="toleranceMetres"/> of a place.</summary>
    public static (GlobeLayer layer, int index)? Pick(GlobeLayer input, double longitude, double latitude, double toleranceMetres)
    {
        double kx = MetresPerDegreeLonAtEquator * Math.Cos(latitude * Math.PI / 180), ky = MetresPerDegreeLat;
        var toLocal = AffineTransformation.TranslationInstance(-longitude, -latitude).Scale(kx, ky);
        var here = new GeometryFactory().CreatePoint(new Coordinate(0, 0));
        var leaves = input.Leaves().Where(l => l.Raster is null && l.Vector is null).ToList();
        for (int li = leaves.Count - 1; li >= 0; li--)
        {
            var layer = leaves[li];
            for (int i = layer.Geometries.Count - 1; i >= 0; i--)
            {
                var g = layer.Geometries[i];
                var env = g.EnvelopeInternal;
                // a cheap reject before the copy: the envelope, widened by the tolerance in degrees
                double dLon = toleranceMetres / Math.Max(1, kx), dLat = toleranceMetres / ky;
                if (longitude < env.MinX - dLon || longitude > env.MaxX + dLon || latitude < env.MinY - dLat || latitude > env.MaxY + dLat) continue;
                var local = toLocal.Transform(g);
                if (local is IPolygonal && local.Contains(here)) return (layer, i);
                if (local.IsWithinDistance(here, toleranceMetres)) return (layer, i);
            }
        }
        return null;
    }

    /// <summary>Meridians and parallels every <paramref name="spacing"/> degrees, as lines with a point each degree (they follow the sphere when draped).</summary>
    public static List<Geometry> Graticule(double spacing)
    {
        var f = new GeometryFactory();
        var lines = new List<Geometry>();
        if (!(spacing > 0)) return lines;
        spacing = Math.Max(spacing, 0.01);
        const double MaxLat = 85.0511;   // the Web Mercator tiles' edge: lines are draped into them
        for (double lon = -180; lon < 180 - 1e-9; lon += spacing)
        {
            var pts = new List<Coordinate>();
            for (double lat = -MaxLat; lat < MaxLat; lat += Math.Min(1, spacing)) pts.Add(new Coordinate(lon, lat));
            pts.Add(new Coordinate(lon, MaxLat));
            lines.Add(f.CreateLineString(pts.ToArray()));
        }
        for (double lat = 0; lat <= MaxLat; lat += spacing)
            foreach (var l in lat == 0 ? new[] { 0.0 } : new[] { lat, -lat })
            {
                var pts = new List<Coordinate>();
                for (double lon = -180; lon < 180; lon += Math.Min(1, spacing)) pts.Add(new Coordinate(lon, l));
                pts.Add(new Coordinate(180, l));
                lines.Add(f.CreateLineString(pts.ToArray()));
            }
        return lines;
    }
}
