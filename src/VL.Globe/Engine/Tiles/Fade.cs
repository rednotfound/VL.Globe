using System;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine.Tiles;

// Cross-fade from the tile that stood in to the tile that replaces it (the user, 2026-11-20: "做一下
// 300ms的淡入淡出"). MapLibre's raster fade: a child is drawn mixing its own image with its parent's,
// the parent's sampled at the child's quarter (`parentScaleBy`, draw_raster.ts), over
// raster-fade-duration = 300 ms. Here the mix is a material: ancestor * (1 - f) + own * f.
internal static class Fade
{
    /// <summary>The texture-coordinate transform that maps <paramref name="child"/>'s 0..1 onto its part of <paramref name="ancestor"/>'s image: uv * scale + offset, as Stride's ComputeColorTextureScaledOffsetDynamicSampler samples.</summary>
    public static (Vector2 scale, Vector2 offset) AncestorTransform(TileKey child, TileKey ancestor)
    {
        int k = child.Zoom - ancestor.Zoom;
        if (k < 0) throw new ArgumentException("not an ancestor", nameof(ancestor));
        double s = 1.0 / (1 << k);
        double ox = (child.X - ((long)ancestor.X << k)) * s, oy = (child.Y - ((long)ancestor.Y << k)) * s;
        return (new Vector2((float)s), new Vector2((float)ox, (float)oy));
    }

    /// <summary>True when the descendants of <paramref name="t"/> together cover all of it: each covers 4^-(levels below t). Distinct descendants never overlap (DrawSet draws none over its own ancestor).</summary>
    public static bool Covers(TileKey t, System.Collections.Generic.IEnumerable<TileKey> descendants)
    {
        double sum = 0;
        foreach (var d in descendants) if (t.IsAncestorOf(d)) sum += Math.Pow(0.25, d.Zoom - t.Zoom);
        return sum >= 1 - 1e-9;
    }

    /// <summary>How far a fade started at <paramref name="start"/> has come at <paramref name="now"/>, 0..1; 1 at once when <paramref name="duration"/> is 0.</summary>
    public static float Progress(double start, double now, double duration)
        => duration <= 0 ? 1f : (float)Math.Clamp((now - start) / duration, 0, 1);
}
