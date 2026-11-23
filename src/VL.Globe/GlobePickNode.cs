using System;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Globe;

// ROADMAP 0.1 item 4 (2026-11-23), VL.Mapsui's Pick on the globe: it answers about a position, not a
// click - wire GlobeWindow's Cursor in for a continuous answer, put a S+H after it for a click (Mapsui's
// reasoning, PickNode.cs there). The arithmetic is Engine/Picking.cs.

/// <summary>Finds the feature under a point of the screen: the topmost FeatureLayer or Geometry there.</summary>
/// <remarks>
/// Wire GlobeWindow's View and Cursor, and the layers that go into its Input.
/// Reports continuously; to report on click, put a S+H after it, gated on a button.
/// </remarks>
[ProcessNode(Name = "Pick", Category = "Globe")]
public sealed class GlobePickNode
{
    /// <param name="view">From GlobeWindow or GlobeCamera.</param>
    /// <param name="input">The layers to look in - what goes into GlobeWindow's Input.</param>
    /// <param name="position">The cursor in projection space - GlobeWindow's Cursor.</param>
    /// <param name="tolerance">Pixels round the cursor that still hit a line or a point.</param>
    /// <param name="hit">True when a feature is under the cursor.</param>
    /// <param name="layer">The name of the layer it is in.</param>
    /// <param name="longitude">Degrees east under the cursor.</param>
    /// <param name="latitude">Degrees north under the cursor.</param>
    [return: Pin(Name = "Output")]
    public IFeature? Update(out bool hit, out string layer, out double longitude, out double latitude,
        GlobeView? view = null, GlobeLayer? input = null, Vector2 position = default, float tolerance = 4f)
    {
        hit = false; layer = ""; longitude = 0; latitude = 0;
        if (view is null || input is null) return null;
        if (Engine.Picking.Pick(view, input, position.X, position.Y, tolerance) is not { } p) return null;
        hit = true;
        layer = p.layer.Name;
        longitude = p.longitude; latitude = p.latitude;
        return p.layer.Features is { } features && p.index < features.Count
            ? features[p.index]
            : new Feature(p.layer.Geometries[p.index], new AttributesTable());
    }
}
