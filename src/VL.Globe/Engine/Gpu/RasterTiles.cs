using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace VL.Globe.Engine.Gpu;

// A raster tile layer drawn by the GPU (ROADMAP 6c, docs/DESIGN-RASTER-GPU.md): one source's textures,
// fetched, decoded and mipmapped on the thread pool, and what each shown globe tile draws of them this
// frame. Cesium's way: per globe tile, not per source tile - the globe tiles wanted never overlap, so no
// stencil. A globe tile draws its own source tile (its square at min(zoom, the source's maxzoom)), else
// the finest loaded ancestor through a rectangle: a stand-in and the overscale are the same case. GL JS's
// raster fade (draw_raster.ts: u_image0 own, u_image1 the parent, mixed by u_fade_t) both ways, as the
// globe's own imagery fades (GlobeNode.Show): in over the ancestor that stood in; out, zooming out, the
// finer squares go on being drawn for one fade, each mixing towards the coarser image.
internal sealed class RasterTiles : IDisposable
{
    sealed class Entry
    {
        public Texture? Texture;
        public Task<Texture?>? Job;
        public bool Failed;   // an answer without an image: not asked again (as GlobeNode's _failed)
        public int LastUsed;
    }

    /// <summary>One square drawn this frame: its own image, mixed with another while a fade runs.</summary>
    internal readonly record struct Draw(TileKey Square, TileKey Own, TileKey? Other, float OwnWeight);

    // what a square drew: the image key, and the image it fades in from
    readonly record struct Shown(TileKey Image, TileKey? From, double Start);
    sealed record OutFade(List<(TileKey Square, TileKey Image)> From, double Start);

    readonly GlobeTileSource _source;
    readonly int _maxZoom;
    readonly Dictionary<TileKey, Entry> _entries = new();
    Dictionary<TileKey, Shown> _shown = new();
    Dictionary<TileKey, OutFade> _outFades = new();
    readonly List<Draw> _draws = new();
    int _running, _frame = -1;
    volatile bool _disposed;

    const int KeepTextures = 600, KeepZoom = 3;

    public RasterTiles(GlobeTileSource source)
    {
        _source = source;
        // BruTile's schema names the levels the server has (GlobalSphericalMercator: 0-19)
        var levels = source.Http.Schema.Resolutions.Keys;
        _maxZoom = levels.Count > 0 ? levels.Max() : 19;
    }

    public int LastUsedFrame { get; private set; }
    public int Loaded => _entries.Values.Count(e => e.Texture != null);
    public int Loading => _running;
    public string LastError { get; private set; } = "";
    /// <summary>Requests made, and bytes of image data arrived (from the network or the disk cache).</summary>
    public int Requests { get; private set; }
    long _bytes;
    public long Bytes => System.Threading.Interlocked.Read(ref _bytes);

    /// <summary>The source tile a globe tile draws: its own square, or above the source's maxzoom the ancestor there.</summary>
    internal TileKey SourceKey(TileKey globeTile) => globeTile.AncestorAt(_maxZoom);

    internal Texture? TextureOf(TileKey key) => _entries.TryGetValue(key, out var e) ? e.Texture : null;

    bool IsLoaded(TileKey key) => _entries.TryGetValue(key, out var e) && e.Texture != null;

    // the finest loaded image at or above the key
    TileKey? Best(TileKey key)
    {
        for (var a = key; ; a = a.Parent)
        {
            if (IsLoaded(a)) return a;
            if (a.Zoom == 0) return null;
        }
    }

