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
        public int LastUsed;
        public void Dispose() { Texture.Dispose(); Vertices.Dispose(); Indices.Dispose(); }
    }

    const int MaxCreatedPerFrame = 8;
    const int MaxSlots = 600;

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

    public GlobeNode(NodeContext nodeContext)
    {
        _context = nodeContext;
        _logger = nodeContext.GetLogger();
    }

    /// <param name="tileSource">From OpenStreetMap or XyzTiles.</param>
    /// <param name="camera">The camera entity the scene is rendered with.</param>
    /// <param name="enabled">Off: nothing is drawn or requested.</param>
    /// <param name="radius">The globe's radius in scene units.</param>
    /// <param name="maxZoom">The finest zoom ever requested.</param>
    /// <param name="screenSpaceError">Refine while a tile pixel would cover more screen pixels than this.</param>
    /// <param name="maxRequestsInFlight">Requests running at once.</param>
    /// <param name="screenHeight">The height of the view in pixels.</param>
    /// <param name="tilesVisible">Tiles drawn this frame.</param>
    /// <param name="tilesLoading">Requests not yet answered.</param>
    /// <param name="requestsSent">Requests made since the source changed - cached tiles are not requests.</param>
    /// <param name="bytesReceived">Bytes of tile data arrived, from the network or the disk cache.</param>
    /// <param name="attribution">The credit to show.</param>
    /// <param name="status">Why nothing is drawn, or what is happening.</param>
    [return: Pin(Name = "Entity")]
    public Entity Update(
        out int tilesVisible, out int tilesLoading, out int requestsSent, out long bytesReceived, out string attribution, out string status,
        GlobeTileSource? tileSource = null, Entity? camera = null, bool enabled = false, float radius = 1f,
        int maxZoom = 18, float screenSpaceError = 2f, int maxRequestsInFlight = 6, int screenHeight = 1080)
    {
        _frame++;
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
            foreach (var t in DrawSet.Requests(wanted, k => _loaded.ContainsKey(k) || _pending.Contains(k) || _failed.Contains(k)))
            {
                if (_pending.Count >= Math.Max(1, maxRequestsInFlight)) break;
                Request(t, _source!);
            }
        }

        Show(DrawSet.Resolve(wanted, _loaded.ContainsKey));
        foreach (var w in wanted) if (_loaded.TryGetValue(w, out var s)) s.LastUsed = _frame;
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

    List<TileKey> Select(Entity camera, int maxZoom, float sse, int screenHeight)
    {
        // vvvv's OrbitCamera leaves its entity's transform at identity and drives the camera through
        // CameraComponent's custom view matrix (the Camera node's "Use Custom View Matrix" defaults to
        // true). Reading only the transform put the camera at the globe's centre - "camera sees no
        // tile", found by this node's own Status on its first run. So: the view matrix when the camera
        // uses one, the transform otherwise; the eye is the view's inverse either way.
        var cc = camera.Get<CameraComponent>();
        camera.Transform.UpdateWorldMatrix();
        var view = cc is { UseCustomViewMatrix: true } ? cc.ViewMatrix : Matrix.Invert(camera.Transform.WorldMatrix);
        var eye = Matrix.Invert(view).TranslationVector;
        _lastEye = eye;
        var fov = MathUtil.DegreesToRadians(cc?.VerticalFieldOfView ?? 45f);
        var aspect = cc is { UseCustomAspectRatio: true } ? cc.AspectRatio : 16f / 9f;
        var near = Math.Max(cc?.NearClipPlane ?? 0.01f, 1e-6f);
        var far = Math.Max(cc?.FarClipPlane ?? 1000f, near * 2);
        var vp = view * Matrix.PerspectiveFovRH(fov, aspect, near, far);
        var frustum = new BoundingFrustum(ref vp);
        var selector = new TileSelector { MaxZoom = maxZoom, ScreenSpaceError = Math.Max(0.25f, sse) };
        return selector.Select(eye, frustum, fov, Math.Max(1, screenHeight), _radius);
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
        using var stream = new MemoryStream(bytes);
        var texture = Texture.Load(device, stream, TextureFlags.ShaderResource, GraphicsResourceUsage.Immutable, loadAsSRGB: true);

        var (positions, uv) = TileGeometry.Vertices(key);
        var vertices = new VertexPositionNormalTexture[positions.Length];
        for (int i = 0; i < positions.Length; i++) vertices[i] = new VertexPositionNormalTexture(positions[i], positions[i], uv[i]);
        var vb = Buffer.Vertex.New(device, vertices, GraphicsResourceUsage.Immutable);
        var indices = TileGeometry.IndicesFor(key.Zoom);
        var ib = Buffer.Index.New(device, indices, GraphicsResourceUsage.Immutable);

        var (centre, r) = TileGeometry.Bounds(key);
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
                Emissive = new MaterialEmissiveMapFeature(new ComputeTextureColor(texture)),
                CullMode = CullMode.None,
            },
        });
        var model = new global::Stride.Rendering.Model { mesh };   // inside namespace VL, "Model" and "Stride" bind to VL.*
        model.Materials.Add(new MaterialInstance(material));
        model.BoundingBox = mesh.BoundingBox;
        model.BoundingSphere = mesh.BoundingSphere;

        var entity = new Entity(key.ToString()) { new ModelComponent(model) };
        entity.Transform.Scale = new Vector3(_radius);
        return new Slot { Entity = entity, Texture = texture, Vertices = vb, Indices = ib, LastUsed = _frame };
    }

    void Show(List<TileKey> draw)
    {
        var next = new HashSet<TileKey>(draw);
        foreach (var t in _shown.Where(t => !next.Contains(t)).ToList())
        {
            if (_loaded.TryGetValue(t, out var s)) _root.Transform.Children.Remove(s.Entity.Transform);
            _shown.Remove(t);
        }
        foreach (var t in draw)
        {
            if (_shown.Contains(t)) continue;
            var s = _loaded[t];
            _root.Transform.Children.Add(s.Entity.Transform);
            _shown.Add(t);
            s.LastUsed = _frame;
        }
    }

    void Evict()
    {
        if (_loaded.Count <= MaxSlots) return;
        foreach (var (k, s) in _loaded.Where(kv => !_shown.Contains(kv.Key)).OrderBy(kv => kv.Value.LastUsed).Take(_loaded.Count - MaxSlots).ToList())
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
        _requests = 0;
        _failedCount = 0;
        _lastError = "";
        _bytes = 0;
    }

    public void Dispose() => Reset();
}
