using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe.Engine;

/// <summary>A bucket on the GPU: its buffers and its ranges by layer.</summary>
internal sealed class GpuTile
{
    public required VectorBucket Bucket;
    public Buffer? Vertices, Indices;   // null: a tile with nothing in it
    public Buffer? SymbolVertices, SymbolIndices;   // the labels' glyph quads, null when it has none
    public Buffer? CircleVertices, CircleIndices;   // circle layers' quads, null when it has none
    // labels, milestone 2: each label's collision data, and per symbol vertex its fade state
    // (LabelFades writes Placed, LabelFades.Pack; the renderer uploads it to PlacedBuffer when dirty)
    public IReadOnlyList<LabelInfo> Labels = Array.Empty<LabelInfo>();
    public float[]? Placed;
    public Buffer? PlacedBuffer;
    public bool PlacedDirty;
    // labels along lines: per symbol vertex, the glyph placed this frame (x, y px, angle, 1) - or (0, 0,
    // 0, 0) for a point label's vertex, (.., -1) hidden (LabelPlacement / LineGlyphs, every frame)
    public IReadOnlyList<LineLabel> LineLabels = Array.Empty<LineLabel>();
    // each label's cross-tile ID (CrossTileSymbolIndex), 0 until the index has seen the tile
    public int[]? LabelIds, LineLabelIds;

    // each symbol layer's labels are one run in Labels and LineLabels (built layer by layer): found once
    Dictionary<int, (int Start, int Count)>? _labelRanges, _lineLabelRanges;
    public (int Start, int Count) LabelRange(int layer) => (_labelRanges ??= Ranges(Labels, l => l.Layer)).GetValueOrDefault(layer);
    public (int Start, int Count) LineLabelRange(int layer) => (_lineLabelRanges ??= Ranges(LineLabels, l => l.Layer)).GetValueOrDefault(layer);
    static Dictionary<int, (int, int)> Ranges<T>(IReadOnlyList<T> list, Func<T, int> layerOf)
    {
        var ranges = new Dictionary<int, (int Start, int Count)>();
        for (int i = 0; i < list.Count; i++)
        {
            int layer = layerOf(list[i]);
            ranges[layer] = ranges.TryGetValue(layer, out var r) ? (r.Start, i - r.Start + 1) : (i, 1);
        }
        return ranges;
    }
    public Vector4[]? Dynamic;
    public Buffer? DynamicBuffer;
    public bool DynamicDirty;
    // the part of Dynamic written since its last upload, vertices [from, to): uploaded alone
    public int DynamicFrom = int.MaxValue, DynamicTo;

