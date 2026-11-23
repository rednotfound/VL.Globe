using System;
using Stride.Core.Mathematics;
using Microsoft.Extensions.Logging;
using Stride.Engine;
using Stride.Input;   // IsButtonDown is an extension there
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

/// <summary>A camera that moves like a map: drag to move, the wheel to zoom, right drag to turn and tilt.</summary>
/// <remarks>
/// For GlobeWindow's Camera, or SceneWindow's Camera and the scene with Globe. It reads the mouse of the window it is shown in, as OrbitCamera does.
/// Reset goes back to the Initial values.
/// </remarks>
// DESIGN-NODES step 2 (the user's decision, 2026-11-23): shaped as Stride's OrbitCamera - Initial pins, the
// mouse read by the camera itself (a CameraInputSourceComponent on its entity, which the window rendering
// it fills, as OrbitCamera's patch does), Reset, Enabled last. Drag, ZoomByWheel and DragRotate stay as
// nodes for patches that want their own gestures (Mouse off), and are what this camera runs inside.
[ProcessNode(Name = "GlobeCamera", Category = "Globe")]
public sealed class GlobeCameraNode : IDisposable
{
    readonly GlobeView _view = new();
    readonly Entity _entity = new("GlobeCamera");
    readonly CameraComponent _camera = new() { UseCustomViewMatrix = true, UseCustomAspectRatio = false };
    readonly Engine.GlobeCameraFrame _frame = new();
    readonly VL.Stride.Input.CameraInputSourceComponent _input = new();
    readonly VL.Lib.Basics.Resources.IResourceHandle<global::Stride.Input.InputManager> _inputManager;
    readonly GlobeDragNode _drag;
    readonly GlobeZoomByWheelNode _wheel;
    readonly GlobeDragRotateNode _rotate;
    bool _initialised, _wasReset;
    readonly ILogger _logger;
    // ROADMAP 4g: GL JS's bench motion, only when vvvv was started with VLGLOBE_BENCH=pan
    readonly Engine.CameraBench? _bench = Engine.CameraBench.FromEnvironment();

    public GlobeCameraNode(NodeContext nodeContext)
    {
        _logger = nodeContext.GetLogger();
        _entity.Add(_camera);
        _entity.Add(_frame);
        _entity.Add(_input);
        _frame.View = _view;
        _inputManager = VL.Stride.Resources.GetInputManagerHandle(nodeContext);
        _drag = new GlobeDragNode(nodeContext);
        _wheel = new GlobeZoomByWheelNode(nodeContext);
        _rotate = new GlobeDragRotateNode(nodeContext);
    }

    public void Dispose() => _inputManager.Dispose();

