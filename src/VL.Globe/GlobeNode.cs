using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BruTile;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Extensions;   // ToMeshDraw
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.Import;
using Buffer = Stride.Graphics.Buffer;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe;

// The /// text is the tooltip. The reasoning lives in these comments.
//
// The engine the lab could not be (vl-overworld ACT-IV-LAB experiments 5-7): every frame it reads
// the camera, asks TileSelector which tiles the view earns, draws what has arrived (DrawSet: the
// nearest arrived ancestor stands in, never a tile over its own ancestor) and requests what has not,
// coarse first, a few at a time. A process node because it owns GPU buffers, textures, HTTP requests
// and a cache; a static method would rebuild all of them sixty times a second.
//
// Threading: requests run on the thread pool and only enqueue bytes. Every GPU resource is created
// and every entity is added or removed in Update, on vvvv's main thread, a few per frame.
//
// Enabled defaults to off, as everywhere in this family: opening a document runs it, and whoever
// opened it has not agreed to fetch anything yet. Off draws nothing and requests nothing.
//
// In parts: GlobeNode.Tiles.cs, .Show.cs (drawing and fades), .Overlays.cs, .Background.cs, .Perf.cs.

/// <summary>Map tiles on a globe: fetches only the tiles the camera can see, at the zoom its distance earns.</summary>
/// <remarks>
/// Put Entity into the scene. Camera is the camera entity you render with (OrbitCamera's output).
/// On by default, as vvvv's windows are; its layers decide what is fetched. The credits are drawn bottom right.
/// </remarks>
[ProcessNode(Name = "Globe", Category = "Globe")]
public sealed partial class GlobeNode : IDisposable
{

    readonly NodeContext _context;
    readonly ILogger _logger;
    string _status = "off", _lastLogged = "", _lastError = "";
    int _failedCount;
    readonly Entity _root = new("Globe");
    readonly Dictionary<TileKey, Slot> _loaded = new();
    readonly HashSet<TileKey> _pending = new();
    readonly HashSet<TileKey> _failed = new();
    readonly HashSet<TileKey> _shown = new();
    // this frame's wanted tiles: only they fetch vector tiles for their overlays; an ancestor shown
    // while they load draws what is already here (on 2026-11-21 stand-ins fetched 118 tiles, 31 MB,
    // for one street-level view: low-zoom vector tiles reach 1.3 MB)
    HashSet<TileKey> _wantedSet = new();
    readonly ConcurrentQueue<(TileKey key, byte[]? bytes, GlobeTileSource source)> _arrived = new();
    GraphicsDevice? _device;
    bool _deviceLogged;

    // ROADMAP 4g step 4: may tile buffers be made on the workers? D3D11's device is free-threaded unless
    // created SingleThreaded - which Stride never sets itself, the host decides (read from Stride
    // 4.2.1's source, 2026-11-23). So the flags vvvv's device has, read once, by reflection (no SharpDX reference)
    static string DeviceDescription(GraphicsDevice device)
    {
        try
        {
            var native = SharpDXInterop.GetNativeDevice(device);
            var flags = native?.GetType().GetProperty("CreationFlags")?.GetValue(native);
            return $"{GraphicsDevice.Platform} {device.Features.CurrentProfile}, creation flags {flags ?? "unknown"}";
        }
        catch (Exception e) { return $"{GraphicsDevice.Platform}, flags not read: {e.Message}"; }
    }
    GlobeTileSource? _source;
    int _frame, _requests;
    long _bytes;
    float _radius = 1f;
    Vector3 _lastEye;
    D3? _relativeEye;   // this frame's eye when the camera is camera-relative (GlobeCamera), else null

    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // VectorTiles drawn by the GPU (docs/DESIGN-GPU-VECTOR.md): their own entity under the root, drawn
    // after the tiles; the camera's zoom (MapLibre's, 512-px tiles) for their zoom-dependent paint
    readonly GpuVectorTiles _gpu = new();
    double _cameraZoom;
    string _gpuError = "";
    int _gpuBuiltLogged = -1;
    double _gpuLoggedAt = -10;
    string _lightInfo = "";

