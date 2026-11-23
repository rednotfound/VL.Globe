using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace VL.Globe.Engine.Symbols;

/// <summary>
/// One image of a style's sprite (GL JS's StyleImage): its size in image pixels, the pixel ratio it was
/// drawn for, whether it is a signed-distance field, its stretch zones and content box (icon-text-fit).
/// </summary>
internal sealed record StyleImage(string Name, int Width, int Height, double PixelRatio, bool Sdf,
    (double From, double To)[]? StretchX, (double From, double To)[]? StretchY, double[]? Content,
    string? TextFitWidth, string? TextFitHeight, byte[] Pixels);

/// <summary>An image's place in the atlas (its 1-px padding included) and the image: GL JS's ImagePosition.</summary>
internal readonly record struct ImagePosition(AtlasRect PaddedRect, StyleImage Image)
{
    /// <summary>The image's size in CSS pixels: GL JS's displaySize.</summary>
    public (double W, double H) DisplaySize => ((PaddedRect.W - 2 * ImageAtlas.Padding) / Image.PixelRatio, (PaddedRect.H - 2 * ImageAtlas.Padding) / Image.PixelRatio);
}

// A style's sprite (icons, milestone 4): GL JS's load_sprite.ts - each sprite of the style's "sprite"
// (a URL, or a list of {id, url}) is <url>.json and <url>.png, "@2x" before the extension when the
// pixel ratio is above 1; an image of a sprite other than "default" is named "<id>:<name>". Loaded
// through the style's Load / Peek / Pending as the glyphs are, decoded once; each image is put into
// the style's one atlas when a layout first asks for it.
internal sealed class SpriteStore
{
    readonly MapStyleDocument _style;
    readonly object _lock = new();
    Dictionary<string, StyleImage>? _images;   // all sprites here
    public readonly ImageAtlas Atlas = new();

    public SpriteStore(MapStyleDocument style) => _style = style;

    /// <summary>Makes sure every sprite is here. False while one is on its way (its URLs in <paramref name="missing"/>).</summary>
    public bool Ensure(bool fetch, ICollection<string>? missing)
    {
        lock (_lock)
        {
            if (_images != null) return true;
            var images = new Dictionary<string, StyleImage>();
            string format = _style.PixelRatio > 1 ? "@2x" : "";
            bool complete = true;
            var loaded = new List<(string id, byte[]? json, byte[]? png)>();
            foreach (var (id, url) in _style.Sprites)
            {
                byte[]? Get(string u)
                {
                    var bytes = fetch || _style.Peek is null ? _style.Load?.Invoke(u) : _style.Peek(u);
                    if (bytes is null && (!fetch && _style.Peek != null || _style.Pending?.Invoke(u) == true)) { complete = false; missing?.Add(u); }
                    return bytes;
                }
                loaded.Add((id, Get(url + format + ".json"), Get(url + format + ".png")));
            }
            if (!complete) return false;
            foreach (var (id, json, png) in loaded)
            {
                if (json is null || png is null) { _style.Skip($"sprite {id}: not there"); continue; }
                try { Read(id, json, png, images); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
                {
                    _style.Skip($"sprite {id}: {e.Message}");
                }
            }
            _images = images;
            return true;
        }
    }

    static void Read(string id, byte[] json, byte[] png, Dictionary<string, StyleImage> images)
    {
        // the browser's canvas: straight RGBA, premultiplied on upload as GL JS's texture.ts does
        // (Math.round(c * a / 255))
        // decoded straight into unpremultiplied RGBA: SKBitmap.Decode premultiplies, and a copy keeps
        // that - premultiplied twice, the icons' soft edges came out dark (2026-11-22)
        using var codec = SKCodec.Create(new SKMemoryStream(png)) ?? throw new InvalidOperationException("the PNG cannot be read");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = SKBitmap.Decode(codec, info) ?? throw new InvalidOperationException("the PNG cannot be decoded");
        var all = bitmap.Bytes;
        int stride = bitmap.RowBytes;
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var v = p.Value;
            int x = v.GetProperty("x").GetInt32(), y = v.GetProperty("y").GetInt32();
            int w = v.GetProperty("width").GetInt32(), h = v.GetProperty("height").GetInt32();
            if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > bitmap.Width || y + h > bitmap.Height) continue;   // GL JS: an image outside the sprite is left out
            var pixels = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                {
                    int s = (y + row) * stride + (x + col) * 4, d = (row * w + col) * 4;
                    int a = all[s + 3];
                    for (int k = 0; k < 3; k++) pixels[d + k] = (byte)Math.Floor(all[s + k] * a / 255.0 + 0.5);
                    pixels[d + 3] = (byte)a;
                }
            double ratio = v.TryGetProperty("pixelRatio", out var pr) ? pr.GetDouble() : 1;
            bool sdf = v.TryGetProperty("sdf", out var sd) && sd.ValueKind == JsonValueKind.True;
            string name = id == "default" ? p.Name : $"{id}:{p.Name}";
            images[name] = new StyleImage(name, w, h, ratio, sdf, Zones(v, "stretchX"), Zones(v, "stretchY"),
                v.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(e => e.GetDouble()).ToArray() : null,
                v.TryGetProperty("textFitWidth", out var tw) ? tw.GetString() : null,
                v.TryGetProperty("textFitHeight", out var th) ? th.GetString() : null,
                pixels);
        }
    }

    static (double, double)[]? Zones(JsonElement v, string name) =>
        v.TryGetProperty(name, out var z) && z.ValueKind == JsonValueKind.Array
            ? z.EnumerateArray().Select(r => (r[0].GetDouble(), r[1].GetDouble())).ToArray()
            : null;

    /// <summary>The image named <paramref name="name"/> and its place in the atlas; false when the sprite has no such image.</summary>
    public bool TryGet(string name, out ImagePosition position)
    {
        position = default;
        StyleImage? image;
        lock (_lock) { if (_images is null || !_images.TryGetValue(name, out image)) return false; }
        position = new ImagePosition(Atlas.Place(image), image);
        return true;
    }
}

