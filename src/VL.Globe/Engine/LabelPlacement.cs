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
    List<int>[] _cells = Array.Empty<List<int>>();
    int _columns, _rows;
    float _width, _height;

    /// <summary>One layer's labels to place: its tiles, each with its world matrix.</summary>
    internal readonly record struct Layer(int Index, IReadOnlyList<(GpuTile tile, Matrix world)> Tiles);

    /// <summary>Places every label of <paramref name="layers"/> (top of the style first) and writes each tile's per-vertex placement (0 or 1).</summary>
    public void Place(IReadOnlyList<Layer> layers, Matrix view, Matrix projection, float width, float height, float cameraToCenter, bool flat)
    {
        Reset(width, height);
        var viewProjection = view * projection;
        foreach (var layer in layers)
            foreach (var (tile, world) in layer.Tiles)
            {
                if (tile.Labels.Count == 0 || tile.Placed is not { } placed) continue;
                var worldViewProjection = world * viewProjection;
                var worldView = world * view;
                var up = tile.Bucket.Centre.ToVector3();
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

    void Reset(float width, float height)
    {
        _width = width; _height = height;
        float w = width + 2 * ViewportPadding, h = height + 2 * ViewportPadding;
        int columns = (int)Math.Ceiling(w / CellSize), rows = (int)Math.Ceiling(h / CellSize);
        if (columns != _columns || rows != _rows)
        {
            _columns = columns; _rows = rows;
            _cells = new List<int>[columns * rows];
            for (int i = 0; i < _cells.Length; i++) _cells[i] = new List<int>();
        }
        else foreach (var c in _cells) c.Clear();
        _boxes.Clear();
    }

    // GL JS grid_index.ts _convertToXCellCoord: clamped to the grid
    int Column(float x) => Math.Clamp((int)Math.Floor(x / CellSize), 0, _columns - 1);
    int Row(float y) => Math.Clamp((int)Math.Floor(y / CellSize), 0, _rows - 1);

    bool Hits(float x1, float y1, float x2, float y2)
    {
        for (int r = Row(y1); r <= Row(y2); r++)
            for (int c = Column(x1); c <= Column(x2); c++)
                foreach (int i in _cells[r * _columns + c])
                {
                    var b = _boxes[i];
                    if (x1 <= b.x2 + Epsilon && y1 <= b.y2 + Epsilon && x2 >= b.x1 - Epsilon && y2 >= b.y1 - Epsilon) return true;
                }
        return false;
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
