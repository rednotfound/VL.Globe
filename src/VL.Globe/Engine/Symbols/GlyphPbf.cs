using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace VL.Globe.Engine.Symbols;

/// <summary>
/// One glyph of a font stack as MapLibre's glyph server sends it: a signed-distance field of the
/// glyph at 24 px (ONE_EM), with a 3-px border on every side, and its metrics in those pixels.
/// </summary>
internal sealed record Glyph(int Id, byte[] Bitmap, int Width, int Height, int Left, int Top, int Advance)
{
    /// <summary>The SDF's border around the glyph's box, in pixels (GL JS's GLYPH_PBF_BORDER).</summary>
    public const int Border = 3;

    /// <summary>The bitmap's size: the glyph's box and the border, or 0 x 0 for a glyph that draws nothing (a space).</summary>
    public int BitmapWidth => Bitmap.Length == 0 ? 0 : Width + 2 * Border;
    public int BitmapHeight => Bitmap.Length == 0 ? 0 : Height + 2 * Border;
}

// A glyph range (256 code points of one font stack, {fontstack}/{start}-{end}.pbf) decoded, as GL JS's
// glyphs.ts reads it (glyphs.proto: glyphs { repeated fontstack stacks = 1 }, fontstack { name = 1,
// range = 2, repeated glyph glyphs = 3 }, glyph { id = 1, bitmap = 2, width = 3, height = 4,
// sint32 left = 5, sint32 top = 6, advance = 7 }).
internal static class GlyphPbf
{
    public static Dictionary<int, Glyph> Decode(byte[] data)
    {
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        var glyphs = new Dictionary<int, Glyph>();
        var r = new Pbf(data, 0, data.Length);
        while (r.Next(out int field, out int wire))
        {
            if (field != 1 || wire != 2) { r.Skip(wire); continue; }
            var (s0, e0) = r.Sub();
            var stack = new Pbf(data, s0, e0);
            while (stack.Next(out int f, out int w))
            {
                if (f != 3 || w != 2) { stack.Skip(w); continue; }
                var (s1, e1) = stack.Sub();
                var g = ReadGlyph(new Pbf(data, s1, e1));
                if (g != null) glyphs[g.Id] = g;
            }
        }
        return glyphs;
    }

    static Glyph? ReadGlyph(Pbf r)
    {
        int id = -1, width = 0, height = 0, left = 0, top = 0, advance = 0;
        byte[] bitmap = Array.Empty<byte>();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: id = (int)r.Varint(); break;
                case 2: { var (s, e) = r.Sub(); bitmap = r.Data.AsSpan(s, e - s).ToArray(); break; }
                case 3: width = (int)r.Varint(); break;
                case 4: height = (int)r.Varint(); break;
                case 5: left = (int)ZigZag(r.Varint()); break;
                case 6: top = (int)ZigZag(r.Varint()); break;
                case 7: advance = (int)r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        if (id < 0) return null;
        // a bitmap of another size than the box and its border would be read out of bounds
        if (bitmap.Length != 0 && bitmap.Length != (width + 2 * Glyph.Border) * (height + 2 * Glyph.Border))
            throw new InvalidDataException($"glyph {id}: a bitmap of {bitmap.Length} bytes for a {width} x {height} box");
        return new Glyph(id, bitmap, width, height, left, top, advance);
    }

    static long ZigZag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);
}
