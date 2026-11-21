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
using VL.Globe.Engine;
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

/// <summary>Map tiles on a globe: fetches only the tiles the camera can see, at the zoom its distance earns.</summary>
/// <remarks>
/// Put Entity into the scene. Camera is the camera entity you render with (OrbitCamera's output).
/// Off until Enabled. Show the tile source's Attribution.
/// </remarks>
[ProcessNode(Name = "Globe", Category = "Globe")]
public sealed class GlobeNode : IDisposable
{
    sealed class Slot
    {
        public required Entity Entity;
        public required Texture Texture;
        public required Buffer Vertices;
        public required Buffer Indices;
        public required D3 Centre;       // on the unit sphere, in double: the vertices are offsets from it
        public required ModelComponent Model;
        public required Material Plain;  // its own image only; a fade swaps in another and back
        public Material? Fading;         // ancestor * (1 - f) + own * f, while the fade runs
        public TileKey FadeFrom;         // the ancestor mixed in
        public bool FadeOut;             // false: own image fades IN over the ancestor; true: fades OUT to it
        public double FadeStart;
        public int LastUsed;
        public Texture? Overlay;         // the vector layers draped into this tile (OverlayRenderer)
        public int OverlayVersion = -1;  // the layers' version the overlay shows
        public int OverlayStamp;         // bumped on every new overlay image, for the stand-ins borrowing it
        public bool OverlayIncomplete;   // drawn while a vector tile it needs was still on its way
        public string[] OverlayMissing = Array.Empty<string>();   // the vector tiles it was drawn without
        public Task<(byte[]? bytes, bool complete, string[] missing, double ms)>? OverlayJob;
        public int OverlayJobVersion;
        public bool OverlayJobFetch, OverlayFetched;   // drawn as a wanted tile (fetching vector tiles) or as a stand-in (only what was here)
        public void Dispose() { Texture.Dispose(); Overlay?.Dispose(); Vertices.Dispose(); Indices.Dispose(); }
    }

    const int MaxCreatedPerFrame = 8;
    const int MaxSlots = 600;
    const int KeepZoom = 3;

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
    GlobeTileSource? _source;
    int _frame, _requests;
    long _bytes;
    float _radius = 1f;
    Vector3 _lastEye;
    D3? _relativeEye;   // this frame's eye when the camera is camera-relative (GlobeCamera), else null

    // The fade's two weights, keyed so the material can be told them every frame without being
    // rebuilt: own * In + ancestor * Out. A float4 per weight, multiplied into each image.
    static readonly ValueParameterKey<Vector4> FadeInKey = ParameterKeys.NewValue(Vector4.One, "Globe.FadeIn");
    static readonly ValueParameterKey<Vector4> FadeOutKey = ParameterKeys.NewValue(Vector4.Zero, "Globe.FadeOut");
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _fadeDuration = 0.3;

    // Zooming out: a parent about to replace descendants that are drawn. For one fade duration the
    // descendants stay drawn instead, each fading to its part of the parent's image; then the parent.
    // Never two layers on one spot, so nothing to sort and nothing to z-fight. (MapLibre fades
    // many-to-one both ways; the user, 2026-11-20: "都加呗".)
    sealed class OutFade { public required List<TileKey> From; public double Start; public bool Done; }
    readonly Dictionary<TileKey, OutFade> _outFades = new();

    // DrawSet's StandIns: a square not yet loaded, drawn with its own mesh and the matching part of an
    // arrived ancestor's image - at the surface, never two things on one spot. Kept by square so a
    // square that stays missing for many frames is built once; dropped when no longer drawn.
    sealed class StandIn
    {
        public required Entity Entity;
        public required Buffer Vertices;
        public required Buffer Indices;
        public required D3 Centre;
        public required ModelComponent Model;
        public TileKey From;
        public int FromOverlayVersion;
        public void Dispose() { Vertices.Dispose(); Indices.Dispose(); }
    }
    readonly Dictionary<TileKey, StandIn> _standIns = new();

    // Vector layers draped into the tiles (ROADMAP 0.1 items 3-4, 4c). Overlays are rendered on worker
    // threads (a street-level vector tile takes milliseconds) and uploaded here, at most
    // OverlaysPerFrame a frame; an overlay drawn while a vector tile was missing is drawn again
    // when tiles have arrived since.
    long _signature;
    List<OverlayLayer> _layers = new();
    List<GlobeLayer> _vectorLayers = new();
    int _overlayVersion;
    const int OverlaysPerFrame = 6;
    static readonly int OverlayJobs = Math.Max(2, Environment.ProcessorCount - 2);
    double _overlayMs; int _overlaysDrawn;   // the mean time an overlay takes, for Status
    // A replaced overlay texture may still be sampled by a fade or a stand-in's material for a while;
    // it is disposed this many frames later instead of at once.
    readonly List<(Texture texture, int frame)> _retired = new();

