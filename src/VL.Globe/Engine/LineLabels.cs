using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine;

/// <summary>
/// A label along a line (symbol-placement line or line-center), as built: its anchor and the line it
/// follows (on the unit sphere or the plane, relative to the bucket's centre), each glyph's offset
/// along the line in 24-px units, and its vertices - 4 a glyph, in glyph order.
/// </summary>
internal sealed class LineLabel
{
    public required int Layer;
    public required Vector3 Anchor;
    public required Vector3[] Line;
    public required int Segment;              // the anchor lies on Line[Segment]..Line[Segment + 1]
    public required float[] GlyphOffsets;     // GL JS's glyphOffsetArray: x + half the advance, at 24 px
    public required int VertexStart;
    public float LineOffsetX, LineOffsetY;    // text-offset at 24 px (GL JS's lineOffsetX/Y)
    public bool KeepUpright = true;
    // GL JS rotateToLine: each glyph turned to its segment (text-rotation-alignment map, or auto on a
    // line); viewport-glyph places the glyphs along the line but keeps each upright on the screen
    public bool RotateToLine = true;
    public float CircleDiameter;              // GL JS's collisionCircleDiameter, px at the layout's text size
    public float Padding;                     // text-padding, px
    public float FeatureSize;                 // the feature's text-size where it depends on it, else 0
    public bool AlwaysOverlap, IgnorePlacement;
    public int Key, AnchorX, AnchorY;         // CrossTileSymbolIndex's key (the text hashed) and anchor (GL JS units)
    // an icon along a line (icon-rotation-alignment map): its quads are "glyphs" at offset 0, turned to
    // the line at the anchor; it collides as GL JS's icon box round the anchor, not as circles
    public SymbolBox? IconBox;
}

// GL JS's projection.ts for labels along lines in the viewport (text-pitch-alignment viewport; seen from
// straight above, map-pitched labels come out the same): every frame each glyph is walked along the
// line projected to the screen, from the anchor, by its offset times the font scale, and turned to the
// segment it lands on. Labels reading backwards are placed again flipped (text-keep-upright); a label
// whose glyphs run off the line is hidden. The placed glyphs go to the GPU per vertex: x, y in pixels,
// the angle, and 1 (along a line) - or -1, hidden.
internal static class LineGlyphs
{
    internal readonly record struct Placed(float X, float Y, float Angle, List<Vector2> Path);

    /// <summary>
    /// Places a label's glyphs at <paramref name="fontSize"/> (px, the perspective ratio applied) and writes
    /// them into <paramref name="dynamic"/>; the first and last glyph's paths for its collision circles.
    /// False when the label is hidden (its glyphs ran off the line, or part of it is behind the camera).
    /// </summary>
    public static bool Place(LineLabel label, Matrix worldViewProjection, float width, float height, float fontSize,
        Vector4[] dynamic, out List<Vector2> pathFirst, out List<Vector2> pathLast)
        => Place(label, worldViewProjection, width, height, fontSize, dynamic, true, out pathFirst, out pathLast);

    /// <summary>
    /// GL JS placeFirstAndLastGlyph: only the first and the last glyph, for the collision circles; nothing
    /// written. The glyphs between lie on the segments those two walked, so they fit when both do.
    /// </summary>
    public static bool PlaceEnds(LineLabel label, Matrix worldViewProjection, float width, float height, float fontSize,
        out List<Vector2> pathFirst, out List<Vector2> pathLast)
        => Place(label, worldViewProjection, width, height, fontSize, null, false, out pathFirst, out pathLast);