// The icons packed into one RGBA image (premultiplied), as GL JS's image_atlas.ts packs a tile's: each
// with a 1-px empty padding (IMAGE_PADDING), its rect that padding included. One atlas for a style,
// shelves, an image never moves - as the GlyphAtlas, whose reasoning this follows.
internal sealed class ImageAtlas : IRowAtlas
{
    public const int Padding = 1;
    public const int Width = 2048;

    readonly Dictionary<string, AtlasRect> _rects = new();
    readonly object _lock = new();
    byte[] _pixels = new byte[Width * 64 * 4];
    int _height = 64, _shelfX, _shelfY, _shelfH;

    public int Version { get; private set; }

    public AtlasRect Place(StyleImage image)
    {
        lock (_lock)
        {
            if (_rects.TryGetValue(image.Name, out var known)) return known;
            int w = image.Width + 2 * Padding, h = image.Height + 2 * Padding;
            if (w > Width) throw new StyleUnsupportedException($"an icon {image.Width} px wide");
            if (_shelfX + w > Width) { _shelfY += _shelfH; _shelfX = 0; _shelfH = 0; }
            while (_shelfY + h > _height) Grow();
            var rect = new AtlasRect(_shelfX, _shelfY, w, h);
            for (int y = 0; y < image.Height; y++)
                Buffer.BlockCopy(image.Pixels, y * image.Width * 4, _pixels, ((rect.Y + Padding + y) * Width + rect.X + Padding) * 4, image.Width * 4);
            _shelfX += w;
            _shelfH = Math.Max(_shelfH, h);
            _rects[image.Name] = rect;
            _dirtyFrom = Math.Min(_dirtyFrom, rect.Y); _dirtyTo = Math.Max(_dirtyTo, rect.Y + rect.H);
            Version++;
            return rect;
        }
    }

    // the rows placed since the last upload, [from, to): what the texture showing the atlas lacks
    int _dirtyFrom = int.MaxValue, _dirtyTo;

    public int Height { get { lock (_lock) return _height; } }

    /// <summary>The whole image to <paramref name="create"/>, under the lock and not copied; every row counts as uploaded.</summary>
    public T Upload<T>(Func<byte[], int, T> create)
    {
        lock (_lock) { _dirtyFrom = int.MaxValue; _dirtyTo = 0; return create(_pixels, _height); }
    }

    /// <summary>The rows placed since the last upload and below <paramref name="height"/> to <paramref name="upload"/>
    /// (pixels, first row, rows), under the lock and not copied.</summary>
    public void UploadChanges(int height, AtlasRows upload)
    {
        lock (_lock)
        {
            int to = Math.Min(_dirtyTo, height);
            if (to <= _dirtyFrom) return;
            upload(_pixels, _dirtyFrom, to - _dirtyFrom);
            if (to >= _dirtyTo) { _dirtyFrom = int.MaxValue; _dirtyTo = 0; } else _dirtyFrom = to;
        }
    }

    void Grow()
    {
        var bigger = new byte[Width * _height * 2 * 4];
        Buffer.BlockCopy(_pixels, 0, bigger, 0, _pixels.Length);
        _pixels = bigger;
        _height *= 2;
    }

    /// <summary>A copy of the image (Width x Height, RGBA premultiplied) and its version, for an upload.</summary>
    public (byte[] pixels, int height, int version) Snapshot()
    {
        lock (_lock) return ((byte[])_pixels.Clone(), _height, Version);
    }
}
