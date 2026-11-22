using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// GL JS anchor.ts, Anchor: a label's anchor on a line - the point, the angle of the line there and the
// index of the segment it lies on (checkMaxAngle walks the line outwards from that segment).
internal readonly record struct LineAnchor(double X, double Y, double Angle, int Segment);

// Where labels along lines are anchored, ported from MapLibre GL JS v6.19.0 (get_anchors.ts,
// check_max_angle.ts, clip_line.ts). Arithmetic is kept in GL JS's order so anchors land on the same
// integers as theirs; the tile extent is a parameter because tiles here are 512 wide, not 8192.
internal static class LineAnchors
{
    // JS Math.round rounds halves up (towards +infinity), Math.Round would round them to even.
    static double JsRound(double x) => Math.Floor(x + 0.5);

    // @mapbox/point-geometry, Point.dist: dx computed as p - this, as there.
    static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // @mapbox/point-geometry, Point.angleTo.
    static double AngleTo((double X, double Y) a, (double X, double Y) b) => Math.Atan2(a.Y - b.Y, a.X - b.X);

    // maplibre-gl-style-spec interpolate-primitives.ts, interpolateNumber.
    static double Lerp(double from, double to, double t) => from + t * (to - from);

    // GL JS get_anchors.ts, getLineLength
    static double GetLineLength(IReadOnlyList<(double X, double Y)> line)
    {
        double lineLength = 0;
        for (int k = 0; k < line.Count - 1; k++)
            lineLength += Dist(line[k], line[k + 1]);
        return lineLength;
    }

    // GL JS get_anchors.ts, getAngleWindowSize - an icon-only label has no window: it is never bent.
    static double GetAngleWindowSize((double Left, double Right)? shapedText, double glyphSize, double boxScale)
        => shapedText.HasValue ? 3.0 / 5 * glyphSize * boxScale : 0;

    // GL JS get_anchors.ts, getShapedLabelLength - only the shapings' horizontal extents matter here.
    static double GetShapedLabelLength((double Left, double Right)? shapedText, (double Left, double Right)? shapedIcon)
        => Math.Max(
            shapedText is { } t ? t.Right - t.Left : 0,
            shapedIcon is { } i ? i.Right - i.Left : 0);

    /// <summary>The anchor at the middle of a line (symbol-placement line-center), or null when the label bends too much there.</summary>
    // GL JS get_anchors.ts, getCenterAnchor. A null shaping stands for GL JS's absent Shaping/PositionedIcon.
    public static LineAnchor? GetCenterAnchor(
        IReadOnlyList<(double X, double Y)> line,
        double maxAngle,
        (double Left, double Right)? shapedText,
        (double Left, double Right)? shapedIcon,
        double glyphSize,
        double boxScale)
    {
        double angleWindowSize = GetAngleWindowSize(shapedText, glyphSize, boxScale);
        double labelLength = GetShapedLabelLength(shapedText, shapedIcon) * boxScale;

        double prevDistance = 0;
        double centerDistance = GetLineLength(line) / 2;

        for (int i = 0; i < line.Count - 1; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double segmentDistance = Dist(a, b);

            if (prevDistance + segmentDistance > centerDistance)
            {
                double t = (centerDistance - prevDistance) / segmentDistance;
                double x = Lerp(a.X, b.X, t), y = Lerp(a.Y, b.Y, t);

                var anchor = new LineAnchor(JsRound(x), JsRound(y), AngleTo(b, a), i);
                if (angleWindowSize == 0 || CheckMaxAngle(line, anchor, labelLength, angleWindowSize, maxAngle))
                    return anchor;
                return null;
            }

            prevDistance += segmentDistance;
        }
        return null;
    }

