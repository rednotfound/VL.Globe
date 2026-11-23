using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// ROADMAP 4e, labels: MapLibre GL JS v6.19.0's text shaping (src/symbol/shaping.ts with the parts of
// tagged_string.ts, graphemes.ts and script_detection.ts it leans on), ported line for line so that
// glyph positions come out bit-identical: the render tests compare pixels, and a label that is half a
// pixel off fails them. Doubles throughout, and every expression in GL JS's order of operations.
//
// Left out, each failing loudly rather than drawing something else:
// - vertical writing mode (CJK set top to bottom) -> StyleUnsupportedException("vertical text");
// - image sections in formatted text -> StyleUnsupportedException("images in text");
// - wrapping a long run of a script written without spaces (Thai, Khmer, Lao, Myanmar, Tibetan ...),
//   which GL JS hands to the browser's dictionary word segmenter; .NET has none ->
//   StyleUnsupportedException("line breaking inside text written without spaces").
// - right-to-left text: the caller hands text already in display order; no bidi is run.

/// <summary>GL JS shaping.ts, WritingMode.</summary>
internal enum WritingMode
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    HorizontalOnly = 3,
}

/// <summary>A label's anchor, the style spec's text-anchor.</summary>
internal enum SymbolAnchor { Center, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>The style spec's text-justify (auto is resolved by the caller).</summary>
internal enum TextJustify { Left, Center, Right }

/// <summary>The style spec's vertical-align of a formatted section.</summary>
internal enum TextVerticalAlign { Bottom, Center, Top }

/// <summary>One section of formatted text, as the style spec's FormattedSection.</summary>
/// <remarks>Null means unset, as in GL JS; <see cref="Image"/> names an image section (not supported).</remarks>
internal sealed record FormattedSection(
    string Text,
    string? Image = null,
    double? Scale = null,
    string? FontStack = null,
    TextVerticalAlign? VerticalAlign = null);

/// <summary>Formatted text, as the style spec's Formatted.</summary>
internal sealed record Formatted(IReadOnlyList<FormattedSection> Sections)
{
    /// <summary>One section holding the whole string, every option unset.</summary>
    public static Formatted FromString(string text) => new(new[] { new FormattedSection(text) });
}

/// <summary>A glyph's metrics in ONE_EM (24 px) units, as GL JS's GlyphMetrics.</summary>
internal readonly record struct GlyphMetrics(double Width, double Height, double Left, double Top, double Advance);

/// <summary>A glyph's place in the atlas, as GL JS's Rect.</summary>
internal readonly record struct GlyphRect(double X, double Y, double W, double H);

/// <summary>A glyph's metrics and, once it is in an atlas, its rect there.</summary>
internal readonly record struct GlyphPosition(GlyphMetrics Metrics, GlyphRect? Rect);

/// <summary>Where shaping finds glyphs: per font stack, per code point.</summary>
internal interface IGlyphSource
{
    bool TryGetGlyph(string fontStack, int codePoint, out GlyphPosition glyph);
}

/// <summary>An <see cref="IGlyphSource"/> over dictionaries: font stack, then code point.</summary>
internal sealed class GlyphDictionary : IGlyphSource
{
    readonly Dictionary<string, Dictionary<int, GlyphPosition>> stacks = new();

    public void Add(string fontStack, int codePoint, GlyphPosition glyph)
    {
        if (!stacks.TryGetValue(fontStack, out var glyphs))
            stacks[fontStack] = glyphs = new Dictionary<int, GlyphPosition>();
        glyphs[codePoint] = glyph;
    }

    public bool TryGetGlyph(string fontStack, int codePoint, out GlyphPosition glyph)
    {
        if (stacks.TryGetValue(fontStack, out var glyphs) && glyphs.TryGetValue(codePoint, out glyph))
            return true;
        glyph = default;
        return false;
    }
}

/// <summary>A glyph placed relative to the label's anchor, as GL JS's PositionedGlyph.</summary>
internal sealed class PositionedGlyph
{
    public int Glyph;
    public string Grapheme = "";
    public string? ImageName;
    public double X;
    public double Y;
    public bool Vertical;
    public double Scale;
    public string FontStack = "";
    public int SectionIndex;
    public GlyphMetrics Metrics;
    public GlyphRect? Rect;
}

/// <summary>One line of a shaped label, as GL JS's PositionedLine.</summary>
internal sealed class PositionedLine
{
    public readonly List<PositionedGlyph> PositionedGlyphs = new();
    public double LineOffset;
}

/// <summary>A shaped label: its glyphs by line and its box, as GL JS's Shaping.</summary>
internal sealed class Shaping
{
    public const double OneEm = 24;
    public const double ShapingDefaultOffset = -17;