    // The globe before any tile: a sphere just below the tiles, drawn by this node because only it
    // knows the eye a camera-relative camera renders from - a Sphere placed by the patch sat at the
    // origin, which is now the camera (ROADMAP item 1). 0.995: no tile facet sags deeper than 0.997
    // (TileGeometryFacts), so it never shows through a tile.
    const float BackgroundRadius = 0.995f;
    Entity? _background;
    GeometricPrimitive? _backgroundMesh;
    Texture? _backgroundTexture;
    Color4 _backgroundColor;
    bool _backgroundShown;

    // VectorTiles drawn by the GPU (docs/DESIGN-GPU-VECTOR.md): their own entity under the root, drawn
    // after the tiles; the camera's zoom (MapLibre's, 512-px tiles) for their zoom-dependent paint
    readonly GpuVectorTiles _gpu = new();
    double _cameraZoom;
    string _gpuError = "";
    int _gpuBuiltLogged = -1;
    double _gpuLoggedAt = -10;

    // Measured, once a second into the log while vector tiles are drawn by the GPU (the user,
    // 2026-11-21: "偶尔在运动加载的时候会卡一下"): the longest frame (time between two Updates) and
    // how many passed 33 ms, the longest this node's own Update took, bytes uploaded and the most in
    // one frame, and garbage collections by generation - a gen-2 one pauses every thread.
    double _perfFrom = -1, _lastUpdateAt = -1, _frameMax, _updateMax;
    int _slowFrames, _perfUploads, _gen0, _gen2;
    long _perfBytes, _perfBytesMax;

    public GlobeNode(NodeContext nodeContext)
    {
        _context = nodeContext;
        _logger = nodeContext.GetLogger();
        _root.Transform.Children.Add(_gpu.Entity.Transform);
    }

    /// <param name="tileSource">From OpenStreetMap or XyzTiles.</param>
    /// <param name="camera">The camera entity the scene is rendered with.</param>
    /// <param name="enabled">Off: nothing is drawn or requested.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    /// <param name="background">The globe's colour where no tile has arrived, also while off. Transparent: none.</param>
    /// <param name="maxZoom">The finest zoom ever requested.</param>
    /// <param name="screenSpaceError">Refine while a tile pixel would cover more screen pixels than this. 1.41 shows tiles at 0.71-1.41x, as MapLibre and Cesium do.</param>
    /// <param name="maxRequestsInFlight">Requests running at once.</param>
    /// <param name="layers">Vector layers from FeatureLayer or Geometry, drawn on the map in order, first at the bottom.</param>
    /// <param name="fadeDuration">Seconds a finer tile takes to fade in over the one it replaces. 0: at once.</param>
    /// <param name="screenHeight">The height of the view in pixels: SceneWindow's Client Bounds. GlobeWindow wires it.</param>
    /// <param name="tilesVisible">Tiles drawn this frame.</param>
    /// <param name="tilesLoading">Requests not yet answered.</param>
    /// <param name="requestsSent">Requests made since the source changed - cached tiles are not requests.</param>
    /// <param name="bytesReceived">Bytes of tile data arrived, from the network or the disk cache.</param>
    /// <param name="attribution">The credit to show.</param>
    /// <param name="status">Why nothing is drawn, or what is happening.</param>
    [return: Pin(Name = "Entity")]
    public Entity Update(
        out int tilesVisible, out int tilesLoading, out int requestsSent, out long bytesReceived, out string attribution, out string status,
        GlobeTileSource? tileSource = null, Entity? camera = null, bool enabled = false, float radius = 1f, Color4 background = default,
        int maxZoom = 19, float screenSpaceError = 1.41f, int maxRequestsInFlight = 6, float screenHeight = 1080f, float fadeDuration = 0.3f,
        IEnumerable<GlobeLayer>? layers = null)
    {
        double updateStart = _clock.Elapsed.TotalMilliseconds;
        _stages.Clear();
        _gcPauseAtStart = GC.GetTotalPauseDuration();
        try { return UpdateCore(out tilesVisible, out tilesLoading, out requestsSent, out bytesReceived, out attribution, out status,
            tileSource, camera, enabled, radius, background, maxZoom, screenSpaceError, maxRequestsInFlight, screenHeight, fadeDuration, layers); }
        finally { Measure(updateStart); }
    }

    // a long Update says where its time went: each stage, and how much of it the collector paused
    // (23:16:05 on 2026-11-21: one Update of 580 ms, a gen-2 collection in the same second)
    readonly List<(string stage, double at)> _stages = new();
    TimeSpan _gcPauseAtStart;
    void Stage(string name) => _stages.Add((name, _clock.Elapsed.TotalMilliseconds));