    public GlobeNode(NodeContext nodeContext)
    {
        _context = nodeContext;
        _logger = nodeContext.GetLogger();
        _root.Transform.Children.Add(_gpu.Entity.Transform);
        // the credits, bottom right over everything (Engine/Tiles/AttributionLayer.cs)
        _skia = new VL.Stride.SkiaRenderer(nodeContext) { Layer = _attributionLayer, Space = VL.Skia.CommonSpace.PixelTopLeft };
        _root.Transform.Children.Add(new Entity("Globe attribution")
        {
            new VL.Stride.Rendering.EntityRendererComponent { Renderer = _skia, RenderStage = VL.Stride.Rendering.DrawerRenderStage.AfterScene },
        }.Transform);
    }

    readonly AttributionLayer _attributionLayer = new();
    readonly VL.Stride.SkiaRenderer _skia;
    string _attribution = "";

    // Every enabled layer's credit, the imagery's first; the same string while they do not change (VL
    // compares by identity)
    string Attribution(GlobeLayer? input)
    {
        var joined = AttributionOf(input);
        if (joined != _attribution) _attribution = joined;
        return _attribution;
    }

    internal static string AttributionOf(GlobeLayer? input)
        => AttributionLayer.Join(input?.Leaves().Select(l => l.Raster is { } r ? r.Attribution : l.Vector is { } v ? v.Attribution : l.Attribution) ?? Enumerable.Empty<string?>());

    /// <param name="input">What to draw on the surface: a layer from OpenStreetMap, XyzTiles, VectorTiles, FeatureLayer or Geometry, or several from Group.</param>
    /// <param name="camera">The camera entity the scene is rendered with.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    /// <param name="background">The globe's colour where no tile has arrived, also while off. Transparent: none.</param>
    /// <param name="screenHeight">The height of the view in pixels: SceneWindow's Client Bounds. GlobeWindow wires it.</param>
    /// <param name="maxZoom">The finest zoom ever requested.</param>
    /// <param name="screenSpaceError">Refine while a tile pixel would cover more screen pixels than this. 1.41 shows tiles at 0.71-1.41x, as MapLibre and Cesium do.</param>
    /// <param name="maxRequestsInFlight">Requests running at once.</param>
    /// <param name="fadeDuration">Seconds a finer tile takes to fade in over the one it replaces. 0: at once.</param>
    /// <param name="enabled">Off: nothing is drawn or requested - the globe shows its Background colour.</param>
    /// <param name="tilesVisible">Tiles drawn this frame.</param>
    /// <param name="tilesLoading">Requests not yet answered.</param>
    /// <param name="requestsSent">Requests made since the source changed - cached tiles are not requests.</param>
    /// <param name="bytesReceived">Bytes of tile data arrived, from the network or the disk cache.</param>
    /// <param name="attribution">Every layer's credit, joined by " | ". Drawn bottom right of the view while Enabled.</param>
    /// <param name="status">Why nothing is drawn, or what is happening.</param>
    [return: Pin(Name = "Entity")]
    public Entity Update(
        out int tilesVisible, out int tilesLoading, out int requestsSent, out long bytesReceived, out string attribution, out string status,
        // vvvv's order (2026-11-23, the user: "vvvv gamma的节点有自己的要求和传统的"): the main inputs first,
        // configuration between, Enabled last - as 237 of the 289 patched nodes with an Enabled pin in
        // vvvv 7.4's packs, and the Gray Book's "Main input left".
        GlobeLayer? input = null, Entity? camera = null, float radius = 1f, Color4 background = default,
        float screenHeight = 1080f, int maxZoom = 19, float screenSpaceError = 1.41f, int maxRequestsInFlight = 6, float fadeDuration = 0.3f,
        // On, as SceneWindow's and Skia's Renderer's Enabled are (the user, 2026-11-23: off, nobody can tell
        // whether the window is working). What is fetched is each layer's matter; help patches still ship
        // with a toggle wired and off (Normalize-HelpPatches), so browsing help fetches nothing.
        bool enabled = true)
    {
        double updateStart = _clock.Elapsed.TotalMilliseconds;
        _stages.Clear();
        _gcPauseAtStart = GC.GetTotalPauseDuration();
        _gen0AtStart = GC.CollectionCount(0); _gen2AtStart = GC.CollectionCount(2);
        // the frame that just ended, taken apart if it missed its vsync (ROADMAP 4g step 4)
        if (_lastUpdateAt >= 0 && _anatomy.Explain(updateStart - _lastUpdateAt, updateStart, _gcPauseAtStart, _gen0AtStart, _gen2AtStart, _gpu.LastDraw) is { } slow)
            _logger.LogInformation("Globe frame: {Frame}", slow);
        // One Input (docs/DESIGN-NODES.md): its layers in order. The bottom tile layer gives the globe's
        // own imagery; every layer after it - more tile layers at their opacity (ROADMAP item 6), the
        // draped vectors - is drawn over it in order, into each tile's overlay.
        var (tileSource, layers) = SplitLayers(input);
        layers = ShapesOnTheGpu(layers);
        try
        {
            var entity = UpdateCore(out tilesVisible, out tilesLoading, out requestsSent, out bytesReceived, out _, out status,
                tileSource, camera, enabled, radius, background, maxZoom, screenSpaceError, maxRequestsInFlight, screenHeight, fadeDuration, layers);
            attribution = Attribution(input);
            _attributionLayer.Text = enabled ? attribution : "";
            return entity;
        }
        finally { Measure(updateStart); }
    }

