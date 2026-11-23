using System;
using System.Collections.Generic;
using System.Linq;

namespace VL.Globe.Engine.Symbols;

/// <summary>An icon shaped round its anchor, CSS pixels at icon-size 1: GL JS's PositionedIcon.</summary>
internal sealed record PositionedIcon(ImagePosition Image, double Top, double Bottom, double Left, double Right,
    (double Left, double Top, double Right, double Bottom)? CollisionPadding = null);

/// <summary>One quad of an icon: corners from the anchor (scaled by the icon-size in the shader), the
/// pixel offsets that are not, the atlas rect, the min font scale - GL JS's SymbolQuad for an icon.</summary>
internal readonly record struct IconQuad(
    (double X, double Y) Tl, (double X, double Y) Tr, (double X, double Y) Bl, (double X, double Y) Br,
    double TexX, double TexY, double TexW, double TexH,
    (double X, double Y) PixelOffsetTl, (double X, double Y) PixelOffsetBr, double MinFontScaleX, double MinFontScaleY);

// Icons (milestone 4, step 5): GL JS's shaping.ts shapeIcon, fitIconToText and applyTextFit, and
// quads.ts getIconQuads with its stretch zones - ported line by line.
internal static class IconShaping
{
    const int Border = ImageAtlas.Padding;   // quads.ts: border = IMAGE_PADDING

    public static PositionedIcon ShapeIcon(ImagePosition image, (double X, double Y) iconOffset, SymbolAnchor iconAnchor)
    {
        var (horizontalAlign, verticalAlign) = Shaping.GetAnchorAlignment(iconAnchor);
        var (w, h) = image.DisplaySize;
        double x1 = iconOffset.X - w * horizontalAlign, x2 = x1 + w;
        double y1 = iconOffset.Y - h * verticalAlign, y2 = y1 + h;
        return new PositionedIcon(image, y1, y2, x1, x2);
    }

    // applyTextFit: the box after textFitWidth / textFitHeight keep the content's aspect ratio
    public static (double X1, double Y1, double X2, double Y2) ApplyTextFit(PositionedIcon shapedIcon)
    {
        double iconLeft = shapedIcon.Left, iconTop = shapedIcon.Top;
        double iconWidth = shapedIcon.Right - iconLeft, iconHeight = shapedIcon.Bottom - iconTop;
        var content = shapedIcon.Image.Image.Content!;
        double contentWidth = content[2] - content[0], contentHeight = content[3] - content[1];
        string textFitWidth = shapedIcon.Image.Image.TextFitWidth ?? "stretchOrShrink";
        string textFitHeight = shapedIcon.Image.Image.TextFitHeight ?? "stretchOrShrink";
        double contentAspectRatio = contentWidth / contentHeight;
        if (textFitHeight == "proportional")
        {
            if ((textFitWidth == "stretchOnly" && iconWidth / iconHeight < contentAspectRatio) || textFitWidth == "proportional")
            {
                double newIconWidth = Math.Ceiling(iconHeight * contentAspectRatio);
                iconLeft *= newIconWidth / iconWidth;
                iconWidth = newIconWidth;
            }
        }
        else if (textFitWidth == "proportional")
        {
            if (textFitHeight == "stretchOnly" && contentAspectRatio != 0 && iconWidth / iconHeight > contentAspectRatio)
            {
                double newIconHeight = Math.Ceiling(iconWidth / contentAspectRatio);
                iconTop *= newIconHeight / iconHeight;
                iconHeight = newIconHeight;
            }
        }
        return (iconLeft, iconTop, iconLeft + iconWidth, iconTop + iconHeight);
    }

    static bool HasTextFitAspect(ImagePosition image) => image.Image.Content != null && (image.Image.TextFitWidth != null || image.Image.TextFitHeight != null);

