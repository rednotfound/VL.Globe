using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Globe.Engine.Gpu;

// Labels (ROADMAP 4e), milestone 1: point text. GL JS's symbol_layout.ts (anchors, the layout
// properties at the tile's zoom), shaping.ts (Shaping.cs) and quads.ts (getGlyphQuads) build each
// label's glyph quads into the bucket, around an anchor that is projected on the GPU; GlobeSymbol.sdsl
// draws them in screen pixels. No collision yet (milestone 2): every label is placed.

/// <summary>One corner of a glyph's or an icon's quad; 36 bytes, as GlobeSymbol.sdsl reads it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SymbolVertex
{
    public Vector3 Anchor;     // the label's anchor, on the unit sphere (or the plane) relative to the bucket's centre
    public short OffsetX, OffsetY;   // the corner from the anchor, x 32, at 24 px (GL JS's a_offset)
    public short TexX, TexY;   // the corner in the atlas, pixels
    public System.Half TextSize, Opacity;   // the feature's text-size and text-opacity where they depend on it
    public uint Color;         // the feature's text-color (straight sRGB bytes) where it depends on it; else white
    // a stretchable icon's parts that are not stretched (GL JS's a_pixeloffset): pixels x 16, and the
    // font scale the stretched part never shrinks below, x 256. Zero for text and plain icons
    public short PixelOffsetX, PixelOffsetY, MinFontScaleX, MinFontScaleY;

    public const int Size = 36;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector3>(),
        new VertexElement("TEXCOORD", 0, PixelFormat.R16G16B16A16_SInt, 12),
        new VertexElement("TEXCOORD", 1, PixelFormat.R16G16_Float, 20),
        new VertexElement("COLOR", 0, PixelFormat.R8G8B8A8_UNorm, 24),
        new VertexElement("TEXCOORD", 4, PixelFormat.R16G16B16A16_SInt, 28));
}

/// <summary>A symbol layer's paint this frame, at the camera's zoom.</summary>
internal sealed record SymbolPaint(StyleColor Color, double Opacity, StyleColor HaloColor, double HaloWidth, double HaloBlur,
    double Size, bool SizePerFeature, double LetterSpacing, bool RotateWithMap = false);

internal sealed partial class GpuStyleLayer
{
    public bool IsSymbol => Layer.Type == "symbol";
    public JsonElement? TextField, TextFont, TextSize, HaloColor, HaloWidth, HaloBlur;
    public bool SizePerFeature;
    // icons (milestone 4)
    public JsonElement? IconImage, IconSize, IconColor, IconOpacity, IconHaloColor, IconHaloWidth, IconHaloBlur;
    public bool IconSizePerFeature, IconColorPerFeature, IconOpacityPerFeature;

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
        g.IconImage = Get(l.Layout, "icon-image");
        g.IconSize = Get(l.Layout, "icon-size");
        g.IconColor = Get(l.Paint, "icon-color");
        g.IconOpacity = Get(l.Paint, "icon-opacity");
        g.IconHaloColor = Get(l.Paint, "icon-halo-color");
        g.IconHaloWidth = Get(l.Paint, "icon-halo-width");
        g.IconHaloBlur = Get(l.Paint, "icon-halo-blur");
        g.IconSizePerFeature = g.IconSize is { } i && Expression.IsFeatureDependent(i);
        g.IconColorPerFeature = g.IconColor is { } ic && Expression.IsFeatureDependent(ic);
        g.IconOpacityPerFeature = g.IconOpacity is { } io && Expression.IsFeatureDependent(io);
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
            Expression.Number(Get(l.Layout, "text-letter-spacing"), ctx, 0),
            RotatesWithMap(ctx, "text-rotation-alignment"));
        return true;
    }

    /// <summary>This frame's icon paint at the camera's zoom, or null when the layer has no icon-image.</summary>
    public SymbolPaint? IconUniforms(StyleContext ctx)
    {
        if (IconImage is null) return null;
        ctx.Properties = null; ctx.GeometryType = null; ctx.Id = null;
        foreach (var v in new[] { IconHaloColor, IconHaloWidth, IconHaloBlur })
            if (v is { } e && Expression.IsFeatureDependent(e)) throw new StyleUnsupportedException("an icon halo that depends on the feature");
        var white = new StyleColor(1, 1, 1, 1);
        return new SymbolPaint(
            IconColorPerFeature ? white : Expression.Color(IconColor, ctx, StyleColor.Black),
            IconOpacityPerFeature ? 1 : Expression.Number(IconOpacity, ctx, 1),
            Expression.Color(IconHaloColor, ctx, StyleColor.Transparent),
            Expression.Number(IconHaloWidth, ctx, 0),
            Expression.Number(IconHaloBlur, ctx, 0),
            IconSizePerFeature ? 0 : Expression.Number(IconSize, ctx, 1),
            IconSizePerFeature,
            Expression.Number(Get(Layer.Layout, "text-letter-spacing"), ctx, 0),
            RotatesWithMap(ctx, "icon-rotation-alignment"));
    }

    // a point symbol's quads turned with the map in the shader (GL JS's rotateInShader); along a line the
    // glyphs are placed and turned on the CPU (LineLabels), so this is for point placement only
    bool RotatesWithMap(StyleContext ctx, string alignment)
        => Expression.String(Get(Layer.Layout, "symbol-placement"), ctx, "point") == "point"
           && Expression.String(Get(Layer.Layout, alignment), ctx, "auto") == "map";
}

