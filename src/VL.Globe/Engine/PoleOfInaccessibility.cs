using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// Where a polygon's point label goes: the point inside it farthest from its edges, to within
// `precision` (GL JS's find_pole_of_inaccessibility.ts, mapbox/polylabel: cells over the bounding box
// split while one could hold a farther point, the area centroid as the first guess). GL JS calls it
// with precision 16 tile units of 8192, 2 px.
internal static class PoleOfInaccessibility
{
    sealed class Cell
    {
        public double X, Y, H, D, Max;
        public Cell(double x, double y, double h, IReadOnlyList<(double X, double Y)[]> polygon)
        {
            X = x; Y = y; H = h;
            D = Distance(x, y, polygon);
            Max = D + H * Math.Sqrt(2);
        }
    }

    public static (double X, double Y) Find(IReadOnlyList<(double X, double Y)[]> polygon, double precision = 1)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (x, y) in polygon[0])
        {
            if (x < minX) minX = x; if (y < minY) minY = y;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }
        double width = maxX - minX, height = maxY - minY;
        double cellSize = Math.Min(width, height);
        double h = cellSize / 2;
        if (cellSize == 0) return (minX, minY);

        // the cell that could hold the farthest point first (GL JS: TinyQueue on b.max - a.max)
        var queue = new PriorityQueue<Cell, double>();
        for (double x = minX; x < maxX; x += cellSize)
            for (double y = minY; y < maxY; y += cellSize)
            {
                var c = new Cell(x + h, y + h, h, polygon);
                queue.Enqueue(c, -c.Max);
            }

        var best = Centroid(polygon);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            if (cell.D > best.D || best.D == 0) best = cell;
            if (cell.Max - best.D <= precision) continue;
            h = cell.H / 2;
            foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                var c = new Cell(cell.X + dx * h, cell.Y + dy * h, h, polygon);
                queue.Enqueue(c, -c.Max);
            }
        }
        return (best.X, best.Y);
    }

    // signed distance from a point to the polygon's outline: positive inside
    static double Distance(double px, double py, IReadOnlyList<(double X, double Y)[]> polygon)
    {
        bool inside = false;
        double minDistSq = double.PositiveInfinity;
        foreach (var ring in polygon)
            for (int i = 0, len = ring.Length, j = len - 1; i < len; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > py) != (b.Y > py) && px < (b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                minDistSq = Math.Min(minDistSq, SegmentDistSq(px, py, a, b));
            }
        return (inside ? 1 : -1) * Math.Sqrt(minDistSq);
    }

    static Cell Centroid(IReadOnlyList<(double X, double Y)[]> polygon)
    {
        double area = 0, x = 0, y = 0;
        var points = polygon[0];
        for (int i = 0, len = points.Length, j = len - 1; i < len; j = i++)
        {
            var a = points[i]; var b = points[j];
            double f = a.X * b.Y - b.X * a.Y;
            x += (a.X + b.X) * f;
            y += (a.Y + b.Y) * f;
            area += f * 3;
        }
        return area == 0 ? new Cell(points[0].X, points[0].Y, 0, polygon) : new Cell(x / area, y / area, 0, polygon);
    }

    static double SegmentDistSq(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        double x = a.X, y = a.Y, dx = b.X - x, dy = b.Y - y;
        if (dx != 0 || dy != 0)
        {
            double t = ((px - x) * dx + (py - y) * dy) / (dx * dx + dy * dy);
            if (t > 1) { x = b.X; y = b.Y; }
            else if (t > 0) { x += dx * t; y += dy * t; }
        }
        dx = px - x; dy = py - y;
        return dx * dx + dy * dy;
    }
}
