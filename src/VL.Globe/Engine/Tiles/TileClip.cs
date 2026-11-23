using System;
using System.Collections.Generic;

namespace VL.Globe.Engine.Tiles;

// GeoJSON cut to a tile before it is built (2026-11-23). GeoJSON was handed to every tile whole, the shader
// clipping what lies outside the square - but since the 16-byte vertex (ROADMAP 4g) a vertex holds its
// place in the tile only from -0.5 to 1.5 tiles: a long line's far end was clamped to that edge, and the
// segment from it into the tile pointed the wrong way (a Geometry line over vector tiles came out in
// stray pieces). GL JS cuts GeoJSON into tiles with geojson-vt; so here, to the square widened by a
// quarter of a tile (the shader still clips at the edge; the cut edges lie outside it).
//
// Built on the tile workers, in parallel: every feature here is a new object (a shared one's Index and
// Bounds would be written from several threads), its bounds computed here, not read from the cache.
internal static class TileClip
{
    public const double Margin = 0.25;

    /// <summary>The features of <paramref name="features"/> (world coordinates, Mercator 0..1) cut to <paramref name="key"/>'s square widened by <see cref="Margin"/>.</summary>
    public static FeatureTable Clip(FeatureTable features, TileKey key)
    {
        double n = key.TilesPerSide;
        double x0 = (key.X - Margin) / n, x1 = (key.X + 1 + Margin) / n, y0 = (key.Y - Margin) / n, y1 = (key.Y + 1 + Margin) / n;
        var kept = new List<StyleFeature>();
        var parts = new List<List<(double X, double Y)[]>>();
        foreach (var f in features)
        {
            parts.Clear();
            foreach (var part in f.Parts)
            {
                var rings = new List<(double X, double Y)[]>();
                foreach (var ring in part)
                {
                    var span = ring.Span;
                    switch (f.Kind)
                    {
                        case StyleFeature.Shape.Point:
                            foreach (var p in span) if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1) rings.Add(new[] { p });
                            break;
                        case StyleFeature.Shape.Line:
                            foreach (var piece in ClipLine(span, x0, y0, x1, y1)) rings.Add(piece);
                            break;
                        default:
                            if (ClipRing(span, x0, y0, x1, y1) is { } r) rings.Add(r);
                            else if (rings.Count == 0) goto nextPart;   // the outer ring gone: its holes too
                            break;
                    }
                }
                if (f.Kind == StyleFeature.Shape.Polygon) { if (rings.Count > 0) parts.Add(rings); }
                else foreach (var r in rings) parts.Add(new List<(double X, double Y)[]> { r });
            nextPart:;
            }
            if (parts.Count == 0) continue;
            var c = new StyleFeature { Kind = f.Kind, Properties = f.Properties, Id = f.Id };
            c.SetParts(parts);
            kept.Add(c);
        }
        return new FeatureTable(kept.ToArray());
    }

    // Liang-Barsky segment by segment; a line leaving and coming back becomes several
    internal static List<(double X, double Y)[]> ClipLine(ReadOnlySpan<(double X, double Y)> line, double x0, double y0, double x1, double y1)
    {
        var pieces = new List<(double X, double Y)[]>();
        var current = new List<(double X, double Y)>();
        for (int i = 0; i + 1 < line.Length; i++)
        {
            var a = line[i]; var b = line[i + 1];
            if (!Segment(ref a, ref b, x0, y0, x1, y1, out bool aMoved, out bool bMoved))
            {
                if (current.Count > 1) pieces.Add(current.ToArray());
                current.Clear();
                continue;
            }
            if (current.Count == 0 || aMoved) { if (current.Count > 1) pieces.Add(current.ToArray()); current.Clear(); current.Add(a); }
            current.Add(b);
            if (bMoved) { pieces.Add(current.ToArray()); current.Clear(); }
        }
        if (current.Count > 1) pieces.Add(current.ToArray());
        return pieces;
    }

    static bool Segment(ref (double X, double Y) a, ref (double X, double Y) b, double x0, double y0, double x1, double y1, out bool aMoved, out bool bMoved)
    {
        aMoved = bMoved = false;
        double dx = b.X - a.X, dy = b.Y - a.Y, t0 = 0, t1 = 1;
        bool Edge(double p, double q)
        {
            if (p == 0) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        if (!Edge(-dx, a.X - x0) || !Edge(dx, x1 - a.X) || !Edge(-dy, a.Y - y0) || !Edge(dy, y1 - a.Y)) return false;
        var na = (a.X + t0 * dx, a.Y + t0 * dy); var nb = (a.X + t1 * dx, a.Y + t1 * dy);
        aMoved = t0 > 0; bMoved = t1 < 1;
        a = na; b = nb;
        return true;
    }

    // Sutherland-Hodgman against the four edges; null when nothing of the ring is left
    internal static (double X, double Y)[]? ClipRing(ReadOnlySpan<(double X, double Y)> ring, double x0, double y0, double x1, double y1)
    {
        int count = ring.Length;
        bool closed = count > 1 && ring[0] == ring[count - 1];
        if (closed) count--;
        var poly = new List<(double X, double Y)>(count);
        for (int i = 0; i < count; i++) poly.Add(ring[i]);
        poly = Cut(poly, p => p.X >= x0, (a, b) => Lerp(a, b, (x0 - a.X) / (b.X - a.X)));
        poly = Cut(poly, p => p.X <= x1, (a, b) => Lerp(a, b, (x1 - a.X) / (b.X - a.X)));
        poly = Cut(poly, p => p.Y >= y0, (a, b) => Lerp(a, b, (y0 - a.Y) / (b.Y - a.Y)));
        poly = Cut(poly, p => p.Y <= y1, (a, b) => Lerp(a, b, (y1 - a.Y) / (b.Y - a.Y)));
        if (poly.Count < 3) return null;
        if (closed) poly.Add(poly[0]);
        return poly.ToArray();
    }

    static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    static List<(double X, double Y)> Cut(List<(double X, double Y)> poly, Func<(double X, double Y), bool> inside, Func<(double X, double Y), (double X, double Y), (double X, double Y)> cross)
    {
        var result = new List<(double X, double Y)>(poly.Count + 4);
        for (int i = 0; i < poly.Count; i++)
        {
            var cur = poly[i]; var prev = poly[(i + poly.Count - 1) % poly.Count];
            bool ci = inside(cur), pi = inside(prev);
            if (ci) { if (!pi) result.Add(cross(prev, cur)); result.Add(cur); }
            else if (pi) result.Add(cross(prev, cur));
        }
        return result;
    }
}