    public readonly List<PositionedLine> PositionedLines = new();
    public double Top;
    public double Bottom;
    public double Left;
    public double Right;
    public WritingMode WritingMode;
    public string Text = "";
    public bool IconsInText;
    public bool Verticalizable;

    // GL JS shaping.ts, shapeText. Returns null where GL JS returns false: no glyph was placed.
    /// <summary>Breaks, places and aligns the glyphs of a label; null when no glyph could be placed.</summary>
    public static Shaping? ShapeText(
        Formatted text,
        IGlyphSource glyphs,
        string defaultFontStack,
        double maxWidth,
        double lineHeight,
        SymbolAnchor textAnchor,
        TextJustify textJustify,
        double spacing,
        (double X, double Y) translate,
        WritingMode writingMode,
        bool allowVerticalPlacement,
        double layoutTextSize,
        double layoutTextSizeThisZoom)
    {
        // Vertical mode needs Unicode's Vertical_Orientation tables and the punctuation
        // verticalisation GL JS generates from them; neither is here yet.
        if (writingMode == WritingMode.Vertical)
            throw new StyleUnsupportedException("vertical text");
        if (writingMode != WritingMode.Horizontal)
            throw new ArgumentOutOfRangeException(nameof(writingMode), writingMode, "horizontal or vertical");

        AllocMeter.Lap(7, "(before shaping)");
        var logicalInput = TaggedString.FromFeature(text, defaultFontStack);
        AllocMeter.Lap(10, "shaping: tagged string");
        var lineBreaks = logicalInput.DetermineLineBreaks(spacing, maxWidth, glyphs, layoutTextSize);
        AllocMeter.Lap(11, "shaping: line breaks");
        var lines = BreakLines(logicalInput, lineBreaks);
        AllocMeter.Lap(12, "shaping: break lines");

        var shaping = new Shaping
        {
            Text = logicalInput.Text,
            Top = translate.Y,
            Bottom = translate.Y,
            Left = translate.X,
            Right = translate.X,
            WritingMode = writingMode,
            IconsInText = false,
            Verticalizable = false,
        };

        ShapeLines(shaping, glyphs, lines, lineHeight, textAnchor, textJustify, writingMode, spacing,
            allowVerticalPlacement, layoutTextSizeThisZoom);
        AllocMeter.Lap(13, "shaping: shape lines");
        if (IsEmpty(shaping.PositionedLines)) return null;
        return shaping;
    }

    // GL JS shaping.ts, isEmpty
    static bool IsEmpty(List<PositionedLine> positionedLines)
    {
        foreach (var line in positionedLines)
            if (line.PositionedGlyphs.Count != 0) return false;
        return true;
    }

    // GL JS shaping.ts, breakLines
    static List<TaggedString> BreakLines(TaggedString input, List<int> lineBreakPoints)
    {
        var lines = new List<TaggedString>();
        var start = 0;
        foreach (var lineBreak in lineBreakPoints)
        {
            lines.Add(input.Substring(start, lineBreak));
            start = lineBreak;
        }
        if (start < input.Length)
            lines.Add(input.Substring(start, input.Length));
        return lines;
    }

    // GL JS shaping.ts, getAnchorAlignment
    /// <summary>The fraction of the box left of and above the anchor.</summary>
    public static (double HorizontalAlign, double VerticalAlign) GetAnchorAlignment(SymbolAnchor anchor)
    {
        double horizontalAlign = 0.5, verticalAlign = 0.5;
        switch (anchor)
        {
            case SymbolAnchor.Right:
            case SymbolAnchor.TopRight:
            case SymbolAnchor.BottomRight:
                horizontalAlign = 1;
                break;
            case SymbolAnchor.Left:
            case SymbolAnchor.TopLeft:
            case SymbolAnchor.BottomLeft:
                horizontalAlign = 0;
                break;
        }
        switch (anchor)
        {
            case SymbolAnchor.Bottom:
            case SymbolAnchor.BottomRight:
            case SymbolAnchor.BottomLeft:
                verticalAlign = 1;
                break;
            case SymbolAnchor.Top:
            case SymbolAnchor.TopRight:
            case SymbolAnchor.TopLeft:
                verticalAlign = 0;
                break;
        }
        return (horizontalAlign, verticalAlign);
    }

