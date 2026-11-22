using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine;

/// <summary>
/// A label's collision data, built with its glyph quads: its box in pixels around the anchor (at the
/// layout's text size, text-padding included) and the vertices to show or hide.
/// </summary>
internal readonly record struct LabelInfo(int Layer, Vector3 Anchor, float X1, float Y1, float X2, float Y2,
    bool AlwaysOverlap, bool IgnorePlacement, int VertexStart, int VertexCount);

// Labels, milestone 2: GL JS's placement for point labels in the viewport (placement.ts,
// collision_index.ts, grid_index.ts), every frame from the camera. Layers are placed from the top of
// the style down, each tile's labels in their bucket's order; a label is placed when its box hits
// no box placed before it (boxes touching count, within 1e-6), or always with text-allow-overlap; a
// placed label's box goes into the grid unless text-ignore-placement. The anchor is projected; the
// box is the layout's, times GL JS's perspective ratio. Unplaceable: behind the camera or the globe,
// a perspective ratio under 0.6 (GL JS's cutoff), or entirely outside the grid (the view and 100 px).
internal sealed class LabelPlacement
{
    const float ViewportPadding = 100, CellSize = 25, PerspectiveCutoff = 0.6f, Epsilon = 1e-6f;

    readonly List<(float x1, float y1, float x2, float y2)> _boxes = new();
    readonly List<(float x, float y, float r)> _circles = new();   // labels along lines (GL JS's collision circles)
    List<int>[] _circleCells = Array.Empty<List<int>>();
    List<int>[] _cells = Array.Empty<List<int>>();
    int _columns, _rows;
    float _width, _height;

    /// <summary>One layer's labels to place: its tiles, each with its world matrix, and its text-size at this zoom.</summary>
    internal readonly record struct Layer(int Index, IReadOnlyList<(GpuTile tile, Matrix world)> Tiles, float Size = 16);

    /// <summary>Places every label of <paramref name="layers"/> (top of the style first) and writes each tile's per-vertex placement (0 or 1).</summary>
    public void Place(IReadOnlyList<Layer> layers, Matrix view, Matrix projection, float width, float height, float cameraToCenter, bool flat)
    {
        Reset(width, height);
        var viewProjection = view * projection;
        foreach (var layer in layers)
            foreach (var (tile, world) in layer.Tiles)
            {
                if (tile.Labels.Count == 0 && tile.LineLabels.Count == 0 || tile.Placed is not { } placed) continue;
                var worldViewProjection = world * viewProjection;
                var worldView = world * view;
                var up = tile.Bucket.Centre.ToVector3();
                foreach (var line in tile.LineLabels)
                {
                    if (line.Layer != layer.Index) continue;
                    bool show = PlaceLine(line, tile, worldViewProjection, worldView, up, layer.Size, cameraToCenter, flat);
                    float value = show ? 1 : 0;
                    for (int v = line.VertexStart; v < line.VertexStart + line.GlyphOffsets.Length * 4; v++)
                        if (placed[v] != value) { placed[v] = value; tile.PlacedDirty = true; }
                }
                foreach (var label in tile.Labels)
                {
                    if (label.Layer != layer.Index) continue;
                    bool show = PlaceOne(label, worldViewProjection, worldView, up, cameraToCenter, flat);
                    float value = show ? 1 : 0;
                    for (int v = label.VertexStart; v < label.VertexStart + label.VertexCount; v++)
                        if (placed[v] != value) { placed[v] = value; tile.PlacedDirty = true; }
                }
            }
    }

