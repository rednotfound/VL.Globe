using System;
using System.Collections.Generic;
using SkiaSharp;

namespace VL.Globe.Engine;

// A line turned into triangles the way MapLibre GL JS does it (src/data/bucket/line_bucket.ts,
// v6.19.0, addLine), so a translucent line looks as it does there: triangles that overlap blend
// twice. A gentle corner shares its miter vertices (no overlap); a corner past the miter limit
// becomes a bevel or flipbevel whose two segments overlap on the inside, and a round join is two
// half discs that overlap. Skia's stroker unions the whole outline and blends it once, which drew
// MapLibre's transparent line-join fixtures 7% wrong (ROADMAP 4b).
//
// GL JS draws a round cap or join as a rectangle and cuts it to a half disc in the fragment shader
// (length of the interpolated normal); here the rectangle's triangles are dropped and a half-disc
// fan is emitted instead.
//
// GL JS antialiases in the fragment shader: alpha = clamp(outset - distance from the centre line,
// 0, 1) in device pixels, outset = half width + 0.5, the distance being |interpolated normal| x
// outset. Each vertex here carries that normal (N: -1..1 across a strip, 0..1 out from a disc's
// centre); Shade cuts every triangle where the alpha bends (N = 0 and the plateau's edges), so
// within each piece alpha is affine in position and per-vertex colours reproduce it exactly.
internal readonly record struct LineVertex(SKPoint P, float N);

// The same vertex before it is extruded: the point on the line (X, Y), the extrusion (QX, QY) in
// units of the outset, and N. GL JS's vertex buffer holds exactly this, and its vertex shader
// extrudes by the width in pixels - the GPU path (VectorBucket) does the same.
internal readonly record struct ExtrudedVertex(double X, double Y, double QX, double QY, float N);

internal sealed class LineTessellator
{
    const double CosHalfSharpCorner = 0.79335334029123516;   // cos(75° / 2)
    const double DegPerTriangle = 20;
    const double ExtrudeScale = 63;                          // GL JS stores extrusions as bytes of 1/63

    List<ExtrudedVertex> _triangles = null!;
    double _outset;
    readonly List<(ExtrudedVertex v, bool round)> _strip = new();
    int _e1 = -1, _e2 = -1;

    // One tessellator and one array of points a thread, reset per line: a new one a line grew a new strip
    // list each time - the (ExtrudedVertex, bool)[] at the top of a zoomed-out tile's garbage (4g step 4)
    [ThreadStatic] static LineTessellator? t_tessellator;
    [ThreadStatic] static V[]? t_vertices;

    static LineTessellator For(List<ExtrudedVertex> into, double outset)
    {
        var t = t_tessellator ??= new LineTessellator();
        t._triangles = into; t._outset = outset;
        t._strip.Clear(); t._e1 = -1; t._e2 = -1;
        return t;
    }

    static V[] Points(int n)
    {
        var a = t_vertices;
        if (a is null || a.Length < n) t_vertices = a = new V[Math.Max(n, 2 * (a?.Length ?? 16))];
        return a;
    }

    [ThreadStatic] static List<ExtrudedVertex>? _scratch;