    /// <summary>This frame's squares, once a frame however often the layer is in the Input; requests what they lack, nearest first.</summary>
    /// <param name="wanted">The globe tiles wanted, nearest first.</param>
    public IReadOnlyList<Draw> Step(GraphicsDevice device, IReadOnlyList<TileKey> wanted, int frame, double now, double fadeDuration, int maxRequests)
    {
        if (frame == _frame) return _draws;
        _frame = frame;
        LastUsedFrame = frame;
        Finish();

        // coarse first, as the globe's own tiles: each wanted square's fallback ancestor, then the square
        var keys = wanted.Select(SourceKey).Distinct().ToList();
        foreach (var k in DrawSet.Requests(keys, k => _entries.ContainsKey(k)))
        {
            if (_running >= Math.Max(1, maxRequests)) break;
            Request(device, k);
        }

        _draws.Clear();
        var next = new Dictionary<TileKey, Shown>(wanted.Count);
        var outFades = new Dictionary<TileKey, OutFade>();
        bool fades = fadeDuration > 0;
        foreach (var g in wanted)
        {
            if (Best(SourceKey(g)) is not { } image) continue;   // nothing of this layer here yet
            Touch(image);
            // zooming out: the finer squares drawn here last frame fade towards this one's image
            if (_outFades.TryGetValue(g, out var of) && Fade.Progress(of.Start, now, fadeDuration) is var p && p < 1 && of.From.All(f => IsLoaded(f.Image)))
            {
                foreach (var (square, own) in of.From) { Touch(own); _draws.Add(new Draw(square, own, image, 1 - p)); }
                outFades[g] = of;
                next[g] = new Shown(image, null, 0);
                continue;
            }
            Shown s;
            if (_shown.TryGetValue(g, out var prev))
            {
                s = prev.Image == image ? prev
                    // a finer image came: it fades in over the one that stood in
                    : fades && image.Zoom > prev.Image.Zoom && IsLoaded(prev.Image) ? new Shown(image, prev.Image, now)
                    : new Shown(image, null, 0);
            }
            else if (NearestAncestor(g) is { } a && _shown[a].Image is var was && IsLoaded(was))
                // zooming in: what the ancestor drew here last frame
                s = fades && was != image ? new Shown(image, was, now) : new Shown(image, null, 0);
            else
            {
                var under = _shown.Keys.Where(g.IsAncestorOf).ToList();
                if (fades && under.Count > 0 && Fade.Covers(g, under) && under.All(u => IsLoaded(_shown[u].Image)))
                {
                    var from = under.Select(u => (u, _shown[u].Image)).ToList();
                    outFades[g] = new OutFade(from, now);
                    foreach (var (square, own) in from) { Touch(own); _draws.Add(new Draw(square, own, image, 1)); }
                    next[g] = new Shown(image, null, 0);
                    continue;
                }
                s = new Shown(image, null, 0);
            }
            // a fade ends on its own image, or at once when what it fades from went
            if (s.From is { } f && (Fade.Progress(s.Start, now, fadeDuration) >= 1 || !IsLoaded(f))) s = s with { From = null };
            next[g] = s;
            if (s.From is { } from2) Touch(from2);
            _draws.Add(new Draw(g, image, s.From, s.From is null ? 1 : Fade.Progress(s.Start, now, fadeDuration)));
        }
        _shown = next;
        _outFades = outFades;
        Evict();
        return _draws;
    }

    TileKey? NearestAncestor(TileKey g)
    {
        for (var a = g; a.Zoom > 0;) { a = a.Parent; if (_shown.ContainsKey(a)) return a; }
        return null;
    }

    void Touch(TileKey key) { if (_entries.TryGetValue(key, out var e)) e.LastUsed = _frame; }

    void Request(GraphicsDevice device, TileKey key)
    {
        var e = new Entry { LastUsed = _frame };
        _entries[key] = e;
        _running++;
        Requests++;
        var http = _source.Http;
        e.Job = Task.Run(async () =>
        {
            var bytes = await http.GetTileAsync(new BruTile.TileInfo { Index = new BruTile.TileIndex(key.X, key.Y, key.Zoom) }).ConfigureAwait(false);
            if (bytes is not { Length: > 0 } || _disposed) return null;
            System.Threading.Interlocked.Add(ref _bytes, bytes.Length);
            // decoded and mipmapped here, not in Update; a translucent PNG's levels averaged by alpha
            return TileTextures.LoadMipmapped(device, bytes, straightAlpha: true);
        });
    }

    void Finish()
    {
        foreach (var (key, e) in _entries)
        {
            if (e.Job is not { IsCompleted: true } job) continue;
            e.Job = null;
            _running--;
            if (job.IsCompletedSuccessfully && job.Result is { } texture) e.Texture = texture;
            else
            {
                e.Failed = true;   // an answer, counted, not retried
                if (job.Exception is { } x) LastError = $"{key}: {x.InnerException?.GetType().Name} {x.InnerException?.Message}";
            }
        }
    }

    // the least recently used beyond KeepTextures; never what this frame draws, nor zoom 3 and coarser
    // (at most 85, the stand-ins of last resort - as GlobeNode.Evict)
    void Evict()
    {
        if (_entries.Count <= KeepTextures || _frame % 30 != 0) return;
        foreach (var (k, e) in _entries.Where(kv => kv.Key.Zoom > KeepZoom && kv.Value.LastUsed < _frame && kv.Value.Job is null)
                     .OrderBy(kv => kv.Value.LastUsed).Take(_entries.Count - KeepTextures).ToList())
        {
            e.Texture?.Dispose();
            _entries.Remove(k);
        }
    }

    /// <summary>The rectangle of <paramref name="square"/> in the image of <paramref name="image"/>, its ancestor or itself: u0 v0 u1 v1.</summary>
    internal static Vector4 Rect(TileKey image, TileKey square) => GpuVectorTiles.Rect(image, square);

    public void Dispose()
    {
        _disposed = true;
        foreach (var e in _entries.Values)
        {
            e.Texture?.Dispose();
            // a request still running: its texture, made after all, is released when it ends
            e.Job?.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
        }
        _entries.Clear();
    }
}
