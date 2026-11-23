using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using VL.Lib.IO.Notifications;
using VL.Skia;

namespace VL.Globe.Engine.Tiles;

// The credits every tile and style service requires, drawn bottom right of the view as MapLibre GL JS's
// AttributionControl and Mapsui's MapRenderer draw them, with no node to add (the user, 2026-11-23:
// "别忘了支持Attribution的信息等等的呈现"). A VL.Skia layer: the Globe puts it into the scene through
// VL.Stride's SkiaRenderer, after the scene, so it is over the globe, the vectors and the fog.
internal sealed class AttributionLayer : ILayer
{
    // MapLibre's .maplibregl-ctrl-attrib: 12 px text, rgba(255,255,255,0.5) behind it, padding 0 5 px,
    // at the very corner; text rgba(0,0,0,0.75)
    const float TextSize = 12, PadX = 5, PadY = 3;

    volatile string _text = "";
    public string Text { get => _text; set => _text = value ?? ""; }

    public global::Stride.Core.Mathematics.RectangleF? Bounds => null;
    public bool Notify(INotification notification, CallerInfo caller) => false;

    public void Render(CallerInfo caller)
    {
        var text = _text;
        var canvas = caller.Canvas;
        if (text.Length == 0 || canvas is null) return;
        int count = canvas.Save();
        try
        {
            // device pixels, origin top left: VL.Skia's transformation is what maps its space to them
            canvas.SetMatrix(SKMatrix.Identity);
            Draw(canvas, caller.ViewportBounds, text, Math.Max(1f, caller.Scaling));
        }
        finally { canvas.RestoreToCount(count); }
    }

    /// <summary>The box and its text at the bottom right of <paramref name="viewport"/>; the box drawn.</summary>
    internal static SKRect Draw(SKCanvas canvas, SKRect viewport, string text, float scaling)
    {
        // SkiaSharp 2.88 (vvvv's): text through SKPaint
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 191), TextSize = TextSize * scaling, Typeface = SKTypeface.Default };
        // too wide for the view: its start kept, the rest cut to an ellipsis
        float maxWidth = Math.Max(0, viewport.Width - 2 * PadX * scaling);
        float width = paint.MeasureText(text);
        if (width > maxWidth && maxWidth > 0)
        {
            int keep = (int)paint.BreakText(text, Math.Max(0, maxWidth - paint.MeasureText("…")));
            text = text[..Math.Clamp(keep, 0, text.Length)].TrimEnd() + "…";
            width = paint.MeasureText(text);
        }
        var metrics = paint.FontMetrics;
        float height = metrics.Descent - metrics.Ascent + 2 * PadY * scaling;
        var box = new SKRect(viewport.Right - width - 2 * PadX * scaling, viewport.Bottom - height, viewport.Right, viewport.Bottom);
        using (var back = new SKPaint { Color = new SKColor(255, 255, 255, 128) }) canvas.DrawRect(box, back);
        canvas.DrawText(text, box.Left + PadX * scaling, box.Bottom - PadY * scaling - metrics.Descent, paint);
        return box;
    }

    /// <summary>Every credit once, in the layers' order, as MapLibre joins its sources': " | ".</summary>
    internal static string Join(IEnumerable<string?> credits)
        => string.Join(" | ", credits.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct());
}
