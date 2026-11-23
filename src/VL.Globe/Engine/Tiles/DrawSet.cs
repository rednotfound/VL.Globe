using System;
using System.Collections.Generic;
using System.Linq;

namespace VL.Globe.Engine.Tiles;

// From the tiles the camera wants and the tiles that have arrived: what to draw now, and what to
// ask for. Two kinds of thing are drawn, never two on one spot (coplanar tiles z-fight):
//   Top      - every wanted tile that arrived, and for one that has not, its loaded descendants (a
//              zoom out leaves them loaded; MapLibre's _retainLoadedChildren).
//   StandIns - each square still uncovered, drawn with ITS OWN mesh and the matching part of its
//              nearest arrived ancestor's image (as Cesium and MapLibre draw a missing tile from the
//              parent's texture). Everything stays at the surface.
// History (2026-11-21): first the stand-in ancestor alone hid every loaded tile under it (a zoom-3
// "white flash" over half the screen); then the whole ancestor was drawn SUNK beneath the loaded
// tiles, which a tilted view showed as tiles "dropping and rising" and black cracks down the step
// to the background (the user: "那个地砖往下下降了一点，然后抬起来的那种感觉").
internal static class DrawSet
{
    /// <summary>The coarse fallback level: every wanted tile also asks for its ancestor here.</summary>
    public const int FallbackZoom = 2;

    /// <summary>How many levels below a missing tile its loaded descendants are looked for.</summary>
    public const int MaxChildLevels = 3;

    /// <summary>What to draw: <c>Top</c> tiles, and <c>StandIns</c> - squares drawn from part of an ancestor's image.</summary>
    public readonly record struct Layers(List<TileKey> Top, List<(TileKey Square, TileKey From)> StandIns);

    public static Layers Resolve(IReadOnlyList<TileKey> wanted, Func<TileKey, bool> isLoaded)
    {
        var top = new HashSet<TileKey>();
        var holes = new List<TileKey>();
        foreach (var w in wanted) Fill(w, isLoaded, MaxChildLevels, top, holes);
        var standIns = new List<(TileKey, TileKey)>();
        foreach (var h in holes)
        {
            for (var a = h; a.Zoom > 0;)
            {
                a = a.Parent;
                if (isLoaded(a)) { standIns.Add((h, a)); break; }
            }
        }
        var topList = top.Where(t => !HasAncestorIn(t, top)).OrderBy(t => t.Zoom).ToList();
        return new Layers(topList, standIns);
    }

    // t itself when loaded; else its loaded descendants within `levels`, and the squares between
    // them that nothing covers - each as coarse as possible.
    static void Fill(TileKey t, Func<TileKey, bool> isLoaded, int levels, HashSet<TileKey> top, List<TileKey> holes)
    {
        if (isLoaded(t)) { top.Add(t); return; }
        if (levels > 0 && AnyLoadedBelow(t, isLoaded, levels))
        {
            for (int i = 0; i < 4; i++) Fill(t.Child(i), isLoaded, levels - 1, top, holes);
            return;
        }
        holes.Add(t);
    }

    static bool AnyLoadedBelow(TileKey t, Func<TileKey, bool> isLoaded, int levels)
    {
        if (levels <= 0) return false;
        for (int i = 0; i < 4; i++)
        {
            var c = t.Child(i);
            if (isLoaded(c) || AnyLoadedBelow(c, isLoaded, levels - 1)) return true;
        }
        return false;
    }

    static bool HasAncestorIn(TileKey t, HashSet<TileKey> set)
    {
        for (var a = t; a.Zoom > 0;) { a = a.Parent; if (set.Contains(a)) return true; }
        return false;
    }

    /// <summary>The wanted tiles ordered by the distance of their centres from the eye, nearest first.</summary>
    public static List<TileKey> NearestFirst(IReadOnlyList<TileKey> wanted, global::Stride.Core.Mathematics.Vector3 eye, float radius)
    {
        double ex = eye.X, ey = eye.Y, ez = eye.Z;
        return wanted.OrderBy(t =>
        {
            var c = TileGeometry.Centre(t);
            double dx = c.X * radius - ex, dy = c.Y * radius - ey, dz = c.Z * radius - ez;
            return dx * dx + dy * dy + dz * dz;
        }).ToList();
    }

    /// <summary>Tiles to request, coarse first: each wanted tile's fallback ancestor, then the tile.</summary>
    public static List<TileKey> Requests(IReadOnlyList<TileKey> wanted, Func<TileKey, bool> isLoadedOrPending)
    {
        var list = new List<TileKey>();
        var seen = new HashSet<TileKey>();
        foreach (var w in wanted)
        {
            var f = w.AncestorAt(FallbackZoom);
            if (seen.Add(f) && !isLoadedOrPending(f)) list.Add(f);
        }
        foreach (var w in wanted)
            if (seen.Add(w) && !isLoadedOrPending(w)) list.Add(w);
        return list;
    }
}
