using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Globe.Engine;

// Labels (ROADMAP 4e), milestone 1: point text. GL JS's symbol_layout.ts (anchors, the layout
// properties at the tile's zoom), shaping.ts (Shaping.cs) and quads.ts (getGlyphQuads) build each
// label's glyph quads into the bucket, around an anchor that is projected on the GPU; GlobeSymbol.sdsl
// draws them in screen pixels. No collision yet (milestone 2): every label is placed.

/// <summary>One corner of a glyph's quad; 28 bytes, as GlobeSymbol.sdsl reads it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SymbolVertex
{
    public Vector3 Anchor;     // the label's anchor, on the unit sphere (or the plane) relative to the bucket's centre
    public short OffsetX, OffsetY;   // the corner from the anchor, x 32, at 24 px (GL JS's a_offset)
    public short TexX, TexY;   // the corner in the atlas, pixels
    public System.Half TextSize, Opacity;   // the feature's text-size and text-opacity where they depend on it
    public uint Color;         // the feature's text-color (straight sRGB bytes) where it depends on it; else white

    public const int Size = 28;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector3>(),
        new VertexElement("TEXCOORD", 0, PixelFormat.R16G16B16A16_SInt, 12),
        new VertexElement("TEXCOORD", 1, PixelFormat.R16G16_Float, 20),
        new VertexElement("COLOR", 0, PixelFormat.R8G8B8A8_UNorm, 24));
}

/// <summary>A symbol layer's paint this frame, at the camera's zoom.</summary>
internal sealed record SymbolPaint(StyleColor Color, double Opacity, StyleColor HaloColor, double HaloWidth, double HaloBlur,
    double Size, bool SizePerFeature, double LetterSpacing);

internal sealed partial class GpuStyleLayer
{
    public bool IsSymbol => Layer.Type == "symbol";
    public JsonElement? TextField, TextFont, TextSize, HaloColor, HaloWidth, HaloBlur;
    public bool SizePerFeature;

    static GpuStyleLayer SymbolOf(int index, MapStyleDocument.Layer l)
    {
        var g = new GpuStyleLayer { Index = index, Layer = l };
        g.TextField = Get(l.Layout, "text-field");
        g.TextFont = Get(l.Layout, "text-font");
        g.TextSize = Get(l.Layout, "text-size");
        g.Color = Get(l.Paint, "text-color");
        g.Opacity = Get(l.Paint, "text-opacity");
        g.HaloColor = Get(l.Paint, "text-halo-color");
        g.HaloWidth = Get(l.Paint, "text-halo-width");
        g.HaloBlur = Get(l.Paint, "text-halo-blur");
        g.ColorPerFeature = g.Color is { } c && Expression.IsFeatureDependent(c);
        g.OpacityPerFeature = g.Opacity is { } o && Expression.IsFeatureDependent(o);
        g.SizePerFeature = g.TextSize is { } s && Expression.IsFeatureDependent(s);
        return g;
    }

    /// <summary>This frame's label paint at the camera's zoom; false when the layer is not drawn at it.</summary>
    public bool SymbolUniforms(StyleContext ctx, out SymbolPaint paint)
    {
        paint = null!;
        ctx.Properties = null; ctx.GeometryType = null; ctx.Id = null;
        var l = Layer;
        if ((l.MinZoom > 0 && ctx.Zoom < l.MinZoom) || ctx.Zoom >= l.MaxZoom) return false;
        if (Expression.String(Get(l.Layout, "visibility"), ctx, "visible") == "none") return false;
        foreach (var v in new[] { HaloColor, HaloWidth, HaloBlur })
            if (v is { } e && Expression.IsFeatureDependent(e)) throw new StyleUnsupportedException("a text halo that depends on the feature");
        var white = new StyleColor(1, 1, 1, 1);
        paint = new SymbolPaint(
            ColorPerFeature ? white : Expression.Color(Color, ctx, StyleColor.Black),
            OpacityPerFeature ? 1 : Expression.Number(Opacity, ctx, 1),
            Expression.Color(HaloColor, ctx, StyleColor.Transparent),
            Expression.Number(HaloWidth, ctx, 0),
            Expression.Number(HaloBlur, ctx, 0),
            SizePerFeature ? 0 : Expression.Number(TextSize, ctx, 16),
            SizePerFeature,
            Expression.Number(Get(l.Layout, "text-letter-spacing"), ctx, 0));
        return true;
    }
}

