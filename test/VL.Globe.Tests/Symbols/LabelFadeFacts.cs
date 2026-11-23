using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Globe.Tests.Symbols;

// GL JS's label fades (LabelFades): cross-tile IDs, the pauseable placement, its commit, the vertices'
// states. Tiles are built on the CPU only - no device: a bucket, its labels and a Placed array.
public class LabelFadeFacts
{
    const float Width = 800, Height = 600, Distance = 1;
    static readonly Matrix Projection = Matrix.PerspectiveFovRH(0.8f, Width / Height, 0.01f, 100);

    // one point label of four vertices at the middle of the screen, its anchor at (x, y) in GL JS units
    static GpuTile Tile(TileKey key, string text, int x, int y, SymbolFlags flags = SymbolFlags.None)
    {
        var label = new LabelInfo(0, Vector3.Zero, new SymbolBox(-20, -10, 20, 10), 0, 4, null, 0, 0, flags)
        {
            Key = CrossTileSymbolIndex.KeyOf(text), AnchorX = x, AnchorY = y,
        };
        return new GpuTile
        {
            Bucket = new VectorBucket { Key = key, OverscaledZoom = key.Zoom },
            ByLayer = new(),
            Labels = new List<LabelInfo> { label },
            Placed = new float[4],
        };
    }

    static List<LabelPlacement.Layer> Layer(params GpuTile[] tiles)
    {
        var list = new List<(GpuTile, Matrix)>();
        foreach (var t in tiles) list.Add((t, Matrix.Translation(0, 0, -Distance)));
        return new List<LabelPlacement.Layer> { new(0, list) };
    }

    static LabelFades.Did Step(LabelFades fades, List<LabelPlacement.Layer> layers, double now, D3? eye = null)
        => fades.Step(layers, Matrix.Identity, Projection, Width, Height, Distance, flat: true, eye, now);

    [Fact]
    public void A_placed_label_fades_in_over_the_fade_duration()
    {
        var fades = new LabelFades();
        var tile = Tile(new TileKey(14, 100, 200), "Kyoto", 4096, 4096);
        var did = Step(fades, Layer(tile), 0);
        Assert.True(did.HasFlag(LabelFades.Did.Committed));
        // placed, from opacity 0: the shader adds the time since the commit, in fades
        Assert.Equal(1f, tile.Placed![0]);
        Assert.Equal(0.5f, fades.FadeChange(150), 3);
        Assert.Equal(1f, fades.FadeChange(300), 3);
    }

    [Fact]
    public void A_fade_does_not_begin_with_a_blink()
    {
        // the renderer's frame: the states written by a commit drawn with the fade change of that commit.
        // With the change taken before the step, a label placed at 500 was drawn whole for one frame
        // (the time since the commit at 0) and then fell to 0 to fade in (the user, 2026-11-22)
        var renderer = new VectorRenderer();
        var frame = new VectorRenderer.Frame { CameraToCenter = Distance };
        var kyoto = Tile(new TileKey(14, 100, 200), "Kyoto", 4096, 4096);
        frame.Labels.AddRange(Layer(kyoto));
        renderer.StepLabels(frame, Matrix.Identity, Projection, Width, Height, true, 0, out _);
        Assert.Equal(0f, LabelFades.Shown(kyoto.Placed![0], renderer.StepLabels(frame, Matrix.Identity, Projection, Width, Height, true, 1, out _)), 2);

        var nara = Tile(new TileKey(14, 101, 200), "Nara", 4096, 4096, SymbolFlags.TextAllowOverlap);
        frame.Labels.Clear();
        frame.Labels.AddRange(Layer(kyoto, nara));
        float change = renderer.StepLabels(frame, Matrix.Identity, Projection, Width, Height, true, 500, out var did);
        Assert.True(did.HasFlag(LabelFades.Did.Committed));
        Assert.Equal(1f, LabelFades.Shown(kyoto.Placed[0], change));   // shown all along
        Assert.Equal(0f, LabelFades.Shown(nara.Placed![0], change));   // from 0, not whole first
        Assert.Equal(0.5f, LabelFades.Shown(nara.Placed[0], renderer.StepLabels(frame, Matrix.Identity, Projection, Width, Height, true, 650, out _)), 2);
        // what the old order drew in that first frame: whole
        Assert.Equal(1f, LabelFades.Shown(nara.Placed[0], 500 / 300f));
    }

    [Fact]
    public void With_fades_off_a_label_is_whole_at_once()
    {
        var fades = new LabelFades { FadeDuration = 0 };
        var tile = Tile(new TileKey(14, 100, 200), "Kyoto", 4096, 4096);
        Step(fades, Layer(tile), 0);
        // 1 + 0 + a fade change of 1: whole in the frame it is placed
        Assert.Equal(1f, tile.Placed![0]);
        Assert.Equal(1f, fades.FadeChange(0));
    }