    void Measure(double updateStart)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _updateMax = Math.Max(_updateMax, now - updateStart);
        if (now - updateStart > 50)
        {
            var parts = new List<string>();
            double prev = updateStart;
            foreach (var (stage, at) in _stages) { parts.Add($"{stage} {at - prev:F0}"); prev = at; }
            parts.Add($"rest {now - prev:F0}");
            _logger.LogWarning("Globe perf: an Update of {Ms:F0} ms - {Stages} ms; GC paused {Pause:F0} ms in it; gpu vectors: {Gpu} ms",
                now - updateStart, string.Join(", ", parts), (GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds, _gpu.Breakdown);
        }
        if (_lastUpdateAt >= 0)
        {
            double frame = updateStart - _lastUpdateAt;
            _frameMax = Math.Max(_frameMax, frame);
            if (frame > 33) _slowFrames++;
        }
        _lastUpdateAt = updateStart;
        _perfBytes += _gpu.UploadedBytes;
        _perfBytesMax = Math.Max(_perfBytesMax, _gpu.UploadedBytes);
        _perfUploads += _gpu.Uploads;
        if (_perfFrom < 0) { _perfFrom = now; _gen0 = GC.CollectionCount(0); _gen2 = GC.CollectionCount(2); return; }
        if (now - _perfFrom < 1000) return;
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        if (_gpu.TilesBuilt > 0 && (_slowFrames > 0 || _perfUploads > 0 || gen2 > _gen2))
            _logger.LogInformation("Globe perf: frame max {FrameMax:F0} ms, {Slow} over 33 ms; Update max {UpdateMax:F1} ms; uploaded {Uploads} tiles {MB:F1} MB, most {MaxMB:F1} MB in a frame; GC gen0 +{Gen0}, gen2 +{Gen2}",
                _frameMax, _slowFrames, _updateMax, _perfUploads, _perfBytes / 1048576.0, _perfBytesMax / 1048576.0, gen0 - _gen0, gen2 - _gen2);
        _perfFrom = now; _frameMax = 0; _updateMax = 0; _slowFrames = 0; _perfUploads = 0; _perfBytes = 0; _perfBytesMax = 0;
        _gen0 = gen0; _gen2 = gen2;
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
            : _source is null && !layersOnly ? "no Tile Source and no layer to draw"
            : camera is null ? "no Camera"
            : _device is null ? "no Stride graphics device (is this patch rendering a Stride scene?)"
            : wanted.Count == 0 ? $"camera sees no tile (camera at {_lastEye.Length() / _radius:F3} radii)"
            : $"{wanted.Count} wanted, zoom {wanted.Min(t => t.Zoom)}..{wanted.Max(t => t.Zoom)}; {_loaded.Count} loaded, {_pending.Count} loading, {_failedCount} failed"
              + (_lastError.Length > 0 ? " - last error: " + _lastError : "")
              + (layersOnly ? "; no Tile Source: layers on plain tiles" : "")
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
            ? layers.Where(l => l is { Enabled: true, Gpu: true, Vector: not null }).Select(l => l.Vector!).ToList()
            : new List<VectorTileService>();
        if (_device is not null) _gpu.Update(_device, wanted, _wantedSet, gpuServices, _radius, _relativeEye, _cameraZoom, _frame);
        Stage("gpu vectors");
        // into the log: the renderer's error once, and how far the building is, once a second at most
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

    void Request(TileKey t, GlobeTileSource source)
    {
        _pending.Add(t);
        _requests++;
        var info = new TileInfo { Index = new TileIndex(t.X, t.Y, t.Zoom) };
        Task.Run(async () =>
        {
            byte[]? bytes = null;
            try { bytes = await source.Http.GetTileAsync(info).ConfigureAwait(false); }
            catch (Exception e) { _lastError = $"{t}: {e.GetType().Name} {e.Message}"; }   // an answer, counted, not retried
            _arrived.Enqueue((t, bytes, source));
        });
    }

    void CreateArrived(GraphicsDevice device)
    {
        for (int n = 0; n < MaxCreatedPerFrame && _arrived.TryDequeue(out var a); n++)
        {
            if (!ReferenceEquals(a.source, _source)) continue;      // from a source since replaced
            _pending.Remove(a.key);
            if (a.bytes is not { Length: > 0 }) { _failed.Add(a.key); _failedCount++; continue; }
            _bytes += a.bytes.Length;
            try { _loaded[a.key] = Build(device, a.key, a.bytes); }
            catch (Exception e) { _failed.Add(a.key); _failedCount++; _lastError = $"{a.key}: {e.GetType().Name} {e.Message}"; _logger.LogWarning(e, "Globe: building tile {Tile} failed", a.key); }
        }
    }

    int _created;

    // bytes null: a tile without imagery, one pixel of the plain colour
    Slot Build(GraphicsDevice device, TileKey key, byte[]? bytes, Color4 plain = default)
    {
        var texture = bytes is null
            ? Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm_SRgb, new[] { (Color)plain })
            : LoadMipmapped(device, bytes);
        var (vb, ib, mesh, tileCentre) = BuildMesh(device, key);

        // Emissive only, as in the lab: the map is the light, so the globe reads the same in any scene.
        var material = Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                // Anisotropic between the mip levels (MipChain) - a tilted view sees the ground at a grazing
                // angle, where trilinear picks the blurriest level - clamped at the tile's edge: wrapping
                // blended a tile's opposite edge into its border once mips were sampled.
                Emissive = new MaterialEmissiveMapFeature(Sampled(texture, Vector2.One, Vector2.Zero)),
                CullMode = CullMode.None,
            },
        });
        var (entity, modelComponent) = MakeEntity(key, mesh, material, tileCentre);
        return new Slot { Entity = entity, Texture = texture, Vertices = vb, Indices = ib, Centre = tileCentre, Model = modelComponent, Plain = material, LastUsed = _frame };
    }

    // What a tile looks like: its imagery, with its overlay laid over it (BinaryOperator.Over is the
    // 3ds over, straight alpha: front x a + back x (1 - a)) - the same for the tile itself, the fades
    // and the stand-ins that borrow it, so vectors do not blink while tiles change.
    static IComputeColor Look(Slot s, Vector2 scale, Vector2 offset)
        => s.Overlay is null ? Sampled(s.Texture, scale, offset)
         : new ComputeBinaryColor(Sampled(s.Texture, scale, offset), Sampled(s.Overlay, scale, offset), BinaryOperator.Over);

    static SKColor ToSk(Color4 c) => new((byte)Math.Clamp(c.R * 255, 0, 255), (byte)Math.Clamp(c.G * 255, 0, 255), (byte)Math.Clamp(c.B * 255, 0, 255), (byte)Math.Clamp(c.A * 255, 0, 255));

    void UpdateOverlays(IEnumerable<GlobeLayer>? layers)
    {
        foreach (var r in _retired.Where(r => _frame - r.frame > 60).ToList()) { r.texture.Dispose(); _retired.Remove(r); }
        // VectorTiles drawn by the GPU are not draped
        var list = layers?.Where(l => l is { Enabled: true } && l.HasContent && !(l.Vector != null && l.Gpu)).Select(l => l!).ToList() ?? new List<GlobeLayer>();
        var h = new HashCode();
        h.Add(list.Count);
        foreach (var l in list) h.Add(l.Signature());
        long signature = h.ToHashCode();
        if (signature != _signature)
        {
            _signature = signature;
            _layers = list.Select(l => l.Vector?.Document is { } doc
                ? (OverlayLayer)new StyleOverlay(doc)
                : new ShapesOverlay(l.Geometries, new TileOverlay.Style(ToSk(l.Style.LineColor), l.Style.LineWidth, ToSk(l.Style.FillColor)))).ToList();
            _vectorLayers = list.Where(l => l.Vector != null).ToList();
            _overlayVersion++;
        }
        if (_device is null) return;
        bool Came(string url) => _vectorLayers.Any(l => l.Vector!.IsHere(url));

        // finished jobs: upload, nearest first, a few a frame
        int budget = OverlaysPerFrame;
        int running = 0;
        // the wanted tiles first, then the stand-ins shown while they load; each nearest first
        var nearest = DrawSet.NearestFirst(_shown.ToList(), _lastEye, _radius);
        var order = nearest.Where(_wantedSet.Contains).Concat(nearest.Where(t => !_wantedSet.Contains(t))).ToList();
        foreach (var t in order)
        {
            var s = _loaded[t];
            if (s.OverlayJob is not { } job) continue;
            if (!job.IsCompleted) { running++; continue; }
            if (budget == 0) continue;
            s.OverlayJob = null;
            if (job.IsFaulted)
            {
                _logger.LogWarning(job.Exception, "Globe: the overlay of {Tile} failed", t);
                s.OverlayVersion = s.OverlayJobVersion; s.OverlayIncomplete = false;   // not retried in a loop
                continue;
            }
            if (s.OverlayJobVersion != _overlayVersion) continue;   // the layers changed meanwhile
            budget--;
            var (bytes, complete, missing, ms) = job.Result;
            SetOverlay(s, bytes);
            _overlaysDrawn++;
            _overlayMs += (ms - _overlayMs) / Math.Min(_overlaysDrawn, 50);   // a running mean of the last ~50
            s.OverlayVersion = s.OverlayJobVersion;
            s.OverlayIncomplete = !complete;
            s.OverlayMissing = missing;
            s.OverlayFetched = s.OverlayJobFetch;
        }

        // new jobs, nearest first
        foreach (var t in order)
        {
            if (running >= OverlayJobs) break;
            var s = _loaded[t];
            if (s.OverlayJob != null) continue;
            bool fetch = _wantedSet.Contains(t);
            // drawn again only when it can come out different: the layers changed, a stand-in became
            // wanted (now it may fetch), or a vector tile it was drawn without has come since
            bool stale = s.OverlayVersion != _overlayVersion
                || (s.OverlayIncomplete && ((fetch && !s.OverlayFetched) || s.OverlayMissing.Any(Came)));
            if (!stale) continue;
            if (_layers.Count == 0)
            {
                if (s.Overlay != null) SetOverlay(s, null);   // nothing to drape: undo
                s.OverlayVersion = _overlayVersion; s.OverlayIncomplete = false;
                continue;
            }
            var snapshot = _layers;
            var key = t;
            s.OverlayJobVersion = _overlayVersion;
            s.OverlayJobFetch = fetch;
            s.OverlayJob = Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var missing = new List<string>();
                var bytes = OverlayRenderer.Render(key, snapshot, 256, out bool complete, fetch, missing);
                return (bytes, complete, missing.Distinct().ToArray(), clock.Elapsed.TotalMilliseconds);
            });
            running++;
        }
    }

    void SetOverlay(Slot s, byte[]? bytes)
    {
        if (s.Overlay is { } old) _retired.Add((old, _frame));
        s.Overlay = bytes is null ? null : FromLevels(_device!, bytes, 256, 256, PixelFormat.R8G8B8A8_UNorm_SRgb, srgb: true, straightAlpha: true);
        s.OverlayStamp++;
        s.Plain = Material.New(_device!, new MaterialDescriptor
        {
            Attributes = { Emissive = new MaterialEmissiveMapFeature(Look(s, Vector2.One, Vector2.Zero)), CullMode = CullMode.None },
        });
        if (s.Fading is null) s.Model.Materials[0] = s.Plain;
    }

    static IComputeColor Sampled(Texture texture, Vector2 scale, Vector2 offset) => new ComputeTextureColor(texture)
    {
        Filtering = TextureFilter.Anisotropic, AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
        Scale = scale, Offset = offset,
    };

    (Entity, ModelComponent) MakeEntity(TileKey key, Mesh mesh, Material material, D3 centre)
    {
        var model = new global::Stride.Rendering.Model { mesh };   // inside namespace VL, "Model" and "Stride" bind to VL.*
        model.Materials.Add(new MaterialInstance(material));
        model.BoundingBox = mesh.BoundingBox;
        model.BoundingSphere = mesh.BoundingSphere;
        var modelComponent = new ModelComponent(model);
        var entity = new Entity(key.ToString()) { modelComponent };
        entity.Transform.Scale = new Vector3(_radius);
        entity.Transform.Position = TileGeometry.Translation(centre, _radius, _relativeEye);
        return (entity, modelComponent);
    }

    static (Buffer vb, Buffer ib, Mesh mesh, D3 centre) BuildMesh(GraphicsDevice device, TileKey key)
    {
        var (tileCentre, offsets, normals, uv) = TileGeometry.RelativeVertices(key);
        var vertices = new VertexPositionNormalTexture[offsets.Length];
        for (int i = 0; i < offsets.Length; i++) vertices[i] = new VertexPositionNormalTexture(offsets[i], normals[i], uv[i]);
        var vb = Buffer.Vertex.New(device, vertices, GraphicsResourceUsage.Immutable);
        var indices = TileGeometry.IndicesFor(key.Zoom);
        var ib = Buffer.Index.New(device, indices, GraphicsResourceUsage.Immutable);

        var (boundsCentre, r) = TileGeometry.Bounds(key);
        var centre = (new D3(boundsCentre.X, boundsCentre.Y, boundsCentre.Z) - tileCentre).ToVector3();   // in the mesh's own space
        var mesh = new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                DrawCount = indices.Length,
                IndexBuffer = new IndexBufferBinding(ib, false, indices.Length),
                VertexBuffers = new[] { new VertexBufferBinding(vb, VertexPositionNormalTexture.Layout, vertices.Length) },
            },
            MaterialIndex = 0,
            BoundingBox = new BoundingBox(centre - new Vector3(r), centre + new Vector3(r)),
            BoundingSphere = new BoundingSphere(centre, r),
        };
        return (vb, ib, mesh, tileCentre);
    }

    void ShowBackground(GraphicsDevice? device, Color4 color)
    {
        bool want = device is not null && color.A > 0;
        if (want && (_background is null || color != _backgroundColor))
        {
            _backgroundMesh ??= GeometricPrimitive.Sphere.New(device!, 1f, 64);
            var draw = _backgroundMesh.ToMeshDraw();
            // The colour as a 1x1 sRGB texture, the tiles' own path: a ComputeColor emissive rendered
            // pure black in vvvv (2026-11-20). An sRGB texel also reads the colour the way the tiles'
            // pixels are read, so the two match.
            _backgroundTexture?.Dispose();
            _backgroundTexture = Texture.New2D(device!, 1, 1, PixelFormat.R8G8B8A8_UNorm_SRgb, new[] { (Color)color });
            var material = Material.New(device!, new MaterialDescriptor
            {
                // CullMode None as for the tiles. Not the cause of the black sphere, but a camera that
                // ever ends up inside it (a bad eye) then shows it rather than hiding the fault.
                Attributes = { Emissive = new MaterialEmissiveMapFeature(new ComputeTextureColor(_backgroundTexture)), CullMode = CullMode.None },
            });
            var mesh = new Mesh { Draw = draw, MaterialIndex = 0, BoundingBox = new BoundingBox(new Vector3(-1), new Vector3(1)), BoundingSphere = new BoundingSphere(Vector3.Zero, 1) };
            var model = new global::Stride.Rendering.Model { mesh };
            model.Materials.Add(new MaterialInstance(material));
            model.BoundingBox = mesh.BoundingBox;
            model.BoundingSphere = mesh.BoundingSphere;
            if (_background is not null && _backgroundShown) _root.Transform.Children.Remove(_background.Transform);
            _background = new Entity("Globe background") { new ModelComponent(model) };
            _backgroundShown = false;
            _backgroundColor = color;
        }
        if (want != _backgroundShown || (want && _background is null))
            _logger.LogInformation("Globe: background {State}, colour {Color}, device {Device}", want ? "shown" : "hidden", color, device is not null);
        if (_background is null) return;
        if (want != _backgroundShown)
        {
            if (want) _root.Transform.Children.Add(_background.Transform);
            else _root.Transform.Children.Remove(_background.Transform);
            _backgroundShown = want;
        }
        _background.Transform.Scale = new Vector3(_radius * BackgroundRadius);
        _background.Transform.Position = TileGeometry.Translation(default, _radius, _relativeEye);
    }

    // A tile's image with its whole mip chain (MipChain). Formats other than 4-byte RGBA/BGRA load
    // as before, without mips - an unknown format draws soft or shimmering, never not at all.
    static Texture LoadMipmapped(GraphicsDevice device, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var source = Image.Load(stream, loadAsSRGB: true);
        var pb = source.PixelBuffer[0];
        var format = pb.Format;
        bool fourBytes = format is PixelFormat.R8G8B8A8_UNorm_SRgb or PixelFormat.R8G8B8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb or PixelFormat.B8G8R8A8_UNorm;
        if (!fourBytes || pb.Width < 2)
            return Texture.New(device, source, TextureFlags.ShaderResource, GraphicsResourceUsage.Immutable);

        int w = pb.Width, h = pb.Height;
        var level0 = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(pb.DataPointer + y * pb.RowStride, level0, y * w * 4, w * 4);
        bool srgb = format is PixelFormat.R8G8B8A8_UNorm_SRgb or PixelFormat.B8G8R8A8_UNorm_SRgb;
        return FromLevels(device, level0, w, h, format, srgb, straightAlpha: false);
    }

    static Texture FromLevels(GraphicsDevice device, byte[] level0, int w, int h, PixelFormat format, bool srgb, bool straightAlpha)
    {
        var levels = MipChain.Build(level0, w, h, srgb, straightAlpha);

        using var image = Image.New2D(w, h, MipMapCount.Auto, format);
        for (int m = 0; m < levels.Count && m < image.Description.MipLevels; m++)
        {
            var dst = image.GetPixelBuffer(0, 0, m);
            var (pixels, lw, lh) = levels[m];
            for (int y = 0; y < lh; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * lw * 4, dst.DataPointer + y * dst.RowStride, lw * 4);
        }
        return Texture.New(device, image, TextureFlags.ShaderResource, GraphicsResourceUsage.Immutable);
    }

    void Show(DrawSet.Layers layers)
    {
        var draw = layers.Top;
        var now = _clock.Elapsed.TotalSeconds;
        var before = new HashSet<TileKey>(_shown);
        var borrowed = _standIns.ToDictionary(kv => kv.Key, kv => kv.Value.From);   // what each square showed last frame
        ShowStandIns(layers.StandIns);
        var drawn = new HashSet<TileKey>(draw);
        foreach (var k in _outFades.Keys.Where(k => !drawn.Contains(k)).ToList()) _outFades.Remove(k);
        var display = new List<TileKey>();
        foreach (var t in draw)
        {
            _outFades.TryGetValue(t, out var of);
            if (of is null && _fadeDuration > 0 && !before.Contains(t))
            {
                var under = before.Where(t.IsAncestorOf).ToList();
                if (under.Count > 0 && Fade.Covers(t, under) && under.All(_loaded.ContainsKey))
                    _outFades[t] = of = new OutFade { From = under, Start = now };
            }
            if (of is { Done: false } && Fade.Progress(of.Start, now, _fadeDuration) < 1 && of.From.All(_loaded.ContainsKey))
            {
                display.AddRange(of.From);
                foreach (var d in of.From)
                {
                    var ds = _loaded[d];
                    if (ds.Fading is null || !ds.FadeOut || ds.FadeFrom != t) StartFade(ds, d, t, _loaded[t], outward: true, of.Start);
                }
            }
            else
            {
                if (of is not null) of.Done = true;
                display.Add(t);
            }
        }
        draw = display;
        var next = new HashSet<TileKey>(draw);
        foreach (var t in _shown.Where(t => !next.Contains(t)).ToList())
        {
            if (_loaded.TryGetValue(t, out var s)) { _root.Transform.Children.Remove(s.Entity.Transform); EndFade(s); }
            _shown.Remove(t);
        }
        foreach (var t in draw)
        {
            if (_shown.Contains(t)) continue;
            var s = _loaded[t];
            _root.Transform.Children.Add(s.Entity.Transform);
            _shown.Add(t);
            s.LastUsed = _frame;
            // A finer tile replacing the ancestor drawn here last frame fades in over it. Not the other
            // way (a parent replacing its children) and not over nothing - the pop that shows is the
            // one on the way in.
            // A tile replacing the ancestor drawn here last frame - itself, or as this square's stand-in -
            // fades in over exactly the image that was showing.
            var ancestor = borrowed.TryGetValue(t, out var lent) ? lent : NearestAncestorIn(t, before);
            if (_fadeDuration > 0 && ancestor is { } a && _loaded.TryGetValue(a, out var from))
                StartFade(s, t, a, from, outward: false, now);
        }
    }

    void ShowStandIns(List<(TileKey Square, TileKey From)> wanted)
    {
        var keep = new HashSet<TileKey>();
        foreach (var (square, fromKey) in wanted)
        {
            if (_device is null || !_loaded.TryGetValue(fromKey, out var from)) continue;
            keep.Add(square);
            if (!_standIns.TryGetValue(square, out var si))
            {
                var (vb, ib, mesh, centre) = BuildMesh(_device, square);
                var (entity, model) = MakeEntity(square, mesh, StandInMaterial(square, fromKey, from), centre);
                si = new StandIn { Entity = entity, Vertices = vb, Indices = ib, Centre = centre, Model = model, From = fromKey, FromOverlayVersion = from.OverlayStamp };
                _standIns[square] = si;
                _root.Transform.Children.Add(entity.Transform);
            }
            else if (si.From != fromKey || si.FromOverlayVersion != from.OverlayStamp)
            {
                si.Model.Materials[0] = StandInMaterial(square, fromKey, from);   // a nearer ancestor arrived, or its overlay changed
                si.From = fromKey;
                si.FromOverlayVersion = from.OverlayStamp;
            }
        }
        foreach (var square in _standIns.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var si = _standIns[square];
            _root.Transform.Children.Remove(si.Entity.Transform);
            si.Dispose();
            _standIns.Remove(square);
        }
    }

    Material StandInMaterial(TileKey square, TileKey fromKey, Slot from)
    {
        var (scale, offset) = Fade.AncestorTransform(square, fromKey);
        return Material.New(_device!, new MaterialDescriptor
        {
            Attributes = { Emissive = new MaterialEmissiveMapFeature(Look(from, scale, offset)), CullMode = CullMode.None },
        });
    }

    static TileKey? NearestAncestorIn(TileKey t, HashSet<TileKey> set)
    {
        for (var a = t; a.Zoom > 0;) { a = a.Parent; if (set.Contains(a)) return a; }
        return null;
    }

    void StartFade(Slot s, TileKey t, TileKey a, Slot from, bool outward, double start)
    {
        var (scale, offset) = Fade.AncestorTransform(t, a);
        try
        {
            s.Fading = Material.New(_device!, new MaterialDescriptor
            {
                Attributes =
                {
                    Emissive = new MaterialEmissiveMapFeature(new ComputeBinaryColor(
                        new ComputeBinaryColor(Look(from, scale, offset), new ComputeFloat4(Vector4.One) { Key = FadeOutKey }, BinaryOperator.Multiply),
                        new ComputeBinaryColor(Look(s, Vector2.One, Vector2.Zero), new ComputeFloat4(new Vector4(0, 0, 0, 1)) { Key = FadeInKey }, BinaryOperator.Multiply),
                        BinaryOperator.Add)),
                    CullMode = CullMode.None,
                },
            });
        }
        catch (Exception e) { _logger.LogWarning(e, "Globe: fade material for {Tile} failed; drawn without a fade", t); s.Fading = null; return; }
        s.FadeFrom = a;
        s.FadeOut = outward;
        s.FadeStart = start;
        SetFade(s.Fading, OwnWeight(s, _clock.Elapsed.TotalSeconds));
        s.Model.Materials[0] = s.Fading;
    }

    static void SetFade(Material m, float f)
    {
        var p = m.Passes[0].Parameters;
        // Alpha stays 1. BinaryOperator.Add is Stride's ComputeColorAdd3ds, a 3ds Max blend that weighs
        // each side by its ALPHA (BlendUtils.BasicBlend); weights of (f, f, f, f) made a half-way fade
        // half as bright - every tile dipped dark as it changed (the user, 2026-11-20).
        p.Set(FadeInKey, new Vector4(f, f, f, 1));
        p.Set(FadeOutKey, new Vector4(1 - f, 1 - f, 1 - f, 1));
    }

    void AdvanceFades()
    {
        var now = _clock.Elapsed.TotalSeconds;
        foreach (var t in _shown)
        {
            if (!_loaded.TryGetValue(t, out var s) || s.Fading is null) continue;
            // the ancestor's image must outlive the fade; if it was evicted anyway, stop at once
            if (!_loaded.ContainsKey(s.FadeFrom)) { EndFade(s); continue; }
            // A fade in ends on its own image; a fade out holds the ancestor's until Show swaps the
            // ancestor itself in next frame - ending it here would flash the old image for a frame.
            if (!s.FadeOut && Fade.Progress(s.FadeStart, now, _fadeDuration) >= 1) EndFade(s);
            else SetFade(s.Fading, OwnWeight(s, now));
        }
    }

    float OwnWeight(Slot s, double now)
    {
        var p = Fade.Progress(s.FadeStart, now, _fadeDuration);
        return s.FadeOut ? 1 - p : p;
    }

    static void EndFade(Slot s)
    {
        if (s.Fading is null) return;
        s.Model.Materials[0] = s.Plain;
        s.Fading = null;
    }

    void Evict()
    {
        if (_loaded.Count <= MaxSlots) return;
        var fadingFrom = _shown.Select(t => _loaded.TryGetValue(t, out var f) && f.Fading is not null ? f.FadeFrom : (TileKey?)null).Where(k => k is not null).Select(k => k!.Value).ToHashSet();
        // Zoom 3 and coarser are never evicted: at most 85 tiles, and the stand-ins of last resort.
        // Least-recently-used evicted them first while the camera sat low, and a fast zoom out then
        // found nothing but the zoom-2 fallback still loading (the user, 2026-11-21).
        fadingFrom.UnionWith(_standIns.Values.Select(si => si.From));   // borrowed by a stand-in
        foreach (var (k, s) in _loaded.Where(kv => kv.Key.Zoom > KeepZoom && !_shown.Contains(kv.Key) && !fadingFrom.Contains(kv.Key)).OrderBy(kv => kv.Value.LastUsed).Take(_loaded.Count - MaxSlots).ToList())
        {
            s.Dispose();
            _loaded.Remove(k);
        }
    }

    void Reset()
    {
        foreach (var t in _shown) if (_loaded.TryGetValue(t, out var s)) _root.Transform.Children.Remove(s.Entity.Transform);
        _shown.Clear();
        foreach (var s in _loaded.Values) s.Dispose();
        _loaded.Clear();
        _pending.Clear();
        _failed.Clear();
        _outFades.Clear();
        foreach (var si in _standIns.Values) { _root.Transform.Children.Remove(si.Entity.Transform); si.Dispose(); }
        _standIns.Clear();
        foreach (var r in _retired) r.texture.Dispose();
        _retired.Clear();
        _requests = 0;
        _failedCount = 0;
        _lastError = "";
        _bytes = 0;
    }

    public void Dispose()
    {
        _gpu.Dispose();
        Reset();
        if (_background is not null && _backgroundShown) _root.Transform.Children.Remove(_background.Transform);
        _backgroundMesh?.Dispose();
        _backgroundTexture?.Dispose();
    }
}
