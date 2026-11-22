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
        public bool Incomplete;   // built while some of it (a label's glyph range) was on its way: built again once it is
        public bool Fetched;   // asked for with fetch (a wanted globe tile) at least once
        public int LastUsed;
    }

    sealed class StyleInfo
    {
        public required MapStyleDocument Document;
        public required List<GpuStyleLayer> Layers;                         // style order
        public required Dictionary<string, List<GpuStyleLayer>> BySource;   // vector sources only; no symbol layers
        public required Dictionary<string, List<GpuStyleLayer>> SymbolsBySource;   // their symbol layers, built apart
        public string? BackgroundSource;                                   // whose tiles carry the background grid
        public Texture? Atlas;                                             // the labels' glyphs on the GPU, and the atlas version it shows
        public int AtlasVersion = -1;
        public Texture? Images;                                            // the icons on the GPU, and the image atlas version it shows
        public int ImagesVersion = -1;
    }

    // keyed by Scale too: 0 the tile's fills, lines and circles; 1, 2, 4... its symbols, laid out at that
    // overscale (GL JS lays symbols out again past the source's maxzoom: spacing in the shown pixels)
    readonly Dictionary<(VectorTileService service, string source, TileKey key, int scale), Entry> _entries = new();
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
        IReadOnlyList<VectorTileService> services, float radius, D3? eye, double zoom, int frame, double cameraToCenter = 1)
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
        result.CameraToCenter = (float)cameraToCenter;
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
            var symbolDraws = new Dictionary<string, List<(Built built, Vector4 clip)>>();
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
                // the vector tile each shown globe tile needs, the globe tiles each serves, and the
                // overscale its symbols are laid out at (the finest any of them is shown at)
                var needed = new Dictionary<TileKey, List<TileKey>>();
                var scales = new Dictionary<TileKey, int>();
                foreach (var g in shown)
                    if (VectorKey(minZoom, maxZoom, tileSize, g) is { } vk)
                    {
                        if (!needed.TryGetValue(vk, out var served)) needed[vk] = served = new List<TileKey>();
                        served.Add(g);
                        scales[vk] = Math.Max(scales.GetValueOrDefault(vk, 1), Overscale(tileSize, vk, g));
                    }
                var symbolLayers = info.SymbolsBySource.GetValueOrDefault(sourceId);
                // the tiles wanted globe tiles need build first
                foreach (var (vk, served) in needed.OrderByDescending(n => n.Value.Any(wanted.Contains)))
                {
                    if (layers.Count > 0) Schedule(vk, served, 0, layers);
                    if (symbolLayers != null) Schedule(vk, served, scales[vk], symbolLayers);
                }

                void Schedule(TileKey vk, List<TileKey> served, int scale, List<GpuStyleLayer> built)
                {
                    var e = Ensure(service, sourceId, vk, scale);
                    // not built, or built without a glyph range that has come since
                    if ((e.Built is null || e.Incomplete && e.Missing.Any(service.IsHere)) && e.Job is null && _running < Workers.Count)
                    {
                        bool fetch = served.Any(wanted.Contains);
                        bool retry = e.Built != null || e.Missing.Length == 0 || e.Missing.Any(service.IsHere) || (fetch && !e.Fetched);
                        if (retry)
                        {
                            e.Fetched |= fetch;
                            e.Job = Workers.Run(() =>
                            {
                                var missing = new List<string>();
                                var bucket = VectorBucketBuilder.Build(doc, source, built, false, vk, fetch, missing, out bool complete,
                                    overscale: Math.Max(1, scale));
                                return (bucket, complete, missing.ToArray());
                            });
                            _running++;
                        }
                    }
                }
                Lap(1);
                draws[sourceId] = layers.Count > 0 ? Resolve(service, sourceId, needed) : new List<(Built, Vector4)>();
                if (symbolLayers != null) symbolDraws[sourceId] = ResolveSymbols(service, sourceId, needed, scales);
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
            // the labels' glyphs: uploaded again when the atlas has grown
            if (info.Layers.Any(l => l.IsSymbol) && doc.Glyphs != null && doc.GlyphStore.Atlas.Version != info.AtlasVersion)
            {
                var (pixels, atlasHeight, version) = doc.GlyphStore.Atlas.Snapshot();
                info.Atlas?.Dispose();
                info.Atlas = Texture.New2D(device, GlyphAtlas.Width, atlasHeight, PixelFormat.R8_UNorm, pixels);
                info.AtlasVersion = version;
            }
            // the icons: uploaded again when the image atlas has grown
            if (info.Layers.Any(l => l.IconImage != null) && doc.SpriteStore.Atlas.Version != info.ImagesVersion)
            {
                var (pixels, imagesHeight, version) = doc.SpriteStore.Atlas.Snapshot();
                info.Images?.Dispose();
                info.Images = Texture.New2D(device, ImageAtlas.Width, imagesHeight, PixelFormat.R8G8B8A8_UNorm, pixels);
                info.ImagesVersion = version;
            }
            var ctx = new StyleContext { Zoom = zoom, GlobalState = doc.GlobalState };
            Matrix World(Built tile) => Matrix.Scaling(radius) * Matrix.Translation(TileGeometry.Translation(tile.Bucket.Centre, radius, eye));
            foreach (var layer in info.Layers)
            {
                if (doc.Lenient && doc.IsDropped(layer.Layer.Id)) continue;
                if (layer.IsCircle)
                {
                    CirclePaint? circlePaint;
                    try { circlePaint = layer.CircleUniforms(ctx); }
                    catch (StyleUnsupportedException e) when (doc.Lenient) { doc.Drop(layer.Layer.Id, e.Message); continue; }
                    if (circlePaint is null || layer.Layer.Source is not { } circleSource || !draws.TryGetValue(circleSource, out var circleDrawn)) continue;
                    // as labels: only from tiles drawn whole, each once (the circle shader does not clip)
                    var whole = new Vector4(0, 0, 1, 1);
                    var circled = circleDrawn.Where(d => d.clip == whole).DistinctBy(d => d.built).ToList();
                    VectorDrawList.AddCircleLayer(result.Items, layer, circlePaint, circled, World, linearize: true, cameraToCenter, 1);
                    continue;
                }
                if (layer.IsSymbol)
                {
                    SymbolPaint? paint = null, iconPaint = null;
                    try { if (!layer.SymbolUniforms(ctx, out paint)) continue; iconPaint = layer.IconUniforms(ctx); }
                    catch (StyleUnsupportedException e) when (doc.Lenient) { doc.Drop(layer.Layer.Id, e.Message); continue; }
                    if (info.Atlas is null && info.Images is null || layer.Layer.Source is not { } labelSource || !symbolDraws.TryGetValue(labelSource, out var drawn)) continue;
                    // labels only from tiles drawn whole, each once: the symbol shader does not clip, and a
                    // tile standing in for finer ones is drawn once per square - its labels drawn several
                    // times, colliding with themselves. A stand-in shows no labels; the finer tile will.
                    var full = new Vector4(0, 0, 1, 1);
                    var labelled = drawn.Where(d => d.clip == full && d.built.PlacedBuffer != null).DistinctBy(d => d.built).ToList();
                    VectorDrawList.AddSymbolLayer(result.Items, layer, paint!, labelled, World, linearize: true, info.Atlas, cameraToCenter, 1, iconPaint, info.Images);
                    // placed from the top of the style down
                    result.Labels.Insert(0, new LabelPlacement.Layer(layer.Index, labelled.Select(d => (d.built, World(d.built))).ToList(), (float)paint!.Size));
                    continue;
                }
                bool visible;
                StyleColor color, outline; double width;
                try { visible = layer.Uniforms(ctx, out color, out width, out outline); }
                catch (StyleUnsupportedException e) when (doc.Lenient) { doc.Drop(layer.Layer.Id, e.Message); continue; }
                if (!visible) continue;
                List<(Built built, Vector4 clip)>? list = backgroundDraws;
                if (layer.Type != "background" && (layer.Layer.Source is not { } source || !draws.TryGetValue(source, out list))) continue;
                VectorDrawList.AddLayer(result.Items, layer, color, width, outline, list, World, linearize: true);
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
        var symbolsBySource = new Dictionary<string, List<GpuStyleLayer>>();
        foreach (var l in layers)
            if (l.Type != "background" && l.Layer.Source is { } s && doc.Sources.GetValueOrDefault(s) is VectorSource or GeoJsonSource)
            {
                if (!bySource.TryGetValue(s, out var list)) bySource[s] = list = new List<GpuStyleLayer>();
                if (l.IsSymbol)
                {
                    if (!symbolsBySource.TryGetValue(s, out var symbols)) symbolsBySource[s] = symbols = new List<GpuStyleLayer>();
                    symbols.Add(l);
                }
                else list.Add(l);
            }
        string? background = layers.Any(l => l.Type == "background")
            ? bySource.Keys.FirstOrDefault() ?? doc.Sources.FirstOrDefault(s => s.Value is VectorSource).Key
            : null;
        if (background != null && !bySource.ContainsKey(background)) bySource[background] = new List<GpuStyleLayer>();
        info = new StyleInfo { Document = doc, Layers = layers, BySource = bySource, SymbolsBySource = symbolsBySource, BackgroundSource = background };
        _styles[service] = info;
        return info;
    }

    Entry Ensure(VectorTileService service, string source, TileKey key, int scale)
    {
        if (!_entries.TryGetValue((service, source, key, scale), out var e)) _entries[(service, source, key, scale)] = e = new Entry();
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
        e.Incomplete = !complete;
        e.Missing = complete ? Array.Empty<string>() : missing;
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
        // a tile built again (a glyph range came) replaces the old one, drawn last frame and no more
        if (e.Built is { } old) { _tilesBuilt--; _gpuBytes -= old.Bytes; old.Dispose(); }
        e.Built = built;
        _tilesBuilt++;
        _gpuBytes += built.Bytes;
    }

    void Drop((VectorTileService, string, TileKey, int) k)
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
            var e = _entries[(service, source, vk, 0)];
            if (e.Built is { } built)
            {
                bool finerInside = needed.Keys.Any(o => o.Zoom > vk.Zoom && Contains(vk, o));
                if (!finerInside) list.Add((built, full));
                else foreach (var g in served) list.Add((built, Rect(vk, g)));
                continue;
            }
            // zooming out: finer tiles already built cover it (MapLibre keeps children for this too)
            bool Here(TileKey k) => _entries.TryGetValue((service, source, k, 0), out var c) && c.Built != null;
            bool Covered(TileKey k, int depth) => (k != vk && Here(k)) || (depth > 0 && Children(k).All(c => Covered(c, depth - 1)));
            if (Covered(vk, 2))
            {
                void Add(TileKey k, int depth)
                {
                    if (k != vk && _entries.TryGetValue((service, source, k, 0), out var c) && c.Built is { } cb)
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
                if (_entries.TryGetValue((service, source, a, 0), out var ae) && ae.Built is { } ab)
                {
                    ae.LastUsed = _frame;
                    list.Add((ab, Rect(a, vk)));
                    break;
                }
            }
        }
        return list;
    }

    // a needed tile's symbols, drawn whole (labels come only from whole tiles): laid out at its overscale,
    // or - while that layout is built - at the nearest other one already here, so zooming in past the
    // source's maxzoom does not blank the labels
    List<(Built, Vector4)> ResolveSymbols(VectorTileService service, string source, Dictionary<TileKey, List<TileKey>> needed, Dictionary<TileKey, int> scales)
    {
        var list = new List<(Built, Vector4)>();
        var full = new Vector4(0, 0, 1, 1);
        foreach (var vk in needed.Keys)
        {
            int scale = scales[vk];
            Entry? best = _entries.TryGetValue((service, source, vk, scale), out var exact) && exact.Built != null ? exact : null;
            if (best is null)
                for (int step = 1; step <= 5 && best is null; step++)
                    foreach (int other in new[] { scale >> step, scale << step })
                        if (other >= 1 && _entries.TryGetValue((service, source, vk, other), out var near) && near.Built != null) { best = near; break; }
            if (best?.Built is { } built) { best.LastUsed = _frame; list.Add((built, full)); }
        }
        return list;
    }

    // GL JS's overscaling: how many times its own size the vector tile vk is shown at in globe tile g
    // (the ideal zoom past the source's maxzoom)
    internal static int Overscale(int tileSize, TileKey vk, TileKey g)
    {
        int ideal = Math.Max(0, (int)Math.Floor(g.Zoom - 1 + Math.Log2(512.0 / tileSize)));
        return ideal > vk.Zoom ? 1 << Math.Min(ideal - vk.Zoom, 8) : 1;
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