    [Fact]
    public void The_same_text_at_the_same_place_in_a_child_tile_is_the_same_symbol()
    {
        // the parent's (2048, 2048) is the child's (4096, 4096) in tile (2x, 2y)
        var index = new CrossTileSymbolIndex();
        var parent = Tile(new TileKey(14, 100, 200), "Kyoto", 2048, 2048);
        var child = Tile(new TileKey(15, 200, 400), "Kyoto", 4096, 4096);
        var other = Tile(new TileKey(15, 200, 400), "Osaka", 4096, 4096);
        index.AddLayer(0, new[] { parent });
        index.AddLayer(0, new[] { parent, child });
        Assert.Equal(parent.LabelIds![0], child.LabelIds![0]);
        index.AddLayer(0, new[] { parent, other });
        Assert.NotEqual(parent.LabelIds![0], other.LabelIds![0]);
    }

    [Fact]
    public void A_label_carries_its_opacity_into_the_tile_that_replaces_its_tile()
    {
        // zooming in: the child replaces the parent; its label goes on shown, not faded in from nothing
        var fades = new LabelFades();
        var parent = Tile(new TileKey(14, 100, 200), "Kyoto", 2048, 2048);
        var child = Tile(new TileKey(15, 200, 400), "Kyoto", 4096, 4096);
        Step(fades, Layer(parent), 0);
        Step(fades, Layer(parent), 400);   // at rest: nothing more
        Step(fades, Layer(child), 500);
        // the parent's state at once (placed, from 0 at the commit at 0: whole by now), then placed
        // again from it: opacity 1
        Assert.Equal(2f, child.Placed![0]);

        // a new label, nowhere before: hidden until placed, then fading in from 0
        var fresh = Tile(new TileKey(15, 201, 400), "Nara", 4096, 4096, SymbolFlags.TextAllowOverlap);   // over the other
        Step(fades, Layer(child, fresh), 600);
        Assert.Equal(0f, fresh.Placed![0]);   // the placement is still recent (100 ms after a commit)
        Step(fades, Layer(child, fresh), 800);
        Assert.Equal(1f, fresh.Placed![0]);
    }

    [Fact]
    public void A_label_in_a_coarser_tile_drawn_beside_the_finer_one_is_a_duplicate()
    {
        var fades = new LabelFades { FadeDuration = 0 };
        var parent = Tile(new TileKey(14, 100, 200), "Kyoto", 2048, 2048);
        var child = Tile(new TileKey(15, 200, 400), "Kyoto", 4096, 4096);
        Step(fades, Layer(parent, child), 0);
        Assert.Equal(1f, child.Placed![0]);    // the finer tile's, placed first
        Assert.Equal(0f, parent.Placed![0]);   // hidden
    }

    [Fact]
    public void A_placement_pauses_and_goes_on_in_the_next_frame()
    {
        var fades = new LabelFades { FrameBudget = 0 };   // a pause after every tile
        var a = Tile(new TileKey(14, 100, 200), "A", 1000, 1000);
        var b = Tile(new TileKey(14, 101, 200), "B", 1000, 1000);
        var c = Tile(new TileKey(14, 102, 200), "C", 1000, 1000);
        var layers = Layer(a, b, c);
        Assert.False(Step(fades, layers, 0).HasFlag(LabelFades.Did.Committed));
        Assert.False(Step(fades, layers, 16).HasFlag(LabelFades.Did.Committed));
        Assert.False(Step(fades, layers, 33).HasFlag(LabelFades.Did.Committed));   // the last tile placed, then the pause
        var did = Step(fades, layers, 50);
        Assert.True(did.HasFlag(LabelFades.Did.Committed));
        // they collide (all at the middle of the screen): the first tile's is shown
        Assert.Equal(1f, a.Placed![0]);
        Assert.Equal(0f, b.Placed![0]);
    }

    [Fact]
    public void A_pan_that_leaves_the_rotation_only_view_unchanged_still_moves_the_glyphs_along_lines()
    {
        // camera-relative: a pan at street level moves the eye, and the view (a rotation) can stay bit
        // for bit the same. The glyphs along lines stood still in such frames and caught up in steps.
        var fades = new LabelFades();
        var layers = Layer(Tile(new TileKey(14, 100, 200), "Kyoto", 4096, 4096));
        var eye = new D3(0.1, 0.2, 0.97);
        Step(fades, layers, 0, eye);
        Assert.False(Step(fades, layers, 16, eye).HasFlag(LabelFades.Did.Lines));
        var panned = eye with { X = eye.X + 1e-7 };   // about half a metre on the unit globe
        var did = Step(fades, layers, 33, panned);
        Assert.True(did.HasFlag(LabelFades.Did.Lines));
        Assert.False(did.HasFlag(LabelFades.Did.Began));   // placed again only 300 ms after the last commit
        Assert.True(Step(fades, layers, 320, panned).HasFlag(LabelFades.Did.Began));
    }
}