    public void MarkDynamic(int start, int count)
    {
        DynamicDirty = true;
        DynamicFrom = Math.Min(DynamicFrom, start);
        DynamicTo = Math.Max(DynamicTo, start + count);
    }
    public required Dictionary<int, List<VectorRange>> ByLayer;   // -1: the background grid
    public long Bytes => (Vertices?.SizeInBytes ?? 0) + (Indices?.SizeInBytes ?? 0) + (SymbolVertices?.SizeInBytes ?? 0) + (SymbolIndices?.SizeInBytes ?? 0)
        + (CircleVertices?.SizeInBytes ?? 0) + (CircleIndices?.SizeInBytes ?? 0);
    public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); SymbolVertices?.Dispose(); SymbolIndices?.Dispose(); PlacedBuffer?.Dispose(); DynamicBuffer?.Dispose(); CircleVertices?.Dispose(); CircleIndices?.Dispose(); }

    public static GpuTile Empty(VectorBucket bucket) => new() { Bucket = bucket, ByLayer = new() };

    /// <summary>Uploads a bucket's arrays and gives them back to the pool; the ranges stay.</summary>
    public static GpuTile Upload(GraphicsDevice device, VectorBucket bucket)
    {
        var byLayer = new Dictionary<int, List<VectorRange>>();
        foreach (var r in bucket.Ranges)
        {
            if (!byLayer.TryGetValue(r.Layer, out var list)) byLayer[r.Layer] = list = new List<VectorRange>();
            list.Add(r);
        }
        Buffer? symbolVertices = null, symbolIndices = null, placedBuffer = null, dynamicBuffer = null;
        float[]? placed = null;
        Vector4[]? dynamic = null;
        if (bucket.SymbolIndices.Length > 0)
        {
            // every label hidden until LabelFades gives it its state (in the frame the tile is first drawn)
            placed = new float[bucket.SymbolVertices.Length];
            placedBuffer = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(placed.AsSpan()), sizeof(float), BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Default);
            dynamic = new Vector4[bucket.SymbolVertices.Length];
            dynamicBuffer = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(dynamic.AsSpan()), 16, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Default);
            symbolVertices = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.SymbolVertices.AsSpan()), SymbolVertex.Size, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable);
            symbolIndices = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.SymbolIndices.AsSpan()), sizeof(uint), BufferFlags.IndexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable);
            bucket.SymbolVertices = Array.Empty<SymbolVertex>();
            bucket.SymbolIndices = Array.Empty<uint>();
        }
        Buffer? circleVertices = null, circleIndices = null;
        if (bucket.CircleIndices.Length > 0)
        {
            circleVertices = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.CircleVertices.AsSpan()), CircleVertex.Size, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable);
            circleIndices = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.CircleIndices.AsSpan()), sizeof(uint), BufferFlags.IndexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable);
            bucket.CircleVertices = Array.Empty<CircleVertex>();
            bucket.CircleIndices = Array.Empty<uint>();
        }
        if (bucket.IndexCount == 0)
        {
            bucket.Release();
            return new GpuTile { Bucket = bucket, ByLayer = byLayer, SymbolVertices = symbolVertices, SymbolIndices = symbolIndices,
                CircleVertices = circleVertices, CircleIndices = circleIndices,
                Labels = bucket.Labels, Placed = placed, PlacedBuffer = placedBuffer,
                LineLabels = bucket.LineLabels, Dynamic = dynamic, DynamicBuffer = dynamicBuffer };
        }
        var vertices = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.VertexSpan);
        var indices = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.IndexSpan);
        var tile = new GpuTile
        {
            Bucket = bucket,
            ByLayer = byLayer,
            Vertices = Buffer.New(device, vertices, VectorVertex.Size, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable),
            Indices = Buffer.New(device, indices, sizeof(ushort), BufferFlags.IndexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable),
            SymbolVertices = symbolVertices,
            SymbolIndices = symbolIndices,
            CircleVertices = circleVertices,
            CircleIndices = circleIndices,
            Labels = bucket.Labels,
            Placed = placed,
            PlacedBuffer = placedBuffer,
            LineLabels = bucket.LineLabels,
            Dynamic = dynamic,
            DynamicBuffer = dynamicBuffer,
        };
        bucket.Release();   // the arrays back to the pool: on the GPU now
        return tile;
    }
}