    /// <summary>
    /// The globe's own imagery and what is drawn over it, from the Input's layers in order: the bottom
    /// tile layer is the imagery when it is opaque; any other layer - and that one too when its Opacity
    /// is below 1, over plain tiles in the Background colour - goes into the overlay.
    /// </summary>
    // The bottom layer's Opacity was first left without effect ("it is the imagery"); the user: "没有必要，
    // 因为用户知道自己再做什么" (2026-11-23). Translucent, it goes the overlay's way like the layers above it.
    internal static (GlobeTileSource? imagery, List<GlobeLayer> over) SplitLayers(GlobeLayer? input)
    {
        var leaves = input?.Leaves().ToList() ?? new List<GlobeLayer>();
        var bottom = leaves.FirstOrDefault(l => l.Raster is not null);
        if (bottom is null || bottom.Opacity < 1) return (null, leaves);
        return (bottom.Raster, leaves.Where(l => !ReferenceEquals(l, bottom)).ToList());
    }

    // Every FeatureLayer, Geometry and Graticule is a layer of its own drawn by the GPU (GeometryStyle), with
    // or without VectorTiles in the Input - as MapLibre's and deck.gl's GeoJSON layers, and Cesium's
    // primitives (Cesium drapes imagery only). They were draped into the tiles' images by Skia unless a
    // VectorTiles layer came before them; the user, 2026-11-24: "有vectorTiles和没有都应该统一的画法 ...
    // 肯定是要作为独立的layer存在". Each made again only when the layer's content changes; services of layers
    // gone are disposed. Raster layers stay in the tiles' images, so shapes are drawn over every one of them.
    readonly Dictionary<GlobeLayer, (long signature, GlobeLayer gpu)> _gpuShapes = new(ReferenceEqualityComparer.Instance);