internal static partial class VectorBucketBuilder
{
    sealed partial class Builder
    {
        readonly List<SymbolVertex> _symbolVertices = new();
        readonly List<uint> _symbolIndices = new();
        readonly List<LabelInfo> _labels = new();
        readonly List<LineLabel> _lineLabels = new();
        int _layerSymbolVertexStart, _layerSymbolIndexStart, _layerLabelStart, _layerLineLabelStart;

        // GL JS symbol_layout.ts performSymbolLayout + addFeature, for point placement
        public void Symbol(GpuStyleLayer layer, List<StyleFeature> features, StyleContext ctx, MapStyleDocument style,
            bool fetch, ICollection<string>? missing, ref bool complete)
        {
            _layerVertexStart = _vertices.Count; _layerIndexStart = _indices.Count; _layerRangeStart = _ranges.Count;
            _layerSymbolVertexStart = _symbolVertices.Count; _layerSymbolIndexStart = _symbolIndices.Count; _layerLabelStart = _labels.Count;
            _layerLineLabelStart = _lineLabels.Count;
            var l = layer.Layer;
            string placement = Expression.String(GpuStyleLayer.Get(l.Layout, "symbol-placement"), ctx, "point");
            if (placement is not ("point" or "line" or "line-center")) throw new StyleUnsupportedException($"symbol-placement {placement}");
            string rotationAlignment = Expression.String(GpuStyleLayer.Get(l.Layout, "text-rotation-alignment"), ctx, "auto");
            // GL JS: text along the line unless the rotation is the viewport's or the placement a point
            bool alongLine = placement != "point" && rotationAlignment != "viewport";
            if (placement != "point" && !alongLine) throw new StyleUnsupportedException("a line label aligned to the viewport");
            if (placement == "point")
                foreach (var alignment in new[] { "text-rotation-alignment", "text-pitch-alignment" })
                    if (Expression.String(GpuStyleLayer.Get(l.Layout, alignment), ctx, "auto") is not ("auto" or "viewport"))
                        throw new StyleUnsupportedException($"{alignment} map");
            var repeated = new Dictionary<string, List<(double X, double Y)>>();   // GL JS's compareText
            var store = style.GlyphStore;
            int start = _symbolIndices.Count;
            bool alwaysOverlap = Flag("text-allow-overlap"), ignorePlacement = Flag("text-ignore-placement");
            bool Flag(string name) => GpuStyleLayer.Get(l.Layout, name) is { } v && Expression.Evaluate(v, ctx, Expression.Kind.Any) is true;
            double tileZoom = ctx.Zoom;
            foreach (var f in features)
            {
                ctx.Zoom = tileZoom;
                ctx.Properties = f.Properties; ctx.GeometryType = f.GeometryType; ctx.Id = f.Id;
                if (l.Filter is { } filter && !Expression.Filter(filter, ctx)) continue;
                var text = TextOf(layer, ctx);
                if (string.IsNullOrEmpty(text)) continue;
                string stack = FontStack(layer, ctx);
                if (!store.Ensure(stack, text, fetch, missing)) { complete = false; continue; }

                // GL JS: the shaping's text size at the bucket's zoom + 1 and at its zoom
                ctx.Zoom = tileZoom + 1;
                double layoutTextSize = Expression.Number(layer.TextSize, ctx, 16);
                ctx.Zoom = tileZoom;
                double layoutTextSizeThisZoom = Expression.Number(layer.TextSize, ctx, 16);
                double maxWidth = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-max-width"), ctx, 10) * Shaping.OneEm;
                double lineHeight = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-line-height"), ctx, 1.2) * Shaping.OneEm;
                double spacing = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-letter-spacing"), ctx, 0) * Shaping.OneEm;
                var anchor = ParseAnchor(Expression.String(GpuStyleLayer.Get(l.Layout, "text-anchor"), ctx, "center"));
                var justifyName = Expression.String(GpuStyleLayer.Get(l.Layout, "text-justify"), ctx, "center");
                var justify = justifyName == "auto" ? AnchorJustification(anchor) : ParseJustify(justifyName);
                var offset = Offset(GpuStyleLayer.Get(l.Layout, "text-offset"), ctx);
                double rotate = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-rotate"), ctx, 0) * Math.PI / 180;
                // GL JS: a label along a line is one line, centred
                if (alongLine) { maxWidth = double.PositiveInfinity; justify = TextJustify.Center; }

                var shaping = Shaping.ShapeText(Formatted.FromString(text), store, stack, maxWidth, lineHeight, anchor, justify,
                    spacing, (offset.X, offset.Y), WritingMode.Horizontal, false, layoutTextSize, layoutTextSizeThisZoom);
                if (shaping is null) continue;

                double opacity = layer.OpacityPerFeature ? Expression.Number(layer.Opacity, ctx, 1) : 1;
                double size = layer.SizePerFeature ? layoutTextSizeThisZoom : 0;
                uint color = layer.ColorPerFeature ? Pack(Expression.Color(layer.Color, ctx, StyleColor.Black)) : uint.MaxValue;
                double padding = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-padding"), ctx, 2);
                if (alongLine)
                {
                    LineLabels(layer, f, placement, shaping, text, ctx, layoutTextSize, padding, offset, (float)size, (float)opacity, color,
                        alwaysOverlap, ignorePlacement, repeated);
                    continue;
                }
                var box = CollisionBox(shaping, layoutTextSize / Shaping.OneEm, padding, rotate);
                foreach (var (ax, ay) in Anchors(f))
                {
                    // GL JS draws a label from the one tile its anchor is in, not from a neighbour's buffer
                    if (ax < 0 || ax >= Extent || ay < 0 || ay >= Extent) continue;
                    var anchorPosition = Vertex(ax, ay, 0, 0, 0, 1, 0).Position;
                    int first = _symbolVertices.Count;
                    GlyphQuads(anchorPosition, shaping, offset, rotate, (float)size, (float)opacity, color);
                    if (_symbolVertices.Count > first)
                        _labels.Add(new LabelInfo(layer.Index, anchorPosition, box.X1, box.Y1, box.X2, box.Y2,
                            alwaysOverlap, ignorePlacement, first, _symbolVertices.Count - first));
                }
            }
            ctx.Zoom = tileZoom;
            if (_symbolIndices.Count > start) _ranges.Add(new VectorRange(layer.Index, RangeKind.Text, start, _symbolIndices.Count - start));
        }