    /// <summary>Anchors spaced along a line (symbol-placement line), each with room for the label and no sharper bend than Max Angle.</summary>
    // GL JS get_anchors.ts, getAnchors. A null shaping stands for GL JS's absent Shaping/PositionedIcon.
    public static List<LineAnchor> GetAnchors(
        IReadOnlyList<(double X, double Y)> line,
        double spacing,
        double maxAngle,
        (double Left, double Right)? shapedText,
        (double Left, double Right)? shapedIcon,
        double glyphSize,
        double boxScale,
        double overscaling,
        double tileExtent)
    {
        double angleWindowSize = GetAngleWindowSize(shapedText, glyphSize, boxScale);
        double shapedLabelLength = GetShapedLabelLength(shapedText, shapedIcon);
        double labelLength = shapedLabelLength * boxScale;

        // A line starting on the tile edge continues one from the neighbour: anchors there follow the
        // spacing, not the line's own start, so the two tiles' anchors line up.
        bool isLineContinued = line[0].X == 0 || line[0].X == tileExtent || line[0].Y == 0 || line[0].Y == tileExtent;

        // A long label keeps at least a quarter of the spacing between label edges.
        if (spacing - labelLength < spacing / 4)
            spacing = labelLength + spacing / 4;

        // The fixed extra offset keeps labels clear of T intersections at a line's start.
        double fixedExtraOffset = glyphSize * 2;

        double offset = !isLineContinued
            ? ((shapedLabelLength / 2 + fixedExtraOffset) * boxScale * overscaling) % spacing
            : (spacing / 2 * overscaling) % spacing;

        return Resample(line, offset, spacing, angleWindowSize, maxAngle, labelLength, isLineContinued, false, tileExtent);
    }

    // GL JS get_anchors.ts, resample
    static List<LineAnchor> Resample(
        IReadOnlyList<(double X, double Y)> line, double offset, double spacing, double angleWindowSize,
        double maxAngle, double labelLength, bool isLineContinued, bool placeAtMiddle, double tileExtent)
    {
        double halfLabelLength = labelLength / 2;
        double lineLength = GetLineLength(line);

        double distance = 0;
        double markedDistance = offset - spacing;

        var anchors = new List<LineAnchor>();

        for (int i = 0; i < line.Count - 1; i++)
        {
            var a = line[i];
            var b = line[i + 1];

            double segmentDist = Dist(a, b);
            double angle = AngleTo(b, a);

            while (markedDistance + spacing < distance + segmentDist)
            {
                markedDistance += spacing;

                double t = (markedDistance - distance) / segmentDist;
                double x = Lerp(a.X, b.X, t), y = Lerp(a.Y, b.Y, t);

                // Inside this tile (the neighbour places the rest), and the label fits between the
                // line's two ends.
                if (x >= 0 && x < tileExtent && y >= 0 && y < tileExtent &&
                    markedDistance - halfLabelLength >= 0 &&
                    markedDistance + halfLabelLength <= lineLength)
                {
                    var anchor = new LineAnchor(JsRound(x), JsRound(y), angle, i);
                    if (angleWindowSize == 0 || CheckMaxAngle(line, anchor, labelLength, angleWindowSize, maxAngle))
                        anchors.Add(anchor);
                }
            }

            distance += segmentDist;
        }

        // The offset aligns overscaled tiles' anchors with their parent's, which can leave a short line
        // with none: one try at its middle, as GL JS does.
        if (!placeAtMiddle && anchors.Count == 0 && !isLineContinued)
            anchors = Resample(line, distance / 2, spacing, angleWindowSize, maxAngle, labelLength, isLineContinued, true, tileExtent);

        return anchors;
    }