    List<GlobeLayer> ShapesOnTheGpu(List<GlobeLayer> layers)
    {
        var seen = new HashSet<GlobeLayer>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                if (l.Raster is not null || l.Vector is not null || l.Geometries.Count == 0) continue;
                seen.Add(l);
                long signature = l.Signature();
                if (!_gpuShapes.TryGetValue(l, out var entry) || entry.signature != signature)
                {
                    entry.gpu?.Vector?.Dispose();
                    var service = new Engine.VectorTiles.VectorTileService("shapes:" + l.Name,
                        System.Text.Encoding.UTF8.GetBytes(Engine.Styling.GeometryStyle.Json(l.Geometries, l.Style)));
                    service.Start();
                    entry = (signature, new GlobeLayer { Name = l.Name, Vector = service });
                    _gpuShapes[l] = entry;
                }
                layers[i] = entry.gpu;
            }
        foreach (var gone in _gpuShapes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _gpuShapes[gone].gpu.Vector?.Dispose();
            _gpuShapes.Remove(gone);
        }
        return layers;
    }

    Entity UpdateCore(
        out int tilesVisible, out int tilesLoading, out int requestsSent, out long bytesReceived, out string attribution, out string status,
        GlobeTileSource? tileSource, Entity? camera, bool enabled, float radius, Color4 background,
        int maxZoom, float screenSpaceError, int maxRequestsInFlight, float screenHeight, float fadeDuration,
        IEnumerable<GlobeLayer>? layers)
    {
        _frame++;
        _fadeDuration = Math.Max(0, fadeDuration);
        if (!ReferenceEquals(tileSource, _source)) { Reset(); _source = tileSource; }
        attribution = _source?.Attribution ?? "";
        status = "";

        _device ??= GetDevice();
        if (_device is not null && !_deviceLogged) { _deviceLogged = true; _logger.LogInformation("Globe device: {Device}", DeviceDescription(_device)); }
        if (_device is not null) CreateArrived(_device);
        Stage("arrived");

        if (Math.Abs(radius - _radius) > 0 && radius > 0)
        {
            _radius = radius;
            foreach (var s in _loaded.Values) s.Entity.Transform.Scale = new Vector3(radius);
        }

        // a globe with layers and no Tile Source still has tiles: plain ones in the background colour,
        // the layers draped on them (the user, 2026-11-21: vector tiles must not need the raster)
        bool layersOnly = _source is null && layers is not null && layers.Any(l => l is { Enabled: true } && l.HasContent);
        var wanted = enabled && (_source is not null || layersOnly) && camera is not null && _device is not null
            ? Select(camera, Math.Clamp(maxZoom, 0, 22), screenSpaceError, screenHeight)
            : new List<TileKey>();
        _wantedSet = new HashSet<TileKey>(wanted);

        // Status says WHY the globe is empty before it says anything else: every silent failure in
        // this family looked like a working node with nothing to show.
        _status = !enabled ? "off - switch Enabled on to fetch"
            : _source is null && !layersOnly ? "nothing to draw: connect OpenStreetMap, XyzTiles or another layer to Input"
            : camera is null ? "no Camera"
            : _device is null ? "no Stride graphics device (is this patch rendering a Stride scene?)"
            : wanted.Count == 0 ? $"camera sees no tile (camera at {_lastEye.Length() / _radius:F3} radii)"
            : $"{wanted.Count} wanted, zoom {wanted.Min(t => t.Zoom)}..{wanted.Max(t => t.Zoom)}; {_loaded.Count} loaded, {_pending.Count} loading, {_failedCount} failed"
              + (_lastError.Length > 0 ? " - last error: " + _lastError : "")
              + (layersOnly ? "; no tile layer: layers on plain tiles" : "")
              + (_overlaysDrawn > 0 ? $"; overlays {_overlayMs:F0} ms each" : "")
              + (_gpu.Status.Length > 0 ? "; " + _gpu.Status : "");
        var head = _status.Split(';')[0];
        if (head != _lastLogged) { _logger.LogInformation("Globe: {Status}", _status); _lastLogged = head; }

        _created = 0;
        if (wanted.Count > 0)
        {
            // nearest first: a tilted view wants a hundred tiles at once, and the ground in front of the
            // camera should sharpen before the horizon does (MapLibre sorts by distance to the centre)
            foreach (var t in DrawSet.Requests(DrawSet.NearestFirst(wanted, _lastEye, _radius), k => _loaded.ContainsKey(k) || _pending.Contains(k) || _failed.Contains(k)))
            {
                if (_source is null)
                {
                    // no imagery to wait for: the tile is built at once, a few a frame
                    if (_created++ >= MaxCreatedPerFrame) break;
                    try { _loaded[t] = Build(_device!, t, null, background); }
                    catch (Exception e) { _failed.Add(t); _failedCount++; _lastError = $"{t}: {e.GetType().Name} {e.Message}"; }
                    continue;
                }
                if (_pending.Count >= Math.Max(1, maxRequestsInFlight)) break;
                Request(t, _source!);
            }
        }

        // Drawn whenever there is a camera, Enabled or not: it fetches nothing, and a globe that is
        // off reads as a plain sphere rather than as nothing (the user, 2026-11-20).
        // Read here, every frame, not in Select: Select runs only while Enabled, and the background is
        // drawn while off too - left at null it put the sphere around the camera (2026-11-20).
        _relativeEye = camera?.Get<GlobeCameraFrame>()?.Eye;
        ShowBackground(camera is not null ? _device : null, background);
        Stage("select+requests");
        Show(DrawSet.Resolve(wanted, _loaded.ContainsKey));
        Stage("show");
        // Every frame, not on change: with a camera-relative camera each tile sits at (centre - eye),
        // and the eye moved. Subtracted in double, then cast - the whole point of item 1.
        foreach (var t in _shown)
        {
            if (!_loaded.TryGetValue(t, out var s)) continue;
            s.Entity.Transform.Scale = new Vector3(_radius);
            s.Entity.Transform.Position = TileGeometry.Translation(s.Centre, _radius, _relativeEye);
        }
        foreach (var si in _standIns.Values)
        {
            si.Entity.Transform.Scale = new Vector3(_radius);
            si.Entity.Transform.Position = TileGeometry.Translation(si.Centre, _radius, _relativeEye);
        }
        foreach (var w in wanted) if (_loaded.TryGetValue(w, out var s)) s.LastUsed = _frame;
        AdvanceFades();
        Stage("fades");
        UpdateOverlays(layers);
        Stage("overlays");
        // the GPU's vector tiles follow the wanted tiles, not the ones shown: they need no imagery,
        // and the wanted set covers the view at the right zoom even while the raster loads
        var gpuServices = enabled && layers is not null
            ? layers.Where(l => l is { Enabled: true, Vector: not null }).Select(l => l.Vector!).ToList()
            : new List<VectorTileService>();
        // the distance to the view's centre, for the labels' perspective ratio: the eye's height above the ground
        if (_device is not null) _gpu.Update(_device, wanted, _wantedSet, gpuServices, _radius, _relativeEye, _cameraZoom, _frame,
            Math.Max(_lastEye.Length() - _radius, 1e-9));
        foreach (var skip in _gpu.TakeNewSkips()) _logger.LogWarning("Globe style: {Skip}", skip);
        Stage("gpu vectors");
        // into the log: the renderer's error once, and how far the building is, once a second at most
        // which light the buildings are drawn with, once per change (ROADMAP 6b): the scene's light is read
        // on the render thread, and nothing else shows whether it was found
        if (gpuServices.Count > 0 && _gpu.LightInfo is var lightInfo && lightInfo != _lightInfo)
        {
            _lightInfo = lightInfo;
            _logger.LogInformation("Globe: buildings lit by {Light}", lightInfo);
        }
        if (_gpu.Error != _gpuError) { _gpuError = _gpu.Error; if (_gpuError.Length > 0) _logger.LogError("Globe: {Error}", _gpuError); }
        if (gpuServices.Count > 0 && _gpu.TilesBuilt != _gpuBuiltLogged && _clock.Elapsed.TotalSeconds - _gpuLoggedAt > 1)
        {
            _gpuBuiltLogged = _gpu.TilesBuilt; _gpuLoggedAt = _clock.Elapsed.TotalSeconds;
            _logger.LogInformation("Globe: {Status} (zoom {Zoom:F2})", _gpu.Status, _cameraZoom);
        }
        Evict();
        Stage("evict");

        tilesVisible = _shown.Count;
        tilesLoading = _pending.Count;
        requestsSent = _requests;
        bytesReceived = _bytes;
        status = _status;
        return _root;
    }

    GraphicsDevice? GetDevice()
    {
        // As VL.Stride's own SkiaTexture does: the game is a service of the app host.
        var game = _context.AppHost.Services.GetService(typeof(VL.Stride.Games.VLGame)) as VL.Stride.Games.VLGame;
        return game?.GraphicsDevice;
    }

    List<TileKey> Select(Entity camera, int maxZoom, float sse, float screenHeight)
    {
        // vvvv's OrbitCamera leaves its entity's transform at identity and drives the camera through
        // CameraComponent's custom view matrix (the Camera node's "Use Custom View Matrix" defaults to
        // true). Reading only the transform put the camera at the globe's centre - "camera sees no
        // tile", found by this node's own Status on its first run. So: the view matrix when the camera
        // uses one, the transform otherwise; the eye is the view's inverse either way.
        //
        // A GlobeCamera renders camera-relative: its view is rotation only and the eye is in its
        // GlobeCameraFrame. Selection stays absolute (put the eye back into the view): culling and the
        // zoom choice tolerate float's 0.4 m, only drawing does not.
        var cc = camera.Get<CameraComponent>();
        camera.Transform.UpdateWorldMatrix();
        var view = cc is { UseCustomViewMatrix: true } ? cc.ViewMatrix : Matrix.Invert(camera.Transform.WorldMatrix);
        var frame = camera.Get<GlobeCameraFrame>();
        if (frame is not null) view = Matrix.Translation(-frame.Eye.ToVector3()) * view;
        _movedThisFrame |= view != _statsView;
        _statsView = view;
        var eye = Matrix.Invert(view).TranslationVector;
        _lastEye = eye;
        var fov = MathUtil.DegreesToRadians(cc?.VerticalFieldOfView ?? 45f);
        // The window's real aspect, from the projection Stride computed for it (as GlobeCamera reads it
        // for the gestures). 16:9 was assumed before: a wider window lost its side tiles to culling.
        var aspect = cc is { UseCustomAspectRatio: true } ? cc.AspectRatio
            : cc is { } c && c.ProjectionMatrix.M11 > 0 && c.ProjectionMatrix.M22 > 0 ? c.ProjectionMatrix.M22 / c.ProjectionMatrix.M11
            : 16f / 9f;
        var near = Math.Max(cc?.NearClipPlane ?? 0.01f, 1e-6f);
        var far = Math.Max(cc?.FarClipPlane ?? 1000f, near * 2);
        var vp = view * Matrix.PerspectiveFovRH(fov, aspect, near, far);
        var frustum = new BoundingFrustum(ref vp);
        // the camera's zoom as MapLibre counts it (512-px tiles): metres per pixel straight below the eye
        double height = Math.Max(eye.Length() - _radius, 1e-9);
        double metresPerPixel = height * 2 * Math.Tan(fov / 2) / Math.Max(1f, screenHeight) * GlobeCoordinates.EarthRadius / _radius;
        double latitude = Math.Asin(Math.Clamp(eye.Y / Math.Max(eye.Length(), 1e-9), -1, 1)) * 180 / Math.PI;
        _cameraZoom = GlobeView.ZoomOf(metresPerPixel, Math.Clamp(latitude, -85, 85)) - 1;
        var selector = new TileSelector { MaxZoom = maxZoom, ScreenSpaceError = Math.Max(0.25f, sse) };
        return selector.Select(eye, frustum, fov, Math.Max(1f, screenHeight), _radius);
    }

    public void Dispose()
    {
        _skia.Dispose();
        _gpu.Dispose();
        Reset();
        if (_background is not null && _backgroundShown) _root.Transform.Children.Remove(_background.Transform);
        _backgroundMesh?.Dispose();
        _backgroundTexture?.Dispose();
    }
}
