using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 4e, labels: the parts that need no GPU - glyph ranges, shaping, quads - against MapLibre's
// own test assets (the glyphs its render tests draw with).
public class LabelFacts
{
    static string Glyphs(string range)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VL.Globe.nuspec")))
            {
                var file = Path.Combine(d.FullName, "test", "fixtures", "maplibre", "v6.19.0", "_assets", "glyphs",
                    "Open Sans Semibold,Arial Unicode MS Bold", range + ".pbf");
                Assert.True(File.Exists(file), $"fetch the fixtures first: .\\tools\\Fetch-MapLibreFixtures.ps1 ({file})");
                return file;
            }
        throw new DirectoryNotFoundException("no VL.Globe.nuspec above the test assembly");
    }

    [Fact]
    public void AGlyphRangeHoldsSignedDistanceFieldsWithTheirMetrics()
    {
        var glyphs = GlyphPbf.Decode(File.ReadAllBytes(Glyphs("0-255")));
        Assert.True(glyphs.Count > 150, $"{glyphs.Count} glyphs in 0-255");
        var a = glyphs['A'];
        Assert.True(a.Width > 10 && a.Height > 10 && a.Advance > 10, $"A: {a}");
        Assert.Equal((a.Width + 6) * (a.Height + 6), a.Bitmap.Length);   // the box and a 3-px border
        // a distance field: the border is far outside (low), the stroke's middle inside (high)
        Assert.True(a.Bitmap[0] < 64, $"corner {a.Bitmap[0]}");
        Assert.Contains(a.Bitmap, b => b > 192);
        var space = glyphs[' '];
        Assert.Empty(space.Bitmap);
        Assert.True(space.Advance > 0);
    }
}
