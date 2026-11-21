using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using VL.Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;
using Built = VL.Globe.Engine.GpuTile;

namespace VL.Globe.Engine;

// The GPU path's bookkeeping (docs/DESIGN-GPU-VECTOR.md), owned by the Globe node: which vector tiles
// the shown globe tiles need, their buckets built on worker threads and uploaded a few a frame, and
// each frame's draw list for VectorRenderer - layer by layer in style order, tile by tile.
internal sealed class GpuVectorTiles : IDisposable
{
    public readonly Entity Entity;
    readonly VectorRenderer _renderer = new();

    sealed class Entry
    {
        public Task<(VectorBucket? bucket, bool complete, string[] missing)>? Job;
        public Built? Built;
        public string[] Missing = Array.Empty<string>();   // the tile was not here: built again once it is
        public bool Fetched;   // asked for with fetch (a wanted globe tile) at least once
        public int LastUsed;
    }

    sealed class StyleInfo
    {
        public required MapStyleDocument Document;
        public required List<GpuStyleLayer> Layers;                         // style order
        public required Dictionary<string, List<GpuStyleLayer>> BySource;   // vector sources only
        public string? BackgroundSource;                                   // whose tiles carry the background grid
    }

    readonly Dictionary<(VectorTileService service, string source, TileKey key), Entry> _entries = new();
    readonly Dictionary<VectorTileService, StyleInfo> _styles = new();
    readonly VectorRenderer.Frame[] _frames = { new(), new() };
    // the background grids, by tile: no data in them, built at once on this thread
    readonly Dictionary<TileKey, (Built built, int lastUsed)> _backgrounds = new();
    // Uploads by bytes, not by tiles: four street-level tiles arriving together were tens of MB in one
    // frame. At least one tile a frame goes up whatever its size.
    const long UploadBytesPerFrame = 8L << 20;
    const int KeepFrames = 600, KeepTiles = 160;
    // kept by bytes, the least recently used out
    const long GpuBudget = 768L << 20;
    int _frame, _built, _tilesBuilt, _running;
    long _gpuBytes;
    double _buildMs;

    // where this frame's Update went (ms): finish+upload, tiles+jobs, resolve, backgrounds, draw list,
    // evict - logged by the node when an Update is long (two of 200 and 330 ms on 2026-11-21, all here)
    readonly System.Diagnostics.Stopwatch _timer = new();
    readonly double[] _ms = new double[6];
    double _lapAt;
    void Lap(int stage) { double t = _timer.Elapsed.TotalMilliseconds; if (stage == 0) _lapAt = 0; _ms[stage] += t - _lapAt; _lapAt = t; }
    public string Breakdown => $"finish+upload {_ms[0]:F0}, tiles+jobs {_ms[1]:F0}, resolve {_ms[2]:F0}, backgrounds {_ms[3]:F0}, draw list {_ms[4]:F0}, evict {_ms[5]:F0}";

    /// <summary>Bytes uploaded in this frame's Update, and buckets: for the node's measurements.</summary>
    public long UploadedBytes { get; private set; }
    public int Uploads { get; private set; }

    // MapLibre's workers: half the cores, at most 6 (maplibre-gl's default workerCount), as threads of
    // their own below normal priority - on the thread pool at normal priority, ProcessorCount - 2 of
    // them, the builds competed with vvvv's main thread for the cores
    static class Workers
    {
        public static readonly int Count = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        static readonly BlockingCollection<Action> Queue = new();

        static Workers()
        {
            for (int i = 0; i < Count; i++)
                new Thread(() => { foreach (var job in Queue.GetConsumingEnumerable()) job(); })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"VL.Globe vector tiles {i}" }.Start();
        }

