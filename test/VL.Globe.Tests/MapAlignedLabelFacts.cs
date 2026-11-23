using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Globe.Tests;

// Point labels turned with the map (text-rotation-alignment map): the collision box turned to the map's
// east as projected, as GL JS's _projectCollisionBox. The pictures are MapLibre's three
// *-rotation-alignment/*-symbol-placement-point fixtures; this is the collision they do not test.
public class MapAlignedLabelFacts
{
    const float Width = 800, Height = 600, Distance = 1;
    static readonly Matrix Projection = Matrix.PerspectiveFovRH(0.8f, Width / Height, 0.01f, 100);

    // two labels 100 x 20 px, one 30 px above the other on the screen when the map is not turned
    static GpuTile TwoLabels(SymbolFlags flags)
    {
        float up = 30 * MathF.Tan(0.4f) / (Height / 2);   // 30 px at the distance 1
        LabelInfo Label(string text, float y) => new(0, new Vector3(0, y, 0), new SymbolBox(-50, -10, 50, 10), 0, 4, null, 0, 0, flags)
        {
            Key = CrossTileSymbolIndex.KeyOf(text), AnchorX = 4096, AnchorY = (int)(4096 - y * 1000),
        };
        var tile = new GpuTile
        {
            Bucket = new VectorBucket { Key = new TileKey(14, 100, 200), OverscaledZoom = 14 },
            ByLayer = new(),
            Labels = new List<LabelInfo> { Label("Kyoto", 0), Label("Nara", up) },
            Placed = new float[8],
        };
        new CrossTileSymbolIndex().AddLayer(0, new[] { tile });
        return tile;
    }

    static int Placed(GpuTile tile, Matrix view)
    {
        var placement = new LabelPlacement();
        var layers = new List<LabelPlacement.Layer> { new(0, new List<(GpuTile, Matrix)> { (tile, Matrix.Translation(0, 0, -Distance)) }) };
        placement.Place(layers, view, Projection, Width, Height, Distance, flat: true);
        int shown = 0;
        foreach (var id in tile.LabelIds!) if (placement.Placements[id].Text) shown++;
        return shown;
    }

    [Fact]
    public void A_box_turned_with_the_map_keeps_clear_of_its_neighbour()
    {
        // the map turned a quarter: the two anchors side by side on the screen, 30 px apart
        var quarter = Matrix.RotationZ(MathF.PI / 2);
        Assert.Equal(2, Placed(TwoLabels(SymbolFlags.None), Matrix.Identity));
        Assert.Equal(2, Placed(TwoLabels(SymbolFlags.TextRotateWithMap), Matrix.Identity));
        // in the viewport the boxes stay 100 wide: side by side they meet
        Assert.Equal(1, Placed(TwoLabels(SymbolFlags.None), quarter));
        // turned with the map they are 20 wide and 100 tall, side by side as they were one above the other
        Assert.Equal(2, Placed(TwoLabels(SymbolFlags.TextRotateWithMap), quarter));
    }

    [Fact]
    public void East_on_the_screen_is_the_map_s_east_turned_by_the_view()
    {
        var world = Matrix.Translation(0, 0, -Distance);
        foreach (var degrees in new[] { 0f, 45f, 90f, 200f })
        {
            var wvp = world * Matrix.RotationZ(MathUtil.DegreesToRadians(degrees)) * Projection;
            var clip = Vector4.Transform(new Vector4(Vector3.Zero, 1), wvp);
            var (cos, sin) = LabelPlacement.EastOnScreen(Vector3.Zero, Vector3.Zero, clip, wvp, flat: true, Width, Height);
            // a turn of the view counter-clockwise turns east counter-clockwise: up the screen, y down
            float a = MathUtil.DegreesToRadians(degrees);
            Assert.Equal(MathF.Cos(a), cos, 4);
            Assert.Equal(-MathF.Sin(a), sin, 4);
        }
    }
}