// One style layer's draw items over a set of tiles - shared by the globe (GpuVectorTiles) and
// MapLibre's render tests drawn by the GPU path (MapLibreGpuRenderFacts), so both draw alike.
internal static class VectorDrawList
{
    // world: Where a tile is drawn: scale and translation, camera-relative.
    // linearize: The colours go linear in the shader (vvvv); false keeps them sRGB, as GL JS blends.
    public static void AddLayer(List<VectorRenderer.Item> items, GpuStyleLayer layer, StyleColor color, double width, StyleColor outline,
        IReadOnlyList<(GpuTile tile, Vector4 clip)> tiles, Func<GpuTile, Matrix> world, bool linearize)
    {
        int rangeLayer = layer.Type == "background" ? -1 : layer.Index;
        // a fill-extrusion layer: every tile's depth first, then every tile's colour
        int passes = layer.IsExtrusion ? 2 : 1;
        for (int pass = 0; pass < passes; pass++)
            foreach (var (tile, clip) in tiles)
            {
                if (tile.Vertices is null || !tile.ByLayer.TryGetValue(rangeLayer, out var ranges)) continue;
                var w = world(tile);
                var centre = tile.Bucket.Centre.ToVector3();
                foreach (var r in ranges)
                {
                    if (r.Kind is RangeKind.Text or RangeKind.Icon or RangeKind.Circle) continue;   // drawn by AddSymbolLayer, AddCircleLayer
                    var (c, half, line) = r.Kind switch
                    {
                        RangeKind.Line => (color, width / 2, true),
                        RangeKind.Outline => (outline, 0.0, false),
                        _ => (color, 0.0, false),
                    };
                    if (c.A <= 0) continue;
                    var kind = r.Kind switch
                    {
                        RangeKind.Outline => VectorRenderer.Pass.LineList,
                        RangeKind.Extrusion => pass == 0 ? VectorRenderer.Pass.ExtrusionDepth : VectorRenderer.Pass.ExtrusionColor,
                        _ => VectorRenderer.Pass.Triangles,
                    };
                    // an extrusion is lit in sRGB as GL JS lights it, then linearised in the shader
                    var tint = r.Kind == RangeKind.Extrusion || !linearize ? new Vector4((float)c.R, (float)c.G, (float)c.B, (float)c.A) : Linear(c);
                    items.Add(new VectorRenderer.Item(tile.Vertices, tile.Indices!, r.Start, r.Count, w, centre,
                        tint, (float)half, line, Widen(clip), kind, 1 << tile.Bucket.Key.Zoom, Geo: tile.Bucket.Geo, BaseVertex: r.BaseVertex));
                }
            }
    }

    // A symbol layer's draw items (labels, 4e): GL JS's draw_symbol.ts - the halo and the text in one
    // pass, or in two (halo, then text) where glyphs overlap: letter spacing below 0 or a halo wider
    // than 1 px. Colours premultiplied, as GL JS's uniforms are.
    // Icons (milestone 4) first, from the image atlas, then the text: each tile's icons are an image or
    // an SDF (the bucket's first icon decides, as GL JS's bucket.sdfIcons), sampled NEAREST where GL JS
    // does - an icon at its own size and pixel ratio, unrotated - when nearest is allowed (the render tests).
    public static void AddSymbolLayer(List<VectorRenderer.Item> items, GpuStyleLayer layer, SymbolPaint? paint,
        IReadOnlyList<(GpuTile tile, Vector4 clip)> tiles, Func<GpuTile, Matrix> world, bool linearize,
        Texture? atlas, double cameraToCenter, double pixelRatio, SymbolPaint? iconPaint = null, Texture? images = null, bool allowNearest = false)
    {
        if (iconPaint != null && images != null) Add(iconPaint, RangeKind.Icon, images);
        if (paint != null && atlas != null) Add(paint, RangeKind.Text, atlas);

        void Add(SymbolPaint p, RangeKind kind, Texture texture)
        {
            foreach (var (halo, plain) in Passes(p, kind == RangeKind.Text || System.Linq.Enumerable.Any(tiles, t => t.tile.Bucket.IconModes.GetValueOrDefault(layer.Index).Sdf)))
                foreach (var (tile, clip) in tiles)
                {
                    if (tile.SymbolVertices is null || !tile.ByLayer.TryGetValue(layer.Index, out var ranges)) continue;
                    int mode = 0;
                    bool nearest = false;
                    if (kind == RangeKind.Icon)
                    {
                        var m = tile.Bucket.IconModes.GetValueOrDefault(layer.Index);
                        if (!m.Sdf && halo && !plain) continue;   // an image has no halo pass
                        mode = m.Sdf ? 2 : 1;
                        nearest = allowNearest && !m.Sdf && !m.NeedsLinear && !p.SizePerFeature && p.Size == 1;
                    }
                    var w = world(tile);
                    var draw = new VectorRenderer.TextDraw(texture, Premultiplied(p.Color), Premultiplied(p.HaloColor), (float)p.Size,
                        p.SizePerFeature, (float)p.Opacity, (float)p.HaloWidth, (float)p.HaloBlur, mode == 1 ? false : halo, mode == 1 || plain,
                        (float)cameraToCenter, (float)pixelRatio, tile.PlacedBuffer!, tile.DynamicBuffer!, mode, nearest, p.RotateWithMap);
                    foreach (var r in ranges)
                        if (r.Kind == kind)
                            items.Add(new VectorRenderer.Item(tile.SymbolVertices, tile.SymbolIndices!, r.Start, r.Count, w,
                                tile.Bucket.Centre.ToVector3(), default, 0, false, Widen(clip), VectorRenderer.Pass.Text, 1 << tile.Bucket.Key.Zoom, draw));
                }
        }

        Vector4 Premultiplied(StyleColor c)
        {
            var v = linearize ? Linear(c) : new Vector4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
            return new Vector4(v.X * v.W, v.Y * v.W, v.Z * v.W, v.W);
        }
    }

