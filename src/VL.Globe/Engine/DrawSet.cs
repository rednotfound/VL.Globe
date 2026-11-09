using System;
using System.Collections.Generic;
using System.Linq;

namespace VL.Globe.Engine;

// From the tiles the camera wants and the tiles that have arrived: what to draw now, and what to
// ask for. A wanted tile that has not arrived is stood in for by its nearest arrived ancestor, so
// the globe is never empty while detail loads; and nothing is drawn on top of its own ancestor
// (two coplanar tiles at one place would z-fight), so a stand-in ancestor hides any of its
// descendants that did arrive until all of them can replace it - the "parents kept until children
// arrive" rule.
internal static class DrawSet
{
    /// <summary>The coarse fallback level: every wanted tile also asks for its ancestor here.</summary>
    public const int FallbackZoom = 2;

    public static List<TileKey> Resolve(IReadOnlyList<TileKey> wanted, Func<TileKey, bool> isLoaded)
    {
        var chosen = new HashSet<TileKey>();
        foreach (var w in wanted)
        {
            var t = w;
            while (!isLoaded(t) && t.Zoom > 0) t = t.Parent;
            if (isLoaded(t)) chosen.Add(t);
        }
        return chosen.Where(t => !HasAncestorIn(t, chosen)).OrderBy(t => t.Zoom).ToList();
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
