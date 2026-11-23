using System;

namespace VL.Globe.Engine.Tiles;

/// <summary>A Web Mercator tile: zoom, column, row (row 0 at the north, as OSM numbers them).</summary>
internal readonly record struct TileKey(int Zoom, int X, int Y)
{
    public int TilesPerSide => 1 << Zoom;

    public TileKey Parent => Zoom == 0 ? this : new TileKey(Zoom - 1, X >> 1, Y >> 1);

    /// <summary>The ancestor at <paramref name="zoom"/>, or this tile when it is already that coarse.</summary>
    public TileKey AncestorAt(int zoom)
    {
        if (zoom >= Zoom) return this;
        var shift = Zoom - zoom;
        return new TileKey(zoom, X >> shift, Y >> shift);
    }

    public bool IsAncestorOf(TileKey other)
        => other.Zoom > Zoom && other.AncestorAt(Zoom) == this;

    public TileKey Child(int i) => new(Zoom + 1, X * 2 + (i & 1), Y * 2 + (i >> 1));

    public override string ToString() => $"{Zoom}/{X}/{Y}";
}
