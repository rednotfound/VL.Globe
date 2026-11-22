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
    // labels, milestone 2: each label's collision data, and per symbol vertex whether it is placed
    // (LabelPlacement writes Placed every frame, the renderer uploads it to PlacedBuffer when dirty)
    public IReadOnlyList<LabelInfo> Labels = Array.Empty<LabelInfo>();
    public float[]? Placed;
    public Buffer? PlacedBuffer;
    public bool PlacedDirty;
    // labels along lines: per symbol vertex, the glyph placed this frame (x, y px, angle, 1) - or (0, 0,
    // 0, 0) for a point label's vertex, (.., -1) hidden (LabelPlacement / LineGlyphs, every frame)
    public IReadOnlyList<LineLabel> LineLabels = Array.Empty<LineLabel>();
    public Vector4[]? Dynamic;
    public Buffer? DynamicBuffer;
    public bool DynamicDirty;
    public required Dictionary<int, List<VectorRange>> ByLayer;   // -1: the background grid
    public long Bytes => (Vertices?.SizeInBytes ?? 0) + (Indices?.SizeInBytes ?? 0) + (SymbolVertices?.SizeInBytes ?? 0) + (SymbolIndices?.SizeInBytes ?? 0);
    public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); SymbolVertices?.Dispose(); SymbolIndices?.Dispose(); PlacedBuffer?.Dispose(); DynamicBuffer?.Dispose(); }

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
            // every label shown until the first placement
            placed = new float[bucket.SymbolVertices.Length];
            Array.Fill(placed, 1f);
            placedBuffer = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(placed.AsSpan()), sizeof(float), BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Default);
            dynamic = new Vector4[bucket.SymbolVertices.Length];
            dynamicBuffer = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(dynamic.AsSpan()), 16, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Default);
            symbolVertices = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.SymbolVertices.AsSpan()), SymbolVertex.Size, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable);
            symbolIndices = Buffer.New(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.SymbolIndices.AsSpan()), sizeof(uint), BufferFlags.IndexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable);
            bucket.SymbolVertices = Array.Empty<SymbolVertex>();
            bucket.SymbolIndices = Array.Empty<uint>();
        }
        if (bucket.IndexCount == 0)
        {
            bucket.Release();
            return new GpuTile { Bucket = bucket, ByLayer = byLayer, SymbolVertices = symbolVertices, SymbolIndices = symbolIndices,
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
            Indices = Buffer.New(device, indices, sizeof(uint), BufferFlags.IndexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable),
            SymbolVertices = symbolVertices,
            SymbolIndices = symbolIndices,
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
                    if (r.Kind == RangeKind.Text) continue;   // AddSymbolLayer's
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
                        tint, (float)half, line, Widen(clip), kind, 1 << tile.Bucket.Key.Zoom));
                }
            }
    }

    // A symbol layer's draw items (labels, 4e): GL JS's draw_symbol.ts - the halo and the text in one
    // pass, or in two (halo, then text) where glyphs overlap: letter spacing below 0 or a halo wider
    // than 1 px. Colours premultiplied, as GL JS's uniforms are.
    public static void AddSymbolLayer(List<VectorRenderer.Item> items, GpuStyleLayer layer, SymbolPaint paint,
        IReadOnlyList<(GpuTile tile, Vector4 clip)> tiles, Func<GpuTile, Matrix> world, bool linearize,
        Texture atlas, double cameraToCenter, double pixelRatio)
    {
        Vector4 Premultiplied(StyleColor c)
        {
            var v = linearize ? Linear(c) : new Vector4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
            return new Vector4(v.X * v.W, v.Y * v.W, v.Z * v.W, v.W);
        }
        bool hasHalo = paint.HaloWidth != 0;
        bool twoPass = paint.LetterSpacing * Shaping.OneEm < 0 || paint.HaloWidth > 1;
        var passes = !hasHalo ? new[] { (halo: false, plain: true) }
            : twoPass ? new[] { (halo: true, plain: false), (halo: false, plain: true) }
            : new[] { (halo: true, plain: true) };
        foreach (var (halo, plain) in passes)
            foreach (var (tile, clip) in tiles)
            {
                if (tile.SymbolVertices is null || !tile.ByLayer.TryGetValue(layer.Index, out var ranges)) continue;
                var w = world(tile);
                var text = new VectorRenderer.TextDraw(atlas, Premultiplied(paint.Color), Premultiplied(paint.HaloColor), (float)paint.Size,
                    paint.SizePerFeature, (float)paint.Opacity, (float)paint.HaloWidth, (float)paint.HaloBlur, halo, plain,
                    (float)cameraToCenter, (float)pixelRatio, tile.PlacedBuffer!, tile.DynamicBuffer!);
                foreach (var r in ranges)
                    if (r.Kind == RangeKind.Text)
                        items.Add(new VectorRenderer.Item(tile.SymbolVertices, tile.SymbolIndices!, r.Start, r.Count, w,
                            tile.Bucket.Centre.ToVector3(), default, 0, false, Widen(clip), VectorRenderer.Pass.Text, 1 << tile.Bucket.Key.Zoom, text));
            }
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
