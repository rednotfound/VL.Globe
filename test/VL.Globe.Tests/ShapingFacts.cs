using System.Text.Json;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 4e, labels: the port of GL JS's shapeText against GL JS's own unit tests
// (test/unit/symbol/shaping.test.ts) and the Shaping objects they expect, field by field.
public class ShapingFacts
{
    const double OneEm = 24;
    const double LayoutTextSize = 16;
    const double LayoutTextSizeThisZoom = 16;
    const string FontStack = "Test";

    // The expected Shapings, copied from GL JS v6.19.0's test/integration/symbol-shaping/tests
    // (BSD-3-Clause, MapLibre: MapLibre\shaping\LICENSE.txt), copied to the output by the csproj.
    static readonly string ExpectedFolder = Path.Combine(AppContext.BaseDirectory, "MapLibre", "shaping");

    static readonly Lazy<GlyphDictionary> TestGlyphs = new(LoadGlyphs);

    static GlyphDictionary LoadGlyphs()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VL.Globe.nuspec")))
            {
                var file = Path.Combine(d.FullName, "test", "fixtures", "maplibre", "v6.19.0", "_assets", "glyphs",
                    "fontstack-glyphs.json");
                Assert.True(File.Exists(file), $"fetch the fixtures first: .\\tools\\Fetch-MapLibreFixtures.ps1 ({file})");
                var glyphs = new GlyphDictionary();
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var entry in json.RootElement.EnumerateObject())
                {
                    var m = entry.Value.GetProperty("metrics");
                    var r = entry.Value.GetProperty("rect");
                    glyphs.Add(FontStack, int.Parse(entry.Name), new GlyphPosition(
                        new GlyphMetrics(m.GetProperty("width").GetDouble(), m.GetProperty("height").GetDouble(),
                            m.GetProperty("left").GetDouble(), m.GetProperty("top").GetDouble(),
                            m.GetProperty("advance").GetDouble()),
                        new GlyphRect(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
                            r.GetProperty("w").GetDouble(), r.GetProperty("h").GetDouble())));
                }
                return glyphs;
            }
        throw new DirectoryNotFoundException("no VL.Globe.nuspec above the test assembly");
    }

    // The arguments every case of shaping.test.ts passes but the text, maxWidth and spacing.
    static Shaping? Shape(Formatted text, double maxWidth, double spacing = 0,
        WritingMode writingMode = WritingMode.Horizontal, bool allowVerticalPlacement = false) =>
        Shaping.ShapeText(text, TestGlyphs.Value, FontStack, maxWidth, OneEm, SymbolAnchor.Center, TextJustify.Center,
            spacing, (0, 0), writingMode, allowVerticalPlacement, LayoutTextSize, LayoutTextSizeThisZoom);

    static Shaping? Shape(string text, double maxWidth, double spacing = 0) =>
        Shape(Formatted.FromString(text), maxWidth, spacing);

    static JsonElement Expected(string name)
    {
        var path = Path.Combine(ExpectedFolder, $"text-shaping-{name}.json");
        Assert.True(File.Exists(path), $"missing {path}");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.Clone();
    }

    static void Near(double expected, double actual, string where) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-9, $"{where}: expected {expected}, got {actual}");

    static void AssertLines(JsonElement expectedLines, List<PositionedLine> lines)
    {
        Assert.Equal(expectedLines.GetArrayLength(), lines.Count);
        var l = 0;
        foreach (var expectedLine in expectedLines.EnumerateArray())
        {
            var line = lines[l];
            Near(expectedLine.GetProperty("lineOffset").GetDouble(), line.LineOffset, $"line {l} lineOffset");
            var glyphs = expectedLine.GetProperty("positionedGlyphs");
            Assert.Equal(glyphs.GetArrayLength(), line.PositionedGlyphs.Count);
            var g = 0;
            foreach (var e in glyphs.EnumerateArray())
            {
                var a = line.PositionedGlyphs[g];
                var at = $"line {l} glyph {g}";
                Assert.Equal(e.GetProperty("glyph").GetInt32(), a.Glyph);
                Assert.Equal(e.GetProperty("grapheme").GetString(), a.Grapheme);
                Assert.Equal(e.GetProperty("imageName").ValueKind == JsonValueKind.Null ? null : e.GetProperty("imageName").GetString(), a.ImageName);
                Near(e.GetProperty("x").GetDouble(), a.X, at + " x");
                Near(e.GetProperty("y").GetDouble(), a.Y, at + " y");
                Assert.Equal(e.GetProperty("vertical").GetBoolean(), a.Vertical);
                Near(e.GetProperty("scale").GetDouble(), a.Scale, at + " scale");
                Assert.Equal(e.GetProperty("fontStack").GetString(), a.FontStack);
                Assert.Equal(e.GetProperty("sectionIndex").GetInt32(), a.SectionIndex);
                var m = e.GetProperty("metrics");
                Near(m.GetProperty("width").GetDouble(), a.Metrics.Width, at + " width");
                Near(m.GetProperty("height").GetDouble(), a.Metrics.Height, at + " height");
                Near(m.GetProperty("left").GetDouble(), a.Metrics.Left, at + " left");
                Near(m.GetProperty("top").GetDouble(), a.Metrics.Top, at + " top");
                Near(m.GetProperty("advance").GetDouble(), a.Metrics.Advance, at + " advance");
                var r = e.GetProperty("rect");
                Assert.True(a.Rect.HasValue, at + " rect");
                Near(r.GetProperty("x").GetDouble(), a.Rect!.Value.X, at + " rect x");
                Near(r.GetProperty("y").GetDouble(), a.Rect.Value.Y, at + " rect y");
                Near(r.GetProperty("w").GetDouble(), a.Rect.Value.W, at + " rect w");
                Near(r.GetProperty("h").GetDouble(), a.Rect.Value.H, at + " rect h");
                g++;
            }
            l++;
        }
    }

    static void AssertShaping(JsonElement expected, Shaping? actual)
    {
        Assert.NotNull(actual);
        AssertLines(expected.GetProperty("positionedLines"), actual!.PositionedLines);
        Assert.Equal(expected.GetProperty("text").GetString(), actual.Text);
        Near(expected.GetProperty("top").GetDouble(), actual.Top, "top");
        Near(expected.GetProperty("bottom").GetDouble(), actual.Bottom, "bottom");
        Near(expected.GetProperty("left").GetDouble(), actual.Left, "left");
        Near(expected.GetProperty("right").GetDouble(), actual.Right, "right");
        Assert.Equal(expected.GetProperty("writingMode").GetInt32(), (int)actual.WritingMode);
        Assert.Equal(expected.GetProperty("iconsInText").GetBoolean(), actual.IconsInText);
        Assert.Equal(expected.GetProperty("verticalizable").GetBoolean(), actual.Verticalizable);
    }

    [Fact] public void NullCharacterIsSkipped() => AssertShaping(Expected("null"), Shape("hi\0", 15 * OneEm));

    [Fact] public void DefaultShaping() => AssertShaping(Expected("default"), Shape("abcde", 15 * OneEm));

    [Fact] public void LetterSpacing() => AssertShaping(Expected("spacing"), Shape("abcde", 15 * OneEm, 0.125 * OneEm));

    [Fact] public void LineBreak() => AssertShaping(Expected("linebreak"), Shape("abcde abcde", 4 * OneEm));

    [Fact] public void NewLine() => AssertShaping(Expected("newline"), Shape("abcde\nabcde", 15 * OneEm));

    [Fact]
    public void NewLineWithCrLf() =>
        AssertLines(Expected("newline").GetProperty("positionedLines"), Shape("abcde\r\nabcde", 15 * OneEm)!.PositionedLines);

    [Fact] public void NewLinesInTheMiddle() => AssertShaping(Expected("newlines-in-middle"), Shape("abcde\n\nabcde", 15 * OneEm));

    [Fact]
    public void ZeroWidthSpacesAreThePreferredBreaks() =>
        AssertShaping(Expected("zero-width-space"),
            Shape("三三\u200b三三\u200b三三\u200b三三三三三三\u200b三三", 5 * OneEm));

    [Fact]
    public void BreakBeforeLeftParenthesisWithoutWhitespace() =>
        AssertShaping(Expected("break-before-bracket"), Shape("oooooooo(aaaaaaaaa)", 5 * OneEm));

    [Fact]
    public void EmptyTextShapesToNothing()
    {
        Assert.Null(Shape("", 15 * OneEm));
        Assert.Null(Shape("\0", 15 * OneEm));
    }

    // mapbox-gl-js#3254: leading and trailing whitespace is trimmed off each line
    [Fact]
    public void WhitespaceAtTheEndsIsTrimmed()
    {
        var shaped = Shape("   foo bar\n", 15 * OneEm)!;
        var shaped2 = Shape("foo bar", 15 * OneEm)!;
        Assert.Equal(shaped2.PositionedLines.Count, shaped.PositionedLines.Count);
        for (var l = 0; l < shaped.PositionedLines.Count; l++)
        {
            var a = shaped.PositionedLines[l].PositionedGlyphs;
            var b = shaped2.PositionedLines[l].PositionedGlyphs;
            Assert.Equal(b.Count, a.Count);
            for (var g = 0; g < a.Count; g++)
            {
                Assert.Equal(b[g].Glyph, a[g].Glyph);
                Assert.Equal(b[g].X, a[g].X);
                Assert.Equal(b[g].Y, a[g].Y);
            }
        }
    }

    [Fact]
    public void CharactersBeyondTheBasicMultilingualPlane()
    {
        var glyphs = Shape("🗺️🌐", 5 * OneEm)!.PositionedLines[0].PositionedGlyphs;
        Assert.Equal(0x1f5fa, glyphs[0].Glyph);
        Assert.Equal(-31.5, glyphs[0].X);
        Assert.Equal(0xfe0f, glyphs[1].Glyph);
        Assert.Equal(-10.5, glyphs[1].X);
        Assert.Equal(0x1f310, glyphs[2].Glyph);
        Assert.Equal(10.5, glyphs[2].X);
    }

    [Fact]
    public void SectionsAlignVertically()
    {
        var glyphs = Shape(new Formatted(new[]
        {
            new FormattedSection("A", Scale: 3),
            new FormattedSection("A", Scale: 1, VerticalAlign: TextVerticalAlign.Top),
            new FormattedSection("A", Scale: 1, VerticalAlign: TextVerticalAlign.Center),
            new FormattedSection("A", Scale: 1, VerticalAlign: TextVerticalAlign.Bottom),
        }), 5 * OneEm)!.PositionedLines[0].PositionedGlyphs;
        Assert.Equal(-36, glyphs[0].Y);
        Assert.Equal(-36, glyphs[1].Y);
        Assert.Equal(-12, glyphs[2].Y);
        Assert.Equal(12, glyphs[3].Y);
    }

    [Fact]
    public void WhatIsNotPortedFailsLoudly()
    {
        Assert.Throws<StyleUnsupportedException>(() =>
            Shape(Formatted.FromString("()"), 5 * OneEm, 0, WritingMode.Vertical, true));
        Assert.Throws<StyleUnsupportedException>(() =>
            Shape(new Formatted(new[] { new FormattedSection("", Image: "square") }), 5 * OneEm));
        // a long Thai run with no break opportunity GL JS could find without a word dictionary
        Assert.Throws<StyleUnsupportedException>(() => Shape("กรุงเทพมหานคร", 0.5 * OneEm));
    }
}
