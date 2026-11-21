using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using VL.Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe.Engine;

// The GPU path's bookkeeping (docs/DESIGN-GPU-VECTOR.md), owned by the Globe node: which vector tiles
// the shown globe tiles need, their buckets built on worker threads and uploaded a few a frame, and
// each frame's draw list for VectorRenderer - layer by layer in style order, tile by tile.
internal sealed class GpuVectorTiles : IDisposable
{
    public readonly Entity Entity;
    readonly VectorRenderer _renderer = new();

    sealed class Built
    {
        public required VectorBucket Bucket;
        public Buffer? Vertices, Indices;   // null: a tile with nothing in it
        public required Dictionary<int, List<VectorRange>> ByLayer;   // -1: the background grid
        public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); }
    }

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
    static readonly int MaxJobs = Math.Max(2, Environment.ProcessorCount - 2);
    const int UploadsPerFrame = 4;
    const int KeepFrames = 600, KeepTiles = 160;
    int _frame, _built;
    double _buildMs;

    public GpuVectorTiles()
    {
        Entity = new Entity("Globe vector tiles")
        {
            new EntityRendererComponent { Renderer = _renderer, RenderStage = DrawerRenderStage.Transparent },
        };
    }

    public string Error => _renderer.Error;
    public int TilesBuilt => _entries.Values.Count(e => e.Built != null);

    public string Status => _styles.Count == 0 ? "" :
        $"GPU vectors: {_entries.Values.Count(e => e.Built != null)} tiles built ({_buildMs:F0} ms each), " +
        $"{_entries.Values.Count(e => e.Job != null)} building, {_renderer.ItemsDrawn} draws" +
        (_renderer.Error.Length > 0 ? " - " + _renderer.Error : "");

    // shown: the globe tiles whose area is drawn; wanted: those that fetch vector tiles; zoom: the
    // camera's, MapLibre's (512-px tiles), at which the zoom-dependent paint is evaluated
    public void Update(GraphicsDevice device, IReadOnlyCollection<TileKey> shown, IReadOnlySet<TileKey> wanted,
        IReadOnlyList<VectorTileService> services, float radius, D3? eye, double zoom, int frame)
    {
        _frame = frame;
        var result = new VectorRenderer.Frame { Radius = radius };
        int uploads = UploadsPerFrame;
        foreach (var e in _entries.Values) Finish(device, e, ref uploads);
        int running = _entries.Values.Count(e => e.Job != null);

        foreach (var service in services.Distinct())
        {
            var info = Analyse(service);
            if (info is null) continue;
            var doc = info.Document;
            var draws = new Dictionary<string, List<(Built built, Vector4 clip)>>();
            foreach (var (sourceId, layers) in info.BySource)
            {
                if (doc.Sources[sourceId] is not VectorSource source) continue;
                // the vector tile each shown globe tile needs, and the globe tiles each serves
                var needed = new Dictionary<TileKey, List<TileKey>>();
                foreach (var g in shown)
                    if (VectorKey(source, g) is { } vk)
                    {
                        if (!needed.TryGetValue(vk, out var served)) needed[vk] = served = new List<TileKey>();
                        served.Add(g);
                    }
                bool withBackground = sourceId == info.BackgroundSource;
                // the tiles wanted globe tiles need build first
                foreach (var (vk, served) in needed.OrderByDescending(n => n.Value.Any(wanted.Contains)))
                {
                    var e = Ensure(service, sourceId, vk);
                    if (e.Built is null && e.Job is null && running < MaxJobs)
                    {
                        bool fetch = served.Any(wanted.Contains);
                        bool retry = e.Missing.Length == 0 || e.Missing.Any(service.IsHere) || (fetch && !e.Fetched);
                        if (retry)
                        {
                            e.Fetched |= fetch;
                            var key = vk;
                            e.Job = Task.Run(() =>
                            {
                                var missing = new List<string>();
                                var bucket = VectorBucketBuilder.Build(doc, source, layers, withBackground, key, fetch, missing, out bool complete);
                                return (bucket, complete, missing.ToArray());
                            });
                            running++;
                        }
                    }
                }
                draws[sourceId] = Resolve(service, sourceId, needed);
            }

            var ctx = new StyleContext { Zoom = zoom, GlobalState = doc.GlobalState };
            foreach (var layer in info.Layers)
            {
                if (doc.Lenient && doc.IsDropped(layer.Layer.Id)) continue;
                bool visible;
                StyleColor color, outline; double width;
                try { visible = layer.Uniforms(ctx, out color, out width, out outline); }
                catch (StyleUnsupportedException e) when (doc.Lenient) { doc.Drop(layer.Layer.Id, e.Message); continue; }
                if (!visible) continue;
                var source = layer.Type == "background" ? info.BackgroundSource : layer.Layer.Source;
                if (source is null || !draws.TryGetValue(source, out var list)) continue;
                int rangeLayer = layer.Type == "background" ? -1 : layer.Index;
                foreach (var (built, clip) in list)
                {
                    if (built.Vertices is null || !built.ByLayer.TryGetValue(rangeLayer, out var ranges)) continue;
                    var world = Matrix.Scaling(radius) * Matrix.Translation(TileGeometry.Translation(built.Bucket.Centre, radius, eye));
                    var centre = built.Bucket.Centre.ToVector3();
                    foreach (var r in ranges)
                    {
                        var (c, half, line) = r.Kind switch
                        {
                            RangeKind.Line => (color, width / 2, true),
                            RangeKind.Outline => (outline, 0.5, true),
                            _ => (color, 0.0, false),
                        };
                        if (c.A <= 0) continue;
                        result.Items.Add(new VectorRenderer.Item(built.Vertices, built.Indices!, r.Start, r.Count, world, centre,
                            Linear(c), (float)half, line, clip));
                    }
                }
            }
        }
        _renderer.Current = result;
        Evict();
    }

    StyleInfo? Analyse(VectorTileService service)
    {
        var doc = service.Document;
        if (doc is null) return null;
        if (_styles.TryGetValue(service, out var info) && ReferenceEquals(info.Document, doc)) return info;
        // a new style: what was built for the old one goes
        foreach (var k in _entries.Keys.Where(k => k.service == service).ToList()) { _entries[k].Built?.Dispose(); _entries.Remove(k); }
        var layers = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null).Select(l => l!).ToList();
        var bySource = new Dictionary<string, List<GpuStyleLayer>>();
        foreach (var l in layers)
            if (l.Type != "background" && l.Layer.Source is { } s && doc.Sources.GetValueOrDefault(s) is VectorSource)
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

    void Finish(GraphicsDevice device, Entry e, ref int uploads)
    {
        if (e.Job is not { IsCompleted: true } job || uploads == 0) return;
        e.Job = null;
        if (job.IsFaulted) { e.Missing = Array.Empty<string>(); e.Built = Empty(new VectorBucket()); return; }   // not retried in a loop
        var (bucket, complete, missing) = job.Result;
        if (bucket is null)
        {
            if (!complete) { e.Missing = missing; return; }
            e.Built = Empty(new VectorBucket());   // nothing there
            return;
        }
        uploads--;
        _built++;
        _buildMs += (bucket.Ms - _buildMs) / Math.Min(_built, 50);
        var byLayer = new Dictionary<int, List<VectorRange>>();
        foreach (var r in bucket.Ranges)
        {
            if (!byLayer.TryGetValue(r.Layer, out var list)) byLayer[r.Layer] = list = new List<VectorRange>();
            list.Add(r);
        }
        if (bucket.Indices.Length == 0) { e.Built = Empty(bucket); return; }
        e.Built = new Built
        {
            Bucket = bucket,
            ByLayer = byLayer,
            Vertices = Buffer.Vertex.New(device, bucket.Vertices, GraphicsResourceUsage.Immutable),
            Indices = Buffer.Index.New(device, bucket.Indices, GraphicsResourceUsage.Immutable),
        };
        // the arrays are on the GPU now; the bucket keeps only its ranges
        bucket.Vertices = Array.Empty<VectorVertex>();
        bucket.Indices = Array.Empty<uint>();

        static Built Empty(VectorBucket b) => new() { Bucket = b, ByLayer = new() };
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
    internal static TileKey? VectorKey(VectorSource source, TileKey g)
    {
        int ideal = Math.Max(0, (int)Math.Floor(g.Zoom - 1 + Math.Log2(512.0 / source.TileSize)));
        if (ideal < source.MinZoom) return null;
        int z = Math.Min(Math.Min(ideal, source.MaxZoom), g.Zoom);
        return new TileKey(z, g.X >> (g.Zoom - z), g.Y >> (g.Zoom - z));
    }

    static bool Contains(TileKey a, TileKey d) => d.Zoom >= a.Zoom && (d.X >> (d.Zoom - a.Zoom)) == a.X && (d.Y >> (d.Zoom - a.Zoom)) == a.Y;

    // d's square in a's, 0..1
    internal static Vector4 Rect(TileKey a, TileKey d)
    {
        double s = Math.Pow(2, d.Zoom - a.Zoom);
        double u0 = d.X / s - a.X, v0 = d.Y / s - a.Y;
        return new Vector4((float)u0, (float)v0, (float)(u0 + 1 / s), (float)(v0 + 1 / s));
    }

    static Vector4 Linear(StyleColor c)
    {
        var l = new Color4((float)c.R, (float)c.G, (float)c.B, (float)c.A).ToLinear();
        return new Vector4(l.R, l.G, l.B, l.A);
    }

    void Evict()
    {
        if (_entries.Count <= KeepTiles) return;
        foreach (var (k, e) in _entries.Where(kv => _frame - kv.Value.LastUsed > KeepFrames && kv.Value.Job is null).ToList())
        {
            e.Built?.Dispose();
            _entries.Remove(k);
        }
    }

    public void Dispose()
    {
        _renderer.Current = null;
        foreach (var e in _entries.Values) e.Built?.Dispose();
        _entries.Clear();
    }
}
