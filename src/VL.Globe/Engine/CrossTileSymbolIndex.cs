using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// GL JS cross_tile_symbol_index.ts: one "unique symbol" is its text and its anchor, whichever tile and
// zoom it comes from. Each symbol instance (a point label or a label along a line, per layer) gets a
// cross-tile ID - matched to the same text within about 4 px in an indexed parent or child tile, or a
// new one. Placement keys its results and the fades' opacities by it, so a label carries on through
// the tile that replaces its tile (zooming in or out, a tile rebuilt), and the same label in a tile
// of another zoom drawn beside it is a duplicate, hidden.
//
// Not ported: the KDBush for keys with more than 128 symbols (a plain scan here), wrap jumps (the
// globe does not wrap), promoteId's feature ids in the key.
internal sealed class CrossTileSymbolIndex
{
    // GL JS: anchors rounded to a roughly 4-pixel grid, in its 8192-unit tile
    const double RoundingFactor = 512.0 / 8192 / 2;
    const int GlExtent = 8192;

    readonly Dictionary<int, LayerIndex> _layers = new();
    int _maxId;

    /// <summary>GL JS's symbolInstance.key: the shaped text hashed (murmur3 there; any stable hash does here).</summary>
    public static int KeyOf(string? text)
    {
        // FNV-1a: the same in every process, unlike string.GetHashCode
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in text ?? "") { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    /// <summary>
    /// Gives every symbol of <paramref name="tiles"/> in layer <paramref name="layer"/> its cross-tile ID
    /// (GpuTile.LabelIds / LineLabelIds) and forgets the tiles no longer there. True when any tile came or went.
    /// </summary>
    public bool AddLayer(int layer, IEnumerable<GpuTile> tiles)
    {
        if (!_layers.TryGetValue(layer, out var index)) _layers[layer] = index = new LayerIndex();
        bool changed = false;
        var current = new HashSet<GpuTile>(ReferenceEqualityComparer.Instance);
        foreach (var tile in tiles)
        {
            if (!current.Add(tile)) continue;   // a wrapped copy: the same buffers, the same symbols
            changed |= index.AddBucket(tile, layer, ref _maxId);
        }
        changed |= index.RemoveStale(current);
        return changed;
    }

    /// <summary>Forgets the layers not in <paramref name="used"/> (GL JS pruneUnusedLayers).</summary>
    public void PruneUnusedLayers(IReadOnlySet<int> used)
    {
        foreach (var layer in new List<int>(_layers.Keys))
            if (!used.Contains(layer)) _layers.Remove(layer);
    }

    /// <summary>The tile's place in the index: GL JS's OverscaledTileID without the wrap.</summary>
    internal readonly record struct TileId(int OverscaledZ, TileKey Canonical)
    {
        public static TileId Of(GpuTile tile) => new(tile.Bucket.OverscaledZoom, tile.Bucket.Key);

        // GL JS OverscaledTileID.scaledTo
        public TileId ScaledTo(int z) => z <= Canonical.Zoom
            ? new TileId(z, Canonical.AncestorAt(z))
            : new TileId(z, Canonical);

        // GL JS OverscaledTileID.isChildOf
        public bool IsChildOf(TileId parent)
        {
            int parentZ = parent.Canonical.Zoom;
            int dz = Canonical.Zoom - parentZ;
            return parent.OverscaledZ < OverscaledZ && dz >= 0
                && (parentZ == 0 || (parent.Canonical.X == Canonical.X >> dz && parent.Canonical.Y == Canonical.Y >> dz));
        }
    }

    // GL JS TileLayerIndex: one tile's symbols of one layer, by key, at rounded positions
    sealed class TileLayerIndex
    {
        public readonly TileId Id;
        public readonly GpuTile Tile;
        readonly Dictionary<int, (List<(int X, int Y)> Positions, List<int> Ids)> _byKey = new();

        public TileLayerIndex(TileId id, GpuTile tile, int layer)
        {
            Id = id; Tile = tile;
            foreach (var (key, x, y, crossTileId) in Symbols(tile, layer))
            {
                if (!_byKey.TryGetValue(key, out var entry)) _byKey[key] = entry = (new List<(int, int)>(), new List<int>());
                entry.Positions.Add(((int)Math.Floor(x * RoundingFactor), (int)Math.Floor(y * RoundingFactor)));
                entry.Ids.Add(crossTileId);
            }
        }

        public IEnumerable<int> Ids()
        {
            foreach (var entry in _byKey.Values) foreach (int id in entry.Ids) yield return id;
        }

        // GL JS getScaledCoordinates: a symbol of another tile in this tile's coordinates, rounded
        (int X, int Y) Scaled(int anchorX, int anchorY, TileId child)
        {
            var local = Id.Canonical; var c = child.Canonical;
            double scale = RoundingFactor / Math.Pow(2, c.Zoom - local.Zoom);
            double xWorld = ((double)c.X * GlExtent + anchorX) * scale, yWorld = ((double)c.Y * GlExtent + anchorY) * scale;
            double xOffset = (double)local.X * GlExtent * RoundingFactor, yOffset = (double)local.Y * GlExtent * RoundingFactor;
            return ((int)Math.Floor(xWorld - xOffset), (int)Math.Floor(yWorld - yOffset));
        }

        // GL JS findMatches: the first unclaimed symbol of the same key within the tolerance
        public void FindMatches(GpuTile tile, int layer, TileId newId, HashSet<int> zoomIds)
        {
            int tolerance = Id.Canonical.Zoom < newId.Canonical.Zoom ? 1 : 1 << (Id.Canonical.Zoom - newId.Canonical.Zoom);
            var (labelStart, labelCount) = tile.LabelRange(layer);
            for (int i = labelStart; i < labelStart + labelCount; i++)
            {
                var l = tile.Labels[i];
                if (tile.LabelIds![i] == 0) tile.LabelIds[i] = Match(l.Key, l.AnchorX, l.AnchorY);
            }
            var (lineStart, lineCount) = tile.LineLabelRange(layer);
            for (int i = lineStart; i < lineStart + lineCount; i++)
            {
                var l = tile.LineLabels[i];
                if (tile.LineLabelIds![i] == 0) tile.LineLabelIds[i] = Match(l.Key, l.AnchorX, l.AnchorY);
            }

            int Match(int key, int anchorX, int anchorY)
            {
                if (!_byKey.TryGetValue(key, out var entry)) return 0;
                var (x, y) = Scaled(anchorX, anchorY, newId);
                for (int k = 0; k < entry.Positions.Count; k++)
                {
                    var p = entry.Positions[k];
                    int id = entry.Ids[k];
                    if (Math.Abs(p.X - x) <= tolerance && Math.Abs(p.Y - y) <= tolerance && !zoomIds.Contains(id))
                    {
                        zoomIds.Add(id);
                        return id;
                    }
                }
                return 0;
            }
        }
    }

    // one layer's symbols in a tile: key, anchor in GL JS units, cross-tile ID
    static IEnumerable<(int Key, int X, int Y, int Id)> Symbols(GpuTile tile, int layer)
    {
        var (labelStart, labelCount) = tile.LabelRange(layer);
        for (int i = labelStart; i < labelStart + labelCount; i++)
            yield return (tile.Labels[i].Key, tile.Labels[i].AnchorX, tile.Labels[i].AnchorY, tile.LabelIds![i]);
        var (lineStart, lineCount) = tile.LineLabelRange(layer);
        for (int i = lineStart; i < lineStart + lineCount; i++)
            yield return (tile.LineLabels[i].Key, tile.LineLabels[i].AnchorX, tile.LineLabels[i].AnchorY, tile.LineLabelIds![i]);
    }

    // GL JS CrossTileSymbolLayerIndex
    sealed class LayerIndex
    {
        readonly Dictionary<int, Dictionary<TileKey, TileLayerIndex>> _indexes = new();   // by overscaled zoom, then tile
        readonly Dictionary<int, HashSet<int>> _usedIds = new();                          // by overscaled zoom

        public bool AddBucket(GpuTile tile, int layer, ref int maxId)
        {
            var id = TileId.Of(tile);
            if (_indexes.TryGetValue(id.OverscaledZ, out var atZoom) && atZoom.TryGetValue(id.Canonical, out var existing))
            {
                if (ReferenceEquals(existing.Tile, tile)) return false;
                // replaced by a rebuilt tile: its IDs free to be claimed again; the old entry is matched
                // against below (the same zoom, the same key) and then overwritten
                RemoveIds(id.OverscaledZ, existing);
            }
            tile.LabelIds ??= new int[tile.Labels.Count];
            tile.LineLabelIds ??= new int[tile.LineLabels.Count];
            var (labelStart, labelCount) = tile.LabelRange(layer);
            Array.Clear(tile.LabelIds, labelStart, labelCount);
            var (lineStart, lineCount) = tile.LineLabelRange(layer);
            Array.Clear(tile.LineLabelIds, lineStart, lineCount);

            if (!_usedIds.TryGetValue(id.OverscaledZ, out var zoomIds)) _usedIds[id.OverscaledZ] = zoomIds = new HashSet<int>();
            foreach (var (zoom, zoomIndexes) in _indexes)
            {
                if (zoom > id.OverscaledZ)
                {
                    foreach (var child in zoomIndexes.Values)
                        if (child.Id.IsChildOf(id)) child.FindMatches(tile, layer, id, zoomIds);
                }
                else if (zoomIndexes.TryGetValue(id.ScaledTo(zoom).Canonical, out var parent) && parent.Id == id.ScaledTo(zoom))
                    parent.FindMatches(tile, layer, id, zoomIds);
            }
            for (int i = labelStart; i < labelStart + labelCount; i++)
                if (tile.LabelIds[i] == 0) zoomIds.Add(tile.LabelIds[i] = ++maxId);
            for (int i = lineStart; i < lineStart + lineCount; i++)
                if (tile.LineLabelIds[i] == 0) zoomIds.Add(tile.LineLabelIds[i] = ++maxId);

            if (atZoom is null) _indexes[id.OverscaledZ] = atZoom = new Dictionary<TileKey, TileLayerIndex>();
            atZoom[id.Canonical] = new TileLayerIndex(id, tile, layer);
            return true;
        }

        void RemoveIds(int zoom, TileLayerIndex removed)
        {
            if (!_usedIds.TryGetValue(zoom, out var ids)) return;
            foreach (int id in removed.Ids()) ids.Remove(id);
        }

        public bool RemoveStale(HashSet<GpuTile> current)
        {
            bool changed = false;
            foreach (var (zoom, zoomIndexes) in _indexes)
            {
                List<TileKey>? stale = null;
                foreach (var (key, index) in zoomIndexes)
                    if (!current.Contains(index.Tile)) (stale ??= new()).Add(key);
                if (stale is null) continue;
                foreach (var key in stale)
                {
                    RemoveIds(zoom, zoomIndexes[key]);
                    zoomIndexes.Remove(key);
                }
                changed = true;
            }
            return changed;
        }
    }
}
