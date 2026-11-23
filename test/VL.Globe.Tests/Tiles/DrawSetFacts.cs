using Stride.Core.Mathematics;

namespace VL.Globe.Tests.Tiles;

public class DrawSetFacts
{
    static readonly TileKey P = new(1, 0, 0);
    static readonly TileKey[] Kids = Enumerable.Range(0, 4).Select(P.Child).ToArray();

    static List<TileKey> Sorted(IEnumerable<TileKey> t) => t.OrderBy(k => k.Zoom).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();

    [Fact]
    public void AMissingTileIsStoodInForByItsParentUnderneath()
    {
        var d = DrawSet.Resolve(new[] { Kids[0] }, t => t == P);
        Assert.Empty(d.Top);
        Assert.Equal(new[] { (Kids[0], P) }, d.StandIns);
    }

    [Fact]
    public void AllChildrenArrivedReplaceTheParent()
    {
        var d = DrawSet.Resolve(Kids, t => t == P || Kids.Contains(t));
        Assert.Equal(Sorted(Kids), Sorted(d.Top));
        Assert.Empty(d.StandIns);
    }

    [Fact]
    public void ThreeOfFourAreDrawnAndOnlyTheFourthSquareBorrowsTheParent()
    {
        // First the parent was drawn ALONE until all four arrived (a zoom-3 "white flash" over half the
        // screen); then whole and sunk beneath them ("下降了一点，然后抬起来", black cracks when tilted).
        var d = DrawSet.Resolve(Kids, t => t == P || t == Kids[0] || t == Kids[1] || t == Kids[2]);
        Assert.Equal(Sorted(Kids.Take(3)), Sorted(d.Top));
        Assert.Equal(new[] { (Kids[3], P) }, d.StandIns);
    }

    [Fact]
    public void StandInsNeverOverlapTheTopTilesOrEachOther()
    {
        // a deep mix: some grandchildren, a child, nothing in one quarter
        var k0 = W.Child(0);
        var loaded = new[] { k0.Child(1), k0.Child(2), W.Child(1), Coarse }.ToHashSet();
        var d = DrawSet.Resolve(new[] { W }, loaded.Contains);
        var squares = d.Top.Concat(d.StandIns.Select(x => x.Square)).ToList();
        foreach (var a in squares)
            foreach (var b in squares)
                if (a != b) Assert.False(a.IsAncestorOf(b), $"{a} overlaps {b}");
        Assert.True(Fade.Covers(W, squares), "the squares together cover the wanted tile");
    }

    [Fact]
    public void NothingArrivedDrawsNothing()
    {
        var d = DrawSet.Resolve(Kids, _ => false);
        Assert.Empty(d.Top);
        Assert.Empty(d.StandIns);
    }

    // Zooming out fast (the user, 2026-11-21): the coarser tiles now wanted were evicted while the
    // camera sat low. The finer tiles just drawn are still loaded - they stand in on Top, as
    // MapLibre's _retainLoadedChildren does, before any ancestor.
    static readonly TileKey W = new(5, 10, 6);
    static readonly TileKey Coarse = W.AncestorAt(2);

    [Fact]
    public void ZoomingOutDrawsTheLoadedChildrenNotACoarseAncestor()
    {
        var kids = Enumerable.Range(0, 4).Select(W.Child).ToHashSet();
        var d = DrawSet.Resolve(new[] { W }, t => t == Coarse || kids.Contains(t));
        Assert.Equal(Sorted(kids), Sorted(d.Top));
        Assert.Empty(d.StandIns);
    }

    [Fact]
    public void ChildrenAndGrandchildrenTogetherStandIn()
    {
        var k3 = W.Child(3);
        var loaded = new[] { W.Child(0), W.Child(1), W.Child(2), k3.Child(0), k3.Child(1), k3.Child(2), k3.Child(3) }.ToHashSet();
        var d = DrawSet.Resolve(new[] { W }, t => t == Coarse || loaded.Contains(t));
        Assert.Equal(7, d.Top.Count);
        Assert.Empty(d.StandIns);
    }

    [Fact]
    public void ChildrenWithAHoleAreDrawnAndTheHoleBorrowsTheAncestor()
    {
        var loaded = new[] { W.Child(0), W.Child(1), W.Child(2) }.ToHashSet();
        var d = DrawSet.Resolve(new[] { W }, t => t == Coarse || loaded.Contains(t));
        Assert.Equal(Sorted(loaded), Sorted(d.Top));
        Assert.Equal(new[] { (W.Child(3), Coarse) }, d.StandIns);
    }

    [Fact]
    public void DescendantsAreSearchedOnlyAFewLevelsDown()
    {
        // four levels down is 256 tiles for one: not a stand-in, a different view
        var deep = new HashSet<TileKey>();
        void Add(TileKey t, int depth) { if (depth == 0) deep.Add(t); else for (int i = 0; i < 4; i++) Add(t.Child(i), depth - 1); }
        Add(W, 4);
        var d = DrawSet.Resolve(new[] { W }, t => t == Coarse || deep.Contains(t));
        Assert.Empty(d.Top);
        Assert.Equal(new[] { (W, Coarse) }, d.StandIns);
    }

    [Fact]
    public void TheNearestTilesAreRequestedFirst()
    {
        // a tilted view over Tokyo: the tile under the eye before the one toward the horizon
        var near = new TileKey(15, 29104, 12903);
        var far = new TileKey(15, 29104, 12880);
        var eye = GlobeCoordinates.ToGlobe(139.77, 35.68, 2000);
        Assert.Equal(new[] { near, far }, DrawSet.NearestFirst(new[] { far, near }, eye, 1f));
    }

    [Fact]
    public void RequestsAreCoarseFirst()
    {
        var wanted = new[] { new TileKey(9, 270, 254), new TileKey(9, 271, 254) };
        var req = DrawSet.Requests(wanted, _ => false);
        Assert.Equal(new TileKey(2, 2, 1), req[0]);
        Assert.Equal(3, req.Count);
        Assert.Empty(DrawSet.Requests(wanted, _ => true));
    }
}
