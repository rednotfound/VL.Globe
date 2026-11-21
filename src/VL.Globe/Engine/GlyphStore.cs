using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// A style's glyphs (ROADMAP 4e): the ranges of 256 code points its "glyphs" URL serves, loaded as a
// vector tile is (MapStyleDocument's Load / Peek / Pending, set by the host), decoded once, and each
// glyph placed in the style's one atlas when shaping first asks for it - as GL JS's glyph_manager.ts
// requests ranges and glyph_atlas.ts packs them. Shared by the worker threads building buckets.
internal sealed class GlyphStore : IGlyphSource
{
    readonly MapStyleDocument _style;
    readonly ConcurrentDictionary<(string stack, int start), Dictionary<int, Glyph>> _ranges = new();
    public readonly GlyphAtlas Atlas = new();

    public GlyphStore(MapStyleDocument style) => _style = style;

    /// <summary>
    /// Makes sure the ranges holding <paramref name="text"/>'s code points in <paramref name="stack"/> are here.
    /// False when one is still on its way (its URL in <paramref name="missing"/>): shape the label once it has come.
    /// </summary>
    public bool Ensure(string stack, string text, bool fetch, ICollection<string>? missing)
    {
        if (_style.Glyphs is not { } template) throw new StyleUnsupportedException("labels without a glyphs URL");
        bool complete = true;
        foreach (var rune in text.EnumerateRunes())
        {
            int start = rune.Value / 256 * 256;
            if (_ranges.ContainsKey((stack, start))) continue;
            // GL JS's glyph_manager: {fontstack} is the stack joined by commas, {range} start-end
            var url = template.Replace("{fontstack}", stack).Replace("{range}", $"{start}-{start + 255}");
            var bytes = fetch || _style.Peek is null ? _style.Load?.Invoke(url) : _style.Peek(url);
            if (bytes is null)
            {
                if (!fetch && _style.Peek != null || _style.Pending?.Invoke(url) == true) { complete = false; missing?.Add(url); continue; }
                _ranges[(stack, start)] = new Dictionary<int, Glyph>();   // not there: those code points draw nothing
                continue;
            }
            Dictionary<int, Glyph> glyphs;
            try { glyphs = GlyphPbf.Decode(bytes); }
            catch (Exception e) when (e is System.IO.InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                _style.Skip($"glyphs {url}: {e.Message}");
                glyphs = new Dictionary<int, Glyph>();
            }
            _ranges[(stack, start)] = glyphs;
        }
        return complete;
    }

    // GL JS shaping.ts reads a glyph's metrics and its atlas rect (glyph_atlas.ts) by code point
    public bool TryGetGlyph(string fontStack, int codePoint, out GlyphPosition glyph)
    {
        glyph = default;
        if (!_ranges.TryGetValue((fontStack, codePoint / 256 * 256), out var glyphs) || !glyphs.TryGetValue(codePoint, out var g)) return false;
        var rect = Atlas.Place(fontStack, g);
        glyph = new GlyphPosition(new GlyphMetrics(g.Width, g.Height, g.Left, g.Top, g.Advance),
            rect is { } r ? new GlyphRect(r.X, r.Y, r.W, r.H) : null);
        return true;
    }
}
