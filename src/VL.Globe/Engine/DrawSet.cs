using System;
using System.Collections.Generic;
using System.Linq;

namespace VL.Globe.Engine;

// From the tiles the camera wants and the tiles that have arrived: what to draw now, and what to
// ask for. Two layers (2026-11-21):
//   Top   - every wanted tile that arrived, and for one that has not, its loaded descendants (a
//           zoom out leaves them loaded; MapLibre's _retainLoadedChildren). Never two Top tiles
//           on one spot (coplanar tiles z-fight).
//   Under - for whatever part of a missing tile no descendant covers, its nearest arrived
//           ancestor, drawn sunk below the surface (GlobeNode) so Top wins the depth test and the
//           ancestor shows only through the holes - MapLibre draws the parent under its children.
// The old single-layer rule let ONE missing tile's stand-in ancestor hide every loaded tile under
// it until all arrived: zooming out, a not-yet-loaded tile at the edge of the view pulled a whole
// zoom-3 tile over half the screen - "缩小过程中的闪现" a white, blurred flash (the user, 2026-11-21).
internal static class DrawSet
{
    /// <summary>The coarse fallback level: every wanted tile also asks for its ancestor here.</summary>
    public const int FallbackZoom = 2;

    /// <summary>How many levels below a missing tile its loaded descendants are looked for.</summary>
    public const int MaxChildLevels = 3;

    /// <summary>What to draw: <c>Top</c> tiles at the surface, <c>Under</c> stand-ins sunk below it.</summary>
    public readonly record struct Layers(List<TileKey> Top, List<TileKey> Under);

    public static Layers Resolve(IReadOnlyList<TileKey> wanted, Func<TileKey, bool> isLoaded)
    {
        var top = new HashSet<TileKey>();
        var under = new HashSet<TileKey>();
        foreach (var w in wanted)
        {
            if (isLoaded(w)) { top.Add(w); continue; }
            if (LoadedDescendants(w, isLoaded, MaxChildLevels, top)) continue;   // covered entirely
            var t = w;
            while (!isLoaded(t) && t.Zoom > 0) t = t.Parent;
            if (isLoaded(t)) under.Add(t);
        }
        var topList = top.Where(t => !HasAncestorIn(t, top)).OrderBy(t => t.Zoom).ToList();
        var underList = under.Where(t => !top.Contains(t)).OrderBy(t => t.Zoom).ToList();
        return new Layers(topList, underList);
    }

    // Adds t's loaded descendants within `levels` to `into`, each branch as coarse as possible;
    // true when they cover all of t.
    static bool LoadedDescendants(TileKey t, Func<TileKey, bool> isLoaded, int levels, HashSet<TileKey> into)
    {
        if (levels <= 0) return false;
        bool all = true;
        for (int i = 0; i < 4; i++)
        {
            var c = t.Child(i);
            if (isLoaded(c)) { into.Add(c); continue; }
            if (!LoadedDescendants(c, isLoaded, levels - 1, into)) all = false;
        }
        return all;
    }

    static bool HasAncestorIn(TileKey t, HashSet<TileKey> set)
    {
        for (var a = t; a.Zoom > 0;) { a = a.Parent; if (set.Contains(a)) return true; }
        return false;
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
