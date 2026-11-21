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
// Vector layers on the globe (ROADMAP 0.1 item 4). How they are drawn was decided by experiment
// (item 3, 2026-11-21): DRAPED - each layer is drawn by SkiaSharp into each tile's overlay image and
// laid over the map in the tile material (Engine/TileOverlay.cs), Cesium's and Mapsui's own way to
// put vectors on a globe or a raster map. The user judged it on screen ("问题不大"), and a MapLibre-like
// GPU vector renderer stays a later, separate node (docs/RESEARCH-VECTOR.md).
//
// Shaped as VL.Mapsui's FeatureLayer / Geometry / VectorStyle, so a patch moves between the 2D map and
// the globe by changing the category: Features + Style + Enabled + Name in, a layer out, into Globe's
// (or GlobeWindow's) Layers - drawn in order, first at the bottom.

/// <summary>How a vector layer is drawn: fill, line colour and width in pixels.</summary>
public sealed record GlobeVectorStyle(Color4 FillColor, Color4 LineColor, float LineWidth)
{
    internal static readonly GlobeVectorStyle Default = new(new Color4(1, 0, 0, 0.3f), new Color4(1, 0, 0, 1), 2f);
}

/// <summary>Geometry drawn on the globe in one style; from FeatureLayer or Geometry, for Globe's Layers.</summary>
public sealed class GlobeLayer
{
    internal IReadOnlyList<Geometry> Geometries = Array.Empty<Geometry>();
    internal GlobeVectorStyle Style = GlobeVectorStyle.Default;
    internal bool Enabled = true;
    internal string Name = "";
    // a VectorTiles layer: a MapLibre style drawn by the painter instead of Geometries
    internal Engine.VectorTileService? Vector;
    // drawn by the GPU (Engine/GpuVectorTiles) instead of draped into the tiles' overlays
    internal bool Gpu;

    /// <summary>True when the layer has something to drape.</summary>
    internal bool HasContent => Vector is { Document: not null } || Geometries.Count > 0;

    // Content, not identity: FromWKT and the collection nodes build new objects every frame, and the
    // tiles redraw their overlays only when this changes.
    internal long Signature()
    {
        var h = new HashCode();
        h.Add(Enabled);
        if (Vector is { } v) { h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v)); h.Add(v.Version); h.Add(Gpu); }
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
    /// <param name="enabled">Hides the layer while off.</param>
    /// <param name="name">The layer's name.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(IEnumerable<IFeature>? features = null, GlobeVectorStyle? style = null, bool enabled = true, string name = "Features")
    {
        _layer.Set(features?.Select(f => f?.Geometry), style, enabled, name);
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
    /// <param name="enabled">Hides the layer while off.</param>
    /// <param name="name">The layer's name.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(IEnumerable<Geometry>? geometries = null, GlobeVectorStyle? style = null, bool enabled = true, string name = "Geometry")
    {
        _layer.Set(geometries, style, enabled, name);
        return _layer;
    }
}

// VectorTiles (ROADMAP 4c): a MapLibre style - fills, lines and the background - from vector tiles
// fetched as the globe needs them. Drawn by the GPU (docs/DESIGN-GPU-VECTOR.md: geometry built once
// per tile, drawn every frame, lines a constant width on screen), or draped into the tiles'
// overlays like the layers above - the first path, kept to compare and to fall back on (the user,
// 2026-11-21: "我感觉不太行" about the drape). The painter is measured against MapLibre's own render
// tests (MapLibreRenderFacts). What the subset cannot draw yet (labels, icons, 3D buildings,
// patterns, dashes) is left out and counted in Status, not an error: every real style has some.

/// <summary>How VectorTiles is drawn.</summary>
public enum GlobeVectorRendering
{
    /// <summary>Geometry on the GPU, drawn every frame: sharp at every zoom.</summary>
    GPU,
    /// <summary>Drawn into each tile's image on the CPU.</summary>
    Draped,
}

/// <summary>A MapLibre style's vector tiles, drawn on the globe.</summary>
/// <remarks>Put Output into Globe's or GlobeWindow's Layers. Fetches only while Enabled is on. Show the Attribution. Labels and icons are not drawn yet.</remarks>
[ProcessNode(Name = "VectorTiles", Category = "Globe.Layers")]
public sealed class GlobeVectorTilesNode : IDisposable
{
    readonly GlobeLayer _layer = new();
    Engine.VectorTileService? _service;

    /// <param name="attribution">The credit the style's tile sources require.</param>
    /// <param name="status">What is drawn, what is skipped, and the tiles loading.</param>
    /// <param name="style">A MapLibre style: its URL, or the path of a style .json file.</param>
    /// <param name="enabled">Fetches and draws while on.</param>
    /// <param name="name">The layer's name.</param>
    /// <param name="rendering">GPU: geometry drawn every frame. Draped: drawn into the tiles' images.</param>
    [return: Pin(Name = "Output")]
    public GlobeLayer Update(out string attribution, out string status,
        string style = "https://tiles.openfreemap.org/styles/liberty", bool enabled = false, string name = "VectorTiles",
        GlobeVectorRendering rendering = GlobeVectorRendering.GPU)
    {
        style = (style ?? "").Trim();
        if (_service?.StyleUrl != style)
        {
            _service?.Dispose();
            _service = style.Length == 0 ? null : new Engine.VectorTileService(style);
        }
        if (enabled) _service?.Start();
        _layer.Set(null, null, enabled && _service != null, name);
        _layer.Vector = _service;
        _layer.Gpu = rendering == GlobeVectorRendering.GPU;
        attribution = _service?.Attribution ?? "";
        status = _service is null ? "no Style"
            : !enabled ? "off - switch Enabled on to fetch"
            : _service.Status;
        return _layer;
    }

    public void Dispose() => _service?.Dispose();
}
