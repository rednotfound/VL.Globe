using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Globe.Engine;

// Circle layers: GL JS's circle_bucket.ts (a quad at every point of every feature inside the tile)
// and its circle shaders (GlobeCircle.sdsl): the quad extruded in screen pixels round the projected
// centre. Paint that depends on the feature goes into the vertices; the rest is a uniform per frame.

/// <summary>One corner of a circle's quad; 36 bytes, as GlobeCircle.sdsl reads it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CircleVertex
{
    public Vector3 Center;              // on the unit sphere (or the plane), relative to the bucket's centre
    public short ExtrudeX, ExtrudeY;    // -1 or 1
    public uint Color, StrokeColor;     // the feature's colours (straight sRGB bytes) where they depend on it
    public System.Half Radius, Blur, Opacity, StrokeWidth;
    public System.Half StrokeOpacity, Unused;

    public const int Size = 36;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector3>(),
        new VertexElement("TEXCOORD", 0, PixelFormat.R16G16_SInt, 12),
        new VertexElement("COLOR", 0, PixelFormat.R8G8B8A8_UNorm, 16),
        new VertexElement("COLOR", 1, PixelFormat.R8G8B8A8_UNorm, 20),
        new VertexElement("TEXCOORD", 1, PixelFormat.R16G16B16A16_Float, 24),
        new VertexElement("TEXCOORD", 2, PixelFormat.R16G16_Float, 32));
}

/// <summary>A circle layer's paint this frame at the camera's zoom, and which of it comes from the vertices.</summary>
internal sealed record CirclePaint(StyleColor Color, StyleColor StrokeColor, double Radius, double Blur, double Opacity,
    double StrokeWidth, double StrokeOpacity, bool ScaleWithMap, CirclePerFeature PerFeature);

[Flags]
internal enum CirclePerFeature { None = 0, Color = 1, StrokeColor = 2, Radius = 4, Blur = 8, Opacity = 16, StrokeWidth = 32, StrokeOpacity = 64 }

internal sealed partial class GpuStyleLayer
{
    public bool IsCircle => Layer.Type == "circle";
    public JsonElement? CircleRadius, CircleBlur, StrokeWidth, StrokeColor, StrokeOpacity;
    public CirclePerFeature CirclePerFeature;

    static GpuStyleLayer CircleOf(int index, MapStyleDocument.Layer l)
    {
        var g = new GpuStyleLayer { Index = index, Layer = l };
        g.Color = Get(l.Paint, "circle-color");
        g.Opacity = Get(l.Paint, "circle-opacity");
        g.CircleRadius = Get(l.Paint, "circle-radius");
        g.CircleBlur = Get(l.Paint, "circle-blur");
        g.StrokeWidth = Get(l.Paint, "circle-stroke-width");
        g.StrokeColor = Get(l.Paint, "circle-stroke-color");
        g.StrokeOpacity = Get(l.Paint, "circle-stroke-opacity");
        var f = CirclePerFeature.None;
        void Flag(JsonElement? e, CirclePerFeature bit) { if (e is { } v && Expression.IsFeatureDependent(v)) f |= bit; }
        Flag(g.Color, CirclePerFeature.Color); Flag(g.StrokeColor, CirclePerFeature.StrokeColor);
        Flag(g.CircleRadius, CirclePerFeature.Radius); Flag(g.CircleBlur, CirclePerFeature.Blur);
        Flag(g.Opacity, CirclePerFeature.Opacity); Flag(g.StrokeWidth, CirclePerFeature.StrokeWidth);
        Flag(g.StrokeOpacity, CirclePerFeature.StrokeOpacity);
        g.CirclePerFeature = f;
        return g;
    }

    /// <summary>This frame's circle paint at the camera's zoom; null when the layer is not drawn at it.</summary>
    public CirclePaint? CircleUniforms(StyleContext ctx)
    {
        ctx.Properties = null; ctx.GeometryType = null; ctx.Id = null;
        var l = Layer;
        if ((l.MinZoom > 0 && ctx.Zoom < l.MinZoom) || ctx.Zoom >= l.MaxZoom) return null;
        if (Expression.String(Get(l.Layout, "visibility"), ctx, "visible") == "none") return null;
        var f = CirclePerFeature;
        double N(JsonElement? e, CirclePerFeature bit, double fallback) => (f & bit) != 0 ? 0 : Expression.Number(e, ctx, fallback);
        StyleColor C(JsonElement? e, CirclePerFeature bit, StyleColor fallback) => (f & bit) != 0 ? new StyleColor(1, 1, 1, 1) : Expression.Color(e, ctx, fallback);
        var paint = new CirclePaint(
            C(Color, CirclePerFeature.Color, StyleColor.Black), C(StrokeColor, CirclePerFeature.StrokeColor, StyleColor.Black),
            N(CircleRadius, CirclePerFeature.Radius, 5), N(CircleBlur, CirclePerFeature.Blur, 0), N(Opacity, CirclePerFeature.Opacity, 1),
            N(StrokeWidth, CirclePerFeature.StrokeWidth, 0), N(StrokeOpacity, CirclePerFeature.StrokeOpacity, 1),
            Expression.String(Get(l.Paint, "circle-pitch-scale"), ctx, "map") == "map", f);
        // GL JS draw_circle.ts: nothing to draw when neither the fill nor the stroke shows
        if (f == CirclePerFeature.None && paint.Opacity == 0 && (paint.StrokeWidth == 0 || paint.StrokeOpacity == 0)) return null;
        return paint;
    }
}