    bool PlaceOne(in LabelInfo label, Matrix worldViewProjection, Matrix worldView, Vector3 centre, float cameraToCenter, bool flat)
    {
        var clip = Vector4.Transform(new Vector4(label.Anchor, 1), worldViewProjection);
        bool occluded = clip.W <= 0;
        if (!flat && !occluded)
        {
            // the far side of the globe, as GlobeSymbol.sdsl hides it
            var p = Vector3.TransformCoordinate(label.Anchor, worldView);
            var n = centre + label.Anchor; n.Normalize();
            var up = Vector3.TransformNormal(n, worldView);
            occluded = Vector3.Dot(up, -p) <= 0;
        }
        if (occluded) return false;
        float x = (clip.X / clip.W + 1) / 2 * _width + ViewportPadding;
        float y = (-clip.Y / clip.W + 1) / 2 * _height + ViewportPadding;
        float perspective = 0.5f + 0.5f * cameraToCenter / clip.W;
        float x1 = x + label.X1 * perspective, y1 = y + label.Y1 * perspective;
        float x2 = x + label.X2 * perspective, y2 = y + label.Y2 * perspective;
        bool unplaceable = perspective < PerspectiveCutoff
            || !(x2 >= 0 && x1 < _width + 2 * ViewportPadding && y2 >= 0 && y1 < _height + 2 * ViewportPadding);
        // GL JS shows a label that may always overlap even when it could not be placed in the grid
        if (unplaceable) return label.AlwaysOverlap;
        if (!label.AlwaysOverlap && Hits(x1, y1, x2, y2)) return false;
        if (!label.IgnorePlacement) Insert(x1, y1, x2, y2);
        return true;
    }

    // GL JS: the glyphs placed along the projected line (projection.ts), then placeCollisionCircles - a
    // row of circles along the label's path, radius half the circle diameter times the perspective
    // ratio plus text-padding, 2.5 radii apart, a quarter radius in from each end
    bool PlaceLine(LineLabel label, GpuTile tile, Matrix worldViewProjection, Matrix worldView, Vector3 centre, float layerSize, float cameraToCenter, bool flat)
    {
        var dynamic = tile.Dynamic!;
        tile.DynamicDirty = true;
        var clip = Vector4.Transform(new Vector4(label.Anchor, 1), worldViewProjection);
        bool occluded = clip.W <= 0;
        if (!flat && !occluded)
        {
            var p = Vector3.TransformCoordinate(label.Anchor, worldView);
            var n = centre + label.Anchor; n.Normalize();
            occluded = Vector3.Dot(Vector3.TransformNormal(n, worldView), -p) <= 0;
        }
        if (occluded) return LineGlyphs.Hide(label, dynamic);
        float perspective = 0.5f + 0.5f * cameraToCenter / clip.W;
        float size = (label.FeatureSize > 0 ? label.FeatureSize : layerSize) * perspective;
        if (!LineGlyphs.Place(label, worldViewProjection, _width, _height, size, dynamic, out var first, out var last)) return false;
        if (perspective < PerspectiveCutoff) return label.AlwaysOverlap;

        var path = new List<Vector2>();
        for (int i = first.Count - 1; i >= 1; i--) path.Add(first[i]);
        for (int i = 1; i < last.Count; i++) path.Add(last[i]);
        float radius = label.CircleDiameter * 0.5f * perspective + label.Padding;
        var circles = Circles(path, radius);
        bool inGrid = false;
        foreach (var (x, y) in circles)
            inGrid |= x + radius >= 0 && x - radius < _width + 2 * ViewportPadding && y + radius >= 0 && y - radius < _height + 2 * ViewportPadding;
        if (!inGrid) return label.AlwaysOverlap;
        if (!label.AlwaysOverlap)
            foreach (var (x, y) in circles)
                if (HitsCircle(x, y, radius)) return false;
        if (!label.IgnorePlacement)
            foreach (var (x, y) in circles) InsertCircle(x, y, radius);
        return true;
    }

    // GL JS path_interpolator.ts: evenly along the path, padded at both ends; in grid coordinates
    List<(float x, float y)> Circles(List<Vector2> path, float radius)
    {
        var result = new List<(float, float)>();
        if (path.Count == 0) return result;
        var lengths = new float[path.Count];
        for (int i = 1; i < path.Count; i++) lengths[i] = lengths[i - 1] + Vector2.Distance(path[i - 1], path[i]);
        float length = lengths[^1], padding = Math.Min(radius * 0.25f, length / 2), padded = length - 2 * padding;
        int count = length <= 0.5f * radius ? 1 : (int)Math.Ceiling(padded / (radius * 2.5f)) + 1;
        for (int c = 0; c < count; c++)
        {
            float t = c / (float)Math.Max(count - 1, 1);
            float at = padding + t * padded;
            int i = 1;
            while (i < path.Count - 1 && lengths[i] < at) i++;
            float span = lengths[i] - lengths[i - 1];
            var p = span > 0 ? Vector2.Lerp(path[i - 1], path[i], (at - lengths[i - 1]) / span) : path[i];
            result.Add((p.X + ViewportPadding, p.Y + ViewportPadding));
        }
        return result;
    }