    // fitIconToText: the icon round the text plus icon-text-fit-padding ([top, right, bottom, left]),
    // laid out in icon units (the text extent and padding divided by the icon-size)
    public static PositionedIcon FitIconToText(PositionedIcon shapedIcon, Shaping shapedText, string textFit,
        (double Top, double Right, double Bottom, double Left) padding, (double X, double Y) iconOffset, double fontScale, double iconSize)
    {
        var image = shapedIcon.Image;
        (double, double, double, double)? collisionPadding = null;
        if (image.Image.Content is { } content)
        {
            double pixelRatio = image.Image.PixelRatio == 0 ? 1 : image.Image.PixelRatio;
            var (dw, dh) = image.DisplaySize;
            collisionPadding = (content[0] / pixelRatio, content[1] / pixelRatio, dw - content[2] / pixelRatio, dh - content[3] / pixelRatio);
        }
        double textScale = iconSize > 0 ? fontScale / iconSize : fontScale;
        double paddingScale = iconSize > 0 ? 1 / iconSize : 1;
        double textLeft = shapedText.Left * textScale, textRight = shapedText.Right * textScale;
        double top, right, bottom, left;
        var (displayW, displayH) = image.DisplaySize;
        if (textFit is "width" or "both")
        {
            left = iconOffset.X + textLeft - padding.Left * paddingScale;
            right = iconOffset.X + textRight + padding.Right * paddingScale;
        }
        else
        {
            left = iconOffset.X + (textLeft + textRight - displayW) / 2;
            right = left + displayW;
        }
        double textTop = shapedText.Top * textScale, textBottom = shapedText.Bottom * textScale;
        if (textFit is "height" or "both")
        {
            top = iconOffset.Y + textTop - padding.Top * paddingScale;
            bottom = iconOffset.Y + textBottom + padding.Bottom * paddingScale;
        }
        else
        {
            top = iconOffset.Y + (textTop + textBottom - displayH) / 2;
            bottom = top + displayH;
        }
        return new PositionedIcon(image, top, bottom, left, right, collisionPadding);
    }

    /// <summary>collision_feature.ts for an icon in the viewport: the box (text-fit aspect applied), the padding, the content padding.</summary>
    public static (double X1, double Y1, double X2, double Y2) CollisionBox(PositionedIcon shaped, double boxScale,
        (double Top, double Right, double Bottom, double Left) padding)
    {
        var (x1, y1, x2, y2) = HasTextFitAspect(shaped.Image) ? ApplyTextFit(shaped) : (shaped.Left, shaped.Top, shaped.Right, shaped.Bottom);
        y1 = y1 * boxScale - padding.Top;
        y2 = y2 * boxScale + padding.Bottom;
        x1 = x1 * boxScale - padding.Left;
        x2 = x2 * boxScale + padding.Right;
        if (shaped.CollisionPadding is { } c)
        {
            x1 -= c.Left * boxScale;
            y1 -= c.Top * boxScale;
            x2 += c.Right * boxScale;
            y2 += c.Bottom * boxScale;
        }
        return (x1, y1, x2, y2);
    }

    readonly record struct Cut(double Fixed, double Stretch);