    static bool Place(LineLabel label, Matrix worldViewProjection, float width, float height, float fontSize,
        Vector4[]? dynamic, bool all, out List<Vector2> pathFirst, out List<Vector2> pathLast)
    {
        pathFirst = pathLast = null!;
        var projected = new Vector2?[label.Line.Length];
        Vector2? Project(int i)
        {
            if (projected[i] is { } known) return known;
            var c = Vector4.Transform(new Vector4(label.Line[i], 1), worldViewProjection);
            if (c.W <= 0) return null;   // GL JS truncates the segment at the camera plane; hidden here
            var p = new Vector2((c.X / c.W * 0.5f + 0.5f) * width, (-c.Y / c.W * 0.5f + 0.5f) * height);
            projected[i] = p;
            return p;
        }
        var a = Vector4.Transform(new Vector4(label.Anchor, 1), worldViewProjection);
        if (a.W <= 0) return Hide(label, dynamic);
        var anchor = new Vector2((a.X / a.W * 0.5f + 0.5f) * width, (-a.Y / a.W * 0.5f + 0.5f) * height);

        float fontScale = fontSize / 24;
        float lineOffsetX = label.LineOffsetX * fontScale, lineOffsetY = label.LineOffsetY * fontScale;
        if (lineOffsetY != 0) return Hide(label, dynamic);   // refused while building (SymbolLayout); never here

        // GL JS placeGlyphsAlongLine: unflipped first; flipped when it reads backwards
        var placed = PlaceAll(false);
        if (placed is null) return Hide(label, dynamic);
        if (placed.Value.flip)
        {
            placed = PlaceAll(true);
            if (placed is null || placed.Value.flip) return Hide(label, dynamic);
        }
        var glyphs = placed.Value.glyphs;
        if (dynamic != null)
        for (int g = 0; g < glyphs.Count; g++)
            for (int k = 0; k < 4; k++)
                dynamic[label.VertexStart + 4 * g + k] = new Vector4(glyphs[g].X, glyphs[g].Y, glyphs[g].Angle, 1);
        pathFirst = glyphs[0].Path;
        pathLast = glyphs[^1].Path;
        return true;

        (List<Placed> glyphs, bool flip)? PlaceAll(bool flip)
        {
            int n = label.GlyphOffsets.Length;
            var first = Glyph(fontScale * label.GlyphOffsets[0], flip);
            var last = n > 1 ? Glyph(fontScale * label.GlyphOffsets[n - 1], flip) : first;
            if (first is null || last is null) return null;
            // GL JS requiresOrientationChange, horizontal text: the first glyph right of the last
            if (label.KeepUpright && !flip && n > 1 && first.Value.X > last.Value.X) return (new List<Placed>(), true);
            if (label.KeepUpright && !flip && n == 1)
            {
                // a single glyph: the anchor's segment's direction decides
                var b = Project(Math.Min(label.Segment + 1, label.Line.Length - 1));
                if (b is { } end && anchor.X > end.X) return (new List<Placed>(), true);
            }
            var list = new List<Placed> { first.Value };
            for (int g = 1; all && g < n - 1; g++)
            {
                var p = Glyph(fontScale * label.GlyphOffsets[g], flip);
                if (p is null) return null;
                list.Add(p.Value);
            }
            if (n > 1) list.Add(last.Value);
            return (list, false);
        }

        // GL JS placeGlyphAlongLine, lineOffsetY 0
        Placed? Glyph(float offsetX, bool flip)
        {
            float combined = flip ? offsetX - lineOffsetX : offsetX + lineOffsetX;
            int direction = combined > 0 ? 1 : -1;
            float angle = 0;
            if (flip) { direction *= -1; angle = MathF.PI; }
            if (direction < 0) angle += MathF.PI;
            int index = direction > 0 ? label.Segment : label.Segment + 1;
            var current = anchor;
            var previous = anchor;
            float fromAnchor = 0, segmentLength = 0, abs = Math.Abs(combined);
            var path = new List<Vector2>();
            Vector2 segment = default;
            while (fromAnchor + segmentLength <= abs)
            {
                index += direction;
                if (index < 0 || index >= label.Line.Length) return null;
                fromAnchor += segmentLength;
                previous = current;
                if (Project(index) is not { } next) return null;
                current = next;
                path.Add(previous);
                segment = current - previous;
                segmentLength = segment.Length();
            }
            float t = (abs - fromAnchor) / segmentLength;
            var point = segment * t + previous;
            path.Add(point);
            return new Placed(point.X, point.Y, label.RotateToLine ? angle + MathF.Atan2(current.Y - previous.Y, current.X - previous.X) : 0, path);
        }
    }

    internal static bool Hide(LineLabel label, Vector4[]? dynamic)
    {
        if (dynamic is null) return false;
        for (int v = 0; v < label.GlyphOffsets.Length * 4; v++) dynamic[label.VertexStart + v] = new Vector4(0, 0, 0, -1);
        return false;
    }
}