    /// <summary>Whether every Window Size stretch of the label's part of the line turns by no more than Max Angle in all.</summary>
    // GL JS check_max_angle.ts, checkMaxAngle. GL JS's undefined-segment case (a horizontal label) has
    // no counterpart: every LineAnchor lies on a segment.
    public static bool CheckMaxAngle(
        IReadOnlyList<(double X, double Y)> line, LineAnchor anchor, double labelLength, double windowSize, double maxAngle)
    {
        if (labelLength == 0) return true;

        (double X, double Y) p = (anchor.X, anchor.Y);
        int index = anchor.Segment + 1;
        double anchorDistance = 0;

        // Back to the first segment the label appears on.
        while (anchorDistance > -labelLength / 2)
        {
            index--;
            // No room for the label after the start of the line.
            if (index < 0) return false;

            anchorDistance -= Dist(line[index], p);
            p = line[index];
        }

        anchorDistance += Dist(line[index], line[index + 1]);
        index++;

        var recentCorners = new Queue<(double Distance, double AngleDelta)>();
        double recentAngleDelta = 0;

        while (anchorDistance < labelLength / 2)
        {
            // No room for the label before the end of the line.
            if (index + 1 >= line.Count) return false;

            var prev = line[index - 1];
            var current = line[index];
            var next = line[index + 1];

            double angleDelta = AngleTo(prev, current) - AngleTo(current, next);
            // Into -pi..pi before taking the size of the turn.
            angleDelta = Math.Abs(((angleDelta + 3 * Math.PI) % (Math.PI * 2)) - Math.PI);

            recentCorners.Enqueue((anchorDistance, angleDelta));
            recentAngleDelta += angleDelta;

            // Corners further back than the window no longer count.
            while (anchorDistance - recentCorners.Peek().Distance > windowSize)
                recentAngleDelta -= recentCorners.Dequeue().AngleDelta;

            if (recentAngleDelta > maxAngle) return false;

            index++;
            anchorDistance += Dist(current, next);
        }

        return true;
    }

    /// <summary>The parts of the lines inside the box x1..x2, y1..y2; points cut at the edges are rounded to integers.</summary>
    // GL JS clip_line.ts, clipLine
    public static List<List<(double X, double Y)>> ClipLine(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> lines, double x1, double y1, double x2, double y2)
    {
        var clippedLines = new List<List<(double X, double Y)>>();

        foreach (var line in lines)
        {
            List<(double X, double Y)>? clippedLine = null;

            for (int i = 0; i < line.Count - 1; i++)
            {
                var p0 = line[i];
                var p1 = line[i + 1];

                if (p0.X < x1 && p1.X < x1) continue;
                else if (p0.X < x1) p0 = (x1, JsRound(p0.Y + (p1.Y - p0.Y) * ((x1 - p0.X) / (p1.X - p0.X))));
                else if (p1.X < x1) p1 = (x1, JsRound(p0.Y + (p1.Y - p0.Y) * ((x1 - p0.X) / (p1.X - p0.X))));

                if (p0.Y < y1 && p1.Y < y1) continue;
                else if (p0.Y < y1) p0 = (JsRound(p0.X + (p1.X - p0.X) * ((y1 - p0.Y) / (p1.Y - p0.Y))), y1);
                else if (p1.Y < y1) p1 = (JsRound(p0.X + (p1.X - p0.X) * ((y1 - p0.Y) / (p1.Y - p0.Y))), y1);

                if (p0.X >= x2 && p1.X >= x2) continue;
                else if (p0.X >= x2) p0 = (x2, JsRound(p0.Y + (p1.Y - p0.Y) * ((x2 - p0.X) / (p1.X - p0.X))));
                else if (p1.X >= x2) p1 = (x2, JsRound(p0.Y + (p1.Y - p0.Y) * ((x2 - p0.X) / (p1.X - p0.X))));

                if (p0.Y >= y2 && p1.Y >= y2) continue;
                else if (p0.Y >= y2) p0 = (JsRound(p0.X + (p1.X - p0.X) * ((y2 - p0.Y) / (p1.Y - p0.Y))), y2);
                else if (p1.Y >= y2) p1 = (JsRound(p0.X + (p1.X - p0.X) * ((y2 - p0.Y) / (p1.Y - p0.Y))), y2);

                // A segment not joined to the previous kept one starts a new line.
                if (clippedLine == null || p0 != clippedLine[^1])
                {
                    clippedLine = [p0];
                    clippedLines.Add(clippedLine);
                }

                clippedLine.Add(p1);
            }
        }

        return clippedLines;
    }
}