    // GL JS shaping.ts, calculateLineContentSize - without images, whose sizes are 0 here.
    static (double VerticalLineContentWidth, double HorizontalLineContentHeight) CalculateLineContentSize(TaggedString line)
    {
        var maxGlyphSize = line.GetMaxScale() * OneEm;
        const double maxImageWidth = 0, maxImageHeight = 0, layoutTextSizeFactor = 1;
        var horizontalLineContentHeight = Math.Max(maxGlyphSize, maxImageHeight * layoutTextSizeFactor);
        var verticalLineContentWidth = Math.Max(maxGlyphSize, maxImageWidth * layoutTextSizeFactor);
        return (verticalLineContentWidth, horizontalLineContentHeight);
    }

    // GL JS shaping.ts, getVerticalAlignFactor
    static double GetVerticalAlignFactor(TextVerticalAlign verticalAlign) => verticalAlign switch
    {
        TextVerticalAlign.Top => 0,
        TextVerticalAlign.Center => 0.5,
        _ => 1,
    };

    // GL JS shaping.ts, isLineVertical - horizontal mode only reaches here, so it is always false;
    // kept as the place vertical mode will come in.
    static bool IsLineVertical(WritingMode writingMode, bool allowVerticalPlacement, int codePoint)
    {
        if (writingMode == WritingMode.Horizontal) return false;
        throw new StyleUnsupportedException("vertical text");
    }

    // GL JS shaping.ts, shapeLines
    static void ShapeLines(Shaping shaping,
        IGlyphSource glyphs,
        List<TaggedString> lines,
        double lineHeight,
        SymbolAnchor textAnchor,
        TextJustify textJustify,
        WritingMode writingMode,
        double spacing,
        bool allowVerticalPlacement,
        double layoutTextSizeThisZoom)
    {
        double x = 0;
        double y = 0;

        double maxLineLength = 0;
        double maxLineHeight = 0;

        double justify =
            textJustify == TextJustify.Right ? 1 :
            textJustify == TextJustify.Left ? 0 : 0.5;
        // Only image sections are scaled by layoutTextSizeFactor (ONE_EM / layoutTextSizeThisZoom),
        // and they are refused before this.
        _ = layoutTextSizeThisZoom;

        var lineIndex = 0;
        foreach (var line in lines)
        {
            line.Trim();

            var lineMaxScale = line.GetMaxScale();
            var positionedLine = new PositionedLine();
            shaping.PositionedLines.Add(positionedLine);
            var positionedGlyphs = positionedLine.PositionedGlyphs;
            var imageOffset = 0.0;

            if (line.Length == 0)
            {
                y += lineHeight; // Still need a line feed after empty line
                ++lineIndex;
                continue;
            }

            var lineShapingSize = CalculateLineContentSize(line);

            var graphemes = line.Graphemes();
            for (var i = 0; i < graphemes.Count; i++)
            {
                var section = line.GetSection(i);
                var grapheme = graphemes[i];
                var codePoint = TaggedString.FirstCodePoint(grapheme);
                var vertical = IsLineVertical(writingMode, allowVerticalPlacement, codePoint);

                // A cluster the font has no shape for is drawn a code point at a time; anything else is
                // its own one key (without an array a glyph: ROADMAP 4g step 4)
                bool byCodePoint = TaggedString.IsCluster(grapheme) && !TaggedString.TryGetGlyph(glyphs, section.FontStack, grapheme, out _);
                using var codePoints = byCodePoint ? TaggedString.CodePoints(grapheme).GetEnumerator() : null;
                for (bool first = true; ; first = false)
                {
                    string key;
                    if (codePoints is null) { if (!first) break; key = grapheme; }
                    else { if (!codePoints.MoveNext()) break; key = codePoints.Current; }
                    var positionedGlyph = new PositionedGlyph
                    {
                        Glyph = TaggedString.FirstCodePoint(key),
                        Grapheme = key,
                        ImageName = null,
                        X = x,
                        Y = y + ShapingDefaultOffset,
                        Vertical = vertical,
                        Scale = 1,
                        FontStack = "",
                        SectionIndex = line.GetSectionIndex(i),
                    };

                    var sectionAttributes = ShapeTextSection(section, key, vertical, lineShapingSize, glyphs);
                    if (sectionAttributes is not { } attributes) continue;
                    positionedGlyph.FontStack = section.FontStack;

                    positionedGlyph.Y += attributes.BaselineOffset;
                    positionedGlyph.Scale = section.Scale;
                    positionedGlyph.Metrics = attributes.Metrics;
                    positionedGlyph.Rect = attributes.Rect;
                    positionedGlyphs.Add(positionedGlyph);

                    if (!vertical)
                    {
                        x += attributes.Metrics.Advance * section.Scale + spacing;
                    }
                    else
                    {
                        shaping.Verticalizable = true;
                        var verticalAdvance = OneEm;
                        x += verticalAdvance * section.Scale + spacing;
                    }
                }
            }

            // Only justify if we placed at least one glyph
            if (positionedGlyphs.Count != 0)
            {
                var lineLength = x - spacing;
                maxLineLength = Math.Max(lineLength, maxLineLength);
                JustifyLine(positionedGlyphs, 0, positionedGlyphs.Count - 1, justify);
            }

            x = 0;
            var maxLineOffset = (lineMaxScale - 1) * OneEm;
            positionedLine.LineOffset = Math.Max(imageOffset, maxLineOffset);
            var currentLineHeight = lineHeight * lineMaxScale + imageOffset;
            y += currentLineHeight;
            maxLineHeight = Math.Max(currentLineHeight, maxLineHeight);
            ++lineIndex;
        }

        // Calculate the bounding box and justify / align text block.
        var (horizontalAlign, verticalAlign) = GetAnchorAlignment(textAnchor);
        Align(shaping.PositionedLines, justify, horizontalAlign, verticalAlign, maxLineLength, maxLineHeight, lineHeight, y, lines.Count);

        // shaping.Top and Left already include the text offset (translate)
        shaping.Top += -verticalAlign * y;
        shaping.Bottom = shaping.Top + y;
        shaping.Left += -horizontalAlign * maxLineLength;
        shaping.Right = shaping.Left + maxLineLength;
    }