        // GL JS symbol_layout.ts addFeature for symbol-placement line / line-center, then addSymbol's text
        // along the line. GL JS's tile is 8192 units, ours 512: the anchors are found at GL JS's scale (x 16),
        // so they round as GL JS rounds them.
        void LineLabels(GpuStyleLayer layer, StyleFeature f, string placement, Shaping shaping, string text, StyleContext ctx,
            double layoutTextSize, double padding, (double X, double Y) offset, float size, float opacity, uint color,
            bool alwaysOverlap, bool ignorePlacement, Dictionary<string, List<(double X, double Y)>> repeated)
        {
            const double Up = 16, GlExtent = Extent * Up;
            var l = layer.Layer;
            double spacing = Expression.Number(GpuStyleLayer.Get(l.Layout, "symbol-spacing"), ctx, 250) * Up;
            double maxAngle = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-max-angle"), ctx, 45) / 180 * Math.PI;
            bool keepUpright = GpuStyleLayer.Get(l.Layout, "text-keep-upright") is not { } ku || Expression.Evaluate(ku, ctx, Expression.Kind.Any) is not false;
            double boxScale = Up * layoutTextSize / Shaping.OneEm;   // textMaxBoxScale: no composite text-size here
            var shaped = (shaping.Left, shaping.Right);
            double height = shaping.Bottom - shaping.Top;
            float diameter = height > 0 ? (float)(Math.Max(10, height) * layoutTextSize / Shaping.OneEm) : 0;

            foreach (var part in f.Parts)
                foreach (var ring in part)
                {
                    if (f.Kind == StyleFeature.Shape.Point || ring.Length < 2) continue;
                    var line = ring.Select(p => { var (x, y) = Local(p.X, p.Y); return (x * Up, y * Up); }).ToList();
                    var placements = new List<(IReadOnlyList<(double X, double Y)> line, LineAnchor anchor)>();
                    if (placement == "line")
                    {
                        foreach (var clipped in LineAnchors.ClipLine(new[] { line }, 0, 0, GlExtent, GlExtent))
                            foreach (var a in LineAnchors.GetAnchors(clipped, spacing, maxAngle, shaped, null, Shaping.OneEm, boxScale, 1, GlExtent))
                                if (!TooClose(a)) placements.Add((clipped, a));
                    }
                    else
                    {
                        // GeoJSON is cut into tiles by geojson-vt first, 128 px of buffer round each (a
                        // quarter of a 512-px tile): a line-center label sits at the middle of the tile's
                        // piece, not of the whole line (text-max-width/force-newline-line-center).
                        // A vector tile's data comes cut already.
                        const double Buffer = GlExtent / 4;
                        var pieces = _grid ? LineAnchors.ClipLine(new[] { line }, -Buffer, -Buffer, GlExtent + Buffer, GlExtent + Buffer) : new List<List<(double X, double Y)>> { line };
                        foreach (var piece in pieces)
                            if (piece.Count > 1 && LineAnchors.GetCenterAnchor(piece, maxAngle, shaped, null, Shaping.OneEm, boxScale) is { } centre)
                                placements.Add((piece, centre));
                    }

                    foreach (var (used, anchor) in placements)
                    {
                        // GL JS draws a label from the one tile its anchor is in
                        if (anchor.X < 0 || anchor.X >= GlExtent || anchor.Y < 0 || anchor.Y >= GlExtent) continue;
                        var anchorPosition = Vertex(anchor.X / Up, anchor.Y / Up, 0, 0, 0, 1, 0).Position;
                        var points = used.Select(p => Vertex(p.X / Up, p.Y / Up, 0, 0, 0, 1, 0).Position).ToArray();
                        int first = _symbolVertices.Count;
                        var offsets = new List<float>();
                        foreach (var lineGlyphs in shaping.PositionedLines)
                            foreach (var g in lineGlyphs.PositionedGlyphs)
                            {
                                if (g.Rect is not { } r) continue;
                                // GL JS getGlyphQuads along a line: the quad round the glyph's own point,
                                // which is glyphOffset (x + half the advance) along the line, y across it
                                double halfAdvance = g.Metrics.Advance * g.Scale / 2;
                                offsets.Add((float)(g.X + halfAdvance));
                                const double RectBuffer = Glyph.Border + GlyphAtlas.Padding;
                                double x1 = (g.Metrics.Left - RectBuffer) * g.Scale - halfAdvance;
                                double y1 = (-g.Metrics.Top - RectBuffer) * g.Scale + g.Y;
                                double x2 = x1 + r.W * g.Scale, y2 = y1 + r.H * g.Scale;
                                uint i = (uint)_symbolVertices.Count;
                                Add(x1, y1, r.X, r.Y);
                                Add(x2, y1, r.X + r.W, r.Y);
                                Add(x1, y2, r.X, r.Y + r.H);
                                Add(x2, y2, r.X + r.W, r.Y + r.H);
                                _symbolIndices.Add(i); _symbolIndices.Add(i + 1); _symbolIndices.Add(i + 2);
                                _symbolIndices.Add(i + 1); _symbolIndices.Add(i + 2); _symbolIndices.Add(i + 3);
                            }
                        if (offsets.Count == 0) continue;
                        _lineLabels.Add(new LineLabel
                        {
                            Layer = layer.Index, Anchor = anchorPosition, Line = points, Segment = anchor.Segment,
                            GlyphOffsets = offsets.ToArray(), VertexStart = first,
                            LineOffsetX = (float)offset.X, LineOffsetY = (float)offset.Y, KeepUpright = keepUpright,
                            CircleDiameter = diameter, Padding = (float)padding, FeatureSize = size,
                            AlwaysOverlap = alwaysOverlap, IgnorePlacement = ignorePlacement,
                        });

                        void Add(double x, double y, double tx, double ty) => _symbolVertices.Add(new SymbolVertex
                        {
                            Anchor = anchorPosition,
                            OffsetX = (short)Math.Floor(x * 32 + 0.5),
                            OffsetY = (short)Math.Floor(y * 32 + 0.5),
                            TexX = (short)tx, TexY = (short)ty,
                            TextSize = (System.Half)size, Opacity = (System.Half)opacity, Color = color,
                        });
                    }
                }

            // GL JS anchorIsTooClose: the same text again within half the spacing is left out
            bool TooClose(LineAnchor a)
            {
                double repeatDistance = spacing / 2;
                if (!repeated.TryGetValue(text, out var others)) repeated[text] = others = new List<(double X, double Y)>();
                foreach (var o in others)
                    if (Math.Sqrt((a.X - o.X) * (a.X - o.X) + (a.Y - o.Y) * (a.Y - o.Y)) < repeatDistance) return true;
                others.Add((a.X, a.Y));
                return false;
            }
        }