internal static partial class VectorBucketBuilder
{
    sealed partial class Builder
    {
        readonly List<SymbolVertex> _symbolVertices = new();
        readonly List<uint> _symbolIndices = new();
        readonly List<LabelInfo> _labels = new();
        readonly List<LineLabel> _lineLabels = new();
        readonly List<uint> _iconIndices = new();   // a layer's icon quads, put before its text's when the layer is done
        readonly Dictionary<int, (bool Sdf, bool NeedsLinear)> _iconModes = new();
        int _layerSymbolVertexStart, _layerSymbolIndexStart, _layerLabelStart, _layerLineLabelStart;

        // GL JS symbol_layout.ts performSymbolLayout + addFeature, for point placement
        public void Symbol(GpuStyleLayer layer, FeatureTable features, StyleContext ctx, MapStyleDocument style,
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
            // ... and an icon along it when its rotation is the map's (auto: the map's on a line)
            bool iconAlongLine = placement != "point" && Expression.String(GpuStyleLayer.Get(l.Layout, "icon-rotation-alignment"), ctx, "auto") != "viewport";
            // A point symbol turned with the map (rotation-alignment map): its quads and its collision box
            // turned to the map's east as projected (GL JS's rotateInShader and _projectCollisionBox);
            // viewport-glyph is the viewport's at a point. Pitched with the map: not yet - an explicit
            // pitch-alignment map throws; auto, which GL JS resolves to map here, is drawn in the screen,
            // turned (the same picture untilted, flat on the screen tilted)
            if (placement == "point")
                foreach (var alignment in new[] { "text-pitch-alignment", "icon-pitch-alignment" })
                    if (Expression.String(GpuStyleLayer.Get(l.Layout, alignment), ctx, "auto") is not ("auto" or "viewport"))
                        throw new StyleUnsupportedException($"{alignment} map");
            var turnedWithMap = SymbolFlags.None;
            if (placement == "point" && rotationAlignment == "map") turnedWithMap |= SymbolFlags.TextRotateWithMap;
            if (placement == "point" && Expression.String(GpuStyleLayer.Get(l.Layout, "icon-rotation-alignment"), ctx, "auto") == "map")
                turnedWithMap |= SymbolFlags.IconRotateWithMap;
            // On a line: text and icon both upright at the line's anchors (Liberty's shields), or an icon
            // alone along the line (its one-way arrows). Not yet an icon beside text along a line: a strict
            // parse (the render tests) says so; a real style keeps the layer's text without its icons.
            string? iconsOff = layer.IconImage != null && placement != "point" && layer.TextField != null && (alongLine || iconAlongLine)
                ? "an icon with text along a line" : null;
            string iconTextFit = Expression.String(GpuStyleLayer.Get(l.Layout, "icon-text-fit"), ctx, "none");
            if (iconTextFit is not ("none" or "width" or "height" or "both")) throw new StyleUnsupportedException($"icon-text-fit {iconTextFit}");
            if (iconsOff != null)
            {
                if (!style.Lenient) throw new StyleUnsupportedException(iconsOff);
                style.Skip($"{l.Id}: {iconsOff} (its icons left out)");
            }
            var repeated = new Dictionary<string, List<(double X, double Y)>>();   // GL JS's compareText
            // a layer's icons shaped once for each set of inputs (read only after: quads, extent, box)
            var shapedIcons = new Dictionary<(ImagePosition, (double, double), SymbolAnchor, double, double, (double, double, double, double), bool),
                (List<IconQuad> Quads, (double Left, double Right) Extent, SymbolBox Box)>();
            var store = style.GlyphStore;
            var sprites = style.SpriteStore;
            int start = _symbolIndices.Count;
            _iconIndices.Clear();
            var flags = SymbolFlags.None;
            if (Flag("text-allow-overlap")) flags |= SymbolFlags.TextAllowOverlap;
            if (Flag("text-ignore-placement")) flags |= SymbolFlags.TextIgnorePlacement;
            if (Flag("icon-allow-overlap")) flags |= SymbolFlags.IconAllowOverlap;
            if (Flag("icon-ignore-placement")) flags |= SymbolFlags.IconIgnorePlacement;
            if (Flag("text-optional")) flags |= SymbolFlags.TextOptional;
            if (Flag("icon-optional")) flags |= SymbolFlags.IconOptional;
            flags |= turnedWithMap;
            bool alwaysOverlap = (flags & SymbolFlags.TextAllowOverlap) != 0, ignorePlacement = (flags & SymbolFlags.TextIgnorePlacement) != 0;
            bool Flag(string name) => GpuStyleLayer.Get(l.Layout, name) is { } v && Expression.Evaluate(v, ctx, Expression.Kind.Any) is true;
            var iconRotateValue = GpuStyleLayer.Get(l.Layout, "icon-rotate");
            bool rotatePerFeature = iconRotateValue is { } rv && Expression.IsFeatureDependent(rv);
            double tileZoom = ctx.Zoom;
            // GL JS symbol_bucket.ts sortFeatures: symbols that may overlap are drawn by their anchor's y
            // (symbol-z-order auto, no sort key), so the lower one is on top; each one's index ranges
            var instances = new List<(int Y, int Feature, int IconStart, int IconCount, int TextStart, int TextCount)>();
            int featureIndex = -1;
            bool textAlongLineSeen = false;   // text along lines: its indices are no instance's, so no sorting
            foreach (var f in features)
            {
                featureIndex++;
                AllocMeter.Start();
                ctx.Zoom = tileZoom;
                ctx.Use(f);
                if (l.Filter is { } filter && !Expression.FilterFeature(filter, ctx, l.Id)) { AllocMeter.Lap(0, "filter"); continue; }
                AllocMeter.Lap(0, "filter");
                var text = TextOf(layer, ctx);
                AllocMeter.Lap(1, "text");
                string? iconName = layer.IconImage is null || iconsOff != null ? null : IconOf(layer, ctx);
                AllocMeter.Lap(2, "icon name");
                bool hasText = !string.IsNullOrEmpty(text), hasIcon = !string.IsNullOrEmpty(iconName);
                if (!hasText && !hasIcon) continue;
                string stack = hasText ? FontStack(layer, ctx) : "";
                AllocMeter.Lap(3, "font stack");
                if (hasText && !store.Ensure(stack, text!, fetch, missing)) { complete = false; continue; }
                if (hasIcon && !sprites.Ensure(fetch, missing)) { complete = false; continue; }
                AllocMeter.Lap(4, "ensure");

                // GL JS: the shaping's text size at the bucket's zoom + 1 and at its zoom; icon-size too
                ctx.Zoom = tileZoom + 1;
                double layoutTextSize = Expression.Number(layer.TextSize, ctx, 16);
                double layoutIconSize = Expression.Number(layer.IconSize, ctx, 1);
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
                // GL JS: a label on a line is one line; along it, centred
                if (placement != "point") maxWidth = double.PositiveInfinity;
                if (alongLine) justify = TextJustify.Center;
                AllocMeter.Lap(5, "layout properties");

                Shaping? shaping = null;
                if (hasText)
                {
                    try
                    {
                        shaping = Shaping.ShapeText(Formatted.FromString(text!), store, stack, maxWidth, lineHeight, anchor, justify,
                            spacing, (offset.X, offset.Y), WritingMode.Horizontal, false, layoutTextSize, layoutTextSizeThisZoom);
                    }
                    catch (StyleUnsupportedException e) when (style.Lenient)
                    {
                        // One label's text, not the layer's: a Thai or Burmese place name too long for one line
                        // (no dictionary to break it, Shaping.cs) dropped label_city for every tile after it -
                        // the user's city names gone "from some time on" (2026-11-23). In a lenient parse the
                        // text goes on one line, as a browser without a segmenter sets it; if even that fails,
                        // only this label is left out. A strict parse (the render tests) still refuses it.
                        style.Skip($"{l.Id}: {e.Message} (a label's text set on one line, or left out)");
                        try
                        {
                            shaping = Shaping.ShapeText(Formatted.FromString(text!), store, stack, double.PositiveInfinity, lineHeight, anchor, justify,
                                spacing, (offset.X, offset.Y), WritingMode.Horizontal, false, layoutTextSize, layoutTextSizeThisZoom);
                        }
                        catch (StyleUnsupportedException) { shaping = null; }
                    }
                }
                AllocMeter.Lap(6, "shaping");
                if (AllocMeter.On && shaping != null) foreach (var pl in shaping.PositionedLines) AllocMeter.Glyphs += pl.PositionedGlyphs.Count;
                ImagePosition? image = hasIcon && sprites.TryGet(iconName!, out var position) ? position : null;
                if (shaping is null && image is null) continue;

                double opacity = layer.OpacityPerFeature ? Expression.Number(layer.Opacity, ctx, 1) : 1;
                double size = layer.SizePerFeature ? layoutTextSizeThisZoom : 0;
                uint color = layer.ColorPerFeature ? Pack(Expression.Color(layer.Color, ctx, StyleColor.Black)) : uint.MaxValue;
                double padding = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-padding"), ctx, 2);
                if (alongLine && shaping != null)
                {
                    // checked here, while building, where a lenient parse drops the one layer: thrown
                    // while placing, it reached the renderer, which stops on any error
                    if (offset.Y != 0) throw new StyleUnsupportedException("text-offset across a line");
                    LineLabels(layer, f, placement, shaping, text!, ctx, layoutTextSize, padding, offset, (float)size, (float)opacity, color,
                        alwaysOverlap, ignorePlacement, repeated);
                    textAlongLineSeen = true;
                    continue;
                }
                SymbolBox? box = shaping is null ? null : CollisionBox(shaping.Left, shaping.Top, shaping.Right, shaping.Bottom,
                    layoutTextSize / Shaping.OneEm, (padding, padding, padding, padding), rotate);

                // GL JS shaping.ts shapeIcon (and fitIconToText round the text), quads.ts getIconQuads and
                // collision_feature.ts's icon box: the image round the anchor in CSS pixels at icon-size 1
                List<IconQuad>? iconQuads = null;
                SymbolBox? iconBox = null;
                (double Left, double Right)? iconExtent = null;
                float iconSize = 0, iconOpacity = 1;
                uint iconColor = uint.MaxValue;
                if (image is { } img)
                {
                    var iconOffset = Pair(GpuStyleLayer.Get(l.Layout, "icon-offset"), ctx, "icon-offset");
                    var iconAnchor = ParseAnchor(Expression.String(GpuStyleLayer.Get(l.Layout, "icon-anchor"), ctx, "center"));
                    if (iconAlongLine && (iconOffset.X != 0 || iconOffset.Y != 0)) throw new StyleUnsupportedException("icon-offset along a line");
                    double iconRotate = Expression.Number(iconRotateValue, ctx, 0);
                    var iconPadding = Padding(GpuStyleLayer.Get(l.Layout, "icon-padding"), ctx, 2, "icon-padding");
                    bool fitted = iconTextFit != "none" && shaping != null;
                    // the same image, offset, anchor, rotation, size and padding: the same quads and box -
                    // every one-way arrow of a layer was shaped anew (4g step 4). Fitted to text: its own.
                    var iconKey = (img, iconOffset, iconAnchor, iconRotate, layoutIconSize, iconPadding, iconTextFit != "none");
                    if (fitted || !shapedIcons.TryGetValue(iconKey, out var shaped))
                    {
                        var shapedIcon = IconShaping.ShapeIcon(img, iconOffset, iconAnchor);
                        if (fitted)
                            shapedIcon = IconShaping.FitIconToText(shapedIcon, shaping!, iconTextFit,
                                Padding(GpuStyleLayer.Get(l.Layout, "icon-text-fit-padding"), ctx, 0, "icon-text-fit-padding"),
                                iconOffset, layoutTextSize / Shaping.OneEm, layoutIconSize);
                        var quads = IconShaping.GetIconQuads(shapedIcon, iconRotate, iconTextFit != "none", layoutIconSize);
                        var (bx1, by1, bx2, by2) = IconShaping.CollisionBox(shapedIcon, layoutIconSize, iconPadding);
                        shaped = (quads, (shapedIcon.Left, shapedIcon.Right), CollisionBox(bx1, by1, bx2, by2, 1, (0, 0, 0, 0), iconRotate * Math.PI / 180));
                        if (!fitted) shapedIcons[iconKey] = shaped;
                    }
                    (iconQuads, iconExtent, iconBox) = shaped;
                    iconSize = layer.IconSizePerFeature ? (float)Expression.Number(layer.IconSize, ctx, 1) : 0;
                    iconOpacity = layer.IconOpacityPerFeature ? (float)Expression.Number(layer.IconOpacity, ctx, 1) : 1;
                    iconColor = layer.IconColorPerFeature ? Pack(Expression.Color(layer.IconColor, ctx, StyleColor.Black)) : uint.MaxValue;
                    // GL JS: the bucket's first icon says SDF or not; linear sampling for another pixel ratio or a rotation
                    // ... and along a line: pitched with the map there (icon-pitch-alignment auto), so "transformed"
                    bool needsLinear = img.Image.PixelRatio != style.PixelRatio || rotatePerFeature || iconRotate != 0 || iconAlongLine;
                    _iconModes[layer.Index] = _iconModes.TryGetValue(layer.Index, out var mode)
                        ? (mode.Sdf, mode.NeedsLinear || needsLinear)
                        : (img.Image.Sdf, needsLinear);
                }

                AllocMeter.Lap(8, "icon and box");
                if (iconAlongLine && iconQuads != null && shaping is null)
                {
                    foreach (var (y, iconStart, iconCount) in IconsAlongLine(layer, f, placement, ctx, iconQuads, iconExtent!.Value, iconBox!.Value,
                        layoutTextSize, iconSize, iconOpacity, iconColor, flags, repeated))
                        instances.Add((y, featureIndex, iconStart, iconCount, 0, 0));
                    AllocMeter.Lap(14, "icons along line");
                    continue;
                }
                // a point's anchors; on a line, upright symbols at the line's anchors (viewport alignment)
                var anchors = placement == "point" ? Anchors(f)
                    : LinePlacements(layer, f, placement, ctx, shaping is null ? null : (shaping.Left, shaping.Right), iconExtent, layoutTextSize, shaping is null ? null : text, repeated)
                        .Select(p => (p.anchor.X / LineUp, p.anchor.Y / LineUp));
                foreach (var (exactX, exactY) in anchors)
                {
                    // GL JS draws a label from the one tile its anchor is in, not from a neighbour's buffer
                    if (exactX < 0 || exactX >= Extent || exactY < 0 || exactY >= Extent) continue;
                    // GL JS keeps the anchor in Int16 arrays of its 8192-unit tile: a polygon's pole or a line's
                    // anchor, fractional, is truncated to whole units (symbol-placement/point-polygon)
                    double ax = Math.Floor(exactX * LineUp) / LineUp, ay = Math.Floor(exactY * LineUp) / LineUp;
                    var anchorPosition = Relative(ax, ay);
                    // the icon first (symbol_layout.ts addSymbol), then the glyphs
                    int iconFirst = _symbolVertices.Count, iconIndexFirst = _iconIndices.Count;
                    if (iconQuads != null) foreach (var q in iconQuads) IconQuadVertices(anchorPosition, q, iconSize, iconOpacity, iconColor);
                    int textFirst = _symbolVertices.Count, textIndexFirst = _symbolIndices.Count - start;
                    if (shaping != null) GlyphQuads(anchorPosition, shaping, offset, rotate, (float)size, (float)opacity, color);
                    int textCount = _symbolVertices.Count - textFirst, iconCount = textFirst - iconFirst;
                    // GL JS's tile is 8192 units, ours 512: its anchor y, rounded
                    instances.Add(((int)Math.Floor(ay * 16 + 0.5), featureIndex, iconIndexFirst, _iconIndices.Count - iconIndexFirst,
                        textIndexFirst, _symbolIndices.Count - start - textIndexFirst));
                    if (textCount + iconCount > 0)
                        _labels.Add(new LabelInfo(layer.Index, anchorPosition, textCount > 0 ? box : null, textFirst, textCount,
                            iconCount > 0 ? iconBox : null, iconFirst, iconCount, flags)
                        {
                            // GL JS: murmur3(shaping.text), or of '' for an icon alone
                            Key = CrossTileSymbolIndex.KeyOf(shaping != null ? text : ""),
                            AnchorX = (int)(ax * LineUp), AnchorY = (int)(ay * LineUp),
                        });
                }
                AllocMeter.Lap(9, "anchors, quads, labels");
            }
            ctx.Zoom = tileZoom;
            // GL JS placement.ts alwaysShowText / alwaysShowIcon, from what the layer's bucket holds
            bool hasIconData = false, hasTextData = false;
            for (int i = _layerLabelStart; i < _labels.Count; i++) { hasIconData |= _labels[i].IconCount > 0; hasTextData |= _labels[i].TextCount > 0; }
            bool textAlways = (flags & SymbolFlags.TextAllowOverlap) != 0, iconAlways = (flags & SymbolFlags.IconAllowOverlap) != 0;
            var always = SymbolFlags.None;
            if (textAlways && (iconAlways || !hasIconData || (flags & SymbolFlags.IconOptional) != 0)) always |= SymbolFlags.AlwaysShowText;
            if (iconAlways && (textAlways || !hasTextData || (flags & SymbolFlags.TextOptional) != 0)) always |= SymbolFlags.AlwaysShowIcon;
            if (always != SymbolFlags.None)
                for (int i = _layerLabelStart; i < _labels.Count; i++) _labels[i] = _labels[i] with { Flags = _labels[i].Flags | always };
            // canOverlap: any *-allow-overlap or *-ignore-placement. Not across text along lines, whose indices are not an instance's
            bool canOverlap = (flags & (SymbolFlags.TextAllowOverlap | SymbolFlags.IconAllowOverlap | SymbolFlags.TextIgnorePlacement | SymbolFlags.IconIgnorePlacement)) != 0;
            if (canOverlap && !textAlongLineSeen && instances.Count > 1)
            {
                var icons = new List<uint>(_iconIndices.Count);
                var texts = new List<uint>(_symbolIndices.Count - start);
                foreach (var i in instances.OrderBy(i => i.Y).ThenByDescending(i => i.Feature))
                {
                    icons.AddRange(_iconIndices.GetRange(i.IconStart, i.IconCount));
                    texts.AddRange(_symbolIndices.GetRange(start + i.TextStart, i.TextCount));
                }
                _iconIndices.Clear(); _iconIndices.AddRange(icons);
                _symbolIndices.RemoveRange(start, _symbolIndices.Count - start); _symbolIndices.AddRange(texts);
            }
            // the layer's icons are drawn before its text (draw_symbol.ts): their indices go first
            int textIndices = _symbolIndices.Count - start;
            if (_iconIndices.Count > 0)
            {
                var textPart = _symbolIndices.GetRange(start, textIndices);
                _symbolIndices.RemoveRange(start, textIndices);
                _symbolIndices.AddRange(_iconIndices);
                _ranges.Add(new VectorRange(layer.Index, RangeKind.Icon, start, _iconIndices.Count));
                _symbolIndices.AddRange(textPart);
                start += _iconIndices.Count;
                _iconIndices.Clear();
            }
            if (textIndices > 0) _ranges.Add(new VectorRange(layer.Index, RangeKind.Text, start, textIndices));
        }