    void Reset(float width, float height)
    {
        _width = width; _height = height;
        float w = width + 2 * ViewportPadding, h = height + 2 * ViewportPadding;
        int columns = (int)Math.Ceiling(w / CellSize), rows = (int)Math.Ceiling(h / CellSize);
        if (columns != _columns || rows != _rows)
        {
            _columns = columns; _rows = rows;
            _cells = new List<int>[columns * rows];
            _circleCells = new List<int>[columns * rows];
            for (int i = 0; i < _cells.Length; i++) { _cells[i] = new List<int>(); _circleCells[i] = new List<int>(); }
        }
        else { foreach (var c in _cells) c.Clear(); foreach (var c in _circleCells) c.Clear(); }
        _boxes.Clear();
        _circles.Clear();
    }

    // GL JS grid_index.ts _convertToXCellCoord: clamped to the grid
    int Column(float x) => Math.Clamp((int)Math.Floor(x / CellSize), 0, _columns - 1);
    int Row(float y) => Math.Clamp((int)Math.Floor(y / CellSize), 0, _rows - 1);

    bool Hits(float x1, float y1, float x2, float y2)
    {
        for (int r = Row(y1); r <= Row(y2); r++)
            for (int c = Column(x1); c <= Column(x2); c++)
            {
                foreach (int i in _cells[r * _columns + c])
                {
                    var b = _boxes[i];
                    if (x1 <= b.x2 + Epsilon && y1 <= b.y2 + Epsilon && x2 >= b.x1 - Epsilon && y2 >= b.y1 - Epsilon) return true;
                }
                foreach (int i in _circleCells[r * _columns + c])
                    if (CircleAndBox(_circles[i], x1, y1, x2, y2)) return true;
            }
        return false;
    }

    // GL JS grid_index.ts hitTestCircle: against boxes (circleAndRectCollide) and circles (circlesCollide)
    bool HitsCircle(float x, float y, float radius)
    {
        for (int r = Row(y - radius); r <= Row(y + radius); r++)
            for (int c = Column(x - radius); c <= Column(x + radius); c++)
            {
                foreach (int i in _cells[r * _columns + c])
                {
                    var b = _boxes[i];
                    if (CircleAndBox((x, y, radius), b.x1, b.y1, b.x2, b.y2)) return true;
                }
                foreach (int i in _circleCells[r * _columns + c])
                {
                    var o = _circles[i];
                    float dx = o.x - x, dy = o.y - y, rr = o.r + radius;
                    if (dx * dx + dy * dy <= rr * rr) return true;
                }
            }
        return false;
    }

    static bool CircleAndBox((float x, float y, float r) circle, float x1, float y1, float x2, float y2)
    {
        float halfW = (x2 - x1) / 2, halfH = (y2 - y1) / 2;
        float dx = Math.Abs(circle.x - (x1 + halfW)), dy = Math.Abs(circle.y - (y1 + halfH));
        if (dx > halfW + circle.r || dy > halfH + circle.r) return false;
        if (dx <= halfW || dy <= halfH) return true;
        float cx = dx - halfW, cy = dy - halfH;
        return cx * cx + cy * cy <= circle.r * circle.r;
    }

    void InsertCircle(float x, float y, float radius)
    {
        int i = _circles.Count;
        _circles.Add((x, y, radius));
        for (int r = Row(y - radius); r <= Row(y + radius); r++)
            for (int c = Column(x - radius); c <= Column(x + radius); c++)
                _circleCells[r * _columns + c].Add(i);
    }

    void Insert(float x1, float y1, float x2, float y2)
    {
        int i = _boxes.Count;
        _boxes.Add((x1, y1, x2, y2));
        for (int r = Row(y1); r <= Row(y2); r++)
            for (int c = Column(x1); c <= Column(x2); c++)
                _cells[r * _columns + c].Add(i);
    }
}
