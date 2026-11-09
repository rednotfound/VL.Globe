using System;
using Stride.Core.Mathematics;
using Microsoft.Extensions.Logging;
using Stride.Engine;
using VL.Core;
using VL.Core.Import;

namespace VL.Globe;

// The /// text is the tooltip. The reasoning lives here and in GlobeView.cs.
//
// Three nodes mirroring VL.Mapsui's Map + Drag + ZoomByWheel (vl-overworld GLOBE-SCOPE-PROPOSAL.md,
// section 9): GlobeCamera holds the state and turns it into a Stride camera; Drag and ZoomByWheel
// change it. The mouse is the patch's: they take the world Ray that vvvv's own Mouse [Stride.Input]
// node gives for the camera it is wired to, so nothing reads input behind the reader's back - the
// rule VL.Mapsui settled on, and the way Stride patches already speak about the cursor.
//
// The state lags the picture by at most one frame (Drag changes the view after the Mouse node
// computed its ray from last frame's camera); the ray is converted to camera space against the pose
// it was computed from, so the lag never shows as drift.

/// <summary>A camera that moves like a map: looks straight down at a place from a height that never reaches the ground.</summary>
/// <remarks>
/// Wire Camera to SceneWindow's Camera, to the scene, to Globe and to a Mouse node; wire View through Drag and ZoomByWheel.
/// </remarks>
[ProcessNode(Name = "GlobeCamera", Category = "Globe")]
public sealed class GlobeCameraNode
{
    readonly GlobeView _view = new();
    readonly Entity _entity = new("GlobeCamera");
    readonly CameraComponent _camera = new() { UseCustomViewMatrix = true, UseCustomAspectRatio = false };
    bool _initialised;

    public GlobeCameraNode() => _entity.Add(_camera);

    /// <param name="initialLongitude">Degrees east to look at, applied once on open.</param>
    /// <param name="initialLatitude">Degrees north to look at, applied once on open.</param>
    /// <param name="initialHeight">Metres above the surface, applied once on open.</param>
    /// <param name="minHeight">The camera never comes lower, in metres.</param>
    /// <param name="maxHeight">The camera never goes higher, in metres.</param>
    /// <param name="radius">The globe's radius in scene units - the same as Globe's.</param>
    /// <param name="fieldOfView">Vertical field of view in degrees.</param>
    /// <param name="view">The camera's state, for Drag and ZoomByWheel.</param>
    /// <param name="longitude">Degrees east straight below the camera.</param>
    /// <param name="latitude">Degrees north straight below the camera.</param>
    /// <param name="height">Metres above the surface.</param>
    [return: Pin(Name = "Camera")]
    public Entity Update(
        out GlobeView view, out double longitude, out double latitude, out double height,
        double initialLongitude = 0, double initialLatitude = 20, double initialHeight = 1.5e7,
        double minHeight = 100, double maxHeight = 2.5e7, float radius = 1f, float fieldOfView = 45f)
    {
        _view.MinHeight = Math.Max(1, minHeight);
        _view.MaxHeight = Math.Max(_view.MinHeight, maxHeight);
        _view.Radius = radius > 0 ? radius : 1f;
        _view.FovY = MathUtil.DegreesToRadians(Math.Clamp(fieldOfView, 1f, 170f));
        if (!_initialised)
        {
            _view.Longitude = initialLongitude;
            _view.Latitude = Math.Clamp(initialLatitude, -89.9, 89.9);
            _initialised = true;
            _view.SetHeight(initialHeight);
        }
        else _view.SetHeight(_view.Height);   // a changed Min/Max applies at once

        _camera.ViewMatrix = _view.ViewMatrix();
        var (near, far) = _view.ClipPlanes();
        _camera.NearClipPlane = near;
        _camera.FarClipPlane = far;
        _camera.VerticalFieldOfView = fieldOfView;

        view = _view;
        longitude = _view.Longitude;
        latitude = _view.Latitude;
        height = _view.Height;
        return _entity;
    }
}