    /// <param name="initialLongitude">Degrees east to look at, on open and on Reset.</param>
    /// <param name="initialLatitude">Degrees north to look at, on open and on Reset.</param>
    /// <param name="initialHeight">Metres above the surface, on open and on Reset.</param>
    /// <param name="initialHeading">Degrees clockwise from north the top of the view points to, on open and on Reset.</param>
    /// <param name="initialTilt">Degrees from looking straight down, on open and on Reset.</param>
    /// <param name="minHeight">The camera never comes lower, in metres.</param>
    /// <param name="maxHeight">The camera never goes higher, in metres.</param>
    /// <param name="fieldOfView">Vertical field of view in degrees.</param>
    /// <param name="maxTilt">The most the view can be tilted, in degrees.</param>
    /// <param name="radius">The globe's radius in scene units - the same as Globe's.</param>
    /// <param name="mouse">Moves with the mouse of the window it is shown in. Off: only Drag, ZoomByWheel and the navigation nodes move it.</param>
    /// <param name="screenHeight">The height of the view in pixels; 0 takes it from the window it is shown in.</param>
    /// <param name="reset">Goes back to the Initial values.</param>
    /// <param name="enabled">Off: the mouse does not move it.</param>
    /// <param name="view">The camera's state, for the navigation nodes, ScreenToGlobe and GlobeViewInfo.</param>
    [return: Pin(Name = "Output")]
    public Entity Update(
        out GlobeView view,
        double initialLongitude = 0, double initialLatitude = 20, double initialHeight = 1.5e7, double initialHeading = 0, double initialTilt = 0,
        double minHeight = 20, double maxHeight = 2.5e7, float fieldOfView = 45f, double maxTilt = 60, float radius = 1f,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] bool mouse = true,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] float screenHeight = 0f,
        bool reset = false, bool enabled = true)
    {
        _view.MinHeight = Math.Max(1, minHeight);
        _view.MaxHeight = Math.Max(_view.MinHeight, maxHeight);
        _view.Radius = radius > 0 ? radius : 1f;
        _view.FovY = MathUtil.DegreesToRadians(Math.Clamp(fieldOfView, 1f, 170f));
        _view.MaxTilt = Math.Clamp(maxTilt, 0, 85);

        // the window this camera is shown in: its mouse, and its size (Stride's SplitMouseDevice reads them so)
        var device = _input.InputSource is { } source ? FirstMouse(source) : null;
        float height = screenHeight > 0 ? screenHeight : device is { SurfaceSize.Y: > 0 } d ? d.SurfaceSize.Y : 1080f;
        _view.Height_px = Math.Max(1, height);   // for GlobeViewInfo's resolution and zoom
        if (_view.Tilt > _view.MaxTilt) _view.RotateBy(0, 0);   // a lowered limit applies at once

        bool resetNow = reset && !_wasReset;
        _wasReset = reset;
        if (!_initialised || resetNow)
        {
            _view.Longitude = initialLongitude;
            _view.Latitude = Math.Clamp(initialLatitude, -89.9, 89.9);
            _view.Heading = Engine.Navigation.WrapHeading(initialHeading);
            _view.Tilt = Math.Clamp(initialTilt, 0, _view.MaxTilt);
            _initialised = true;
            _view.SetHeight(initialHeight);
            if (resetNow) GlobeViewLog.Write(_logger, _view);
        }
        else _view.SetHeight(_view.Height);   // a changed Min/Max applies at once
        _bench?.Step(_view, _logger);

        if (enabled && mouse && device is not null)
        {
            // projection space, y up, as Mouse [Stride.Input]'s Position in Projection
            var p = device.Position;
            var position = new Vector2(p.X * 2 - 1, 1 - p.Y * 2);
            float wheel = 0;
            foreach (var e in _inputManager.Resource.Events)
                if (e is global::Stride.Input.MouseWheelEvent w && ReferenceEquals(w.Device, device)) wheel += w.WheelDelta;
            _drag.Update(_view, position, device.IsButtonDown(global::Stride.Input.MouseButton.Left));
            _wheel.Update(_view, position, wheel);
            _rotate.Update(_view, position, device.IsButtonDown(global::Stride.Input.MouseButton.Right));
        }

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
        return _entity;
    }

    static global::Stride.Input.IMouseDevice? FirstMouse(global::Stride.Input.IInputSource source)
    {
        foreach (var d in source.Devices.Values)
            if (d is global::Stride.Input.IMouseDevice m) return m;
        return null;
    }
}

/// <summary>Drag the globe as MapLibre GL JS drags it: zoomed out the map follows the cursor, near the ground the place you grab stays under it.</summary>
/// <remarks>Position and Dragging come from a Mouse [Stride.Input] node: its Position in Projection and Left Pressed.</remarks>
[ProcessNode(Name = "Drag", Category = "Globe")]
public sealed class GlobeDragNode
{
    GlobeView.DragSession? _drag;
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
            if (!dragging) GlobeViewLog.Write(_logger, view);
        }
        if (!dragging) { _drag = null; return view; }
        // pixels, y down, from projection space (y up)
        double x = (position.X + 1) / 2 * view.Width, y = (1 - position.Y) / 2 * view.Height_px;
        // the rule is chosen at the press (GlobeView.DragSession): zoomed out GL JS's globe pan, near the
        // ground the grab - pressed in space, the horizon's nearest point is held (GlobeView.HorizonToward)
        if (_drag is null) { _drag = new GlobeView.DragSession(view, x, y, camDir); return view; }
        _drag.Move(view, x, y, camDir);
        return view;
    }
}