        // GL JS collision_feature.ts, for a label in the viewport: the shaping's box at the layout's text
        // size (textBoxScale, then textPixelRatio: pixels), text-padding round it, rotated with the text
        static (float X1, float Y1, float X2, float Y2) CollisionBox(Shaping shaping, double scale, double padding, double rotate)
        {
            double x1 = shaping.Left * scale - padding, y1 = shaping.Top * scale - padding;
            double x2 = shaping.Right * scale + padding, y2 = shaping.Bottom * scale + padding;
            if (rotate != 0)
            {
                double sin = Math.Sin(rotate), cos = Math.Cos(rotate);
                // Point._rotate: (cos x - sin y, sin x + cos y), the four corners' bounds
                var xs = new[] { cos * x1 - sin * y1, cos * x2 - sin * y1, cos * x1 - sin * y2, cos * x2 - sin * y2 };
                var ys = new[] { sin * x1 + cos * y1, sin * x2 + cos * y1, sin * x1 + cos * y2, sin * x2 + cos * y2 };
                (x1, x2, y1, y2) = (xs.Min(), xs.Max(), ys.Min(), ys.Max());
            }
            return ((float)x1, (float)y1, (float)x2, (float)y2);
        }

        // GL JS quads.ts getGlyphQuads, for horizontal labels not along a line; symbol_bucket.ts addSymbols
        void GlyphQuads(Vector3 anchor, Shaping shaping, (double X, double Y) textOffset, double rotate, float size, float opacity, uint color)
        {
            const double RectBuffer = Glyph.Border + GlyphAtlas.Padding;
            double sin = Math.Sin(rotate), cos = Math.Cos(rotate);
            foreach (var line in shaping.PositionedLines)
                foreach (var g in line.PositionedGlyphs)
                {
                    if (g.Rect is not { } r) continue;
                    double halfAdvance = g.Metrics.Advance * g.Scale / 2;
                    double bx = g.X + halfAdvance + textOffset.X, by = g.Y + textOffset.Y;
                    double x1 = (g.Metrics.Left - RectBuffer) * g.Scale - halfAdvance + bx;
                    double y1 = (-g.Metrics.Top - RectBuffer) * g.Scale + by;
                    double x2 = x1 + r.W * g.Scale, y2 = y1 + r.H * g.Scale;
                    uint i = (uint)_symbolVertices.Count;
                    Corner(x1, y1, r.X, r.Y);
                    Corner(x2, y1, r.X + r.W, r.Y);
                    Corner(x1, y2, r.X, r.Y + r.H);
                    Corner(x2, y2, r.X + r.W, r.Y + r.H);
                    _symbolIndices.Add(i); _symbolIndices.Add(i + 1); _symbolIndices.Add(i + 2);
                    _symbolIndices.Add(i + 1); _symbolIndices.Add(i + 2); _symbolIndices.Add(i + 3);
                }

            void Corner(double x, double y, double tx, double ty)
            {
                // Point._matMult([cos, -sin, sin, cos])
                if (rotate != 0) (x, y) = (cos * x - sin * y, sin * x + cos * y);
                _symbolVertices.Add(new SymbolVertex
                {
                    Anchor = anchor,
                    // symbol_bucket.ts addVertex: Math.round(o * 32), JavaScript's half-up rounding
                    OffsetX = (short)Math.Floor(x * 32 + 0.5),
                    OffsetY = (short)Math.Floor(y * 32 + 0.5),
                    TexX = (short)tx,
                    TexY = (short)ty,
                    TextSize = (System.Half)size,
                    Opacity = (System.Half)opacity,
                    Color = color,
                });
            }
        }

