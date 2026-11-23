using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine;

/// <summary>A collision box in pixels around a symbol's anchor, at the layout's size, its padding included.</summary>
internal readonly record struct SymbolBox(float X1, float Y1, float X2, float Y2);

/// <summary>A point symbol's layout options that placement reads (GL JS's *-allow-overlap, *-ignore-placement, *-optional).</summary>
[Flags]
internal enum SymbolFlags
{
    None = 0, TextAllowOverlap = 1, TextIgnorePlacement = 2, IconAllowOverlap = 4, IconIgnorePlacement = 8,
    TextOptional = 16, IconOptional = 32,
    // GL JS placement.ts alwaysShowText / alwaysShowIcon: shown even where the grid could not take it
    AlwaysShowText = 64, AlwaysShowIcon = 128,
    // a point symbol's *-rotation-alignment map: its box turned to the map's east as projected
    TextRotateWithMap = 256, IconRotateWithMap = 512,
}

/// <summary>
/// A point symbol's collision data, built with its quads: the text's box and the icon's (either may be
/// absent), and the vertices of each to show or hide.
/// </summary>
internal readonly record struct LabelInfo(int Layer, Vector3 Anchor, SymbolBox? Text, int TextStart, int TextCount,
    SymbolBox? Icon, int IconStart, int IconCount, SymbolFlags Flags)
{
    /// <summary>GL JS's symbolInstance.key (the text hashed) and its anchor in GL JS's 8192-unit tile: what CrossTileSymbolIndex matches.</summary>
    public int Key { get; init; }
    public int AnchorX { get; init; }
    public int AnchorY { get; init; }
}