    // GL JS shaping.ts, shapeTextSection - the 'vertical' glyph variant only exists in vertical mode.
    static (GlyphRect? Rect, GlyphMetrics Metrics, double BaselineOffset)? ShapeTextSection(
        TaggedString.SectionOptions section,
        string key,
        bool vertical,
        (double VerticalLineContentWidth, double HorizontalLineContentHeight) lineShapingSize,
        IGlyphSource glyphs)
    {
        // getRectAndMetrics: the position (with its rect) where there is one, else the glyph's metrics.
        if (!TaggedString.TryGetGlyph(glyphs, section.FontStack, key, out var rectAndMetrics)) return null;

        double baselineOffset;
        if (vertical)
        {
            baselineOffset = lineShapingSize.VerticalLineContentWidth - section.Scale * OneEm;
        }
        else
        {
            var verticalAlignFactor = GetVerticalAlignFactor(section.VerticalAlign);
            baselineOffset = (lineShapingSize.HorizontalLineContentHeight - section.Scale * OneEm) * verticalAlignFactor;
        }

        return (rectAndMetrics.Rect, rectAndMetrics.Metrics, baselineOffset);
    }

    // GL JS shaping.ts, justifyLine - justify right = 1, left = 0, center = 0.5
    static void JustifyLine(List<PositionedGlyph> positionedGlyphs, int start, int end, double justify)
    {
        if (justify == 0)
            return;

        var lastPositionedGlyph = positionedGlyphs[end];
        var lastAdvance = lastPositionedGlyph.Metrics.Advance * lastPositionedGlyph.Scale;
        var lineIndent = (positionedGlyphs[end].X + lastAdvance) * justify;

        for (var j = start; j <= end; j++)
            positionedGlyphs[j].X -= lineIndent;
    }

    // GL JS shaping.ts, align
    static void Align(List<PositionedLine> positionedLines,
        double justify,
        double horizontalAlign,
        double verticalAlign,
        double maxLineLength,
        double maxLineHeight,
        double lineHeight,
        double blockHeight,
        int lineCount)
    {
        var shiftX = (justify - horizontalAlign) * maxLineLength;
        double shiftY;

        if (maxLineHeight != lineHeight)
            shiftY = -blockHeight * verticalAlign - ShapingDefaultOffset;
        else
            shiftY = -verticalAlign * lineCount * lineHeight + 0.5 * lineHeight;

        foreach (var line in positionedLines)
            foreach (var positionedGlyph in line.PositionedGlyphs)
            {
                positionedGlyph.X += shiftX;
                positionedGlyph.Y += shiftY;
            }
    }
}

// GL JS tagged_string.ts, TaggedString: the label's text in layout units with the section of each.
// GL JS's units are grapheme clusters from Intl.Segmenter; here they are code points, except that a
// CR LF pair stays one unit as UAX #29 (and so GL JS) keeps it - otherwise "a\r\nb" would break
// twice, leaving an empty line between.
internal sealed class TaggedString
{
    internal sealed class SectionOptions
    {
        public double Scale;
        public TextVerticalAlign VerticalAlign;
        public string FontStack = "";
    }