        public static Task<T> Run<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task;
        }
    }

    public GpuVectorTiles()
    {
        Entity = new Entity("Globe vector tiles")
        {
            new EntityRendererComponent { Renderer = _renderer, RenderStage = DrawerRenderStage.Transparent },
        };
    }

    public string Error => _renderer.Error;
    public int TilesBuilt => _tilesBuilt;

    // counted as they change, not summed every frame
    public string Status => _styles.Count == 0 ? "" :
        $"GPU vectors: {_tilesBuilt} tiles built ({_buildMs:F0} ms each on {Workers.Count} workers), " +
        $"{_running} building, {_gpuBytes >> 20} MB, {_renderer.ItemsDrawn} draws" +
        (_renderer.Error.Length > 0 ? " - " + _renderer.Error : "");

    // shown: the globe tiles whose area is drawn; wanted: those that fetch vector tiles; zoom: the
    // camera's, MapLibre's (512-px tiles), at which the zoom-dependent paint is evaluated
    public void Update(GraphicsDevice device, IReadOnlyCollection<TileKey> shown, IReadOnlySet<TileKey> wanted,
        IReadOnlyList<VectorTileService> services, float radius, D3? eye, double zoom, int frame)
    {
        _frame = frame;
        _timer.Restart();
        Array.Clear(_ms);
        // two frames, reused in turn: the draw list of a globe view is thousands of items (~140 bytes
        // each), a new list every frame was large-object-heap garbage - gen-2 collections, and an
        // allocation that waited ~200 ms for one in the background (2026-11-21: Updates of 200-330 ms
        // with 2 ms of counted GC pause, every one in a second with a gen-2 collection). Draw runs
        // after Update on the same thread, so the other frame is free here.
        var result = _frames[_frame & 1];
        result.Items.Clear();
        result.Labels.Clear();
        result.Radius = radius;
        long budget = UploadBytesPerFrame;
        UploadedBytes = 0; Uploads = 0;
        foreach (var e in _entries.Values) Finish(device, e, ref budget);
        Lap(0);

        foreach (var service in services.Distinct())
        {
            var info = Analyse(service);
            if (info is null) continue;
            var doc = info.Document;
            var draws = new Dictionary<string, List<(Built built, Vector4 clip)>>();
            var backgroundDraws = new List<(Built built, Vector4 clip)>();
            foreach (var (sourceId, layers) in info.BySource)
            {
                // a vector source's tiles, or a GeoJSON source cut into tiles as GL JS does (geojson-vt,
                // maxzoom 18, 512 px): the same key choice for both
                var source = doc.Sources[sourceId];
                var (minZoom, maxZoom, tileSize) = source switch
                {
                    VectorSource v => (v.MinZoom, v.MaxZoom, v.TileSize),
                    GeoJsonSource => (0, 18, 512),
                    _ => (-1, -1, -1),
                };
                if (minZoom < 0) continue;
                // the vector tile each shown globe tile needs, and the globe tiles each serves
                var needed = new Dictionary<TileKey, List<TileKey>>();
                foreach (var g in shown)
                    if (VectorKey(minZoom, maxZoom, tileSize, g) is { } vk)
                    {
                        if (!needed.TryGetValue(vk, out var served)) needed[vk] = served = new List<TileKey>();
                        served.Add(g);
                    }
                // the tiles wanted globe tiles need build first
                foreach (var (vk, served) in needed.OrderByDescending(n => n.Value.Any(wanted.Contains)))
                {
                    var e = Ensure(service, sourceId, vk);
                    if (e.Built is null && e.Job is null && _running < Workers.Count)
                    {
                        bool fetch = served.Any(wanted.Contains);
                        bool retry = e.Missing.Length == 0 || e.Missing.Any(service.IsHere) || (fetch && !e.Fetched);
                        if (retry)
                        {
                            e.Fetched |= fetch;
                            var key = vk;
                            e.Job = Workers.Run(() =>
                            {
                                var missing = new List<string>();
                                var bucket = VectorBucketBuilder.Build(doc, source, layers, false, key, fetch, missing, out bool complete);
                                return (bucket, complete, missing.ToArray());
                            });
                            _running++;
                        }
                    }
                }
                Lap(1);
                draws[sourceId] = Resolve(service, sourceId, needed);
                Lap(2);
                // the background is drawn on every needed tile from the first frame, built or not:
                // the style's ground colour, not the globe's dark one, while the data comes
                if (sourceId == info.BackgroundSource)
                    foreach (var (vk, served) in needed)
                    {
                        var bg = Background(device, vk);
                        if (needed.Keys.Any(o => o.Zoom > vk.Zoom && Contains(vk, o))) foreach (var g in served) backgroundDraws.Add((bg, Rect(vk, g)));
                        else backgroundDraws.Add((bg, new Vector4(0, 0, 1, 1)));
                    }
            }

            Lap(3);
            var ctx = new StyleContext { Zoom = zoom, GlobalState = doc.GlobalState };
            foreach (var layer in info.Layers)
            {
                if (doc.Lenient && doc.IsDropped(layer.Layer.Id)) continue;
                bool visible;
                StyleColor color, outline; double width;
                try { visible = layer.Uniforms(ctx, out color, out width, out outline); }
                catch (StyleUnsupportedException e) when (doc.Lenient) { doc.Drop(layer.Layer.Id, e.Message); continue; }
                if (!visible) continue;
                List<(Built built, Vector4 clip)>? list = backgroundDraws;
                if (layer.Type != "background" && (layer.Layer.Source is not { } source || !draws.TryGetValue(source, out list))) continue;
                VectorDrawList.AddLayer(result.Items, layer, color, width, outline, list,
                    tile => Matrix.Scaling(radius) * Matrix.Translation(TileGeometry.Translation(tile.Bucket.Centre, radius, eye)), linearize: true);
            }
        }
        Lap(4);
        _renderer.Current = result;
        if (_frame % 30 == 0) Evict();
        Lap(5);
    }

    StyleInfo? Analyse(VectorTileService service)
    {
        var doc = service.Document;
        if (doc is null) return null;
        if (_styles.TryGetValue(service, out var info) && ReferenceEquals(info.Document, doc)) return info;
        // a new style: what was built for the old one goes
        foreach (var k in _entries.Keys.Where(k => k.service == service).ToList()) Drop(k);
        var layers = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null).Select(l => l!).ToList();
        var bySource = new Dictionary<string, List<GpuStyleLayer>>();
        foreach (var l in layers)
            if (l.Type != "background" && l.Layer.Source is { } s && doc.Sources.GetValueOrDefault(s) is VectorSource or GeoJsonSource)
            {
                if (!bySource.TryGetValue(s, out var list)) bySource[s] = list = new List<GpuStyleLayer>();
                list.Add(l);
            }
        string? background = layers.Any(l => l.Type == "background")
            ? bySource.Keys.FirstOrDefault() ?? doc.Sources.FirstOrDefault(s => s.Value is VectorSource).Key
            : null;
        if (background != null && !bySource.ContainsKey(background)) bySource[background] = new List<GpuStyleLayer>();
        info = new StyleInfo { Document = doc, Layers = layers, BySource = bySource, BackgroundSource = background };
        _styles[service] = info;
        return info;
    }

    Entry Ensure(VectorTileService service, string source, TileKey key)
    {
        if (!_entries.TryGetValue((service, source, key), out var e)) _entries[(service, source, key)] = e = new Entry();
        e.LastUsed = _frame;
        return e;
    }

    void Finish(GraphicsDevice device, Entry e, ref long budget)
    {
        if (e.Job is not { IsCompleted: true } job) return;
        var bucket0 = job.IsCompletedSuccessfully ? job.Result.bucket : null;
        long size = bucket0 is null ? 0 : (long)bucket0.VertexCount * VectorVertex.Size + bucket0.IndexCount * 4L;
        if (size > 0 && budget <= 0) return;   // next frame
        e.Job = null;
        _running--;
        if (job.IsFaulted) { e.Missing = Array.Empty<string>(); Set(e, Empty(new VectorBucket())); return; }   // not retried in a loop
        var (bucket, complete, missing) = job.Result;
        if (bucket is null)
        {
            if (!complete) { e.Missing = missing; return; }
            Set(e, Empty(new VectorBucket()));   // nothing there
            return;
        }
        budget -= size;
        _built++;
        _buildMs += (bucket.Ms - _buildMs) / Math.Min(_built, 50);
        Set(e, Upload(device, bucket));
    }

    static Built Empty(VectorBucket b) => GpuTile.Empty(b);

    Built Upload(GraphicsDevice device, VectorBucket bucket)
    {
        long bytes = (long)bucket.VertexCount * VectorVertex.Size + bucket.IndexCount * 4L;
        var tile = GpuTile.Upload(device, bucket);
        if (tile.Vertices is not null) { UploadedBytes += bytes; Uploads++; }
        return tile;
    }

    void Set(Entry e, Built built)
    {
        e.Built = built;
        _tilesBuilt++;
        _gpuBytes += built.Bytes;
    }

    void Drop((VectorTileService, string, TileKey) k)
    {
        var e = _entries[k];
        if (e.Built is { } b) { _tilesBuilt--; _gpuBytes -= b.Bytes; b.Dispose(); }
        if (e.Job != null) _running--;   // it finishes unseen; its arrays go to the collector
        _entries.Remove(k);
    }

    // what is drawn for one source: each needed tile that is built, clipped to its square - or to the
    // globe tiles it serves where a finer needed tile lies inside it; a tile not built yet is stood in
    // for by its nearest built ancestor, clipped to the tile's square
    List<(Built, Vector4)> Resolve(VectorTileService service, string source, Dictionary<TileKey, List<TileKey>> needed)
    {
        var list = new List<(Built, Vector4)>();
        var full = new Vector4(0, 0, 1, 1);
        foreach (var (vk, served) in needed)
        {
            var e = _entries[(service, source, vk)];
            if (e.Built is { } built)
            {
                bool finerInside = needed.Keys.Any(o => o.Zoom > vk.Zoom && Contains(vk, o));
                if (!finerInside) list.Add((built, full));
                else foreach (var g in served) list.Add((built, Rect(vk, g)));
                continue;
            }
            // zooming out: finer tiles already built cover it (MapLibre keeps children for this too)
            bool Here(TileKey k) => _entries.TryGetValue((service, source, k), out var c) && c.Built != null;
            bool Covered(TileKey k, int depth) => (k != vk && Here(k)) || (depth > 0 && Children(k).All(c => Covered(c, depth - 1)));
            if (Covered(vk, 2))
            {
                void Add(TileKey k, int depth)
                {
                    if (k != vk && _entries.TryGetValue((service, source, k), out var c) && c.Built is { } cb)
                    {
                        c.LastUsed = _frame;
                        if (!needed.ContainsKey(k)) list.Add((cb, full));   // a needed one draws itself
                        return;
                    }
                    if (depth > 0) foreach (var child in Children(k)) Add(child, depth - 1);
                }
                Add(vk, 2);
                continue;
            }
            for (int z = vk.Zoom - 1; z >= 0; z--)
            {
                var a = new TileKey(z, vk.X >> (vk.Zoom - z), vk.Y >> (vk.Zoom - z));
                if (_entries.TryGetValue((service, source, a), out var ae) && ae.Built is { } ab)
                {
                    ae.LastUsed = _frame;
                    list.Add((ab, Rect(a, vk)));
                    break;
                }
            }
        }
        return list;
    }

    // GL JS's choice: the floored zoom for the source's tile size (a 256-px globe tile at zoom z is
    // MapLibre's zoom z - 1), clamped to the source's range; overscaled beyond its maxzoom
    internal static TileKey? VectorKey(VectorSource source, TileKey g) => VectorKey(source.MinZoom, source.MaxZoom, source.TileSize, g);

    internal static TileKey? VectorKey(int minZoom, int maxZoom, int tileSize, TileKey g)
    {
        int ideal = Math.Max(0, (int)Math.Floor(g.Zoom - 1 + Math.Log2(512.0 / tileSize)));
        if (ideal < minZoom) return null;
        int z = Math.Min(Math.Min(ideal, maxZoom), g.Zoom);
        return new TileKey(z, g.X >> (g.Zoom - z), g.Y >> (g.Zoom - z));
    }

    static TileKey[] Children(TileKey k) => new[]
    {
        new TileKey(k.Zoom + 1, k.X * 2, k.Y * 2), new TileKey(k.Zoom + 1, k.X * 2 + 1, k.Y * 2),
        new TileKey(k.Zoom + 1, k.X * 2, k.Y * 2 + 1), new TileKey(k.Zoom + 1, k.X * 2 + 1, k.Y * 2 + 1),
    };

    Built Background(GraphicsDevice device, TileKey key)
    {
        if (_backgrounds.TryGetValue(key, out var b)) { _backgrounds[key] = (b.built, _frame); return b.built; }
        var bucket = VectorBucketBuilder.BackgroundBucket(key);
        var built = Upload(device, bucket);
        _backgrounds[key] = (built, _frame);
        return built;
    }

    static bool Contains(TileKey a, TileKey d) => d.Zoom >= a.Zoom && (d.X >> (d.Zoom - a.Zoom)) == a.X && (d.Y >> (d.Zoom - a.Zoom)) == a.Y;

    // d's square in a's, 0..1
    internal static Vector4 Rect(TileKey a, TileKey d)
    {
        double s = Math.Pow(2, d.Zoom - a.Zoom);
        double u0 = d.X / s - a.X, v0 = d.Y / s - a.Y;
        return new Vector4((float)u0, (float)v0, (float)(u0 + 1 / s), (float)(v0 + 1 / s));
    }

    void Evict()
    {
        if (_backgrounds.Count > KeepTiles)
            foreach (var (k, b) in _backgrounds.Where(kv => _frame - kv.Value.lastUsed > KeepFrames).ToList()) { b.built.Dispose(); _backgrounds.Remove(k); }
        if (_gpuBytes <= GpuBudget && _entries.Count <= KeepTiles) return;
        // never what this frame draws; the oldest first
        foreach (var (k, e) in _entries.Where(kv => kv.Value.LastUsed < _frame && kv.Value.Job is null).OrderBy(kv => kv.Value.LastUsed).ToList())
        {
            bool stale = _frame - e.LastUsed > KeepFrames && _entries.Count > KeepTiles;
            if (_gpuBytes <= GpuBudget && !stale) break;
            Drop(k);
        }
    }

    public void Dispose()
    {
        _renderer.Current = null;
        foreach (var k in _entries.Keys.ToList()) Drop(k);
        foreach (var b in _backgrounds.Values) b.built.Dispose();
        _backgrounds.Clear();
    }
}