internal static partial class VectorBucketBuilder
{
    sealed partial class Builder
    {
        readonly List<CircleVertex> _circleVertices = new();
        readonly List<uint> _circleIndices = new();
        int _layerCircleVertexStart, _layerCircleIndexStart;
        bool _inCircleLayer;   // Rewind takes back the circles only of a circle layer that threw

        // GL JS circle_bucket.ts addFeature, granularity 1: one quad per point of every ring
        public void Circle(GpuStyleLayer layer, List<StyleFeature> features, StyleContext ctx)
        {
            _layerVertexStart = _vertices.Count; _layerIndexStart = _indices.Count; _layerRangeStart = _ranges.Count;
            _layerCircleVertexStart = _circleVertices.Count; _layerCircleIndexStart = _circleIndices.Count;
            _inCircleLayer = true;
            var l = layer.Layer;
            foreach (var name in new[] { "circle-translate" })
                if (GpuStyleLayer.Get(l.Paint, name) is { } t && !(t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 2 && t[0].GetDouble() == 0 && t[1].GetDouble() == 0))
                    throw new StyleUnsupportedException(name);
            if (Expression.String(GpuStyleLayer.Get(l.Paint, "circle-pitch-alignment"), ctx, "viewport") != "viewport")
                throw new StyleUnsupportedException("circle-pitch-alignment map");
            int start = _circleIndices.Count;
            var f = layer.CirclePerFeature;
            foreach (var feature in features)
            {
                ctx.Properties = feature.Properties; ctx.GeometryType = feature.GeometryType; ctx.Id = feature.Id;
                if (l.Filter is { } filter && !Expression.Filter(filter, ctx)) continue;
                uint color = (f & CirclePerFeature.Color) != 0 ? Pack(Expression.Color(layer.Color, ctx, StyleColor.Black)) : uint.MaxValue;
                uint stroke = (f & CirclePerFeature.StrokeColor) != 0 ? Pack(Expression.Color(layer.StrokeColor, ctx, StyleColor.Black)) : uint.MaxValue;
                System.Half H(JsonElement? e, CirclePerFeature bit, double fallback) => (System.Half)((f & bit) != 0 ? Expression.Number(e, ctx, fallback) : 0);
                var radius = H(layer.CircleRadius, CirclePerFeature.Radius, 5);
                var blur = H(layer.CircleBlur, CirclePerFeature.Blur, 0);
                var opacity = H(layer.Opacity, CirclePerFeature.Opacity, 1);
                var strokeWidth = H(layer.StrokeWidth, CirclePerFeature.StrokeWidth, 0);
                var strokeOpacity = H(layer.StrokeOpacity, CirclePerFeature.StrokeOpacity, 1);
                foreach (var part in feature.Parts)
                    foreach (var ring in part)
                        foreach (var (x, y) in ring)
                        {
                            var (lx, ly) = Local(x, y);
                            // isOutsideTile: drawn from the one tile the point is in
                            if (lx < 0 || lx >= Extent || ly < 0 || ly >= Extent) continue;
                            var centre = Vertex(lx, ly, 0, 0, 0, 1, 0).Position;
                            uint i = (uint)_circleVertices.Count;
                            // extrudes [0, 7] on each axis, y outer: (-1,-1), (1,-1), (-1,1), (1,1)
                            for (int ey = -1; ey <= 1; ey += 2)
                                for (int ex = -1; ex <= 1; ex += 2)
                                    _circleVertices.Add(new CircleVertex
                                    {
                                        Center = centre, ExtrudeX = (short)ex, ExtrudeY = (short)ey, Color = color, StrokeColor = stroke,
                                        Radius = radius, Blur = blur, Opacity = opacity, StrokeWidth = strokeWidth, StrokeOpacity = strokeOpacity,
                                    });
                            _circleIndices.Add(i); _circleIndices.Add(i + 3); _circleIndices.Add(i + 1);
                            _circleIndices.Add(i); _circleIndices.Add(i + 2); _circleIndices.Add(i + 3);
                        }
            }
            if (_circleIndices.Count > start) _ranges.Add(new VectorRange(layer.Index, RangeKind.Circle, start, _circleIndices.Count - start));
            _inCircleLayer = false;
        }

        void RewindCircles()
        {
            if (!_inCircleLayer) return;
            _inCircleLayer = false;
            _circleVertices.RemoveRange(_layerCircleVertexStart, _circleVertices.Count - _layerCircleVertexStart);
            _circleIndices.RemoveRange(_layerCircleIndexStart, _circleIndices.Count - _layerCircleIndexStart);
        }
    }
}