    // A circle layer's draw items: GL JS's draw_circle.ts, tile by tile; colours premultiplied
    public static void AddCircleLayer(List<VectorRenderer.Item> items, GpuStyleLayer layer, CirclePaint paint,
        IReadOnlyList<(GpuTile tile, Vector4 clip)> tiles, Func<GpuTile, Matrix> world, bool linearize, double cameraToCenter, double pixelRatio)
    {
        Vector4 Premultiplied(StyleColor c)
        {
            var v = linearize ? Linear(c) : new Vector4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
            return new Vector4(v.X * v.W, v.Y * v.W, v.Z * v.W, v.W);
        }
        var draw = new VectorRenderer.CircleDraw(paint, Premultiplied(paint.Color), Premultiplied(paint.StrokeColor), (float)cameraToCenter, (float)pixelRatio);
        foreach (var (tile, clip) in tiles)
        {
            if (tile.CircleVertices is null || !tile.ByLayer.TryGetValue(layer.Index, out var ranges)) continue;
            foreach (var r in ranges)
                if (r.Kind == RangeKind.Circle)
                    items.Add(new VectorRenderer.Item(tile.CircleVertices, tile.CircleIndices!, r.Start, r.Count, world(tile),
                        tile.Bucket.Centre.ToVector3(), default, 0, false, Widen(clip), VectorRenderer.Pass.Circle, 1 << tile.Bucket.Key.Zoom, Circle: draw));
        }
    }

    // GL JS draw_symbol.ts: the halo and the fill in one pass, or in two (halo, then fill) where glyphs
    // overlap - text-letter-spacing below 0 or a halo wider than 1 px; no halo without an SDF
    static (bool halo, bool plain)[] Passes(SymbolPaint p, bool sdf)
    {
        bool hasHalo = sdf && p.HaloWidth != 0;
        bool twoPass = p.LetterSpacing * Shaping.OneEm < 0 || p.HaloWidth > 1;
        return !hasHalo ? new[] { (false, true) }
            : twoPass ? new[] { (true, false), (false, true) }
            : new[] { (true, true) };
    }

    // neighbours clipped to the same edge each discarded some pixels on it (interpolated in float): a
    // dotted crack along tile borders (2026-11-21). A sliver of overlap instead.
    public static Vector4 Widen(Vector4 r)
    {
        float e = (r.Z - r.X) * 2e-4f;
        return new Vector4(r.X - e, r.Y - e, r.Z + e, r.W + e);
    }

    public static Vector4 Linear(StyleColor c)
    {
        var l = new Color4((float)c.R, (float)c.G, (float)c.B, (float)c.A).ToLinear();
        return new Vector4(l.R, l.G, l.B, l.A);
    }
}