        // GL JS symbol_bucket.ts addSymbols / addVertex for one quad of an icon: the corners x 32
        // (Math.round); the atlas corner, the pixel offsets x 16 and the min font scale x 256 into
        // integer arrays, which truncate
        void IconQuadVertices(Vector3 anchor, IconQuad q, float size, float opacity, uint color)
        {
            uint i = (uint)_symbolVertices.Count;
            Corner(q.Tl, q.TexX, q.TexY, q.PixelOffsetTl.X, q.PixelOffsetTl.Y);
            Corner(q.Tr, q.TexX + q.TexW, q.TexY, q.PixelOffsetBr.X, q.PixelOffsetTl.Y);
            Corner(q.Bl, q.TexX, q.TexY + q.TexH, q.PixelOffsetTl.X, q.PixelOffsetBr.Y);
            Corner(q.Br, q.TexX + q.TexW, q.TexY + q.TexH, q.PixelOffsetBr.X, q.PixelOffsetBr.Y);
            _iconIndices.Add(i); _iconIndices.Add(i + 1); _iconIndices.Add(i + 2);
            _iconIndices.Add(i + 1); _iconIndices.Add(i + 2); _iconIndices.Add(i + 3);

            void Corner((double X, double Y) c, double tx, double ty, double px, double py) => _symbolVertices.Add(new SymbolVertex
            {
                Anchor = anchor,
                OffsetX = (short)Math.Floor(c.X * 32 + 0.5),
                OffsetY = (short)Math.Floor(c.Y * 32 + 0.5),
                TexX = (short)tx, TexY = (short)ty,
                TextSize = (System.Half)size, Opacity = (System.Half)opacity, Color = color,
                PixelOffsetX = (short)(px * 16), PixelOffsetY = (short)(py * 16),
                MinFontScaleX = (short)Math.Min(short.MaxValue, q.MinFontScaleX * 256), MinFontScaleY = (short)Math.Min(short.MaxValue, q.MinFontScaleY * 256),
            });
        }

