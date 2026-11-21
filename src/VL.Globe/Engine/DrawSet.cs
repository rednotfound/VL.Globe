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

    /// <summary>How many levels below a missing tile its loaded descendants are looked for.</summary>
    public const int MaxChildLevels = 3;

    public static List<TileKey> Resolve(IReadOnlyList<TileKey> wanted, Func<TileKey, bool> isLoaded)
    {
        var chosen = new HashSet<TileKey>();
        foreach (var w in wanted)
        {
            if (isLoaded(w)) { chosen.Add(w); continue; }
            // Zooming out, the tiles just drawn are finer than the ones now wanted and still loaded:
            // they stand in, as MapLibre's _retainLoadedChildren keeps them. Only when they cover the
            // whole tile - a hole would show the background - and only a few levels down.
            if (CoveringDescendants(w, isLoaded, MaxChildLevels) is { } below) { chosen.UnionWith(below); continue; }
            var t = w;
            while (!isLoaded(t) && t.Zoom > 0) t = t.Parent;
            if (isLoaded(t)) chosen.Add(t);
        }
        return chosen.Where(t => !HasAncestorIn(t, chosen)).OrderBy(t => t.Zoom).ToList();
    }

    // Loaded descendants of t that together cover all of it, each branch as coarse as possible, or
    // null when some part of t has none within `levels`.
    static List<TileKey>? CoveringDescendants(TileKey t, Func<TileKey, bool> isLoaded, int levels)
    {
        if (levels <= 0) return null;
        var result = new List<TileKey>();
        for (int i = 0; i < 4; i++)
        {
            var c = t.Child(i);
            if (isLoaded(c)) { result.Add(c); continue; }
            if (CoveringDescendants(c, isLoaded, levels - 1) is not { } below) return null;
            result.AddRange(below);
        }
        return result;
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
