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
    readonly Engine.GlobeCameraFrame _frame = new();
    bool _initialised;

    public GlobeCameraNode()
    {
        _entity.Add(_camera);
        _entity.Add(_frame);
    }

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

        // Camera-relative (ROADMAP item 1): the eye stays in double in the frame component, the view
        // is rotation only. An absolute view put the eye at ~1.0 in float - a 0.4 m lattice.
        var (eye, fwd, up) = _view.Pose();
        _frame.Eye = eye;
        _camera.ViewMatrix = GlobeView.ViewRotation(fwd, up);
        // Stride computes the projection for the window's aspect each frame; keep its scale for the
        // gestures (one frame old, which is the frame the cursor was seen in).
        var proj = _camera.ProjectionMatrix;
        if (proj.M11 > 0 && proj.M22 > 0) { _view.ProjX = proj.M11; _view.ProjY = proj.M22; }
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
/// <remarks>Position and Dragging come from a Mouse [Stride.Input] node: its Position in Projection and Left Pressed.</remarks>
[ProcessNode(Name = "Drag", Category = "Globe")]
public sealed class GlobeDragNode
{
    D3? _grabbed;
    bool _was, _sawRay;
    int _frames;
    readonly ILogger _logger;

    public GlobeDragNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="position">The cursor in projection space, -1..1 - Mouse's Position in Projection.</param>
    /// <param name="dragging">True while the button is held.</param>
    // Not "View": an input and an output of one name made the patch generator wire GlobeCamera's View
    // into Drag's OUTPUT, and Drag received null every frame (NOTES.md 2026-11-09). VL.Mapsui's Drag
    // calls its output Result for the same reason.
    [return: Pin(Name = "Result")]
    public GlobeView? Update(GlobeView? view, Vector2 position = default, bool dragging = false)
    {
        if (view is null) return view;
        // Proof of life: once, the first frame a ray arrives, and once after 300 frames whatever it is
        // - so a log that shows neither says this node never ran, one that shows a zero ray says the
        // Mouse never heard the window.
        _frames++;
        if (!_sawRay && position != Vector2.Zero) { _sawRay = true; _logger.LogInformation("Globe Drag: first cursor {P} at frame {F}", position, _frames); }
        if (_frames == 300) _logger.LogInformation("Globe Drag: alive, 300 frames, cursor {P}, dragging {D}, height {H:F0} m", position, dragging, view.Height);
        var camDir = view.CameraDirectionFromProjection(position.X, position.Y);
        // Logged on the press and release edges only: the first rung 4 of this node showed a drag
        // doing nothing, and the log is where a patch nobody is watching can say why.
        if (dragging != _was)
        {
            _was = dragging;
            _logger.LogInformation("Globe Drag: {State}, cursor {P}, grabbed {Hit}", dragging ? "press" : "release", position,
                dragging ? view.PickDir(camDir)?.ToString() ?? "space" : "-");
        }
        if (!dragging) { _grabbed = null; return view; }
        if (_grabbed is null) { _grabbed = view.PickDir(camDir); return view; }   // grabbed space: nothing to hold
        view.DragTo(_grabbed.Value, camDir);
        return view;
    }
}

/// <summary>Zoom toward the cursor by a fixed fraction of a zoom level per wheel notch, never below Min Height.</summary>
/// <remarks>Position and Wheel Delta come from a Mouse [Stride.Input] node: its Position in Projection and Mouse Wheel Delta.</remarks>
[ProcessNode(Name = "ZoomByWheel", Category = "Globe")]
public sealed class GlobeZoomByWheelNode
{
    readonly ILogger _logger;
    readonly WheelSmoother _smoother = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _last;

    public GlobeZoomByWheelNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera.</param>
    /// <param name="position">The cursor in projection space, -1..1 - Mouse's Position in Projection.</param>
    /// <param name="wheelDelta">The wheel's change this frame.</param>
    /// <param name="notchSize">How much Wheel Delta one notch is.</param>
    /// <param name="levelsPerNotch">Zoom levels per notch: 1 halves the height, 0.25 divides it by 1.19.</param>
    /// <param name="smoothing">Seconds a notch takes to play out. 0: at once.</param>
    [return: Pin(Name = "Output")]
    public GlobeView? Update(GlobeView? view, Vector2 position = default, float wheelDelta = 0f, float notchSize = 1f, float levelsPerNotch = 0.25f, float smoothing = 0.15f)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _last;
        _last = now;
        if (view is null || notchSize == 0) return view;
        // Measured in the web globes' source, 2026-11-09: MapLibre steps 0.151 zoom levels per wheel
        // notch and eases it over ~200 ms; Cesium ~6.5 % of the height (~0.1 level), then inertia.
        // Ours was 0.5 level applied in one frame: "滚一个就拉近了很多，没有谷歌地球啊，maplibre之类得做得顺滑"
        // (the user, 2026-11-20; one notch is Wheel Delta 1, read from this node's log). So: a smaller
        // step, and every notch played out over Smoothing seconds instead of in one frame.
        if (wheelDelta != 0)
        {
            _smoother.Add(levelsPerNotch * wheelDelta / notchSize);
            _logger.LogInformation("Globe ZoomByWheel: delta {Delta}, pending {Pending:F2} levels, cursor {P}, height {H:F0} m", wheelDelta, _smoother.Pending, position, view.Height);
        }
        var levels = _smoother.Step(dt, smoothing);
        if (levels == 0) return view;
        var before = view.Height;
        view.ZoomAt(view.CameraDirectionFromProjection(position.X, position.Y), Math.Pow(0.5, levels));
        // At Min or Max Height the rest of the wheel has nowhere to go: drop it, or it plays out later
        // as a zoom the user did not ask for when they turn back.
        if (view.Height == before) _smoother.Clear();
        return view;
    }
}

/// <summary>Zoom levels asked for by the wheel and not yet applied, played out exponentially over time.</summary>
internal sealed class WheelSmoother
{
    public double Pending { get; private set; }

    public void Add(double levels) => Pending += levels;

    public void Clear() => Pending = 0;

    /// <summary>The levels to apply this frame: all of them when <paramref name="seconds"/> is 0, else the share an exponential with that time constant covers in <paramref name="dt"/>.</summary>
    public double Step(double dt, double seconds)
    {
        if (Pending == 0) return 0;
        double share = seconds <= 0 ? 1 : 1 - Math.Exp(-Math.Max(0, dt) / (seconds / 3));   // ~95 % within Smoothing
        var step = Pending * share;
        if (Math.Abs(Pending - step) < 1e-4) step = Pending;                                // finish, do not crawl
        Pending -= step;
        return step;
    }
}