        // GL JS symbol_layout.ts addFeature for symbol-placement line / line-center, then addSymbol's text
        // along the line. GL JS's tile is 8192 units, ours 512: the anchors are found at GL JS's scale (x 16),
        // so they round as GL JS rounds them.
        void LineLabels(GpuStyleLayer layer, StyleFeature f, string placement, Shaping shaping, string text, StyleContext ctx,
            double layoutTextSize, double padding, (double X, double Y) offset, float size, float opacity, uint color,
            bool alwaysOverlap, bool ignorePlacement, Dictionary<string, List<(double X, double Y)>> repeated)
        {
            const double Up = LineUp;
            var l = layer.Layer;
            bool keepUpright = GpuStyleLayer.Get(l.Layout, "text-keep-upright") is not { } ku || Expression.Evaluate(ku, ctx, Expression.Kind.Any) is not false;
            bool rotateToLine = Expression.String(GpuStyleLayer.Get(l.Layout, "text-rotation-alignment"), ctx, "auto") is "map" or "auto";
            double height = shaping.Bottom - shaping.Top;
            float diameter = height > 0 ? (float)(Math.Max(10, height) * layoutTextSize / Shaping.OneEm) : 0;

                    foreach (var (used, anchor) in LinePlacements(layer, f, placement, ctx, (shaping.Left, shaping.Right), null, layoutTextSize, text, repeated))
                    {
                        var anchorPosition = Relative(anchor.X / Up, anchor.Y / Up);
                        var (lineStart, lineCount) = LineOf(used, Up);
                        int first = _symbolVertices.Count;
                        int glyphStart = _glyphOffsets.Count;
                        foreach (var lineGlyphs in shaping.PositionedLines)
                            foreach (var g in lineGlyphs.PositionedGlyphs)
                            {
                                if (g.Rect is not { } r) continue;
                                // GL JS getGlyphQuads along a line: the quad round the glyph's own point,
                                // which is glyphOffset (x + half the advance) along the line, y across it
                                double halfAdvance = g.Metrics.Advance * g.Scale / 2;
                                _glyphOffsets.Add((float)(g.X + halfAdvance));
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
                        if (_glyphOffsets.Count == glyphStart) continue;
                        _lineLabels.Add(new LineLabel
                        {
                            Layer = layer.Index, Anchor = anchorPosition, Segment = anchor.Segment,
                            LineStart = lineStart, LineCount = lineCount, GlyphStart = glyphStart, GlyphCount = _glyphOffsets.Count - glyphStart,
                            VertexStart = first,
                            LineOffsetX = (float)offset.X, LineOffsetY = (float)offset.Y, KeepUpright = keepUpright, RotateToLine = rotateToLine,
                            CircleDiameter = diameter, Padding = (float)padding, FeatureSize = size,
                            AlwaysOverlap = alwaysOverlap, IgnorePlacement = ignorePlacement,
                            Key = CrossTileSymbolIndex.KeyOf(text), AnchorX = (int)Math.Floor(anchor.X), AnchorY = (int)Math.Floor(anchor.Y),
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

        // GL JS's tile is 8192 units, ours 512: line anchors are found at GL JS's scale
        const double LineUp = 16;

        // An icon alone along a line (icon-rotation-alignment map): GL JS places it as a glyph at offset 0
        // along the projected line, turned to it (icon-keep-upright, false by default, may flip it), and
        // collides it as its box round the anchor
        // Each one's anchor y (GL JS units, rounded) and its icon index range, for sortFeatures.
        // The line a label along a line is walked on, relative to the bucket's centre: one array for all the
        // anchors on one clipped line, not one each (a long road's every one-way arrow held the whole road -
        // 13.8 MB of a z12 tile, and kept with it; GL JS keeps a feature's line once, 4g step 4). Read only.
        // Now a range of one array a bucket (_linePoints3), with the glyph offsets in another: no array a label.
        object? _lineOfUsed;
        double _lineOfScale;
        (int Start, int Count) _lineOf;
        readonly List<Vector3> _linePoints3 = new();
        readonly List<float> _glyphOffsets = new();

        (int Start, int Count) LineOf(IReadOnlyList<(double X, double Y)> used, double scale)
        {
            if (!ReferenceEquals(used, _lineOfUsed) || scale != _lineOfScale)
            {
                int start = _linePoints3.Count;
                for (int i = 0; i < used.Count; i++) _linePoints3.Add(Relative(used[i].X / scale, used[i].Y / scale));
                (_lineOfUsed, _lineOfScale, _lineOf) = (used, scale, (start, used.Count));
            }
            return _lineOf;
        }

        // the labels' lines and offsets as ranges of the finished bucket's two arrays
        List<LineLabel> FinishLineLabels()
        {
            var points = _linePoints3.ToArray();
            var offsets = _glyphOffsets.ToArray();
            var labels = new List<LineLabel>(_lineLabels.Count);
            foreach (var label in _lineLabels)
            {
                var l = label;
                l.Line = new ReadOnlyMemory<Vector3>(points, l.LineStart, l.LineCount);
                l.GlyphOffsets = new ReadOnlyMemory<float>(offsets, l.GlyphStart, l.GlyphCount);
                labels.Add(l);
            }
            return labels;
        }

        List<(int Y, int IconStart, int IconCount)> IconsAlongLine(GpuStyleLayer layer, StyleFeature f, string placement, StyleContext ctx, List<IconQuad> quads,
            (double Left, double Right) extent, SymbolBox box, double layoutTextSize, float size, float opacity, uint color,
            SymbolFlags flags, Dictionary<string, List<(double X, double Y)>> repeated)
        {
            var placed = new List<(int Y, int IconStart, int IconCount)>();
            var l = layer.Layer;
            bool keepUpright = GpuStyleLayer.Get(l.Layout, "icon-keep-upright") is { } ku && Expression.Evaluate(ku, ctx, Expression.Kind.Any) is true;
            foreach (var (used, anchor) in LinePlacements(layer, f, placement, ctx, null, extent, layoutTextSize, null, repeated))
            {
                var anchorPosition = Relative(anchor.X / LineUp, anchor.Y / LineUp);
                int first = _symbolVertices.Count, iconIndexFirst = _iconIndices.Count;
                foreach (var q in quads) IconQuadVertices(anchorPosition, q, size, opacity, color);
                placed.Add(((int)Math.Floor(anchor.Y + 0.5), iconIndexFirst, _iconIndices.Count - iconIndexFirst));
                var (lineStart, lineCount) = LineOf(used, LineUp);
                int glyphStart = _glyphOffsets.Count;
                for (int q = 0; q < quads.Count; q++) _glyphOffsets.Add(0);   // every quad at the anchor
                _lineLabels.Add(new LineLabel
                {
                    Layer = layer.Index, Anchor = anchorPosition, Segment = anchor.Segment,
                    LineStart = lineStart, LineCount = lineCount, GlyphStart = glyphStart, GlyphCount = quads.Count,
                    VertexStart = first, KeepUpright = keepUpright,
                    FeatureSize = size, IconBox = box,
                    AlwaysOverlap = (flags & SymbolFlags.IconAllowOverlap) != 0, IgnorePlacement = (flags & SymbolFlags.IconIgnorePlacement) != 0,
                    Key = CrossTileSymbolIndex.KeyOf(""), AnchorX = (int)Math.Floor(anchor.X), AnchorY = (int)Math.Floor(anchor.Y),
                });
            }
            return placed;
        }

        // GL JS symbol_layout.ts addFeature for symbol-placement line / line-center: the anchors along the
        // feature's lines (GL JS units), each with the line it lies on - only those inside the tile, as GL
        // JS draws a symbol from the one tile its anchor is in. With text, the same text again within half
        // the spacing is left out (anchorIsTooClose).
        List<(IReadOnlyList<(double X, double Y)> line, LineAnchor anchor)> LinePlacements(GpuStyleLayer layer, StyleFeature f, string placement,
            StyleContext ctx, (double Left, double Right)? shapedText, (double Left, double Right)? shapedIcon, double layoutTextSize,
            string? text, Dictionary<string, List<(double X, double Y)>> repeated)
        {
            const double Up = LineUp, GlExtent = Extent * Up;
            var l = layer.Layer;
            // GL JS's tilePixelRatio, EXTENT / (512 x overscaling): tile units a pixel of the overscaled tile
            double tilePixelRatio = Up / Overscale;
            double spacing = Expression.Number(GpuStyleLayer.Get(l.Layout, "symbol-spacing"), ctx, 250) * tilePixelRatio;
            double maxAngle = Expression.Number(GpuStyleLayer.Get(l.Layout, "text-max-angle"), ctx, 45) / 180 * Math.PI;
            double boxScale = tilePixelRatio * layoutTextSize / Shaping.OneEm;   // textMaxBoxScale: no composite text-size here
            var result = new List<(IReadOnlyList<(double X, double Y)> line, LineAnchor anchor)>();
            foreach (var part in f.Parts)
                foreach (var ring in part)
                {
                    if (f.Kind == StyleFeature.Shape.Point || ring.Length < 2) continue;
                    // no LINQ and no list between: 1.6 KB a feature of a z12 tile's one-way arrows (4g step 4)
                    var line = new List<(double X, double Y)>(ring.Length);
                    foreach (var (px, py) in ring) { var (x, y) = Local(px, py); line.Add((x * Up, y * Up)); }
                    var placements = result;
                    int from = result.Count;
                    if (placement == "line")
                    {
                        foreach (var clipped in LineAnchors.ClipLine(new[] { line }, 0, 0, GlExtent, GlExtent))
                            foreach (var a in LineAnchors.GetAnchors(clipped, spacing, maxAngle, shapedText, shapedIcon, Shaping.OneEm, boxScale, Overscale, GlExtent))
                                if (text is null || !TooClose(a)) placements.Add((clipped, a));
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
                            if (piece.Count > 1 && LineAnchors.GetCenterAnchor(piece, maxAngle, shapedText, shapedIcon, Shaping.OneEm, boxScale) is { } centre)
                                placements.Add((piece, centre));
                    }
                    // only the anchors inside the tile, kept in order
                    int to = from;
                    for (int i = from; i < result.Count; i++)
                    {
                        var a = result[i].anchor;
                        if (a.X >= 0 && a.X < GlExtent && a.Y >= 0 && a.Y < GlExtent) result[to++] = result[i];
                    }
                    result.RemoveRange(to, result.Count - to);
                }
            return result;

            // GL JS anchorIsTooClose
            bool TooClose(LineAnchor a)
            {
                double repeatDistance = spacing / 2;
                if (!repeated.TryGetValue(text!, out var others)) repeated[text!] = others = new List<(double X, double Y)>();
                foreach (var o in others)
                    if (Math.Sqrt((a.X - o.X) * (a.X - o.X) + (a.Y - o.Y) * (a.Y - o.Y)) < repeatDistance) return true;
                others.Add((a.X, a.Y));
                return false;
            }
        }

        // GL JS collision_feature.ts, for a label in the viewport: the shaping's box at the layout's text
        // size (textBoxScale, then textPixelRatio: pixels), text-padding round it, rotated with the text
        // (an icon's too: its shaped box at the layout's icon-size, icon-padding [top, right, bottom, left] round it)
        static SymbolBox CollisionBox(double left, double top, double right, double bottom, double scale,
            (double Top, double Right, double Bottom, double Left) padding, double rotate)
        {
            double x1 = left * scale - padding.Left, y1 = top * scale - padding.Top;
            double x2 = right * scale + padding.Right, y2 = bottom * scale + padding.Bottom;
            if (rotate != 0)
            {
                double sin = Math.Sin(rotate), cos = Math.Cos(rotate);
                // Point._rotate: (cos x - sin y, sin x + cos y), the four corners' bounds
                var xs = new[] { cos * x1 - sin * y1, cos * x2 - sin * y1, cos * x1 - sin * y2, cos * x2 - sin * y2 };
                var ys = new[] { sin * x1 + cos * y1, sin * x2 + cos * y1, sin * x1 + cos * y2, sin * x2 + cos * y2 };
                (x1, x2, y1, y2) = (xs.Min(), xs.Max(), ys.Min(), ys.Max());
            }
            return new SymbolBox((float)x1, (float)y1, (float)x2, (float)y2);
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

        // icon-image: a string with tokens, or an expression giving a string (or an image)
        static string? IconOf(GpuStyleLayer layer, StyleContext ctx)
        {
            if (layer.IconImage is not { } image) return null;
            return image.ValueKind == JsonValueKind.String
                ? ResolveTokens(image.GetString()!, ctx.Properties)
                : ToText(Expression.Evaluate(image, ctx, Expression.Kind.Any));
        }

        // two numbers, as given (icon-offset is in pixels, not ems)
        static (double X, double Y) Pair(JsonElement? value, StyleContext ctx, string name)
        {
            if (value is not { } v) return (0, 0);
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && v[0].ValueKind == JsonValueKind.Number)
                return (v[0].GetDouble(), v[1].GetDouble());
            return Expression.Evaluate(v, ctx, Expression.Kind.NumberArray) switch
            {
                JsonElement { ValueKind: JsonValueKind.Array } a when a.GetArrayLength() == 2 => (a[0].GetDouble(), a[1].GetDouble()),
                IReadOnlyList<object?> list when list.Count == 2 && list[0] is double x && list[1] is double y => (x, y),
                _ => throw new StyleUnsupportedException($"an {name} that is not two numbers"),
            };
        }

        // GL JS's Padding: one to four numbers in CSS order (top, right, bottom, left)
        static (double, double, double, double) Padding(JsonElement? value, StyleContext ctx, double fallback, string name)
        {
            if (value is not { } v) return (fallback, fallback, fallback, fallback);
            // up to four numbers, on the stack (a list a call was in every icon's garbage, 4g step 4)
            Span<double> buffer = stackalloc double[5];
            var numbers = new Numbers(buffer);
            object? e = v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                : v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number) ? v
                : Expression.Evaluate(v, ctx, Expression.Kind.Any);
            switch (e)
            {
                case double d: numbers.Add(d); break;
                case JsonElement { ValueKind: JsonValueKind.Number } n: numbers.Add(n.GetDouble()); break;
                case JsonElement { ValueKind: JsonValueKind.Array } a: foreach (var x in a.EnumerateArray()) numbers.Add(x.GetDouble()); break;
                case IEnumerable<object?> list:
                    foreach (var x in list) numbers.Add(x is double d ? d : throw new StyleUnsupportedException($"an {name} that is not numbers"));
                    break;
                default: throw new StyleUnsupportedException($"an {name} that is not numbers");
            }
            return numbers.Count switch
            {
                1 => (numbers[0], numbers[0], numbers[0], numbers[0]),
                2 => (numbers[0], numbers[1], numbers[0], numbers[1]),
                3 => (numbers[0], numbers[1], numbers[2], numbers[1]),
                4 => (numbers[0], numbers[1], numbers[2], numbers[3]),
                _ => throw new StyleUnsupportedException($"an {name} of one to four numbers"),
            };
        }

        // a few numbers on the stack; past four only counted (a padding of five is refused all the same)
        ref struct Numbers(Span<double> items)
        {
            readonly Span<double> _items = items;
            public int Count;
            public void Add(double d) { if (Count < _items.Length) _items[Count] = d; Count++; }
            public double this[int i] => _items[i];
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
            var e = Expression.Evaluate(v, ctx, Expression.Kind.NumberArray);
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
