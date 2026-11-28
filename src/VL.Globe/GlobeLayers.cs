using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using Stride.Core.Mathematics;
using VL.Core.Import;

namespace VL.Globe;

// The /// text is the tooltip; the reasoning is here.
//
// Vector layers on the globe (ROADMAP 0.1 item 4). Draped into the tiles' images by SkiaSharp at first
// (item 3, 2026-11-21, "问题不大"); since 2026-11-24 every FeatureLayer, Geometry and Graticule is a GPU
// layer of its own, as MapLibre's GeoJSON layers, with or without VectorTiles - the user: "有vectorTiles和
// 没有都应该统一的画法 ... 肯定是要作为独立的layer存在" (GlobeNode.ShapesOnTheGpu, Styling/GeometryStyle).
//
// Shaped as VL.Mapsui's FeatureLayer / Geometry / VectorStyle, so a patch moves between the 2D map and
// the globe by changing the category: Features + Style + Name + Enabled in, a layer out.
//
// One Input, layers composed upstream (docs/DESIGN-NODES.md, the user's decision 2026-11-23): as VL.Skia's
// Renderer takes one layer and Group composes them ("leftmost is drawn first"), Globe and GlobeWindow take
// one GlobeLayer and Group / Group (Spectral) here compose them. Every surface thing is a layer -
// OpenStreetMap and XyzTiles too (TileSources.cs) - as in Mapsui, MapLibre, OpenLayers and deck.gl, the
// basemap being the bottom one.

/// <summary>How a vector layer is drawn: fill, line colour and width in pixels.</summary>
public sealed record GlobeVectorStyle(Color4 FillColor, Color4 LineColor, float LineWidth)
{
    internal static readonly GlobeVectorStyle Default = new(new Color4(1, 0, 0, 0.3f), new Color4(1, 0, 0, 1), 2f);
}

/// <summary>Something drawn on the globe's surface: tiles, features or vector tiles, or a group of them. For Globe's or GlobeWindow's Input.</summary>
public sealed class GlobeLayer
{
    // raster tiles (OpenStreetMap, XyzTiles): the globe's tiles come from here
    internal GlobeTileSource? Raster;
    // a group (Group, Group (Spectral)): its layers in order, first at the bottom
    internal IReadOnlyList<GlobeLayer>? Children;
    // a tile layer's opacity: over the layers under it, or for the bottom one over the globe's Background
    internal float Opacity = 1;

    /// <summary>The layers to draw, groups opened, in order (first at the bottom); disabled ones and their groups' contents left out.</summary>
    internal IEnumerable<GlobeLayer> Leaves()
    {
        if (!Enabled) yield break;
        if (Children is null) { yield return this; yield break; }
        foreach (var c in Children)
            if (c is not null)
                foreach (var l in c.Leaves()) yield return l;
    }

    internal IReadOnlyList<Geometry> Geometries = Array.Empty<Geometry>();
    // FeatureLayer's features, one for each of Geometries (Pick hands them back); null for Geometry
    internal IReadOnlyList<IFeature>? Features;
    internal GlobeVectorStyle Style = GlobeVectorStyle.Default;
    internal bool Enabled = true;
    internal string Name = "";
    // FeatureLayer's and Geometry's credit for the user's own data (tile layers carry their source's)
    internal string Attribution = "";
    // a VectorTiles layer: a MapLibre style, drawn by the GPU (Engine/Gpu/GpuVectorTiles)
    internal Engine.VectorTiles.VectorTileService? Vector;

    /// <summary>True when the layer has something to drape.</summary>
    internal bool HasContent => Raster is not null || Vector is { Document: not null } || Geometries.Count > 0;

    // Content, not identity: FromWKT and the collection nodes build new objects every frame, and the
    // tiles redraw their overlays only when this changes.
    internal long Signature()
    {
        var h = new HashCode();
        h.Add(Enabled);
        if (Raster is { } r) { h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r)); h.Add(Opacity); }
        if (Vector is { } v) { h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v)); h.Add(v.Version); }
        h.Add(Style);
        h.Add(Geometries.Count);
        foreach (var g in Geometries)
            foreach (var c in g.Coordinates) { h.Add(c.X); h.Add(c.Y); }
        return h.ToHashCode();
    }

    internal void Set(IEnumerable<Geometry?>? geometries, GlobeVectorStyle? style, bool enabled, string? name)
    {
        Geometries = geometries?.Where(g => g is { IsEmpty: false }).Select(g => g!).ToList() ?? new List<Geometry>();
        Style = style ?? GlobeVectorStyle.Default;
        Enabled = enabled;
        Name = name ?? "";
    }
}