    public string Text;
    public readonly List<SectionOptions> Sections;
    public List<int> SectionIndex;
    List<string>? cachedGraphemes;

    public TaggedString(string text = "", List<SectionOptions>? sections = null, List<int>? sectionIndex = null)
    {
        Text = text;
        Sections = sections ?? new List<SectionOptions>();
        SectionIndex = sectionIndex ?? new List<int>();
    }

    public List<string> Graphemes() => cachedGraphemes ??= ToGraphemes(Text);

    // GL JS tagged_string.ts, TaggedString.fromFeature (vertical punctuation is vertical mode's)
    public static TaggedString FromFeature(Formatted text, string defaultFontStack)
    {
        var result = new TaggedString();
        foreach (var section in text.Sections)
        {
            if (section.Image is null)
                result.AddTextSection(section, defaultFontStack);
            else
                throw new StyleUnsupportedException("images in text");
        }
        return result;
    }

    public int Length => Graphemes().Count;

    public SectionOptions GetSection(int index) => Sections[SectionIndex[index]];

    public int GetSectionIndex(int index) => SectionIndex[index];

    // GL JS tagged_string.ts, hasZeroWidthSpaces
    public bool HasZeroWidthSpaces() => Text.Contains('​');

    // GL JS tagged_string.ts, trim - counted in units, which is what SectionIndex is indexed by.
    public void Trim()
    {
        var graphemes = Graphemes();
        var start = 0;
        while (start < graphemes.Count && IsWhitespaceGrapheme(graphemes[start])) start++;
        var end = graphemes.Count;
        while (end > start && IsWhitespaceGrapheme(graphemes[end - 1])) end--;

        Text = string.Concat(graphemes.GetRange(start, end - start));
        SectionIndex = SectionIndex.GetRange(start, end - start);
        cachedGraphemes = null;
    }

    // GL JS tagged_string.ts, substring
    public TaggedString Substring(int start, int end)
    {
        var text = string.Concat(Graphemes().GetRange(start, end - start));
        var sectionIndex = SectionIndex.GetRange(start, end - start);
        return new TaggedString(text, Sections, sectionIndex);
    }

    // GL JS tagged_string.ts, getMaxScale
    public double GetMaxScale()
    {
        double max = 0;
        foreach (var index in SectionIndex) max = Math.Max(max, Sections[index].Scale);
        return max;
    }

    // GL JS tagged_string.ts, _appendSection - only the last unit and the new text are re-split.
    void AppendSection(string text, int sectionIndex)
    {
        var graphemes = Graphemes();
        var tail = graphemes.Count > 0 ? graphemes[^1] : "";
        var joined = ToGraphemes(tail + text);

        Text += text;
        var units = tail.Length > 0 ? graphemes.GetRange(0, graphemes.Count - 1) : new List<string>(graphemes);
        units.AddRange(joined);
        cachedGraphemes = units;

        var added = joined.Count - (tail.Length > 0 ? 1 : 0);
        for (var i = 0; i < added; i++)
            SectionIndex.Add(sectionIndex);
    }

    // GL JS tagged_string.ts, addTextSection - `scale || 1`: unset, 0 and NaN all mean 1.
    void AddTextSection(FormattedSection section, string defaultFontStack)
    {
        Sections.Add(new SectionOptions
        {
            Scale = section.Scale is double s && s != 0 && !double.IsNaN(s) ? s : 1,
            VerticalAlign = section.VerticalAlign ?? TextVerticalAlign.Bottom,
            FontStack = string.IsNullOrEmpty(section.FontStack) ? defaultFontStack : section.FontStack,
        });
        AppendSection(section.Text, Sections.Count - 1);
    }

    sealed class Break
    {
        public int Index;
        public double X;
        public Break? PriorBreak;
        public double Badness;
    }