        // GL JS symbol_layout.ts addFeature: a point label at every point; on a line at its first point;
        // in a polygon at its pole of inaccessibility (16 of 8192 tile units: 1 of our 512)
        IEnumerable<(double X, double Y)> Anchors(StyleFeature f)
        {
            foreach (var part in f.Parts)
            {
                if (part.Count == 0) continue;
                switch (f.Kind)
                {
                    case StyleFeature.Shape.Point:
                        foreach (var ring in part)
                            foreach (var (x, y) in ring) yield return Local(x, y);
                        break;
                    case StyleFeature.Shape.Line:
                        foreach (var line in part)
                            if (line.Length > 0) yield return Local(line[0].X, line[0].Y);
                        break;
                    default:
                        yield return PoleOfInaccessibility.Find(part.Select(ring => ring.Select(p => Local(p.X, p.Y)).ToArray()).ToList(), 1);
                        break;
                }
            }
        }

        static string? TextOf(GpuStyleLayer layer, StyleContext ctx)
        {
            if (layer.TextField is not { } field) return null;
            string? text = field.ValueKind == JsonValueKind.String
                ? ResolveTokens(field.GetString()!, ctx.Properties)
                : ToText(Expression.Evaluate(field, ctx, Expression.Kind.Any));
            if (text is null) return null;
            return Expression.String(GpuStyleLayer.Get(layer.Layer.Layout, "text-transform"), ctx, "none") switch
            {
                "uppercase" => text.ToUpperInvariant(),
                "lowercase" => text.ToLowerInvariant(),
                _ => text,
            };
        }