/// <summary>A grid of meridians and parallels on the globe.</summary>
[ProcessNode(Name = "Graticule", Category = "Globe.Layers")]
public sealed class GlobeGraticuleNode
{
    readonly GlobeLayer _layer = new() { Name = "Graticule" };
    double _spacing = double.NaN;

    /// <param name="spacing">Degrees between the lines.</param>
    /// <param name="lineColor">Unconnected gives a translucent white.</param>
    /// <param name="lineWidth">Pixels.</param>
    /// <param name="enabled">Hides the grid while off.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(double spacing = 10, Color4? lineColor = null, float lineWidth = 1f, bool enabled = true)
    {
        // VL.Mapsui's Graticule, a layer of lines drawn by the GPU; built again only when the spacing changes
        if (spacing != _spacing)
        {
            _spacing = spacing;
            _layer.Geometries = Engine.Camera.Picking.Graticule(spacing);
        }
        _layer.Style = new GlobeVectorStyle(new Color4(0, 0, 0, 0), lineColor ?? new Color4(1, 1, 1, 0.5f), Math.Max(0, lineWidth));
        _layer.Enabled = enabled;
        return _layer;
    }
}

/// <summary>Layers drawn as one, leftmost at the bottom.</summary>
/// <remarks>Ctrl+ and Ctrl- add and remove inputs, as Skia's and Stride's Group.</remarks>
[ProcessNode(Name = "Group", Category = "Globe.Layers")]
public sealed class GlobeGroupNode
{
    readonly GlobeLayer _layer = new() { Name = "Group" };

    /// <param name="input">The layers, leftmost at the bottom.</param>
    /// <param name="enabled">Hides all of them while off.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update([Pin(PinGroupKind = VL.Model.PinGroupKind.Collection, PinGroupDefaultCount = 2)] VL.Lib.Collections.Spread<GlobeLayer> input, bool enabled = true)
    {
        _layer.Children = input?.ToArray() ?? Array.Empty<GlobeLayer>();
        _layer.Enabled = enabled;
        return _layer;
    }
}

/// <summary>A spread of layers drawn as one, the first at the bottom.</summary>
[ProcessNode(Name = "Group (Spectral)", Category = "Globe.Layers")]
public sealed class GlobeGroupSpectralNode
{
    readonly GlobeLayer _layer = new() { Name = "Group" };

    /// <param name="input">The layers, the first at the bottom.</param>
    /// <param name="enabled">Hides all of them while off.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(IEnumerable<GlobeLayer>? input = null, bool enabled = true)
    {
        _layer.Children = input?.ToArray() ?? Array.Empty<GlobeLayer>();
        _layer.Enabled = enabled;
        return _layer;
    }
}

/// <summary>How a vector layer is drawn on the globe.</summary>
[Name("Styles")]   // namespace VL.Globe + Styles = category Globe.Styles
public static class GlobeStyleNodes
{
    /// <summary>Fill, line colour and line width for FeatureLayer and Geometry.</summary>
    /// <param name="fillColor">The polygon fill. Unconnected gives a translucent red.</param>
    /// <param name="lineColor">Lines and polygon outlines. Unconnected gives red.</param>
    /// <param name="lineWidth">Line width in pixels.</param>
    [return: Pin(Name = "Output")]
    public static GlobeVectorStyle VectorStyle(Color4? fillColor = null, Color4? lineColor = null, float lineWidth = 2f)
        => new(fillColor ?? GlobeVectorStyle.Default.FillColor, lineColor ?? GlobeVectorStyle.Default.LineColor, Math.Max(0, lineWidth));
}

/// <summary>Features drawn on the globe in one style.</summary>
/// <remarks>Put Output into Globe's or GlobeWindow's Layers.</remarks>
[ProcessNode(Name = "FeatureLayer", Category = "Globe.Layers")]
public sealed class GlobeFeatureLayerNode
{
    readonly GlobeLayer _layer = new();

