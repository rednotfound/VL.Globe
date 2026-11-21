using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

/// <summary>A glyph's place in the atlas, its 1-px padding included (GL JS's glyph_atlas.ts rect).</summary>
internal readonly record struct AtlasRect(int X, int Y, int W, int H);

// The glyphs' signed-distance fields packed into one alpha image, as GL JS's glyph_atlas.ts packs
// them: each bitmap with a 1-px empty padding round it, its rect that padding included. GL JS packs
// per tile with potpack; here one atlas grows for a whole style - shelves, a glyph never moves once
// placed, so buckets built earlier keep their texture coordinates and only the image is uploaded
// again (Version says when). A glyph samples only its own rect and padding (the quad reaches 4 px
// past the box: the 3-px SDF border and the padding), so where it lies changes no pixel.
internal sealed class GlyphAtlas
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
            Version++;
            return rect;
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