    // GL JS tagged_string.ts, breakable: characters a line may end on.
    static bool Breakable(int codePoint) => codePoint switch
    {
        0x0a => true, // newline
        0x0d => true, // carriage return, on its own or starting a CRLF unit
        0x20 => true, // space
        0x26 => true, // ampersand
        0x29 => true, // right parenthesis
        0x2b => true, // plus sign
        0x2d => true, // hyphen-minus
        0x2f => true, // solidus
        0xad => true, // soft hyphen
        0xb7 => true, // middle dot
        0x200b => true, // zero-width space
        0x2010 => true, // hyphen
        0x2013 => true, // en dash
        0x2027 => true, // interpunct
        _ => false,
    };

    // GL JS tagged_string.ts, breakableBefore: characters a line may begin with.
    static bool BreakableBefore(int codePoint) => codePoint == 0x28; // left parenthesis

    // GL JS tagged_string.ts, getGlyphAdvance (text sections only)
    static double GetGlyphAdvance(string grapheme, SectionOptions section, IGlyphSource glyphs, double spacing)
    {
        if (TryGetGlyph(glyphs, section.FontStack, grapheme, out var glyph))
            return glyph.Metrics.Advance * section.Scale + spacing;

        double advance = 0;
        foreach (var c in CodePoints(grapheme))
            if (TryGetGlyph(glyphs, section.FontStack, c, out var fallback))
                advance += fallback.Metrics.Advance * section.Scale + spacing;
        return advance;
    }

    // GL JS tagged_string.ts, calculateBadness
    static double CalculateBadness(double lineWidth, double targetWidth, double penalty, bool isLastBreak)
    {
        var raggedness = Math.Pow(lineWidth - targetWidth, 2);
        if (isLastBreak)
        {
            // Favor finals lines shorter than average over longer than average
            if (lineWidth < targetWidth)
                return raggedness / 2;
            else
                return raggedness * 2;
        }
        return raggedness + Math.Abs(penalty) * penalty;
    }

    // GL JS tagged_string.ts, isWhitespaceGrapheme: /^\s+$/u
    static bool IsWhitespaceGrapheme(string grapheme)
    {
        if (grapheme.Length == 0) return false;
        foreach (var c in CodePoints(grapheme))
            if (!CharIsWhitespace(FirstCodePoint(c))) return false;
        return true;
    }

    // GL JS tagged_string.ts, calculatePenalty
    static double CalculatePenalty(int codePoint, int nextCodePoint, bool penalizableIdeographicBreak)
    {
        double penalty = 0;
        if (codePoint == 0x0a || codePoint == 0x0d)
            penalty -= 10000;
        if (penalizableIdeographicBreak)
            penalty += 150;
        if (codePoint == 0x28 || codePoint == 0xff08)
            penalty += 50;
        if (nextCodePoint == 0x29 || nextCodePoint == 0xff09)
            penalty += 50;
        return penalty;
    }

    // GL JS tagged_string.ts, evaluateBreak - lines longer than maxWidth are allowed on purpose.
    static Break EvaluateBreak(int breakIndex, double breakX, double targetWidth, List<Break> potentialBreaks,
        double penalty, bool isLastBreak)
    {
        Break? bestPriorBreak = null;
        var bestBreakBadness = CalculateBadness(breakX, targetWidth, penalty, isLastBreak);

        foreach (var potentialBreak in potentialBreaks)
        {
            var lineWidth = breakX - potentialBreak.X;
            var breakBadness =
                CalculateBadness(lineWidth, targetWidth, penalty, isLastBreak) + potentialBreak.Badness;
            if (breakBadness <= bestBreakBadness)
            {
                bestPriorBreak = potentialBreak;
                bestBreakBadness = breakBadness;
            }
        }

        return new Break { Index = breakIndex, X = breakX, PriorBreak = bestPriorBreak, Badness = bestBreakBadness };
    }

    // GL JS tagged_string.ts, leastBadBreaks - iterative rather than recursive, same order.
    static List<int> LeastBadBreaks(Break? lastLineBreak)
    {
        var result = new List<int>();
        for (var b = lastLineBreak; b != null; b = b.PriorBreak) result.Add(b.Index);
        result.Reverse();
        return result;
    }

