using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NetTopologySuite.Geometries;
using Color4 = Stride.Core.Mathematics.Color4;

namespace VL.Globe.Engine.Styling;

// Shapes over vector tiles (2026-11-23, the user: "测试geometry是否能够顺利地放在vector tiles上"). Draped
// shapes are drawn into the tiles' overlays, and the GPU's vector tiles draw above every tile - so a
// Geometry or FeatureLayer after a VectorTiles layer vanished under its land and buildings. Those layers
// now go the GPU's way too: one MapLibre style a layer, its shapes an inline GeoJSON source, a fill, a
// line and a circle layer in its VectorStyle - drawn after the vector tiles, in Group order, with the
// same lines and antialiasing.
internal static class GeometryStyle
{
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>A MapLibre style drawing <paramref name="geometries"/> (longitude, latitude) in <paramref name="style"/>.</summary>
    public static string Json(IReadOnlyList<Geometry> geometries, GlobeVectorStyle style)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":8,\"sources\":{\"shapes\":{\"type\":\"geojson\",\"data\":{\"type\":\"FeatureCollection\",\"features\":[");
        bool first = true;
        foreach (var g in geometries)
        {
            if (g is null || g.IsEmpty) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"type\":\"Feature\",\"properties\":{},\"geometry\":");
            Write(sb, g);
            sb.Append('}');
        }
        sb.Append("]}}},\"layers\":[");
        string fill = Rgba(style.FillColor), line = Rgba(style.LineColor);
        string width = style.LineWidth.ToString(C);
        sb.Append($"{{\"id\":\"fill\",\"type\":\"fill\",\"source\":\"shapes\",\"filter\":[\"==\",[\"geometry-type\"],\"Polygon\"],\"paint\":{{\"fill-color\":\"{fill}\",\"fill-antialias\":false}}}},");
        sb.Append($"{{\"id\":\"line\",\"type\":\"line\",\"source\":\"shapes\",\"filter\":[\"!=\",[\"geometry-type\"],\"Point\"],\"paint\":{{\"line-color\":\"{line}\",\"line-width\":{width}}},\"layout\":{{\"line-join\":\"round\",\"line-cap\":\"round\"}}}},");
        sb.Append($"{{\"id\":\"point\",\"type\":\"circle\",\"source\":\"shapes\",\"filter\":[\"==\",[\"geometry-type\"],\"Point\"],\"paint\":{{\"circle-color\":\"{line}\",\"circle-radius\":{Math.Max(2, style.LineWidth * 2).ToString(C)}}}}}");
        sb.Append("]}");
        return sb.ToString();
    }

    // the draped path's colours (GlobeNode.ToSk): the channels as they are, 0..255
    static string Rgba(Color4 c) => string.Format(C, "rgba({0},{1},{2},{3})",
        (int)Math.Clamp(c.R * 255, 0, 255), (int)Math.Clamp(c.G * 255, 0, 255), (int)Math.Clamp(c.B * 255, 0, 255), Math.Clamp(c.A, 0, 1));

    static void Write(StringBuilder sb, Geometry g)
    {
        switch (g)
        {
            case Point p: sb.Append("{\"type\":\"Point\",\"coordinates\":"); Pos(sb, p.Coordinate); sb.Append('}'); break;
            case LineString l: sb.Append("{\"type\":\"LineString\",\"coordinates\":"); Line(sb, l.Coordinates); sb.Append('}'); break;
            case Polygon p: sb.Append("{\"type\":\"Polygon\",\"coordinates\":"); Rings(sb, p); sb.Append('}'); break;
            case MultiPoint mp: sb.Append("{\"type\":\"MultiPoint\",\"coordinates\":"); Line(sb, mp.Coordinates); sb.Append('}'); break;
            case MultiLineString ml:
                sb.Append("{\"type\":\"MultiLineString\",\"coordinates\":[");
                for (int i = 0; i < ml.NumGeometries; i++) { if (i > 0) sb.Append(','); Line(sb, ml.GetGeometryN(i).Coordinates); }
                sb.Append("]}"); break;
            case MultiPolygon mpg:
                sb.Append("{\"type\":\"MultiPolygon\",\"coordinates\":[");
                for (int i = 0; i < mpg.NumGeometries; i++) { if (i > 0) sb.Append(','); Rings(sb, (Polygon)mpg.GetGeometryN(i)); }
                sb.Append("]}"); break;
            case GeometryCollection gc:
                sb.Append("{\"type\":\"GeometryCollection\",\"geometries\":[");
                for (int i = 0; i < gc.NumGeometries; i++) { if (i > 0) sb.Append(','); Write(sb, gc.GetGeometryN(i)); }
                sb.Append("]}"); break;
        }
    }

    static void Pos(StringBuilder sb, Coordinate c) => sb.Append('[').Append(c.X.ToString("R", C)).Append(',').Append(c.Y.ToString("R", C)).Append(']');

    static void Line(StringBuilder sb, Coordinate[] cs)
    {
        sb.Append('[');
        for (int i = 0; i < cs.Length; i++) { if (i > 0) sb.Append(','); Pos(sb, cs[i]); }
        sb.Append(']');
    }

    static void Rings(StringBuilder sb, Polygon p)
    {
        sb.Append('[');
        Line(sb, p.ExteriorRing.Coordinates);
        foreach (var hole in p.InteriorRings) { sb.Append(','); Line(sb, hole.Coordinates); }
        sb.Append(']');
    }
}
