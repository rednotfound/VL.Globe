using System;
using System.Collections.Generic;

namespace VL.Globe.Engine.Symbols;

/// <summary>A glyph's place in the atlas, its 1-px padding included (GL JS's glyph_atlas.ts rect).</summary>
internal readonly record struct AtlasRect(int X, int Y, int W, int H);

/// <summary>Rows of an atlas handed out for an upload: its pixels, the first row, the number of rows.</summary>
internal delegate void AtlasRows(byte[] pixels, int fromRow, int rows);

/// <summary>An image that grows by rows: the glyph atlas and the icon atlas.</summary>
internal interface IRowAtlas
{
    int Height { get; }
    T Upload<T>(Func<byte[], int, T> create);
    void UploadChanges(int height, AtlasRows upload);
}

// The glyphs' signed-distance fields packed into one alpha image, as GL JS's glyph_atlas.ts packs
// them: each bitmap with a 1-px empty padding round it, its rect that padding included. GL JS packs
// per tile with potpack; here one atlas grows for a whole style - shelves, a glyph never moves once
// placed, so buckets built earlier keep their texture coordinates and only the image is uploaded
// again (Version says when). A glyph samples only its own rect and padding (the quad reaches 4 px
// past the box: the 3-px SDF border and the padding), so where it lies changes no pixel.
internal sealed class GlyphAtlas : IRowAtlas
{
    public const int Padding = 1;
    public const int Width = 1024;

    readonly Dictionary<(string stack, int id), AtlasRect> _rects = new();
    readonly object _lock = new();
    byte[] _pixels = new byte[Width * 64];
    int _height = 64, _shelfX, _shelfY, _shelfH;

    /// <summary>Bumped whenever a glyph is added: the texture is out of date.</summary>
    public int Version { get; private set; }

    /// <summary>The rect of a glyph, placing it first if it is new; null for a glyph that draws nothing (a space).</summary>
    public AtlasRect? Place(string stack, Glyph glyph)
    {
        if (glyph.Bitmap.Length == 0) return null;
        lock (_lock)
        {
            if (_rects.TryGetValue((stack, glyph.Id), out var known)) return known;
            int w = glyph.BitmapWidth + 2 * Padding, h = glyph.BitmapHeight + 2 * Padding;
            if (w > Width) throw new InvalidOperationException($"glyph {glyph.Id} is {w} px wide");
            if (_shelfX + w > Width) { _shelfY += _shelfH; _shelfX = 0; _shelfH = 0; }
            while (_shelfY + h > _height) Grow();
            var rect = new AtlasRect(_shelfX, _shelfY, w, h);
            for (int y = 0; y < glyph.BitmapHeight; y++)
                Buffer.BlockCopy(glyph.Bitmap, y * glyph.BitmapWidth, _pixels, (rect.Y + Padding + y) * Width + rect.X + Padding, glyph.BitmapWidth);
            _shelfX += w;
            _shelfH = Math.Max(_shelfH, h);
            _rects[(stack, glyph.Id)] = rect;
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
        var bigger = new byte[Width * _height * 2];
        Buffer.BlockCopy(_pixels, 0, bigger, 0, _pixels.Length);
        _pixels = bigger;
        _height *= 2;
    }

    /// <summary>A copy of the image (Width x Height, one byte a pixel) and its version, for an upload.</summary>
    public (byte[] pixels, int height, int version) Snapshot()
    {
        lock (_lock) return ((byte[])_pixels.Clone(), _height, Version);
    }
}
