using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using VL.Core;
using VL.Core.Import;
using VL.Globe.Engine;

namespace VL.Globe;

// The /// text is the tooltip; the reasoning lives here and in Engine/Navigation.cs.
//
// ROADMAP 0.1 item 5 (2026-11-23), in VL.Mapsui's shape: its Navigate nodes, each a node of its own so
// the patch decides what drives it (a bang, a key, OSC, a timeline). Unlike Mapsui's CenterOn and
// ZoomToLevel these act on a Trigger's rising edge: the globe's view is also moved by Drag and
// ZoomByWheel every frame, and a node that set it every frame would hold it still against them.
// Each passes the view on (Output, as ZoomByWheel), so they chain after GlobeCamera's View.
// After a move each writes the view to the log as MapLibre's hash, as the gestures do.

/// <summary>Puts a longitude and latitude at the centre of the view each time Trigger turns on.</summary>
[ProcessNode(Name = "CenterOn", Category = "Globe.Navigate")]
public sealed class GlobeCenterOnNode
{
    readonly ILogger _logger;
    bool _was;

    public GlobeCenterOnNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="trigger">Moves once when this turns on.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, double longitude = 0, double latitude = 0, bool trigger = false)
    {
        bool rising = trigger && !_was;
        _was = trigger;
        if (view is null || !rising) return view;
        var s = ViewState.Of(view);
        (s with { Longitude = longitude, Latitude = latitude }).ApplyTo(view);
        GlobeViewLog.Write(_logger, view);
        return view;
    }
}

/// <summary>Zooms to a web-map zoom level each time Trigger turns on: 0 is the whole world, 19 a building.</summary>
/// <remarks>The zoom of VL.Mapsui and GlobeViewInfo: 256-pixel tiles, at the latitude of the centre of the view.</remarks>
[ProcessNode(Name = "ZoomToLevel", Category = "Globe.Navigate")]
public sealed class GlobeZoomToLevelNode
{
    readonly ILogger _logger;
    bool _was;

    public GlobeZoomToLevelNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="zoomLevel">The zoom level, 0 to 22; fractions allowed.</param>
    /// <param name="trigger">Zooms once when this turns on.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, double zoomLevel = 12, bool trigger = false)
    {
        bool rising = trigger && !_was;
        _was = trigger;
        if (view is null || !rising) return view;
        view.SetHeight(Navigation.HeightForZoom(view, Math.Clamp(zoomLevel, 0, 22), view.Latitude));
        GlobeViewLog.Write(_logger, view);
        return view;
    }
}

/// <summary>Zooms in one level about the centre of the view each time Trigger turns on, eased.</summary>
[ProcessNode(Name = "ZoomIn", Category = "Globe.Navigate")]
public sealed class GlobeZoomInNode
{
    readonly ZoomSteps _steps;
    public GlobeZoomInNode(NodeContext nodeContext) => _steps = new ZoomSteps(nodeContext.GetLogger(), +1);

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="smoothing">Seconds the zoom takes. 0: at once.</param>
    /// <param name="trigger">Zooms in once when this turns on; held on, still once.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, float smoothing = 0.3f, bool trigger = false) => _steps.Update(view, trigger, smoothing);
}

/// <summary>Zooms out one level about the centre of the view each time Trigger turns on, eased.</summary>
[ProcessNode(Name = "ZoomOut", Category = "Globe.Navigate")]
public sealed class GlobeZoomOutNode
{
    readonly ZoomSteps _steps;
    public GlobeZoomOutNode(NodeContext nodeContext) => _steps = new ZoomSteps(nodeContext.GetLogger(), -1);

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="smoothing">Seconds the zoom takes. 0: at once.</param>
    /// <param name="trigger">Zooms out once when this turns on; held on, still once.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, float smoothing = 0.3f, bool trigger = false) => _steps.Update(view, trigger, smoothing);
}

// ZoomIn and ZoomOut: one level a rising edge, played out as the wheel's notches are (WheelSmoother)
internal sealed class ZoomSteps(ILogger logger, int direction)
{
    readonly WheelSmoother _smoother = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _last;
    bool _was, _zooming;