/// <summary>Grab the globe: the point under the cursor when the drag starts stays under the cursor.</summary>
/// <remarks>Ray and Dragging come from a Mouse [Stride.Input] node wired to the GlobeCamera's Camera.</remarks>
[ProcessNode(Name = "Drag", Category = "Globe")]
public sealed class GlobeDragNode
{
    D3? _grabbed;
    bool _was, _sawRay;
    int _frames;
    readonly ILogger _logger;

    public GlobeDragNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="ray">The cursor's ray, from Mouse [Stride.Input].</param>
    /// <param name="dragging">True while the button is held.</param>
    [return: Pin(Name = "View")]
    public GlobeView? Update(GlobeView? view, Ray ray = default, bool dragging = false)
    {
        if (view is null) return view;
        // Proof of life: once, the first frame a ray arrives, and once after 300 frames whatever it is
        // - so a log that shows neither says this node never ran, one that shows a zero ray says the
        // Mouse never heard the window.
        _frames++;
        if (!_sawRay && ray.Direction != Vector3.Zero) { _sawRay = true; _logger.LogInformation("Globe Drag: first ray {Dir} at frame {F}", ray.Direction, _frames); }
        if (_frames == 300) _logger.LogInformation("Globe Drag: alive, 300 frames, ray {Dir}, dragging {D}, height {H:F0} m", ray.Direction, dragging, view.Height);
        // Logged on the press and release edges only: the first rung 4 of this node showed a drag
        // doing nothing, and the log is where a patch nobody is watching can say why.
        if (dragging != _was)
        {
            _was = dragging;
            _logger.LogInformation("Globe Drag: {State}, ray {Dir}, grabbed {Hit}", dragging ? "press" : "release", ray.Direction,
                dragging && ray.Direction != Vector3.Zero ? view.PickDir(view.CameraDirection(new D3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z)))?.ToString() ?? "space" : "-");
        }
        if (!dragging || ray.Direction == Vector3.Zero) { _grabbed = null; return view; }
        var camDir = view.CameraDirection(new D3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        if (_grabbed is null) { _grabbed = view.PickDir(camDir); return view; }   // grabbed space: nothing to hold
        view.DragTo(_grabbed.Value, camDir);
        return view;
    }
}

/// <summary>Zoom toward the cursor by a fixed fraction of a zoom level per wheel notch, never below Min Height.</summary>
/// <remarks>Ray and Wheel Delta come from a Mouse [Stride.Input] node wired to the GlobeCamera's Camera.</remarks>
[ProcessNode(Name = "ZoomByWheel", Category = "Globe")]
public sealed class GlobeZoomByWheelNode
{
    readonly ILogger _logger;

    public GlobeZoomByWheelNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="ray">The cursor's ray, from Mouse [Stride.Input].</param>
    /// <param name="wheelDelta">The wheel's change this frame.</param>
    /// <param name="notchSize">How much Wheel Delta one notch is.</param>
    /// <param name="levelsPerNotch">Zoom levels per notch: 1 halves the height, 0.5 divides it by 1.41.</param>
    [return: Pin(Name = "View")]
    public GlobeView? Update(GlobeView? view, Ray ray = default, float wheelDelta = 0f, float notchSize = 1f, float levelsPerNotch = 0.5f)
    {
        if (view is null || wheelDelta == 0 || notchSize == 0) return view;
        // Measured in the web globes' source, 2026-11-09: MapLibre steps 0.151 zoom levels per wheel
        // notch, Cesium about 6.5 % of the height (~0.1 level) and then replays it with inertia. A full
        // level per notch - VL.Mapsui's 2D default - felt like jumps on a globe. 0.5 sits between,
        // without inertia, and is a pin because the right value is a matter of taste and device.
        var factor = Math.Pow(0.5, levelsPerNotch * wheelDelta / notchSize);
        _logger.LogInformation("Globe ZoomByWheel: delta {Delta}, factor {Factor:F3}, ray {Dir}, height {H:F0} m", wheelDelta, factor, ray.Direction, view.Height);
        var camDir = ray.Direction == Vector3.Zero
            ? new D3(0, 0, 1)
            : view.CameraDirection(new D3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        view.ZoomAt(camDir, factor);
        return view;
    }
}