/// <summary>Turn and tilt the view about its centre: drag sideways to turn, up and down to tilt.</summary>
/// <remarks>Position and Dragging come from a Mouse [Stride.Input] node: its Position in Projection and Right Pressed.</remarks>
// MapLibre's DragRotate handler (right button: x turns the bearing, y the pitch), chosen by the user
// on 2026-11-21. The centre stays where it is - the view's state is centred on it (GlobeView.Tilt).
[ProcessNode(Name = "DragRotate", Category = "Globe")]
public sealed class GlobeDragRotateNode
{
    Vector2? _last;
    readonly ILogger _logger;

    public GlobeDragRotateNode(NodeContext nodeContext) => _logger = nodeContext.GetLogger();

    /// <param name="view">From GlobeCamera, or through Drag and ZoomByWheel.</param>
    /// <param name="position">The cursor in projection space, -1..1 - Mouse's Position in Projection.</param>
    /// <param name="dragging">True while the button is held - Mouse's Right Pressed.</param>
    /// <param name="degreesPerScreen">Degrees the view turns for a drag across the whole width; tilting moves half as fast.</param>
    [return: Pin(Name = "Result")]
    public GlobeView? Update(GlobeView? view, Vector2 position = default, bool dragging = false, float degreesPerScreen = 180f)
    {
        if (view is null) return view;
        if (!dragging)
        {
            if (_last is not null)
            {
                _logger.LogInformation("Globe DragRotate: release, heading {H:F1}, tilt {T:F1}", view.Heading, view.Tilt);
                GlobeViewLog.Write(_logger, view);
            }
            _last = null;
            return view;
        }
        if (_last is not { } last)
        {
            _logger.LogInformation("Globe DragRotate: press, cursor {P}", position);
            _last = position;
            return view;
        }
        var (dh, dt) = Turn(position - last, degreesPerScreen);
        _last = position;
        view.RotateBy(dh, dt);
        return view;
    }

    // Projection space spans 2 across the window: a full-width drag turns degreesPerScreen (deck.gl's
    // 180), tilting half as fast. Dragging RIGHT RAISES the heading - the view turns to look right - as
    // MapLibre (linear), Mapbox, deck.gl and Cesium do. The first version lowered it ("the map turns with
    // the cursor"): true only for the half of the screen above the pivot, which a tilted view is mostly
    // made of, so the horizon ran against the hand (the user, 2026-11-21: "我的鼠标往左转，但是地球的视野是往
    // 右看…体感上面是反的"). Dragging up tilts toward the horizon, as MapLibre, Mapbox and deck.gl do.
    internal static (double heading, double tilt) Turn(Vector2 delta, float degreesPerScreen)
        => (delta.X / 2.0 * degreesPerScreen, delta.Y / 2.0 * degreesPerScreen / 2);
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
    bool _zooming;

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
        // the wheel's zoom played out: where it ended
        if (levels == 0 && _zooming) { _zooming = false; GlobeViewLog.Write(_logger, view); }
        if (levels == 0) return view;
        _zooming = true;
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

// After every gesture, the view in the log as MapLibre's hash: "the problem was at 15:42" is then a
// place, a heading and a tilt that a test can load (GlobeView.TryApplyHash) - the user's request,
// 2026-11-22: "你得给我一种方式让我告诉你我的camera在什么位置，什么角度".
static class GlobeViewLog
{
    internal static void Write(ILogger logger, GlobeView view)
        => logger.LogInformation("Globe view: {Hash} - eye {Eye:F0} m up, zoom {Zoom:F2}", view.Hash(), view.EyeAltitude,
            GlobeView.ZoomOf(view.GroundResolution(), view.Latitude));
}
