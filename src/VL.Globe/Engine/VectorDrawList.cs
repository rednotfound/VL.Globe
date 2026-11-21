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
    public required Dictionary<int, List<VectorRange>> ByLayer;   // -1: the background grid
    public long Bytes => (Vertices?.SizeInBytes ?? 0) + (Indices?.SizeInBytes ?? 0);
    public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); }

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
        if (bucket.IndexCount == 0) { bucket.Release(); return new GpuTile { Bucket = bucket, ByLayer = byLayer }; }
        var vertices = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.VertexSpan);
        var indices = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bucket.IndexSpan);
        var tile = new GpuTile
        {
            Bucket = bucket,
            ByLayer = byLayer,
            Vertices = Buffer.New(device, vertices, VectorVertex.Size, BufferFlags.VertexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable),
            Indices = Buffer.New(device, indices, sizeof(uint), BufferFlags.IndexBuffer, PixelFormat.None, GraphicsResourceUsage.Immutable),
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