    public GlobeView? Update(GlobeView? view, bool trigger, float smoothing)
    {
        double now = _clock.Elapsed.TotalSeconds, dt = now - _last;
        _last = now;
        if (trigger && !_was) _smoother.Add(direction);
        _was = trigger;
        if (view is null) return view;
        var levels = _smoother.Step(dt, smoothing);
        if (levels == 0)
        {
            if (_zooming) { _zooming = false; GlobeViewLog.Write(logger, view); }
            return view;
        }
        _zooming = true;
        var before = view.Height;
        view.ZoomAt(view.CameraDirectionFromProjection(0, 0), Math.Pow(0.5, levels));
        if (view.Height == before) _smoother.Clear();
        return view;
    }
}

/// <summary>Flies to a place each time Trigger turns on: up, across and down along the shortest way, as MapLibre's flyTo.</summary>
/// <remarks>A drag or the wheel during the flight ends it where it is.</remarks>
[ProcessNode(Name = "FlyTo", Category = "Globe.Navigate")]
public sealed class GlobeFlyToNode
{
    readonly ILogger _logger;
    readonly System.Diagnostics.Stopwatch _clock = new();
    Flight? _flight;
    ViewState _wrote;
    bool _was;

    public GlobeFlyToNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="longitude">Degrees east to arrive at.</param>
    /// <param name="latitude">Degrees north to arrive at.</param>
    /// <param name="zoomLevel">The zoom level to arrive at, as ZoomToLevel's.</param>
    /// <param name="heading">Degrees clockwise from north the top of the view points to at the end.</param>
    /// <param name="tilt">Degrees from looking straight down at the end.</param>
    /// <param name="duration">Seconds the flight takes; 0 chooses by the distance, as MapLibre.</param>
    /// <param name="trigger">Starts a flight when this turns on.</param>
    /// <param name="flying">True while on the way.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, out bool flying, double longitude = 0, double latitude = 0, double zoomLevel = 12,
        double heading = 0, double tilt = 0, double duration = 0, bool trigger = false)
    {
        bool rising = trigger && !_was;
        _was = trigger;
        flying = false;
        if (view is null) return view;
        if (rising)
        {
            var to = new ViewState(longitude, Math.Clamp(latitude, -89.9, 89.9),
                Navigation.HeightForZoom(view, Math.Clamp(zoomLevel, 0, 22), latitude), heading, Math.Clamp(tilt, 0, view.MaxTilt));
            _flight = new Flight(ViewState.Of(view), to, view.FovY, duration);
            _clock.Restart();
        }
        else if (_flight != null && !_wrote.Holds(view))
        {
            // something else moved the view since the last frame (a drag, the wheel): the flight ends there
            _flight = null;
            GlobeViewLog.Write(_logger, view);
        }
        if (_flight is null) return view;
        double t = _clock.Elapsed.TotalSeconds;
        _flight.At(t).ApplyTo(view);
        _wrote = ViewState.Of(view);
        if (t >= _flight.Duration) { _flight = null; GlobeViewLog.Write(_logger, view); }
        else flying = true;
        return view;
    }
}

/// <summary>Shows a geometry whole each time Trigger turns on: centred, looking straight down, a margin round it.</summary>
/// <remarks>The geometry in degrees: x longitude, y latitude.</remarks>
[ProcessNode(Name = "ZoomToBounds", Category = "Globe.Navigate")]
public sealed class GlobeZoomToBoundsNode
{
    readonly ILogger _logger;
    bool _was;

    public GlobeZoomToBoundsNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="geometry">What to show, in degrees.</param>
    /// <param name="margin">The share of the view left round it, 0 to 0.9.</param>
    /// <param name="trigger">Moves once when this turns on.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, Geometry? geometry = null, float margin = 0.1f, bool trigger = false)
    {
        bool rising = trigger && !_was;
        _was = trigger;
        if (view is null || !rising || geometry is null || geometry.IsEmpty) return view;
        if (Navigation.Fit(Points(geometry), view, margin) is not { } fit) return view;
        new ViewState(fit.Longitude, fit.Latitude, fit.Height, view.Heading, 0).ApplyTo(view);
        GlobeViewLog.Write(_logger, view);
        return view;
    }

    static IEnumerable<(double, double)> Points(Geometry g)
    {
        foreach (var c in g.Coordinates) yield return (c.X, c.Y);
    }
}
