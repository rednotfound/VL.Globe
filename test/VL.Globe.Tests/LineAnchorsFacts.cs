using VL.Globe.Engine;

namespace VL.Globe.Tests;

// MapLibre GL JS v6.19.0's unit tests for line-label anchors, case for case: get_anchors.test.ts,
// check_max_angle.test.ts and clip_line.test.ts (its clipLine part; clipGeometry is not ported).
public class LineAnchorsFacts
{
    const double TileExtent = 4096;
    const double Tolerance = 1e-9;

    static void Same(LineAnchor expected, LineAnchor actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Angle, actual.Angle, Tolerance);
        Assert.Equal(expected.Segment, actual.Segment);
    }

    static void Same(IReadOnlyList<LineAnchor> expected, IReadOnlyList<LineAnchor> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Same(expected[i], actual[i]);
    }

    // ---- get_anchors.test.ts ----

    static readonly (double X, double Y)[] NonContinuedLine =
        Enumerable.Range(1, 10).Select(i => (1.0, (double)i)).ToArray();
    static readonly (double X, double Y)[] ContinuedLine =
        Enumerable.Range(0, 10).Select(j => (1.0, (double)j)).ToArray();

    const double SmallSpacing = 2, BigSpacing = 3, GlyphSize = 0.1;
    static readonly (double Left, double Right) ShapedText = (-1, 1);
    static readonly (double Left, double Right) ShapedIcon = (-0.5, 0.5);
    static readonly double LabelLength = Math.Max(ShapedText.Right - ShapedText.Left, ShapedIcon.Right - ShapedIcon.Left);
    const double HalfPi = 1.5707963267948966;

    [Fact]
    public void NonContinuedLineWithShortLabels()
    {
        var anchors = LineAnchors.GetAnchors(NonContinuedLine, BigSpacing, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1, 1, TileExtent);
        Same([new(1, 2, HalfPi, 1), new(1, 5, HalfPi, 4), new(1, 8, HalfPi, 7)], anchors);
        Assert.True(LabelLength / 2 + 1 <= anchors[0].Y && anchors[0].Y < LabelLength / 2 + 3 * GlyphSize + 1);
    }

    [Fact]
    public void NonContinuedLineWithLongLabels()
    {
        var anchors = LineAnchors.GetAnchors(NonContinuedLine, SmallSpacing, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1, 1, TileExtent);
        Same([new(1, 2, HalfPi, 1), new(1, 5, HalfPi, 3), new(1, 7, HalfPi, 6)], anchors);
    }

    [Fact]
    public void ContinuedLineWithShortLabels()
    {
        var anchors = LineAnchors.GetAnchors(ContinuedLine, BigSpacing, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1, 1, TileExtent);
        Same([new(1, 2, HalfPi, 1), new(1, 5, HalfPi, 4), new(1, 8, HalfPi, 7)], anchors);
    }

    [Fact]
    public void ContinuedLineWithLongLabels()
    {
        var anchors = LineAnchors.GetAnchors(ContinuedLine, SmallSpacing, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1, 1, TileExtent);
        Same([new(1, 1, HalfPi, 1), new(1, 4, HalfPi, 3), new(1, 6, HalfPi, 6)], anchors);
    }

    [Fact]
    public void OverscaledAnchorsContainAllAnchorsInParent()
    {
        var anchors = LineAnchors.GetAnchors(NonContinuedLine, BigSpacing, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1, 1, TileExtent);
        var childAnchors = LineAnchors.GetAnchors(NonContinuedLine, BigSpacing / 2, Math.PI, ShapedText, ShapedIcon, GlyphSize, 0.5, 2, TileExtent);
        // Point.equals in GL JS: x and y only.
        foreach (var anchor in anchors)
            Assert.Contains(childAnchors, c => c.X == anchor.X && c.Y == anchor.Y);
    }

    [Fact]
    public void UseMiddlePointAsAFallbackPositionForShortNonContinuedLines()
    {
        (double X, double Y)[] line = [(1, 1), (1, 3.1)];
        var anchors = LineAnchors.GetAnchors(line, 2, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1, 1, TileExtent);
        Same([new(1, 2, HalfPi, 0)], anchors);
    }

    [Fact]
    public void GetCenterAnchor()
    {
        (double X, double Y)[] line = [(1, 1), (1, 3.1), (3, 6), (4, 7)];
        var anchor = LineAnchors.GetCenterAnchor(line, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1);
        Assert.NotNull(anchor);
        Same(new(2, 4, 0.9670469933974603, 1), anchor.Value);
    }

    [Fact]
    public void GetCenterAnchorWithCenterOutsideTileBounds()
    {
        (double X, double Y)[] line = [(-10, -10), (5, 5)];
        var anchor = LineAnchors.GetCenterAnchor(line, Math.PI, ShapedText, ShapedIcon, GlyphSize, 1);
        Assert.NotNull(anchor);
        Same(new(-2, -2, 0.7853981633974483, 0), anchor.Value);
    }

    [Fact]
    public void GetCenterAnchorFailingMaxAngleTest()
    {
        (double X, double Y)[] line = [(1, 1), (1, 3), (3, 3)];
        Assert.Null(LineAnchors.GetCenterAnchor(line, Math.PI / 4, ShapedText, ShapedIcon, GlyphSize, 1));
    }

    // ---- check_max_angle.test.ts ----

    [Fact]
    public void LineWithNoSharpAngles()
    {
        (double X, double Y)[] line = [(0, 0), (20, -1), (40, 1), (60, 0)];
        var anchor = new LineAnchor(30, 0, 0, 1);
        Assert.True(LineAnchors.CheckMaxAngle(line, anchor, 25, 20, Math.PI / 8));
        Assert.False(LineAnchors.CheckMaxAngle(line, anchor, 25, 20, 0));
    }

    [Fact]
    public void OneSharpCorner()
    {
        (double X, double Y)[] line = [(0, 0), (0, 10), (10, 10)];
        var anchor = new LineAnchor(0, 10, 0, 1);
        Assert.True(LineAnchors.CheckMaxAngle(line, anchor, 10, 5, Math.PI / 2));
        Assert.False(LineAnchors.CheckMaxAngle(line, anchor, 10, 5, Math.PI / 2 - 0.01));
    }

    [Fact]
    public void ManySmallCornersCloseTogether()
    {
        (double X, double Y)[] line = [(0, 0), (10, 0), (11, 0.1), (12, 0.3), (13, 0.6), (14, 1), (13.9, 10)];
        var anchor = new LineAnchor(12, 0.3, 0, 3);
        Assert.False(LineAnchors.CheckMaxAngle(line, anchor, 10, 5, Math.PI / 2));
        Assert.True(LineAnchors.CheckMaxAngle(line, anchor, 10, 2, Math.PI / 2));
    }

    [Fact]
    public void LabelAppearsOnTheFirstLineSegment()
    {
        (double X, double Y)[] line = [(0, 0), (100, 0)];
        Assert.True(LineAnchors.CheckMaxAngle(line, new LineAnchor(50, 0, 0, 0), 30, 5, Math.PI / 2));
    }

    [Fact]
    public void NotEnoughSpaceBeforeTheEndOfTheLine()
    {
        (double X, double Y)[] line = [(0, 0), (10, 0), (20, 0), (30, 0)];
        var anchor = new LineAnchor(5, 0, 0, 0);
        Assert.False(LineAnchors.CheckMaxAngle(line, anchor, 11, 5, Math.PI));
        Assert.True(LineAnchors.CheckMaxAngle(line, anchor, 10, 5, Math.PI));
    }

    [Fact]
    public void NotEnoughSpaceAfterTheBeginningOfTheLine()
    {
        (double X, double Y)[] line = [(0, 0), (10, 0), (20, 0), (30, 0)];
        var anchor = new LineAnchor(25, 0, 0, 2);
        Assert.False(LineAnchors.CheckMaxAngle(line, anchor, 11, 5, Math.PI));
        Assert.True(LineAnchors.CheckMaxAngle(line, anchor, 10, 5, Math.PI));
    }

    [Fact]
    public void OneSegmentAndLabelLengthIsZero()
    {
        (double X, double Y)[] line = [(0, 0), (10, 0)];
        Assert.True(LineAnchors.CheckMaxAngle(line, new LineAnchor(5, 0, 0, 0), 0, 5, Math.PI));
    }

    // ---- clip_line.test.ts, clipLines ----

    const double MinX = -300, MaxX = 300, MinY = -200, MaxY = 200;

    static List<List<(double X, double Y)>> Clip(params (double X, double Y)[][] lines)
        => LineAnchors.ClipLine(lines, MinX, MinY, MaxX, MaxY);

    // Exact comparison, as toEqual: clipped points are integers, unclipped ones must be bit exact.
    static void SameLines((double X, double Y)[][] expected, List<List<(double X, double Y)>> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void SingleLineFullyInside()
    {
        (double X, double Y)[] line = [(-100, -100), (-40, -100), (200, 0), (-80, 195)];
        SameLines([line], Clip(line));
    }

    [Fact]
    public void MultilineFullyInside()
    {
        (double X, double Y)[] line0 = [(-250, -150), (-250, 150), (-10, 150), (-10, -150)];
        (double X, double Y)[] line1 = [(250, -150), (250, 150), (10, 150), (10, -150)];
        SameLines([line0, line1], Clip(line0, line1));
    }

    [Fact]
    public void LinesFullyOutside()
    {
        (double X, double Y)[] line0 = [(-400, -300), (-350, 0), (-300, 300)];
        (double X, double Y)[] line1 = [(1000, 210), (10000, 500)];
        Assert.Empty(Clip(line0, line1));
    }

    [Fact]
    public void IntersectWithSingleBorder()
    {
        (double X, double Y)[] line0 = [(-400, 0), (0, 0)];
        (double X, double Y)[] line1 = [(250, -50), (350, 50)];
        SameLines([[(MinX, 0), (0, 0)], [(250, -50), (MaxX, 0)]], Clip(line0, line1));
    }

    [Fact]
    public void IntersectWithMultipleBorders()
    {
        (double X, double Y)[] line0 = [(-350, -100), (-200, -250)];
        (double X, double Y)[] line1 = [(-100, 250), (0, 150), (100, 250)];
        SameLines([[(MinX, -150), (-250, MinY)], [(-50, MaxY), (0, 150), (50, MaxY)]], Clip(line0, line1));
    }

    [Fact]
    public void SingleLineCanBeSplitIntoMultipleSegments()
    {
        (double X, double Y)[] line = [(-80, 150), (-80, 350), (120, 1000), (120, 0)];
        SameLines([[(-80, 150), (-80, MaxY)], [(120, MaxY), (120, 0)]], Clip(line));
    }

    [Fact]
    public void NonClippedPointsAreBitExact()
    {
        // GL JS's assertion compares two undefineds (result[1] and line[0][1]); this checks what its
        // name says: the end inside the box comes through untouched.
        (double X, double Y)[] line = [(-500, -200), (131.2356763, 0.956732)];
        var result = Clip(line);
        Assert.Single(result);
        Assert.Equal((131.2356763, 0.956732), result[0][^1]);
    }

    [Fact]
    public void ClippedPointsAreRoundedToTheNearestInteger()
    {
        (double X, double Y)[] line = [(310, 2.9), (290, 2.5)];
        SameLines([[(MaxX, 3), (290, 2.5)]], Clip(line));
    }
}