        // the style spec's tokens: "{name}" is the feature's name property, an absent one empty
        static string ResolveTokens(string s, IReadOnlyDictionary<string, object?>? properties)
        {
            if (!s.Contains('{')) return s;
            var sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                int open = s.IndexOf('{', i), close = open < 0 ? -1 : s.IndexOf('}', open);
                if (open < 0 || close < 0) { sb.Append(s, i, s.Length - i); break; }
                sb.Append(s, i, open - i);
                var key = s.Substring(open + 1, close - open - 1);
                if (properties != null && properties.TryGetValue(key, out var v)) sb.Append(ToText(v));
                i = close + 1;
            }
            return sb.ToString();
        }

        // JavaScript's String(value), as the style spec's to-string
        static string? ToText(object? value) => value switch
        {
            null => null,
            string s => s,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => value.ToString(),
        };

        static string FontStack(GpuStyleLayer layer, StyleContext ctx)
        {
            if (layer.TextFont is not { } font) return "Open Sans Regular,Arial Unicode MS Regular";
            // a plain list of font names, or an expression that gives one
            if (font.ValueKind == JsonValueKind.Array && font.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                && font.GetArrayLength() > 0 && font[0].GetString() is not ("literal" or "step" or "match" or "case" or "coalesce" or "get" or "interpolate"))
                return string.Join(",", font.EnumerateArray().Select(e => e.GetString()));
            var value = Expression.Evaluate(font, ctx, Expression.Kind.Any);
            return value switch
            {
                JsonElement { ValueKind: JsonValueKind.Array } a => string.Join(",", a.EnumerateArray().Select(e => e.GetString())),
                IEnumerable<object?> list => string.Join(",", list.Select(ToText)),
                _ => throw new StyleUnsupportedException("a text-font that is not a list of fonts"),
            };
        }

        static (double X, double Y) Offset(JsonElement? value, StyleContext ctx)
        {
            if (value is not { } v) return (0, 0);
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && v[0].ValueKind == JsonValueKind.Number)
                return (v[0].GetDouble() * Shaping.OneEm, v[1].GetDouble() * Shaping.OneEm);
            var e = Expression.Evaluate(v, ctx, Expression.Kind.Any);
            return e switch
            {
                JsonElement { ValueKind: JsonValueKind.Array } a when a.GetArrayLength() == 2 => (a[0].GetDouble() * Shaping.OneEm, a[1].GetDouble() * Shaping.OneEm),
                IReadOnlyList<object?> list when list.Count == 2 && list[0] is double x && list[1] is double y => (x * Shaping.OneEm, y * Shaping.OneEm),
                _ => throw new StyleUnsupportedException("a text-offset that is not two numbers"),
            };
        }

        static SymbolAnchor ParseAnchor(string s) => s switch
        {
            "center" => SymbolAnchor.Center, "left" => SymbolAnchor.Left, "right" => SymbolAnchor.Right,
            "top" => SymbolAnchor.Top, "bottom" => SymbolAnchor.Bottom, "top-left" => SymbolAnchor.TopLeft,
            "top-right" => SymbolAnchor.TopRight, "bottom-left" => SymbolAnchor.BottomLeft, "bottom-right" => SymbolAnchor.BottomRight,
            _ => throw new StyleUnsupportedException($"text-anchor {s}"),
        };

        static TextJustify ParseJustify(string s) => s switch
        {
            "left" => TextJustify.Left, "center" => TextJustify.Center, "right" => TextJustify.Right,
            _ => throw new StyleUnsupportedException($"text-justify {s}"),
        };

        // GL JS symbol_layout.ts getAnchorJustification
        static TextJustify AnchorJustification(SymbolAnchor anchor) => anchor switch
        {
            SymbolAnchor.Right or SymbolAnchor.TopRight or SymbolAnchor.BottomRight => TextJustify.Right,
            SymbolAnchor.Left or SymbolAnchor.TopLeft or SymbolAnchor.BottomLeft => TextJustify.Left,
            _ => TextJustify.Center,
        };
    }
}