// Labels, milestone 2: GL JS's placement for point labels in the viewport (placement.ts,
// collision_index.ts, grid_index.ts). Layers are placed from the top of the style down, each layer's
// tiles finest first (GL JS sorts them by overscaled zoom), each tile's labels in their bucket's order;
// a label is placed when its box hits no box placed before it (boxes touching count, within 1e-6), or
// always with text-allow-overlap; a placed label's box goes into the grid unless text-ignore-placement.
// The anchor is projected; the box is the layout's, times GL JS's perspective ratio. Unplaceable:
// behind the camera or the globe, a perspective ratio under 0.6 (GL JS's cutoff), or entirely outside
// the grid (the view and 100 px).
// For the fades (LabelFades): what is placed is kept by cross-tile ID (CrossTileSymbolIndex) - a label
// already placed from a finer tile of the same layer is skipped, as GL JS's seenCrossTileIDs - and a
// placement can pause between tiles and go on in the next frame from the camera it began with (GL JS's
// PauseablePlacement / LayerPlacement).
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

    /// <summary>GL JS's JointPlacement: text and icon shown or not; SkipFade for a symbol placed just off the screen (no fade in when it comes in).</summary>
    internal readonly record struct Joint(bool Text, bool Icon, bool SkipFade);

    /// <summary>What the placement decided, by cross-tile ID.</summary>
    public readonly Dictionary<int, Joint> Placements = new();

    // the placement under way: its layers (tiles sorted), its camera, and where it paused
    List<Layer> _layers = new();
    Matrix _view, _viewProjection;
    float _cameraToCenter;
    bool _flat;
    int _layer, _tile;
    // the IDs placed in the layer under way, each with the tile that placed it: a wrapped copy of the
    // same tile (the same buffers drawn elsewhere) is not a duplicate
    readonly Dictionary<int, GpuTile> _seen = new();

    public bool Done { get; private set; } = true;

    /// <summary>Places every label of <paramref name="layers"/> (top of the style first) at once.</summary>
    public void Place(IReadOnlyList<Layer> layers, Matrix view, Matrix projection, float width, float height, float cameraToCenter, bool flat)
    {
        Begin(layers, view, projection, width, height, cameraToCenter, flat);
        Continue(null);
    }

    /// <summary>Starts a placement from this camera; <see cref="Continue"/> does the work.</summary>
    public void Begin(IReadOnlyList<Layer> layers, Matrix view, Matrix projection, float width, float height, float cameraToCenter, bool flat)
    {
        Reset(width, height);
        Placements.Clear();
        _seen.Clear();
        _layers = new List<Layer>(layers.Count);
        foreach (var l in layers)
        {
            // GL JS style.ts: a source's tiles finest first, then by tile
            var tiles = new List<(GpuTile tile, Matrix world)>(l.Tiles);
            tiles.Sort((a, b) => Order(a.tile, b.tile));
            _layers.Add(l with { Tiles = tiles });
        }
        _view = view; _viewProjection = view * projection;
        _cameraToCenter = cameraToCenter; _flat = flat;
        _layer = 0; _tile = 0;
        Done = false;
    }

    /// <summary>GL JS's order of a layer's tiles: overscaled zoom, finest first, then the canonical tile (OverscaledTileID.isLessThan).</summary>
    internal static int Order(GpuTile a, GpuTile b)
    {
        int c = b.Bucket.OverscaledZoom.CompareTo(a.Bucket.OverscaledZoom);
        if (c != 0) return c;
        var (ka, kb) = (a.Bucket.Key, b.Bucket.Key);
        return ka.Zoom != kb.Zoom ? ka.Zoom.CompareTo(kb.Zoom) : ka.X != kb.X ? ka.X.CompareTo(kb.X) : ka.Y.CompareTo(kb.Y);
    }

    /// <summary>Places tile after tile until done, or until <paramref name="shouldPause"/> says so after a tile; true when done.</summary>
    public bool Continue(Func<bool>? shouldPause)
    {
        while (_layer < _layers.Count)
        {
            var layer = _layers[_layer];
            while (_tile < layer.Tiles.Count)
            {
                var (tile, world) = layer.Tiles[_tile++];
                PlaceTile(layer, tile, world);
                if (shouldPause?.Invoke() == true) return false;
            }
            _layer++; _tile = 0;
            _seen.Clear();
        }
        Done = true;
        return true;
    }

    void PlaceTile(Layer layer, GpuTile tile, Matrix world)
    {
        if (tile.Labels.Count == 0 && tile.LineLabels.Count == 0) return;
        var worldViewProjection = world * _viewProjection;
        var worldView = world * _view;
        var up = tile.Bucket.Centre.ToVector3();
        // the layer's own labels: a tile holds every symbol layer's, one layer after another
        var (lineStart, lineCount) = tile.LineLabelRange(layer.Index);
        for (int i = lineStart; i < lineStart + lineCount; i++)
        {
            if (tile.LineLabelIds is not { } lineIds || Seen(lineIds[i], tile, out bool again)) continue;
            bool show = PlaceLine(tile.LineLabels[i], worldViewProjection, worldView, up, layer.Size, _cameraToCenter, _flat, out bool offscreen);
            Record(lineIds[i], again, new Joint(show, show, offscreen));
        }
        var (labelStart, labelCount) = tile.LabelRange(layer.Index);
        for (int i = labelStart; i < labelStart + labelCount; i++)
        {
            if (tile.LabelIds is not { } labelIds || Seen(labelIds[i], tile, out bool again)) continue;
            var (text, icon, offscreen) = PlaceOne(tile.Labels[i], worldViewProjection, worldView, up, _cameraToCenter, _flat);
            Record(labelIds[i], again, new Joint(text, icon, offscreen));
        }
    }

    // GL JS: a symbol placed already from another tile of this layer is a duplicate. The same tile again
    // (a wrapped copy): shown if any copy is
    bool Seen(int id, GpuTile tile, out bool again)
    {
        again = false;
        if (!_seen.TryGetValue(id, out var by)) { _seen[id] = tile; return false; }
        if (!ReferenceEquals(by, tile)) return true;
        again = true;
        return false;
    }

    void Record(int id, bool again, Joint joint)
    {
        if (again && Placements.TryGetValue(id, out var before))
            joint = new Joint(before.Text || joint.Text, before.Icon || joint.Icon, before.SkipFade && joint.SkipFade);
        Placements[id] = joint;
    }

    // GL JS collision_index.ts isOffscreen: outside the viewport itself (the grid reaches 100 px further)
    bool Offscreen(float x1, float y1, float x2, float y2)
        => x2 < ViewportPadding || x1 >= _width + ViewportPadding || y2 < ViewportPadding || y1 > _height + ViewportPadding;

    /// <summary>
    /// The glyphs of the labels along lines that are shown or fading, walked along their projected lines
    /// again - GL JS's updateLineLabels, every frame the camera moves, while placement runs at most every
    /// few hundred milliseconds. Nothing is shown or hidden by collision here.
    /// </summary>
    public void UpdateLines(IReadOnlyList<Layer> layers, Matrix view, Matrix projection, float width, float height, float cameraToCenter, bool flat)
    {
        _width = width; _height = height;
        var viewProjection = view * projection;
        float bufferX = 256 / width * 2 + 1, bufferY = 256 / height * 2 + 1;   // GL JS's clippingBuffer
        foreach (var layer in layers)
            foreach (var (tile, world) in layer.Tiles)
            {
                if (tile.LineLabels.Count == 0 || tile.Placed is not { } placed || tile.Dynamic is not { } dynamic) continue;
                var (lineStart, lineCount) = tile.LineLabelRange(layer.Index);
                if (lineCount == 0) continue;
                var worldViewProjection = world * viewProjection;
                for (int i = lineStart; i < lineStart + lineCount; i++)
                {
                    var line = tile.LineLabels[i];
                    if (placed[line.VertexStart] == 0) continue;   // hidden, not fading out (LabelFades.Pack)
                    var clip = Vector4.Transform(new Vector4(line.Anchor, 1), worldViewProjection);
                    int glyphVertices = line.GlyphOffsets.Length * 4;
                    if (clip.W <= 0) { if (LineGlyphs.HideIfShown(line, dynamic)) tile.MarkDynamic(line.VertexStart, glyphVertices); continue; }
                    // GL JS updateLineLabels' isVisible: an anchor past the viewport and 256 px round it is hidden,
                    // not walked (4g step 4: every shown line label was walked every frame the camera moved)
                    float cx = clip.X / clip.W, cy = clip.Y / clip.W;
                    if (cx < -bufferX || cx > bufferX || cy < -bufferY || cy > bufferY) { if (LineGlyphs.HideIfShown(line, dynamic)) tile.MarkDynamic(line.VertexStart, glyphVertices); continue; }
                    float perspective = 0.5f + 0.5f * cameraToCenter / clip.W;
                    float size = (line.FeatureSize > 0 ? line.FeatureSize : layer.Size) * perspective;
                    LineGlyphs.Place(line, worldViewProjection, _width, _height, size, dynamic, out _, out _);
                    tile.MarkDynamic(line.VertexStart, glyphVertices);
                }
            }
    }

    // GL JS placement.ts placeSymbol for a point symbol: the text's box and the icon's are each placed
    // (collision_index.ts placeCollisionBox), then combined - both or neither, unless one is optional -
    // and only then inserted; what is shown is the placement or the always-show rule, never on the far side
    (bool text, bool icon, bool offscreen) PlaceOne(in LabelInfo label, Matrix worldViewProjection, Matrix worldView, Vector3 centre, float cameraToCenter, bool flat)
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
        if (occluded) return (false, false, false);
        var f = label.Flags;
        float x = (clip.X / clip.W + 1) / 2 * _width + ViewportPadding;
        float y = (-clip.Y / clip.W + 1) / 2 * _height + ViewportPadding;
        float perspective = 0.5f + 0.5f * cameraToCenter / clip.W;
        bool placeText = false, placeIcon = false, offscreen = true;
        SymbolBox textBox = default, iconBox = default;
        // turned with the map: the map's east at the anchor, projected (GL JS projects a point one unit east)
        float eastCos = 1, eastSin = 0;
        if ((f & (SymbolFlags.TextRotateWithMap | SymbolFlags.IconRotateWithMap)) != 0)
            (eastCos, eastSin) = EastOnScreen(label.Anchor, centre, clip, worldViewProjection, flat, _width, _height);
        if (label.Text is { } t)
        {
            placeText = PlaceBox(t, allowOverlap: (f & SymbolFlags.TextAllowOverlap) != 0, turned: (f & SymbolFlags.TextRotateWithMap) != 0, box: out textBox, offscreen: out bool o);
            offscreen = o;
        }
        if (label.Icon is { } i)
        {
            placeIcon = PlaceBox(i, allowOverlap: (f & SymbolFlags.IconAllowOverlap) != 0, turned: (f & SymbolFlags.IconRotateWithMap) != 0, box: out iconBox, offscreen: out bool o);
            offscreen &= o;
        }

        bool iconWithoutText = (f & SymbolFlags.TextOptional) != 0 || label.TextCount == 0;
        bool textWithoutIcon = (f & SymbolFlags.IconOptional) != 0 || label.IconCount == 0;
        if (!iconWithoutText && !textWithoutIcon) placeIcon = placeText = placeIcon && placeText;
        else if (!textWithoutIcon) placeText = placeIcon && placeText;
        else if (!iconWithoutText) placeIcon &= placeText;

        if (placeText && (f & SymbolFlags.TextIgnorePlacement) == 0) Insert(textBox.X1, textBox.Y1, textBox.X2, textBox.Y2);
        if (placeIcon && (f & SymbolFlags.IconIgnorePlacement) == 0) Insert(iconBox.X1, iconBox.Y1, iconBox.X2, iconBox.Y2);
        return (placeText || (f & SymbolFlags.AlwaysShowText) != 0, placeIcon || (f & SymbolFlags.AlwaysShowIcon) != 0, offscreen);

        // placeable: inside the grid, not under the perspective cutoff, and hitting nothing (unless it may overlap)
        bool PlaceBox(SymbolBox b, bool turned, bool allowOverlap, out SymbolBox box, out bool offscreen)
        {
            if (!turned)
                box = new SymbolBox(x + b.X1 * perspective, y + b.Y1 * perspective, x + b.X2 * perspective, y + b.Y2 * perspective);
            else
            {
                // GL JS _projectCollisionBox: eight points round the box, turned east and south, their bounds
                float x1 = b.X1 * perspective, x2 = b.X2 * perspective, y1 = b.Y1 * perspective, y2 = b.Y2 * perspective;
                float xh = (x1 + x2) / 2, yh = (y1 + y2) / 2;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var (ox, oy) in new[] { (x1, y1), (xh, y1), (x2, y1), (x2, yh), (x2, y2), (xh, y2), (x1, y2), (x1, yh) })
                {
                    float px = x + eastCos * ox - eastSin * oy, py = y + eastSin * ox + eastCos * oy;
                    minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                }
                box = new SymbolBox(minX, minY, maxX, maxY);
            }
            bool unplaceable = perspective < PerspectiveCutoff
                || !(box.X2 >= 0 && box.X1 < _width + 2 * ViewportPadding && box.Y2 >= 0 && box.Y1 < _height + 2 * ViewportPadding);
            offscreen = !unplaceable && Offscreen(box.X1, box.Y1, box.X2, box.Y2);
            return !unplaceable && (allowOverlap || !Hits(box.X1, box.Y1, box.X2, box.Y2));
        }
    }

    /// <summary>
    /// The map's east at an anchor as a direction in screen pixels (y down): cos and sin of its angle.
    /// East is +x on the Mercator plane, the sphere's tangent round its axis (y north) on the globe; its
    /// projection is differentiated rather than a point stepped east (GL JS steps one tile unit), so no
    /// step size meets float precision. GlobeSymbol.sdsl turns the quads by the same angle.
    /// </summary>
    internal static (float cos, float sin) EastOnScreen(Vector3 anchor, Vector3 centre, Vector4 clip, Matrix worldViewProjection, bool flat, float width, float height)
    {
        Vector3 east;
        if (flat) east = Vector3.UnitX;
        else
        {
            var up = centre + anchor; up.Normalize();
            east = Vector3.Cross(Vector3.UnitY, up);
            if (east.LengthSquared() < 1e-12f) return (1, 0);   // at a pole east is everywhere
            east.Normalize();
        }
        var d = Vector4.Transform(new Vector4(east, 0), worldViewProjection);
        float w2 = clip.W * clip.W;
        float dx = width / 2 * (d.X * clip.W - clip.X * d.W) / w2;
        float dy = -height / 2 * (d.Y * clip.W - clip.Y * d.W) / w2;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        return length < 1e-20f ? (1, 0) : (dx / length, dy / length);
    }

    // GL JS: the glyphs placed along the projected line (projection.ts), then placeCollisionCircles - a
    // row of circles along the label's path, radius half the circle diameter times the perspective
    // ratio plus text-padding, 2.5 radii apart, a quarter radius in from each end
    bool PlaceLine(LineLabel label, Matrix worldViewProjection, Matrix worldView, Vector3 centre, float layerSize, float cameraToCenter, bool flat, out bool offscreen)
    {
        offscreen = false;
        var clip = Vector4.Transform(new Vector4(label.Anchor, 1), worldViewProjection);
        bool occluded = clip.W <= 0;
        if (!flat && !occluded)
        {
            var p = Vector3.TransformCoordinate(label.Anchor, worldView);
            var n = centre + label.Anchor; n.Normalize();
            occluded = Vector3.Dot(Vector3.TransformNormal(n, worldView), -p) <= 0;
        }
        if (occluded) return false;
        float perspective = 0.5f + 0.5f * cameraToCenter / clip.W;
        float size = (label.FeatureSize > 0 ? label.FeatureSize : layerSize) * perspective;
        if (!LineGlyphs.PlaceEnds(label, worldViewProjection, _width, _height, size, out var first, out var last)) return false;
        if (label.IconBox is { } icon)
        {
            // GL JS placeCollisionBox for the icon's box round the projected anchor
            float x = (clip.X / clip.W + 1) / 2 * _width + ViewportPadding, y = (-clip.Y / clip.W + 1) / 2 * _height + ViewportPadding;
            var b = new SymbolBox(x + icon.X1 * perspective, y + icon.Y1 * perspective, x + icon.X2 * perspective, y + icon.Y2 * perspective);
            bool unplaceable = perspective < PerspectiveCutoff
                || !(b.X2 >= 0 && b.X1 < _width + 2 * ViewportPadding && b.Y2 >= 0 && b.Y1 < _height + 2 * ViewportPadding);
            if (unplaceable) return label.AlwaysOverlap;
            offscreen = Offscreen(b.X1, b.Y1, b.X2, b.Y2);
            if (!label.AlwaysOverlap && Hits(b.X1, b.Y1, b.X2, b.Y2)) return false;
            if (!label.IgnorePlacement) Insert(b.X1, b.Y1, b.X2, b.Y2);
            return true;
        }
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
        // GL JS placeCollisionCircles: offscreen when every circle is
        offscreen = true;
        foreach (var (x, y) in circles) offscreen &= Offscreen(x - radius, y - radius, x + radius, y + radius);
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