    /// <param name="features">The features to draw, in longitude and latitude.</param>
    /// <param name="style">How to draw them. Unconnected gives a default style.</param>
    /// <param name="name">The layer's name.</param>
    /// <param name="attribution">The credit the data's source requires, shown bottom right of the view - e.g. © OpenStreetMap contributors for data from OSM.</param>
    /// <param name="enabled">Hides the layer while off.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(IEnumerable<IFeature>? features = null, GlobeVectorStyle? style = null, string name = "Features", string attribution = "", bool enabled = true)
    {
        _layer.Attribution = attribution ?? "";
        // the features Pick can hand back: the ones whose geometry is drawn, in the same order
        var kept = features?.Where(f => f?.Geometry is { IsEmpty: false }).Select(f => f!).ToList();
        _layer.Set(kept?.Select(f => f.Geometry), style, enabled, name);
        _layer.Features = kept;
        return _layer;
    }
}

/// <summary>Shapes drawn on the globe in one style - the shortcut when there are no attributes.</summary>
/// <remarks>Put Output into Globe's or GlobeWindow's Layers.</remarks>
[ProcessNode(Name = "Geometry", Category = "Globe.Layers")]
public sealed class GlobeGeometryLayerNode
{
    readonly GlobeLayer _layer = new();

    /// <param name="geometries">The shapes to draw, in longitude and latitude.</param>
    /// <param name="style">How to draw them. Unconnected gives a default style.</param>
    /// <param name="name">The layer's name.</param>
    /// <param name="attribution">The credit the data's source requires, shown bottom right of the view.</param>
    /// <param name="enabled">Hides the layer while off.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(IEnumerable<Geometry>? geometries = null, GlobeVectorStyle? style = null, string name = "Geometry", string attribution = "", bool enabled = true)
    {
        _layer.Attribution = attribution ?? "";
        _layer.Set(geometries, style, enabled, name);
        return _layer;
    }
}

// VectorTiles (ROADMAP 4c): a MapLibre style - fills, lines and the background - from vector tiles
// fetched as the globe needs them. Drawn by the GPU (docs/DESIGN-GPU-VECTOR.md: geometry built once
// per tile, drawn every frame, lines a constant width on screen), measured against MapLibre's own
// render tests (MapLibreGpuRenderFacts). The first path draped the style into the tiles' overlays;
// judged "不太行" (2026-11-21) and kept as a Rendering choice, it was removed on 2026-11-24.
// What the subset cannot draw yet is left out and counted in Status, not an error.

/// <summary>A MapLibre style's vector tiles, drawn on the globe.</summary>
/// <remarks>Put Output into Globe's or GlobeWindow's Layers. Fetches only while Enabled is on. Show the Attribution. The Style comes from MapLibreStyle (URL) or MapLibreStyle (File).</remarks>
[ProcessNode(Name = "VectorTiles", Category = "Globe.Layers")]
public sealed class GlobeVectorTilesNode : IDisposable
{
    readonly GlobeLayer _layer = new();
    Engine.VectorTiles.VectorTileService? _service;
    MapLibreStyle? _style;
    string _cacheKey = "";

    /// <param name="attribution">The credit the style's tile sources require.</param>
    /// <param name="status">What is drawn, what is skipped, and the tiles loading.</param>
    /// <param name="style">A MapLibre style, from MapLibreStyle (URL) or MapLibreStyle (File).</param>
    /// <param name="name">The layer's name.</param>
    /// <param name="cache">Where its tiles are kept, from TileCache. Unconnected: the default folder.</param>
    /// <param name="enabled">Fetches and draws while on.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(out string attribution, out string status,
        MapLibreStyle? style = null, string name = "VectorTiles", GlobeTileCache? cache = null, bool enabled = false)
    {
        cache ??= GlobeTileCache.Default;
        TileCacheJanitor.Tend(cache);
        // a new style value - another one, or the same file read again - starts over; its tiles come
        // from the disk cache
        if (!ReferenceEquals(style, _style) || cache.Key != _cacheKey)
        {
            _style = style;
            _cacheKey = cache.Key;
            _service?.Dispose();
            _service = style is null ? null : new Engine.VectorTiles.VectorTileService(style.Source, cache);
        }
        if (enabled) _service?.Start();
        _layer.Set(null, null, enabled && _service != null, name);
        _layer.Vector = _service;
        attribution = _service?.Attribution ?? "";
        status = _service is null ? "no Style"
            : !enabled ? "off - switch Enabled on to fetch"
            : _service.Status;
        return _layer;
    }

    public void Dispose() => _service?.Dispose();
}