    /// <summary>quads.ts getIconQuads.</summary>
    public static List<IconQuad> GetIconQuads(PositionedIcon shapedIcon, double iconRotate, bool hasIconTextFit, double iconSize)
    {
        var quads = new List<IconQuad>();
        var image = shapedIcon.Image;
        double pixelRatio = image.Image.PixelRatio;
        double imageWidth = image.PaddedRect.W - 2 * Border, imageHeight = image.PaddedRect.H - 2 * Border;
        var icon = (X1: shapedIcon.Left, Y1: shapedIcon.Top, X2: shapedIcon.Right, Y2: shapedIcon.Bottom);

        var stretchX = image.Image.StretchX ?? new[] { (0.0, imageWidth) };
        var stretchY = image.Image.StretchY ?? new[] { (0.0, imageHeight) };
        double stretchWidth = stretchX.Sum(r => r.To - r.From), stretchHeight = stretchY.Sum(r => r.To - r.From);
        double fixedWidth = imageWidth - stretchWidth, fixedHeight = imageHeight - stretchHeight;

        double stretchOffsetX = 0, stretchContentWidth = stretchWidth, stretchOffsetY = 0, stretchContentHeight = stretchHeight;
        double fixedOffsetX = 0, fixedContentWidth = fixedWidth, fixedOffsetY = 0, fixedContentHeight = fixedHeight;

        if (image.Image.Content is { } content && hasIconTextFit)
        {
            double contentWidth = content[2] - content[0], contentHeight = content[3] - content[1];
            if (image.Image.TextFitWidth != null || image.Image.TextFitHeight != null)
                icon = ApplyTextFit(shapedIcon);
            stretchOffsetX = SumWithinRange(stretchX, 0, content[0]);
            stretchOffsetY = SumWithinRange(stretchY, 0, content[1]);
            stretchContentWidth = SumWithinRange(stretchX, content[0], content[2]);
            stretchContentHeight = SumWithinRange(stretchY, content[1], content[3]);
            fixedOffsetX = content[0] - stretchOffsetX;
            fixedOffsetY = content[1] - stretchOffsetY;
            fixedContentWidth = contentWidth - stretchContentWidth;
            fixedContentHeight = contentHeight - stretchContentHeight;
        }

        double iconLeft = icon.X1, iconTop = icon.Y1, iconWidth = icon.X2 - iconLeft, iconHeight = icon.Y2 - iconTop;

        IconQuad MakeBox(Cut left, Cut top, Cut right, Cut bottom)
        {
            double leftEm = EmOffset(left.Stretch - stretchOffsetX, stretchContentWidth, iconWidth, iconLeft);
            double leftPx = PxOffset(left.Fixed - fixedOffsetX, fixedContentWidth, left.Stretch, stretchWidth);
            double topEm = EmOffset(top.Stretch - stretchOffsetY, stretchContentHeight, iconHeight, iconTop);
            double topPx = PxOffset(top.Fixed - fixedOffsetY, fixedContentHeight, top.Stretch, stretchHeight);
            double rightEm = EmOffset(right.Stretch - stretchOffsetX, stretchContentWidth, iconWidth, iconLeft);
            double rightPx = PxOffset(right.Fixed - fixedOffsetX, fixedContentWidth, right.Stretch, stretchWidth);
            double bottomEm = EmOffset(bottom.Stretch - stretchOffsetY, stretchContentHeight, iconHeight, iconTop);
            double bottomPx = PxOffset(bottom.Fixed - fixedOffsetY, fixedContentHeight, bottom.Stretch, stretchHeight);

            var tl = (leftEm, topEm); var tr = (rightEm, topEm); var br = (rightEm, bottomEm); var bl = (leftEm, bottomEm);
            double pixelScale = iconSize / pixelRatio;
            var pixelOffsetTl = (leftPx * pixelScale, topPx * pixelScale);
            var pixelOffsetBr = (rightPx * pixelScale, bottomPx * pixelScale);

            double angle = iconRotate * Math.PI / 180;
            if (angle != 0)
            {
                double sin = Math.Sin(angle), cos = Math.Cos(angle);
                (double, double) Rotate((double X, double Y) p) => (cos * p.X - sin * p.Y, sin * p.X + cos * p.Y);   // _matMult([cos, -sin, sin, cos])
                tl = Rotate(tl); tr = Rotate(tr); bl = Rotate(bl); br = Rotate(br);
            }

            double x1 = left.Stretch + left.Fixed, x2 = right.Stretch + right.Fixed;
            double y1 = top.Stretch + top.Fixed, y2 = bottom.Stretch + bottom.Fixed;
            double minFontScaleX = fixedContentWidth * pixelScale / iconWidth;
            double minFontScaleY = fixedContentHeight * pixelScale / iconHeight;
            return new IconQuad(tl, tr, bl, br,
                image.PaddedRect.X + Border + x1, image.PaddedRect.Y + Border + y1, x2 - x1, y2 - y1,
                pixelOffsetTl, pixelOffsetBr, minFontScaleX, minFontScaleY);
        }

        if (!hasIconTextFit || (image.Image.StretchX is null && image.Image.StretchY is null))
            quads.Add(MakeBox(new Cut(0, -1), new Cut(0, -1), new Cut(0, imageWidth + 1), new Cut(0, imageHeight + 1)));
        else
        {
            var xCuts = StretchZonesToCuts(stretchX, fixedWidth, stretchWidth);
            var yCuts = StretchZonesToCuts(stretchY, fixedHeight, stretchHeight);
            for (int xi = 0; xi < xCuts.Count - 1; xi++)
                for (int yi = 0; yi < yCuts.Count - 1; yi++)
                    quads.Add(MakeBox(xCuts[xi], yCuts[yi], xCuts[xi + 1], yCuts[yi + 1]));
        }
        return quads;
    }

    static double SumWithinRange((double From, double To)[] ranges, double min, double max)
    {
        double sum = 0;
        foreach (var (from, to) in ranges)
            sum += Math.Max(min, Math.Min(max, to)) - Math.Max(min, Math.Min(max, from));
        return sum;
    }

    static List<Cut> StretchZonesToCuts((double From, double To)[] zones, double fixedSize, double stretchSize)
    {
        var cuts = new List<Cut> { new(-Border, 0) };
        foreach (var (c1, c2) in zones)
        {
            var last = cuts[^1];
            cuts.Add(new Cut(c1 - last.Stretch, last.Stretch));
            cuts.Add(new Cut(c1 - last.Stretch, last.Stretch + (c2 - c1)));
        }
        cuts.Add(new Cut(fixedSize + Border, stretchSize));
        return cuts;
    }

    static double EmOffset(double stretchOffset, double stretchSize, double iconSize, double iconOffset) =>
        stretchOffset / stretchSize * iconSize + iconOffset;

    static double PxOffset(double fixedOffset, double fixedSize, double stretchOffset, double stretchSize) =>
        fixedOffset - fixedSize * stretchOffset / stretchSize;
}