    // GL JS tagged_string.ts, determineLineBreaks
    public List<int> DetermineLineBreaks(double spacing, double maxWidth, IGlyphSource glyphs, double layoutTextSize)
    {
        var totalWidth = DetermineTotalWidth(spacing, glyphs);
        if (spacing >= 0 && totalWidth > 0 && totalWidth <= maxWidth && Text.IndexOfAny(NewlineChars) < 0)
            return new List<int> { Length };

        var potentialLineBreaks = new List<Break>();
        var targetWidth = totalWidth / Math.Max(1, Math.Ceiling(totalWidth / maxWidth));

        var hasZeroWidthSpaces = HasZeroWidthSpaces();

        var graphemes = Graphemes();
        double currentX = 0;

        for (var i = 0; i < graphemes.Count; i++)
        {
            var grapheme = graphemes[i];

            if (i > 0)
            {
                var previousCodePoint = FirstCodePoint(graphemes[i - 1]);
                var cp = FirstCodePoint(grapheme);
                var ideographicBreak = CodePointAllowsIdeographicBreaking(previousCodePoint);

                // `'imageName' in this.getSection(i - 1)` is never true: image sections are refused.
                var breaks = Breakable(previousCodePoint) ||
                    ideographicBreak ||
                    (i + 1 < graphemes.Count && BreakableBefore(cp));
                if (!breaks && CodePointIsWrittenWithoutSpaces(previousCodePoint) && CodePointIsWrittenWithoutSpaces(cp))
                    // GL JS asks Intl.Segmenter's dictionaries where the words are; .NET has none.
                    throw new StyleUnsupportedException("line breaking inside text written without spaces");

                if (breaks)
                {
                    potentialLineBreaks.Add(
                        EvaluateBreak(
                            i,
                            currentX,
                            targetWidth,
                            potentialLineBreaks,
                            CalculatePenalty(previousCodePoint, cp, ideographicBreak && hasZeroWidthSpaces),
                            false));
                }
            }

            var codePoint = FirstCodePoint(grapheme);
            if (!CharIsWhitespace(codePoint))
                currentX += GetGlyphAdvance(grapheme, GetSection(i), glyphs, spacing);
        }

        return LeastBadBreaks(
            EvaluateBreak(
                Length,
                currentX,
                targetWidth,
                potentialLineBreaks,
                0,
                true));
    }

    static readonly char[] NewlineChars = { '\n', '\r' };

    // GL JS tagged_string.ts, determineTotalWidth
    double DetermineTotalWidth(double spacing, IGlyphSource glyphs)
    {
        double totalWidth = 0;
        var index = 0;
        foreach (var grapheme in Graphemes())
        {
            totalWidth += GetGlyphAdvance(grapheme, GetSection(index), glyphs, spacing);
            index++;
        }
        return totalWidth;
    }

    // ---- units and code points ----

