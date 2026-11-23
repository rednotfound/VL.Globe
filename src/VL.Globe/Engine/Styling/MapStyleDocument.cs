using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace VL.Globe.Engine.Styling;

// A MapLibre style as the GPU path reads it (VectorBucket): its features, its sources and its layers.

/// <summary>One feature of a source: geometry in Web Mercator world coordinates (0..1, y down), and its properties.</summary>
internal sealed class StyleFeature : IReadOnlyDictionary<string, object?>
{
    public enum Shape { Point, Line, Polygon }
    public Shape Kind;
    // Point: each part one coordinate. Line: each part one line. Polygon: each part one polygon, its rings.
    // A view of its layer's geometry (FeatureGeometry.cs: a decoded tile is a few large arrays, 4g step 4)
    public FeatureParts Parts => new(_geometry, _firstPart, _partCount);
    // An MVT feature is its own properties - a view of its layer's key and value tables over one shared
    // tag array - and keeps its id unboxed: one object a feature, not three (4g step 4, 2026-11-23: a
    // heap dump while zooming grew by 181k features, 181k property views and 206k boxed doubles).
    public IReadOnlyDictionary<string, object?> Properties
    {
        get => _properties ?? (_tags != null ? this : NoProperties);
        set => _properties = value;
    }
    public object? Id
    {
        get => _hasNumericId ? _numericId : _id;
        set { _id = value; _hasNumericId = false; }
    }
    IReadOnlyDictionary<string, object?>? _properties;
    object? _id;
    double _numericId;
    bool _hasNumericId;
    string[]? _keys;
    ValueTable? _values;
    int[]? _tags;   // key, value, key, value ...
    int _tagStart, _tagCount;   // in pairs

    /// <summary>An MVT feature's id, unboxed until asked for.</summary>
    public void SetNumericId(double id) { _numericId = id; _hasNumericId = true; _id = null; }

    /// <summary>The feature's properties as <paramref name="count"/> key/value pairs of its layer's tables from <paramref name="start"/>.</summary>
    public void SetTags(string[] keys, ValueTable values, int[] tags, int start, int count)
    {
        _keys = keys; _values = values; _tags = tags; _tagStart = start; _tagCount = count; _properties = null;
    }

    // the properties' dictionary, for an MVT feature (a key given twice keeps its last value, as a dictionary
    // filled in order did)
    public bool TryGetValue(string key, out object? value)
    {
        if (_properties != null) return _properties.TryGetValue(key, out value);
        if (_tags is { } tags)
            for (int i = _tagCount - 1; i >= 0; i--)
            {
                int t = 2 * (_tagStart + i);
                if (string.Equals(_keys![tags[t]], key)) { value = _values![tags[t + 1]]; return true; }
            }
        value = null;
        return false;
    }
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public object? this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);
    IEnumerable<KeyValuePair<string, object?>> Pairs()
    {
        if (_properties != null) { foreach (var kv in _properties) yield return kv; yield break; }
        if (_tags is null) yield break;
        var seen = new HashSet<string>();
        for (int i = 0; i < _tagCount; i++)
        {
            var key = _keys![_tags[2 * (_tagStart + i)]];
            if (seen.Add(key) && TryGetValue(key, out var v)) yield return new(key, v);
        }
    }
    int IReadOnlyCollection<KeyValuePair<string, object?>>.Count { get { int n = 0; foreach (var _ in Pairs()) n++; return n; } }
    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys { get { foreach (var kv in Pairs()) yield return kv.Key; } }
    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values { get { foreach (var kv in Pairs()) yield return kv.Value; } }
    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() => Pairs().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Pairs().GetEnumerator();

    static readonly IReadOnlyDictionary<string, object?> NoProperties = new Dictionary<string, object?>();
    FeatureGeometry _geometry = FeatureGeometry.Empty;
    int _firstPart, _partCount;

    /// <summary>Its place in its <see cref="FeatureTable"/>.</summary>
    public int Index;

    /// <summary>The feature's parts are <paramref name="count"/> parts of <paramref name="geometry"/> from <paramref name="first"/>.</summary>
    public void SetGeometry(FeatureGeometry geometry, int first, int count) { _geometry = geometry; _firstPart = first; _partCount = count; _bounds = null; }

    /// <summary>The feature's parts from lists (GeoJSON, tests): a geometry of its own.</summary>
    public void SetParts(IEnumerable<IEnumerable<(double X, double Y)[]>> parts)
    {
        var b = new FeatureGeometryBuilder();
        foreach (var part in parts)
        {
            foreach (var ring in part) b.AddRing(ring);
            b.EndPart();
        }
        SetGeometry(b.Build(), 0, b.PartCount);
    }
    public string GeometryType => Kind switch { Shape.Point => "Point", Shape.Line => "LineString", _ => "Polygon" };

    /// <summary>The world rectangle the geometry covers, computed once (features are not changed after reading).</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds
    {
        get
        {
            if (_bounds is { } b) return b;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var part in Parts)
                foreach (var ring in part)
                    foreach (var (x, y) in ring)
                    {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
            var r = (x0, y0, x1, y1);
            _bounds = r;
            return r;
        }
    }
    (double, double, double, double)? _bounds;
}