    readonly record struct V(double X, double Y)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y);
        public static V operator *(V a, double k) => new(a.X * k, a.Y * k);
        public double Mag => Math.Sqrt(X * X + Y * Y);
        public V Unit { get { double m = Mag; return m == 0 ? this : new(X / m, Y / m); } }
        public V Perp => new(-Y, X);
        public SKPoint ToSk() => new((float)X, (float)Y);
        public double Dist(V o) => (this - o).Mag;
    }

    /// <summary>The outset GL JS extrudes to: half the width plus half a device pixel for the antialiasing ramp.</summary>
    public static double Outset(double halfWidth) => halfWidth + 0.5;

    /// <summary>Appends the triangles of one line (device pixels) to <paramref name="into"/>, three vertices each, extruded to <paramref name="outset"/>.</summary>
    public static void Tessellate(IReadOnlyList<SKPoint> line, bool isPolygon, string join, string cap,
        double miterLimit, double roundLimit, double outset, double sharpCornerOffset, List<LineVertex> into)
    {
        var vertices = Points(line.Count);
        for (int i = 0; i < line.Count; i++) vertices[i] = new V(line[i].X, line[i].Y);
        var raw = _scratch ??= new List<ExtrudedVertex>();
        raw.Clear();
        For(raw, outset).AddLine(vertices, line.Count, isPolygon, join, cap, miterLimit, roundLimit, sharpCornerOffset);
        into.Capacity = Math.Max(into.Capacity, into.Count + raw.Count);
        foreach (var r in raw) into.Add(new LineVertex(new SKPoint((float)(r.X + r.QX * outset), (float)(r.Y + r.QY * outset)), r.N));
    }

    /// <summary>Appends the triangles of one line to <paramref name="into"/> unextruded, three vertices each; <paramref name="nominalOutset"/> only sets how finely round caps and joins are sliced.</summary>
    public static void TessellateExtruded(IReadOnlyList<(double X, double Y)> line, bool isPolygon, string join, string cap,
        double miterLimit, double roundLimit, double nominalOutset, double sharpCornerOffset, List<ExtrudedVertex> into)
    {
        var vertices = Points(line.Count);
        for (int i = 0; i < line.Count; i++) vertices[i] = new V(line[i].X, line[i].Y);
        For(into, nominalOutset).AddLine(vertices, line.Count, isPolygon, join, cap, miterLimit, roundLimit, sharpCornerOffset);
    }

    /// <summary>Cuts the triangles where GL JS's shader alpha bends and gives every vertex its colour: <paramref name="color"/> (straight) times that alpha.</summary>
    public static void Shade(List<LineVertex> triangles, double outset, SKColorF color, List<SKPoint> positions, List<SKColor> colors)
    {
        // alpha(n) = clamp(outset * (1 - |n|), 0, 1): affine between the bends at -p, 0 and p (p
        // where it reaches 1), so each triangle is cut into the bands between them, without
        // allocating: a street-level tile has tens of thousands of these (2026-11-21: the List per
        // piece made the minor-road layers most of a 100 ms overlay)
        float p = (float)(1 - 1 / outset);
        // the colour's bytes once; per vertex only its alpha
        float o = (float)outset, ca = color.Alpha;
        byte cr = (byte)(Math.Clamp(color.Red, 0f, 1f) * 255 + 0.5f), cg = (byte)(Math.Clamp(color.Green, 0f, 1f) * 255 + 0.5f), cb = (byte)(Math.Clamp(color.Blue, 0f, 1f) * 255 + 0.5f);
        positions.Capacity = Math.Max(positions.Capacity, positions.Count + triangles.Count * 4);
        colors.Capacity = Math.Max(colors.Capacity, colors.Count + triangles.Count * 4);
        Span<float> bounds = stackalloc float[5];
        int nb;
        if (p > 0) { bounds[0] = float.NegativeInfinity; bounds[1] = -p; bounds[2] = 0; bounds[3] = p; bounds[4] = float.PositiveInfinity; nb = 4; }
        else { bounds[0] = float.NegativeInfinity; bounds[1] = 0; bounds[2] = float.PositiveInfinity; nb = 2; }
        Span<LineVertex> tri = stackalloc LineVertex[3];
        Span<LineVertex> a = stackalloc LineVertex[8];
        Span<LineVertex> b = stackalloc LineVertex[8];
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            tri[0] = triangles[t]; tri[1] = triangles[t + 1]; tri[2] = triangles[t + 2];
            float lo = Math.Min(tri[0].N, Math.Min(tri[1].N, tri[2].N)), hi = Math.Max(tri[0].N, Math.Max(tri[1].N, tri[2].N));
            for (int band = 0; band < nb; band++)
            {
                float b0 = bounds[band], b1 = bounds[band + 1];
                if (hi <= b0 || lo >= b1) continue;   // the triangle lies outside this band
                int n = Clip(tri, 3, b0, above: true, a);
                n = Clip(a, n, b1, above: false, b);
                for (int k = 1; k + 1 < n; k++)
                {
                    Emit(b[0]); Emit(b[k]); Emit(b[k + 1]);
                }
            }
        }

        void Emit(LineVertex v)
        {
            float alpha = Math.Clamp(o * (1 - Math.Abs(v.N)), 0f, 1f) * ca;
            positions.Add(v.P);
            colors.Add(new SKColor(cr, cg, cb, (byte)(alpha * 255 + 0.5f)));
        }
    }

    // Sutherland-Hodgman against N = c, keeping N >= c (above) or N <= c; at most n + 1 vertices out
    static int Clip(ReadOnlySpan<LineVertex> poly, int n, float c, bool above, Span<LineVertex> into)
    {
        if (float.IsInfinity(c)) { poly[..n].CopyTo(into); return n; }
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            bool ina = above ? a.N >= c : a.N <= c, inb = above ? b.N >= c : b.N <= c;
            if (ina) into[m++] = a;
            if (ina != inb && a.N != b.N)
            {
                float t = (c - a.N) / (b.N - a.N);
                if (t > 0 && t < 1)
                    into[m++] = new LineVertex(new SKPoint(a.P.X + (b.P.X - a.P.X) * t, a.P.Y + (b.P.Y - a.P.Y) * t), c);
            }
        }
        return m;
    }

    void AddLine(V[] vertices, int count, bool isPolygon, string join, string cap, double miterLimit, double roundLimit, double sharpCornerOffset)
    {
        int len = count;   // the array is the thread's, longer than the line
        while (len >= 2 && vertices[len - 1] == vertices[len - 2]) len--;
        int first = 0;
        while (first < len - 1 && vertices[first] == vertices[first + 1]) first++;
        if (len - first < (isPolygon ? 3 : 2)) return;
        if (join == "bevel") miterLimit = 1.05;

        V? currentVertex = null, prevVertex = null, nextVertex;
        V? prevNormal = null, nextNormal = null;
        if (isPolygon)
        {
            currentVertex = vertices[len - 2];
            nextNormal = (vertices[first] - currentVertex.Value).Unit.Perp;
        }

        for (int i = first; i < len; i++)
        {
            nextVertex = i == len - 1 ? (isPolygon ? vertices[first + 1] : null) : vertices[i + 1];
            if (nextVertex is { } nv0 && vertices[i] == nv0) continue;

            if (nextNormal != null) prevNormal = nextNormal;
            if (currentVertex != null) prevVertex = currentVertex;
            var cur = vertices[i];
            currentVertex = cur;

            nextNormal = nextVertex is { } nv ? (nv - cur).Unit.Perp : prevNormal;
            prevNormal ??= nextNormal;
            var pn = prevNormal!.Value;
            var nn = nextNormal!.Value;

            var joinNormal = pn + nn;
            if (joinNormal.X != 0 || joinNormal.Y != 0) joinNormal = joinNormal.Unit;

            double cosAngle = pn.X * nn.X + pn.Y * nn.Y;
            double cosHalfAngle = joinNormal.X * nn.X + joinNormal.Y * nn.Y;
            double miterLength = cosHalfAngle != 0 ? 1 / cosHalfAngle : double.PositiveInfinity;
            double approxAngle = 2 * Math.Sqrt(2 - 2 * cosHalfAngle);
            bool isSharpCorner = cosHalfAngle < CosHalfSharpCorner && prevVertex != null && nextVertex != null;
            bool lineTurnsLeft = pn.X * nn.Y - pn.Y * nn.X > 0;

            if (isSharpCorner && i > first)
            {
                double prevSegmentLength = cur.Dist(prevVertex!.Value);
                if (prevSegmentLength > 2 * sharpCornerOffset)
                {
                    var newPrev = cur - (cur - prevVertex.Value) * (sharpCornerOffset / prevSegmentLength);
                    AddCurrentVertex(newPrev, pn, 0, 0);
                    prevVertex = newPrev;
                }
            }

            bool middleVertex = prevVertex != null && nextVertex != null;
            string currentJoin = middleVertex ? join : isPolygon ? "butt" : cap;

            if (middleVertex && currentJoin == "round")
            {
                if (miterLength < roundLimit) currentJoin = "miter";
                else if (miterLength <= 2) currentJoin = "fakeround";
            }
            if (currentJoin == "miter" && miterLength > miterLimit) currentJoin = "bevel";
            if (currentJoin == "bevel")
            {
                if (miterLength > 2) currentJoin = "flipbevel";
                if (miterLength < miterLimit) currentJoin = "miter";
            }

            switch (currentJoin)
            {
                case "miter":
                    AddCurrentVertex(cur, joinNormal * miterLength, 0, 0);
                    break;
                case "flipbevel":
                {
                    if (miterLength > 100) joinNormal = nn * -1;
                    else
                    {
                        double bevelLength = miterLength * (pn + nn).Mag / (pn - nn).Mag;
                        joinNormal = joinNormal.Perp * (bevelLength * (lineTurnsLeft ? -1 : 1));
                    }
                    AddCurrentVertex(cur, joinNormal, 0, 0);
                    AddCurrentVertex(cur, joinNormal * -1, 0, 0);
                    break;
                }
                case "bevel":
                case "fakeround":
                {
                    double offset = -Math.Sqrt(miterLength * miterLength - 1);
                    double offsetA = lineTurnsLeft ? offset : 0, offsetB = lineTurnsLeft ? 0 : offset;
                    if (prevVertex != null) AddCurrentVertex(cur, pn, offsetA, offsetB);
                    if (currentJoin == "fakeround")
                    {
                        int n = (int)Math.Round(approxAngle * 180 / Math.PI / DegPerTriangle);
                        for (int m = 1; m < n; m++)
                        {
                            double t = (double)m / n;
                            if (t != 0.5)
                            {
                                double t2 = t - 0.5;
                                double A = 1.0904 + cosAngle * (-3.2452 + cosAngle * (3.55645 - cosAngle * 1.43519));
                                double B = 0.848013 + cosAngle * (-1.06021 + cosAngle * 0.215638);
                                t = t + t * t2 * (t - 1) * (A * t2 * t2 + B);
                            }
                            var extrude = ((nn - pn) * t + pn).Unit * (lineTurnsLeft ? -1 : 1);
                            AddHalfVertex(cur, extrude, false, lineTurnsLeft);
                        }
                    }
                    if (nextVertex != null) AddCurrentVertex(cur, nn, -offsetA, -offsetB);
                    break;
                }
                case "butt":
                    AddCurrentVertex(cur, joinNormal, 0, 0);
                    break;
                case "square":
                {
                    double offset = prevVertex != null ? 1 : -1;
                    AddCurrentVertex(cur, joinNormal, offset, offset);
                    break;
                }
                case "round":
                    if (prevVertex != null)
                    {
                        AddCurrentVertex(cur, pn, 0, 0);
                        AddCurrentVertex(cur, pn, 1, 1, round: true);
                    }
                    if (nextVertex != null)
                    {
                        AddCurrentVertex(cur, nn, -1, -1, round: true);
                        AddCurrentVertex(cur, nn, 0, 0);
                    }
                    break;
            }

            if (isSharpCorner && i < len - 1)
            {
                double nextSegmentLength = cur.Dist(nextVertex!.Value);
                if (nextSegmentLength > 2 * sharpCornerOffset)
                {
                    var newCurrent = cur + (nextVertex.Value - cur) * (sharpCornerOffset / nextSegmentLength);
                    AddCurrentVertex(newCurrent, nn, 0, 0);
                    currentVertex = newCurrent;
                }
            }
        }
    }

    void AddCurrentVertex(V p, V normal, double endLeft, double endRight, bool round = false)
    {
        var left = new V(normal.X + normal.Y * endLeft, normal.Y - normal.X * endLeft);
        var right = new V(-normal.X + normal.Y * endRight, -normal.Y - normal.X * endRight);
        AddHalfVertex(p, left, round, false);
        AddHalfVertex(p, right, round, true);
        if (round) HalfDisc(p, new V(normal.Y, -normal.X) * endLeft, normal);
    }

    void AddHalfVertex(V p, V extrude, bool round, bool up)
    {
        // stored as GL JS stores it: Math.round(63 x) + 128 in a Uint8Array, which wraps - a miter
        // longer than about twice the width folds back (line-miter-limit fixtures show the fold)
        static double Byte(double x) => ((((int)Math.Floor(ExtrudeScale * x + 0.5) + 128) & 0xFF) - 128) / ExtrudeScale;
        var at = new ExtrudedVertex(p.X, p.Y, Byte(extrude.X), Byte(extrude.Y), up ? 1 : -1);
        int e = _strip.Count;
        _strip.Add((at, round));
        if (_e1 >= 0 && _e2 >= 0)
        {
            var (a, ra) = _strip[_e1];
            var (b, rb) = _strip[_e2];
            // a triangle touching a round vertex is part of a cap's rectangle, which GL JS's shader
            // cuts to the half disc emitted in AddCurrentVertex
            if (!ra && !rb && !round) { _triangles.Add(a); _triangles.Add(at); _triangles.Add(b); }
        }
        if (up) _e2 = e; else _e1 = e;
    }

    // the half disc of radius outset at p, on the side `forward` points to
    void HalfDisc(V p, V forward, V normal)
    {
        // as many slices as keep the chord within a quarter pixel of the circle: 4 for a 3 px radius,
        // ~10 for 20 px (a fixed 16 made the round caps of minor roads most of an overlay's triangles)
        double step = 2 * Math.Acos(Math.Max(-1, 1 - 0.25 / Math.Max(_outset, 0.25)));
        int steps = Math.Clamp((int)Math.Ceiling(Math.PI / step), 2, 16);
        var center = new ExtrudedVertex(p.X, p.Y, 0, 0, 0);
        for (int k = 0; k < steps; k++)
        {
            double a0 = Math.PI * k / steps, a1 = Math.PI * (k + 1) / steps;
            ExtrudedVertex At(double a) { var q = normal * Math.Cos(a) + forward * Math.Sin(a); return new(p.X, p.Y, q.X, q.Y, 1); }
            _triangles.Add(center); _triangles.Add(At(a0)); _triangles.Add(At(a1));
        }
    }
}
