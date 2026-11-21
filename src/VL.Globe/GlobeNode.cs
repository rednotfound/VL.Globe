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
        public void Dispose() { Texture.Dispose(); Vertices.Dispose(); Indices.Dispose(); }
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
    readonly ConcurrentQueue<(TileKey key, byte[]? bytes, GlobeTileSource source)> _arrived = new();
    GraphicsDevice? _device;
    GlobeTileSource? _source;
    int _frame, _requests;
    long _bytes;
    float _radius = 1f;
    Vector3 _lastEye;
    D3? _relativeEye;

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

    // DrawSet's Under layer: stand-in ancestors drawn sunk below the surface, so the Top tiles win
    // the depth test and an ancestor shows only through their holes (MapLibre draws the parent under
    // its children). Coarser sinks deeper, so two stand-ins never share a depth. The sink follows the
    // tile's DISTANCE from the eye, 0.3 % of it, 0.5 m to 5 km: the depth buffer's resolution falls
    // with distance squared, and a tilted view sees ground tens of km away from a camera 20 m up - a
    // sink taken from the camera's height (0.5 m there) would z-fight at the horizon.
    readonly HashSet<TileKey> _under = new();
    readonly HashSet<TileKey> _top = new();

    float RadiusFor(TileKey t)
    {
        if (!_under.Contains(t)) return _radius;
        var eye = _relativeEye ?? new D3(_lastEye.X, _lastEye.Y, _lastEye.Z);
        double distanceMetres = (TileGeometry.Centre(t) * _radius - eye).Length / _radius * GlobeCoordinates.EarthRadius;
        double sink = Math.Clamp(0.003 * distanceMetres, 0.5, 5000) * (2 - t.Zoom / 22.0);
        return (float)(_radius * (1 - sink / GlobeCoordinates.EarthRadius));
    }   // this frame's eye when the camera is camera-relative (GlobeCamera), else null

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

    public GlobeNode(NodeContext nodeContext)
    {
        _context = nodeContext;
        _logger = nodeContext.GetLogger();
    }

    /// <param name="tileSource">From OpenStreetMap or XyzTiles.</param>
    /// <param name="camera">The camera entity the scene is rendered with.</param>
    /// <param name="enabled">Off: nothing is drawn or requested.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    /// <param name="background">The globe's colour where no tile has arrived, also while off. Transparent: none.</param>
    /// <param name="maxZoom">The finest zoom ever requested.</param>
    /// <param name="screenSpaceError">Refine while a tile pixel would cover more screen pixels than this. 1.41 shows tiles at 0.71-1.41x, as MapLibre and Cesium do.</param>
    /// <param name="maxRequestsInFlight">Requests running at once.</param>
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
        int maxZoom = 19, float screenSpaceError = 1.41f, int maxRequestsInFlight = 6, float screenHeight = 1080f, float fadeDuration = 0.3f)
    {
        _frame++;
        _fadeDuration = Math.Max(0, fadeDuration);
        if (!ReferenceEquals(tileSource, _source)) { Reset(); _source = tileSource; }
        attribution = _source?.Attribution ?? "";
        status = "";

        _device ??= GetDevice();
        if (_device is not null) CreateArrived(_device);

        if (Math.Abs(radius - _radius) > 0 && radius > 0)
        {
            _radius = radius;
            foreach (var s in _loaded.Values) s.Entity.Transform.Scale = new Vector3(radius);
        }

        var wanted = enabled && _source is not null && camera is not null && _device is not null
            ? Select(camera, Math.Clamp(maxZoom, 0, 22), screenSpaceError, screenHeight)
            : new List<TileKey>();

        // Status says WHY the globe is empty before it says anything else: every silent failure in
        // this family looked like a working node with nothing to show.
        _status = !enabled ? "off - switch Enabled on to fetch"
            : _source is null ? "no Tile Source"
            : camera is null ? "no Camera"
            : _device is null ? "no Stride graphics device (is this patch rendering a Stride scene?)"
            : wanted.Count == 0 ? $"camera sees no tile (camera at {_lastEye.Length() / _radius:F3} radii)"
            : $"{wanted.Count} wanted, zoom {wanted.Min(t => t.Zoom)}..{wanted.Max(t => t.Zoom)}; {_loaded.Count} loaded, {_pending.Count} loading, {_failedCount} failed"
              + (_lastError.Length > 0 ? " - last error: " + _lastError : "");
        var head = _status.Split(';')[0];
        if (head != _lastLogged) { _logger.LogInformation("Globe: {Status}", _status); _lastLogged = head; }

        if (wanted.Count > 0)
        {
            // nearest first: a tilted view wants a hundred tiles at once, and the ground in front of the
            // camera should sharpen before the horizon does (MapLibre sorts by distance to the centre)
            foreach (var t in DrawSet.Requests(DrawSet.NearestFirst(wanted, _lastEye, _radius), k => _loaded.ContainsKey(k) || _pending.Contains(k) || _failed.Contains(k)))
            {
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
        Show(DrawSet.Resolve(wanted, _loaded.ContainsKey));
        // Every frame, not on change: with a camera-relative camera each tile sits at (centre - eye),
        // and the eye moved. Subtracted in double, then cast - the whole point of item 1.
        foreach (var t in _shown)
        {
            if (!_loaded.TryGetValue(t, out var s)) continue;
            var r = RadiusFor(t);
            s.Entity.Transform.Scale = new Vector3(r);
            s.Entity.Transform.Position = TileGeometry.Translation(s.Centre, r, _relativeEye);
        }
        foreach (var w in wanted) if (_loaded.TryGetValue(w, out var s)) s.LastUsed = _frame;
        AdvanceFades();
        Evict();

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

    Slot Build(GraphicsDevice device, TileKey key, byte[] bytes)
    {
        var texture = LoadMipmapped(device, bytes);

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

        // Emissive only, as in the lab: the map is the light, so the globe reads the same in any scene.
        var material = Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                // Anisotropic between the mip levels (MipChain) - a tilted view sees the ground at a grazing
                // angle, where trilinear picks the blurriest level - clamped at the tile's edge: wrapping
                // blended a tile's opposite edge into its border once mips were sampled.
                Emissive = new MaterialEmissiveMapFeature(new ComputeTextureColor(texture)
                {
                    Filtering = TextureFilter.Anisotropic,
                    AddressModeU = TextureAddressMode.Clamp,
                    AddressModeV = TextureAddressMode.Clamp,
                }),
                CullMode = CullMode.None,
            },
        });
        var model = new global::Stride.Rendering.Model { mesh };   // inside namespace VL, "Model" and "Stride" bind to VL.*
        model.Materials.Add(new MaterialInstance(material));
        model.BoundingBox = mesh.BoundingBox;
        model.BoundingSphere = mesh.BoundingSphere;

        var modelComponent = new ModelComponent(model);
        var entity = new Entity(key.ToString()) { modelComponent };
        entity.Transform.Scale = new Vector3(_radius);
        entity.Transform.Position = TileGeometry.Translation(tileCentre, _radius, _relativeEye);
        return new Slot { Entity = entity, Texture = texture, Vertices = vb, Indices = ib, Centre = tileCentre, Model = modelComponent, Plain = material, LastUsed = _frame };
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
        var levels = MipChain.Build(level0, w, h, srgb);

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
        var beforeTop = new HashSet<TileKey>(_top);
        var drawn = new HashSet<TileKey>(draw);
        foreach (var k in _outFades.Keys.Where(k => !drawn.Contains(k)).ToList()) _outFades.Remove(k);
        var display = new List<TileKey>();
        foreach (var t in draw)
        {
            _outFades.TryGetValue(t, out var of);
            if (of is null && _fadeDuration > 0 && !before.Contains(t))
            {
                var under = beforeTop.Where(t.IsAncestorOf).ToList();   // Top only: Under tiles overlap them
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
        _top.Clear();
        _top.UnionWith(display);
        _under.Clear();
        foreach (var u in layers.Under) if (!_top.Contains(u)) _under.Add(u);
        draw = display.Concat(_under).ToList();
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
            if (_under.Contains(t)) { EndFade(s); continue; }      // a stand-in under the others: no fade
            if (_fadeDuration > 0 && NearestAncestorIn(t, before) is { } a && _loaded.TryGetValue(a, out var from))
                StartFade(s, t, a, from, outward: false, now);
        }
    }

    static TileKey? NearestAncestorIn(TileKey t, HashSet<TileKey> set)
    {
        for (var a = t; a.Zoom > 0;) { a = a.Parent; if (set.Contains(a)) return a; }
        return null;
    }

    void StartFade(Slot s, TileKey t, TileKey a, Slot from, bool outward, double start)
    {
        var (scale, offset) = Fade.AncestorTransform(t, a);
        IComputeColor Sampled(Texture texture, Vector2 sc, Vector2 off) => new ComputeTextureColor(texture)
        {
            Filtering = TextureFilter.Anisotropic, AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
            Scale = sc, Offset = off,
        };
        try
        {
            s.Fading = Material.New(_device!, new MaterialDescriptor
            {
                Attributes =
                {
                    Emissive = new MaterialEmissiveMapFeature(new ComputeBinaryColor(
                        new ComputeBinaryColor(Sampled(from.Texture, scale, offset), new ComputeFloat4(Vector4.One) { Key = FadeOutKey }, BinaryOperator.Multiply),
                        new ComputeBinaryColor(Sampled(s.Texture, Vector2.One, Vector2.Zero), new ComputeFloat4(new Vector4(0, 0, 0, 1)) { Key = FadeInKey }, BinaryOperator.Multiply),
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
        _under.Clear();
        _top.Clear();
        _requests = 0;
        _failedCount = 0;
        _lastError = "";
        _bytes = 0;
    }

    public void Dispose()
    {
        Reset();
        if (_background is not null && _backgroundShown) _root.Transform.Children.Remove(_background.Transform);
        _backgroundMesh?.Dispose();
        _backgroundTexture?.Dispose();
    }
}