/// <summary>A source of a style: inline GeoJSON, or vector tiles fetched by URL.</summary>
internal abstract class StyleSource
{
    // the credit the source's provider requires (a style's or TileJSON's "attribution", often HTML)
    public string Attribution = "";
}

internal sealed class GeoJsonSource : StyleSource
{
    public FeatureTable? Features;   // inline, or once loaded from Url
    public string? Url;
}

internal sealed class VectorSource : StyleSource
{
    public string[] Tiles = Array.Empty<string>();
    public int MinZoom, MaxZoom = 22, TileSize = 512;
    public bool Tms;

    public string Url(TileKey t)
    {
        int y = Tms ? (1 << t.Zoom) - 1 - t.Y : t.Y;
        return Tiles[Math.Abs(t.X + t.Y) % Tiles.Length]
            .Replace("{z}", t.Zoom.ToString(CultureInfo.InvariantCulture))
            .Replace("{x}", t.X.ToString(CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>A parsed style: its layers, its sources, the camera of a render test.</summary>
/// <remarks>
/// Strict (the render tests): anything outside the subset throws. Lenient (a real style on the
/// globe): an unsupported source or layer is left out, an unsupported property is ignored, a layer
/// that throws while drawing is dropped - each named in <see cref="Skipped"/>. Drawing may run on
/// several threads at once: the caches are locked.
/// </remarks>
internal sealed class MapStyleDocument
{
    public sealed record Layer(string Id, string Type, string? Source, string? SourceLayer, JsonElement? Filter,
        JsonElement Paint, JsonElement Layout, double MinZoom, double MaxZoom);

    public List<Layer> Layers = new();
    public Dictionary<string, StyleSource> Sources = new();
    public Dictionary<string, JsonElement> GlobalState = new();
    public double CenterLon, CenterLat, Zoom;
    public double Bearing;   // degrees clockwise from north the camera faces (a render test's camera)
    /// <summary>The style's glyph URL template ({fontstack}/{range}.pbf), or null: labels need it.</summary>
    public string? Glyphs;

    /// <summary>The style's glyph ranges and their atlas (labels), made on first use.</summary>
    public GlyphStore GlyphStore => System.Threading.LazyInitializer.EnsureInitialized(ref _glyphStore, () => new GlyphStore(this))!;
    GlyphStore? _glyphStore;
    /// <summary>The style's sprites (icons): (id, URL without extension), "default" for a plain URL.</summary>
    public List<(string Id, string Url)> Sprites = new();
    /// <summary>The style's sprite images and their atlas (icons), made on first use.</summary>
    public SpriteStore SpriteStore => System.Threading.LazyInitializer.EnsureInitialized(ref _spriteStore, () => new SpriteStore(this))!;
    SpriteStore? _spriteStore;
    /// <summary>The device pixel ratio the sprite is chosen for: "@2x" above 1, as GL JS's.</summary>
    public double PixelRatio = 1;
    public bool Lenient;

    /// <summary>What a lenient parse or draw left out, and why: "layer id: reason".</summary>
    public IReadOnlyCollection<string> Skipped { get { lock (_skipped) return _skipped.ToList(); } }
    readonly HashSet<string> _skipped = new();

    /// <summary>Vector tiles decoded so far, by zoom level: what drawing the globe has asked for.</summary>
    public IReadOnlyDictionary<int, int> DecodedByZoom { get { lock (_decoded) return new SortedDictionary<int, int>(_decoded); } }
    readonly Dictionary<int, int> _decoded = new();
    readonly HashSet<string> _dropped = new();   // layers that threw while drawing

    /// <summary>Fetches a resource (a vector tile, GeoJSON data) by URL; null when it does not exist or is not here yet. Set by the host.</summary>
    public Func<string, byte[]?>? Load;
    /// <summary>True while a resource Load returned null for is still on its way. Set by the host.</summary>
    public Func<string, bool>? Pending;
    /// <summary>A resource's bytes only if they are already here (memory or disk), never a fetch; Load when unset. Set by the host.</summary>
    public Func<string, byte[]?>? Peek;

    // decoded tiles, the most recently used last; a miss that is pending is not remembered
    const int TileCacheSize = 256;
    readonly Dictionary<(string, TileKey), LinkedListNode<((string, TileKey) key, Dictionary<string, FeatureTable>? layers)>> _tiles = new();
    readonly LinkedList<((string, TileKey) key, Dictionary<string, FeatureTable>? layers)> _lru = new();

    /// <summary>A vector tile's layers, decoded once; null when it cannot be had, and <paramref name="complete"/> false when it may still come.</summary>
    public Dictionary<string, FeatureTable>? Tile(VectorSource source, TileKey key, ref bool complete, bool fetch = true, ICollection<string>? missing = null)
    {
        var url = source.Url(key);
        var k = (url, key);   // keyed by tile too: a template without {z}/{x}/{y} serves one file for every tile
        if (Cached(k, out var hit)) return hit;
        // one decode per tile: the overlays of the 16 globe tiles a vector tile covers are drawn on
        // several threads at once, and each decoded it (18 decodes for 6 tiles, 2026-11-21)
        lock (_gates.GetOrAdd(k, _ => new object()))
        {
            if (Cached(k, out hit)) return hit;
            return LoadAndDecode(url, key, k, ref complete, fetch, missing);
        }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<(string, TileKey), object> _gates = new();

    /// <summary>Drops every decoded tile (probes measure a decode as a tile coming for the first time).</summary>
    internal void ForgetDecoded() { lock (_tiles) { _tiles.Clear(); _lru.Clear(); } }

    bool Cached((string, TileKey) k, out Dictionary<string, FeatureTable>? layers)
    {
        lock (_tiles)
        {
            if (_tiles.TryGetValue(k, out var node)) { _lru.Remove(node); _lru.AddLast(node); layers = node.Value.layers; return true; }
        }
        layers = null;
        return false;
    }

    Dictionary<string, FeatureTable>? LoadAndDecode(string url, TileKey key, (string, TileKey) k, ref bool complete, bool fetch, ICollection<string>? missing)
    {
        // without fetch (a stand-in tile on the globe) only what is already here is drawn; a miss
        // leaves it incomplete, so it is drawn again once the tile has come for another reason
        var bytes = fetch || Peek is null ? Load?.Invoke(url) : Peek(url);
        if (bytes is null && (!fetch && Peek != null || Pending?.Invoke(url) == true)) { complete = false; missing?.Add(url); return null; }
        Dictionary<string, FeatureTable>? layers = null;
        if (bytes != null)
        {
            lock (_decoded) _decoded[key.Zoom] = _decoded.GetValueOrDefault(key.Zoom) + 1;
            try { layers = VectorTile.Decode(bytes, key); }
            catch (Exception e) when (e is System.IO.InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                Skip($"tile {url}: {e.Message}");
            }
        }
        lock (_tiles)
        {
            if (!_tiles.ContainsKey(k))
            {
                _tiles[k] = _lru.AddLast((k, layers));
                if (_lru.Count > TileCacheSize) { _tiles.Remove(_lru.First!.Value.key); _lru.RemoveFirst(); }
            }
        }
        return layers;
    }

    /// <summary>A GeoJSON source's features, loaded once; unreadable or invalid data draws nothing, as in GL JS.</summary>
    public FeatureTable Features(GeoJsonSource g, ref bool complete)
    {
        lock (g)
        {
            if (g.Features != null) return g.Features;
            var bytes = g.Url is null ? null : Load?.Invoke(g.Url);
            if (bytes is null && g.Url != null && Pending?.Invoke(g.Url) == true) { complete = false; return FeatureTable.Empty; }
            var features = new List<StyleFeature>();
            if (bytes != null)
            {
                try
                {
                    using var json = JsonDocument.Parse(bytes);
                    GeoJson.Read(json.RootElement, features);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
                {
                    features.Clear();
                }
            }
            return g.Features = new FeatureTable(features.ToArray());
        }
    }

    internal void Skip(string what) { lock (_skipped) _skipped.Add(what); }
    internal bool IsDropped(string layerId) { lock (_skipped) return _dropped.Contains(layerId); }
    internal void Drop(string layerId, string why) { lock (_skipped) { _dropped.Add(layerId); _skipped.Add($"{layerId}: {why}"); } }

    static readonly HashSet<string> RootKeys = new() { "version", "name", "metadata", "center", "zoom", "bearing", "pitch", "sources", "layers", "state", "sprite", "glyphs", "transition" };
    static readonly HashSet<string> LayerKeys = new() { "id", "type", "source", "source-layer", "filter", "paint", "layout", "minzoom", "maxzoom", "metadata" };
    static readonly Dictionary<string, HashSet<string>> PaintKeys = new()
    {
        ["background"] = new() { "background-color", "background-opacity" },
        ["fill"] = new() { "fill-color", "fill-opacity", "fill-outline-color", "fill-antialias" },
        ["line"] = new() { "line-color", "line-opacity", "line-width" },
        // lenient parses only (see Parse): drawn by the GPU path
        ["fill-extrusion"] = new() { "fill-extrusion-color", "fill-extrusion-opacity", "fill-extrusion-height", "fill-extrusion-base" },
        // labels, milestone 1 (ROADMAP 4e): point text, drawn by the GPU path
        ["symbol"] = new() { "text-color", "text-opacity", "text-halo-color", "text-halo-width", "text-halo-blur",
            // icons, milestone 4
            "icon-color", "icon-opacity", "icon-halo-color", "icon-halo-width", "icon-halo-blur" },
        // circles, drawn by the GPU path (VectorBucket.Circles.cs)
        ["circle"] = new() { "circle-color", "circle-opacity", "circle-radius", "circle-blur", "circle-stroke-width",
            "circle-stroke-color", "circle-stroke-opacity", "circle-pitch-scale", "circle-pitch-alignment", "circle-translate" },
    };
    static readonly Dictionary<string, HashSet<string>> LayoutKeys = new()
    {
        ["background"] = new() { "visibility" },
        ["circle"] = new() { "visibility" },
        ["fill"] = new() { "visibility" },
        ["fill-extrusion"] = new() { "visibility" },
        // what the layout checks for itself: symbol-placement point only, the alignments viewport/auto
        ["symbol"] = new() { "visibility", "symbol-placement", "text-field", "text-font", "text-size", "text-max-width",
            "text-line-height", "text-letter-spacing", "text-justify", "text-anchor", "text-offset", "text-transform", "text-rotate",
            "text-allow-overlap", "text-ignore-placement", "text-padding", "text-optional", "text-keep-upright",
            "text-rotation-alignment", "text-pitch-alignment",
            // milestone 3, labels along lines
            "symbol-spacing", "text-max-angle",
            // milestone 4, icons
            "icon-image", "icon-size", "icon-anchor", "icon-offset", "icon-rotate", "icon-padding", "icon-allow-overlap",
            "icon-ignore-placement", "icon-optional", "icon-rotation-alignment", "icon-pitch-alignment", "icon-text-fit",
            "icon-text-fit-padding", "icon-keep-upright" },
        ["line"] = new() { "visibility", "line-cap", "line-join", "line-miter-limit", "line-round-limit" },
    };

    /// <summary>Reads a style JSON. Strict: throws StyleUnsupportedException for anything outside the subset.
    /// Lenient: leaves it out and names it in Skipped. <paramref name="tileJson"/> resolves a vector source's "url".</summary>
    public static MapStyleDocument Parse(JsonElement root, bool lenient = false, Func<string, JsonElement?>? tileJson = null)
    {
        var doc = new MapStyleDocument { Lenient = lenient };
        // in a lenient parse, an unsupported part is skipped; in a strict one it throws
        void Unsupported(string what) { if (lenient) doc.Skip(what); else throw new StyleUnsupportedException(what); }

        foreach (var p in root.EnumerateObject())
            if (!RootKeys.Contains(p.Name) && !lenient) throw new StyleUnsupportedException($"style.{p.Name}");
        if (!lenient)
        {
            // a render test's camera; on the globe the camera is the globe's. The bearing is read (the GPU
            // path's harness turns its view by it); the drape has none
            if (root.TryGetProperty("pitch", out var pitch) && pitch.GetDouble() != 0) throw new StyleUnsupportedException("pitch");
        }
        if (root.TryGetProperty("center", out var c) && c.ValueKind == JsonValueKind.Array) { doc.CenterLon = c[0].GetDouble(); doc.CenterLat = c[1].GetDouble(); }
        if (root.TryGetProperty("zoom", out var z) && z.ValueKind == JsonValueKind.Number) doc.Zoom = z.GetDouble();
        if (root.TryGetProperty("bearing", out var b) && b.ValueKind == JsonValueKind.Number) doc.Bearing = b.GetDouble();
        if (root.TryGetProperty("glyphs", out var glyphs) && glyphs.ValueKind == JsonValueKind.String) doc.Glyphs = glyphs.GetString();
        // GL JS's coerceSpriteToArray: a URL is the sprite "default"
        if (root.TryGetProperty("sprite", out var sprite))
        {
            if (sprite.ValueKind == JsonValueKind.String) doc.Sprites.Add(("default", sprite.GetString()!));
            else if (sprite.ValueKind == JsonValueKind.Array)
                foreach (var e in sprite.EnumerateArray())
                    if (e.TryGetProperty("id", out var sid) && e.TryGetProperty("url", out var surl)) doc.Sprites.Add((sid.GetString()!, surl.GetString()!));
        }
        if (root.TryGetProperty("state", out var state))
            foreach (var s in state.EnumerateObject())
                if (s.Value.TryGetProperty("default", out var d)) doc.GlobalState[s.Name] = d.Clone();

        if (root.TryGetProperty("sources", out var sources))
            foreach (var s in sources.EnumerateObject())
            {
                try { doc.Sources[s.Name] = ReadSource(s.Value, lenient, tileJson); }
                catch (StyleUnsupportedException e) when (lenient) { doc.Skip($"source {s.Name}: {e.Message}"); }
            }

        foreach (var l in root.GetProperty("layers").EnumerateArray())
        {
            string id = l.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
            try
            {
                foreach (var p in l.EnumerateObject())
                    if (!LayerKeys.Contains(p.Name)) Unsupported($"{id}: layer.{p.Name}");
                var type = l.GetProperty("type").GetString()!;
                if (!PaintKeys.ContainsKey(type)) throw new StyleUnsupportedException($"{type} layer");
                // A real style hands its buildings from a flat fill to fill-extrusion (Liberty: building
                // below z14, building-3d from 14), so without it buildings vanished as one zoomed in (the
                // user, 2026-11-21). A lenient parse keeps it for the GPU path, which draws it in 3D; a
                // strict one still refuses it until MapLibre's fill-extrusion fixtures are rendered by
                // the GPU path (the harness renders with the drape, which leaves it out).
                if (type == "fill-extrusion" && !lenient) throw new StyleUnsupportedException("fill-extrusion layer");
                var paint = l.TryGetProperty("paint", out var pp) ? pp.Clone() : Empty;
                var layout = l.TryGetProperty("layout", out var ll) ? ll.Clone() : Empty;
                foreach (var p in paint.EnumerateObject())
                {
                    if (PaintKeys[type].Contains(p.Name) || p.Name.EndsWith("-transition")) continue;
                    // without these the layer would be drawn as something else (a pattern fill as
                    // black, a casing's two lines as one): leave the layer out instead
                    if (p.Name is "fill-pattern" or "line-pattern" or "line-gap-width" or "line-gradient" or "line-offset" or "fill-translate" or "line-translate"
                        or "fill-extrusion-pattern" or "fill-extrusion-translate")
                        throw new StyleUnsupportedException(p.Name);
                    Unsupported($"{id}: {p.Name} ignored");
                }
                foreach (var p in layout.EnumerateObject())
                    if (!LayoutKeys[type].Contains(p.Name)) Unsupported($"{id}: {p.Name} ignored");
                string? source = l.TryGetProperty("source", out var src) ? src.GetString() : null;
                if (type != "background" && (source is null || !doc.Sources.ContainsKey(source))) throw new StyleUnsupportedException($"source '{source}'");
                string? sourceLayer = l.TryGetProperty("source-layer", out var sl) ? sl.GetString() : null;
                if (source != null && doc.Sources[source] is VectorSource && sourceLayer is null) throw new StyleUnsupportedException("a vector layer without source-layer");
                doc.Layers.Add(new Layer(id, type, source, sourceLayer,
                    l.TryGetProperty("filter", out var f) ? f.Clone() : null, paint, layout,
                    l.TryGetProperty("minzoom", out var mn) ? mn.GetDouble() : 0,
                    l.TryGetProperty("maxzoom", out var mx) ? mx.GetDouble() : 24));
            }
            catch (StyleUnsupportedException e) when (lenient) { doc.Skip($"{id}: {e.Message}"); }
        }
        return doc;
    }

    static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    static StyleSource ReadSource(JsonElement s, bool lenient, Func<string, JsonElement?>? tileJson)
    {
        var type = s.GetProperty("type").GetString();
        switch (type)
        {
            case "geojson":
            {
                foreach (var p in s.EnumerateObject())
                    if (!lenient && p.Name is not ("type" or "data" or "maxzoom" or "tolerance" or "buffer" or "attribution"))
                        throw new StyleUnsupportedException($"geojson source {p.Name}");
                var data = s.GetProperty("data");
                var credit = s.TryGetProperty("attribution", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                if (data.ValueKind == JsonValueKind.String) return new GeoJsonSource { Url = data.GetString(), Attribution = credit };
                var features = new List<StyleFeature>();
                try { GeoJson.Read(data, features); }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
                {
                    features.Clear();   // invalid GeoJSON draws nothing, as in GL JS
                }
                return new GeoJsonSource { Features = new FeatureTable(features.ToArray()), Attribution = credit };
            }
            case "vector":
            {
                var v = new VectorSource();
                // TileJSON first, then the source's own keys over it, as GL JS merges them
                if (s.TryGetProperty("url", out var url))
                {
                    var tj = url.GetString() is { } u ? tileJson?.Invoke(u) : null;
                    if (tj is null) throw new StyleUnsupportedException($"TileJSON not available: {url.GetString()}");
                    ReadVectorKeys(tj.Value, v, lenient: true);
                }
                ReadVectorKeys(s, v, lenient);
                if (v.Tiles.Length == 0) throw new StyleUnsupportedException("vector source without tiles");
                return v;
            }
        }
        throw new StyleUnsupportedException($"{type} source");
    }

    static void ReadVectorKeys(JsonElement s, VectorSource v, bool lenient)
    {
        foreach (var p in s.EnumerateObject())
            switch (p.Name)
            {
                case "tiles": v.Tiles = p.Value.EnumerateArray().Select(t => t.GetString()!).ToArray(); break;
                case "minzoom": v.MinZoom = (int)p.Value.GetDouble(); break;
                case "maxzoom": v.MaxZoom = (int)p.Value.GetDouble(); break;
                case "tileSize": v.TileSize = (int)p.Value.GetDouble(); break;
                case "scheme": v.Tms = p.Value.GetString() == "tms"; break;
                case "attribution": v.Attribution = p.Value.GetString() ?? ""; break;
                case "type": case "bounds": case "url": break;
                default: if (!lenient) throw new StyleUnsupportedException($"vector source {p.Name}"); break;
            }
    }
}

/// <summary>GeoJSON (Geometry, Feature, FeatureCollection) into StyleFeatures in world coordinates.</summary>
internal static class GeoJson
{
    public static void Read(JsonElement e, List<StyleFeature> into)
    {
        switch (e.GetProperty("type").GetString())
        {
            case "FeatureCollection":
                foreach (var f in e.GetProperty("features").EnumerateArray()) Read(f, into);
                break;
            case "Feature":
            {
                var props = new Dictionary<string, object?>();
                if (e.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var kv in p.EnumerateObject()) props[kv.Name] = Expression.FromJson(kv.Value.Clone(), Expression.Kind.Any);
                object? id = e.TryGetProperty("id", out var i) ? Expression.FromJson(i, Expression.Kind.Any) : null;
                if (e.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object) Geometry(g, props, id, into);
                break;
            }
            default:
                Geometry(e, new Dictionary<string, object?>(), null, into);
                break;
        }
    }

    static void Geometry(JsonElement g, Dictionary<string, object?> props, object? id, List<StyleFeature> into)
    {
        var type = g.GetProperty("type").GetString();
        if (type == "GeometryCollection")
        {
            foreach (var child in g.GetProperty("geometries").EnumerateArray()) Geometry(child, props, id, into);
            return;
        }
        // a geometry without coordinates is skipped, as GL JS does
        if (!g.TryGetProperty("coordinates", out var c) || c.ValueKind != JsonValueKind.Array) return;
        var f = new StyleFeature { Properties = props, Id = id };
        var parts = new List<List<(double, double)[]>>();
        switch (type)
        {
            case "Point": f.Kind = StyleFeature.Shape.Point; parts.Add(new() { new[] { Pos(c) } }); break;
            case "MultiPoint": f.Kind = StyleFeature.Shape.Point; foreach (var p in c.EnumerateArray()) parts.Add(new() { new[] { Pos(p) } }); break;
            case "LineString": f.Kind = StyleFeature.Shape.Line; parts.Add(new() { Line(c) }); break;
            case "MultiLineString": f.Kind = StyleFeature.Shape.Line; foreach (var l in c.EnumerateArray()) parts.Add(new() { Line(l) }); break;
            case "Polygon": f.Kind = StyleFeature.Shape.Polygon; parts.Add(c.EnumerateArray().Select(Line).ToList()); break;
            case "MultiPolygon": f.Kind = StyleFeature.Shape.Polygon; foreach (var p in c.EnumerateArray()) parts.Add(p.EnumerateArray().Select(Line).ToList()); break;
            default: throw new StyleUnsupportedException($"GeoJSON {type}");
        }
        f.SetParts(parts);
        into.Add(f);
    }

    static (double, double) Pos(JsonElement p) => ((p[0].GetDouble() + 180) / 360, TileGeometry.MercatorY(p[1].GetDouble()));
    static (double, double)[] Line(JsonElement l) => l.EnumerateArray().Select(Pos).ToArray();
}