    // GL JS graphemes.ts, toGraphemes - code points, CR LF kept together.
    internal static List<string> ToGraphemes(string text)
    {
        var units = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                units.Add("\r\n");
                i += 2;
                continue;
            }
            var n = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            units.Add(n == 1 ? OneChar(text[i]) : text.Substring(i, n));
            i += n;
        }
        return units;
    }

    // A one-character grapheme as one string for good: every label of every tile was cut into new
    // strings a character each (ROADMAP 4g step 4: garbage from building tiles paused the renderer)
    static readonly string?[] OneChars = new string?[char.MaxValue + 1];
    internal static string OneChar(char c) => OneChars[c] ??= c.ToString();

    // String.prototype.codePointAt(0): a lone surrogate is its own value, as in JS.
    internal static int FirstCodePoint(string s) =>
        s.Length > 1 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1])
            ? char.ConvertToUtf32(s[0], s[1])
            : s[0];

    // [...grapheme]: the code points of a unit, each as a string.
    internal static IEnumerable<string> CodePoints(string s)
    {
        for (var i = 0; i < s.Length;)
        {
            var n = s.Length > i + 1 && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            yield return s.Substring(i, n);
            i += n;
        }
    }

    // GL JS graphemes.ts, isCluster
    internal static bool IsCluster(string grapheme)
    {
        var first = FirstCodePoint(grapheme);
        return grapheme.Length > (first > 0xffff ? 2 : 1);
    }

    // glyphMap[fontStack].default[grapheme]: sources are keyed by code point, so a cluster has none.
    internal static bool TryGetGlyph(IGlyphSource glyphs, string fontStack, string grapheme, out GlyphPosition glyph)
    {
        if (grapheme.Length == 0 || IsCluster(grapheme))
        {
            glyph = default;
            return false;
        }
        return glyphs.TryGetGlyph(fontStack, FirstCodePoint(grapheme), out glyph);
    }

    // GL JS script_detection.ts, charIsWhitespace: JavaScript's /\s/u, which is not .NET's
    // char.IsWhiteSpace (that adds U+0085 and leaves out U+FEFF).
    internal static bool CharIsWhitespace(int c) =>
        c is 0x09 or 0x0a or 0x0b or 0x0c or 0x0d or 0x20 or 0xa0 or 0x1680
            or >= 0x2000 and <= 0x200a
            or 0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000 or 0xfeff;

    // GL JS unicode_properties.g.ts, codePointAllowsIdeographicBreaking, from the blocks and scripts
    // build/generate-unicode-data.ts names. The scripts are taken by their code points within the
    // BMP; beyond it whole blocks stand in for them.
    internal static bool CodePointAllowsIdeographicBreaking(int c) =>
        // blocks
        c is >= 0xfe30 and <= 0xfe4f        // CJK Compatibility Forms
            or >= 0x3300 and <= 0x33ff      // CJK Compatibility
            or >= 0x2e80 and <= 0x2eff      // CJK Radicals Supplement
            or >= 0x31c0 and <= 0x31ef      // CJK Strokes
            or >= 0x3000 and <= 0x303f      // CJK Symbols And Punctuation
            or >= 0x3200 and <= 0x32ff      // Enclosed CJK Letters And Months
            or >= 0x1f200 and <= 0x1f2ff    // Enclosed Ideographic Supplement
            or >= 0xff00 and <= 0xffef      // Halfwidth And Fullwidth Forms
            or >= 0x2ff0 and <= 0x2fff      // Ideographic Description Characters
            or >= 0x16fe0 and <= 0x16fff    // Ideographic Symbols And Punctuation
            or >= 0x1b100 and <= 0x1b12f    // Kana Extended-A
            or >= 0x1aff0 and <= 0x1afff    // Kana Extended-B
            or >= 0x1b000 and <= 0x1b0ff    // Kana Supplement
            or >= 0x2f00 and <= 0x2fdf      // Kangxi Radicals
            or >= 0x31f0 and <= 0x31ff      // Katakana Phonetic Extensions
            or >= 0x1b130 and <= 0x1b16f    // Small Kana Extension
            or >= 0xfe10 and <= 0xfe1f      // Vertical Forms
            // Bopomofo
            or 0x02ea or 0x02eb
            or >= 0x3105 and <= 0x312f
            or >= 0x31a0 and <= 0x31bf
            // Han
            or >= 0x3400 and <= 0x4dbf
            or >= 0x4e00 and <= 0x9fff
            or >= 0xf900 and <= 0xfa6d
            or >= 0xfa70 and <= 0xfad9
            or >= 0x20000 and <= 0x3ffff    // planes 2 and 3: the CJK Unified Ideographs extensions
            // Hiragana (U+3099..309C are Inherited/Common)
            or >= 0x3041 and <= 0x3096
            or >= 0x309d and <= 0x309f
            // Katakana (U+30A0, 30FB, 30FC are Common)
            or >= 0x30a1 and <= 0x30fa
            or >= 0x30fd and <= 0x30ff
            // Khitan Small Script, Nushu, Tangut (with Components and Supplement), Yi
            or >= 0x18b00 and <= 0x18cff
            or >= 0x1b170 and <= 0x1b2ff
            or >= 0x17000 and <= 0x18aff
            or >= 0x18d00 and <= 0x18d7f
            or >= 0xa000 and <= 0xa4cf;

    // GL JS unicode_properties.g.ts, codePointIsWrittenWithoutSpaces: Line_Break=SA plus Balinese,
    // Javanese and Tibetan, approximated by their blocks.
    internal static bool CodePointIsWrittenWithoutSpaces(int c) =>
        c is >= 0x0e00 and <= 0x0e7f        // Thai
            or >= 0x0e80 and <= 0x0eff      // Lao
            or >= 0x0f00 and <= 0x0fff      // Tibetan
            or >= 0x1000 and <= 0x109f      // Myanmar
            or >= 0x1780 and <= 0x17ff      // Khmer
            or >= 0x19e0 and <= 0x19ff      // Khmer Symbols
            or >= 0x1950 and <= 0x197f      // Tai Le
            or >= 0x1980 and <= 0x19df      // New Tai Lue
            or >= 0x1a20 and <= 0x1aaf      // Tai Tham
            or >= 0x1b00 and <= 0x1b7f      // Balinese
            or >= 0xa980 and <= 0xa9df      // Javanese
            or >= 0xa9e0 and <= 0xa9ff      // Myanmar Extended-B
            or >= 0xaa60 and <= 0xaa7f      // Myanmar Extended-A
            or >= 0xaa80 and <= 0xaadf      // Tai Viet
            or >= 0x11700 and <= 0x1174f;   // Ahom
}
