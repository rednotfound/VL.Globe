using System;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Globe;

// ROADMAP 0.1 item 4 (2026-11-23), VL.Mapsui's Pick on the globe: it answers about a position, not a
// click - wire GlobeWindow's Cursor in for a continuous answer, put a S+H after it for a click (Mapsui's
// reasoning, PickNode.cs there). The arithmetic is Engine/Camera/Picking.cs.

/// <summary>Finds the feature under a point of the screen: the topmost one a FeatureLayer, Geometry or VectorTiles layer draws there.</summary>
/// <remarks>
/// Wire GlobeWindow's View and Cursor, and the layers that go into its Input.
/// Reports continuously; to report on click, put a S+H after it, gated on a button.
/// </remarks>
[ProcessNode(Name = "Pick", Category = "Globe")]
public sealed class GlobePickNode
{
    // vector tiles decoded for picking, the last few (ROADMAP 6d)
    readonly Engine.Camera.VectorPicker _vectors = new();

    /// <param name="view">From GlobeWindow or GlobeCamera.</param>
    /// <param name="input">The layers to look in - what goes into GlobeWindow's Input.</param>
    /// <param name="position">The cursor in projection space - GlobeWindow's Cursor.</param>
    /// <param name="tolerance">Pixels round the cursor that still hit a line or a point.</param>
    /// <param name="hit">True when a feature is under the cursor.</param>
    /// <param name="layer">The name of the layer it is in; for vector tiles the style layer's id.</param>
    /// <param name="longitude">Degrees east under the cursor.</param>
    /// <param name="latitude">Degrees north under the cursor.</param>
    [return: Pin(Name = "Output")]
    public IFeature? Update(out bool hit, out string layer, out double longitude, out double latitude,
        GlobeView? view = null, GlobeLayer? input = null, Vector2 position = default, float tolerance = 4f)
    {
        hit = false; layer = ""; longitude = 0; latitude = 0;
        if (view is null || input is null) return null;
        if (Engine.Camera.Picking.Pick(view, input, position.X, position.Y, tolerance, _vectors) is not { } p) return null;
        hit = true;
        layer = p.Layer;
        longitude = p.Longitude; latitude = p.Latitude;
        return p.Feature;
    }
}
