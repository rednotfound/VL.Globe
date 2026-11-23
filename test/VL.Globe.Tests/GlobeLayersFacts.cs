using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using SkiaSharp;
using Stride.Core.Mathematics;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// ROADMAP 0.1 item 4: FeatureLayer / Geometry / VectorStyle, draped into the tiles.
public class GlobeLayersFacts
{
    static readonly GeometryFactory F = new();
    static readonly TileKey T = new(10, 909, 403);

    static Polygon Square(TileKey t)
    {
        double n = t.TilesPerSide;
        double w = TileGeometry.LongitudeOf(t.X / n), e = TileGeometry.LongitudeOf((t.X + 1) / n);
        double no = TileGeometry.LatitudeOf(t.Y / n), so = TileGeometry.LatitudeOf((t.Y + 1) / n);
        return F.CreatePolygon(new[] { new Coordinate(w, so), new Coordinate(e, so), new Coordinate(e, no), new Coordinate(w, no), new Coordinate(w, so) });
    }

    [Fact]
    public void LaterLayersAreDrawnOverEarlierOnes()
    {
        var blue = new TileOverlay.Style(SKColors.Transparent, 0, new SKColor(0, 0, 255));
        var red = new TileOverlay.Style(SKColors.Transparent, 0, new SKColor(255, 0, 0));
        var sq = new Geometry[] { Square(T) };
        var px = TileOverlay.Rasterize(T, new[] { ((IReadOnlyList<Geometry>)sq, blue), (sq, red) }, 32)!;
        Assert.Equal(255, px[(16 * 32 + 16) * 4]);       // red on top
        Assert.Equal(0, px[(16 * 32 + 16) * 4 + 2]);
    }

    [Fact]
    public void ALayerChangesByContentNotByIdentity()
    {
        // FromWKT and Cons hand over new objects every frame: the same coordinates must not redraw
        var a = new GlobeLayer(); a.Set(new[] { Square(T) }, null, true, "x");
        var b = new GlobeLayer(); b.Set(new[] { Square(T) }, null, true, "x");
        Assert.Equal(a.Signature(), b.Signature());
        b.Set(new[] { Square(new TileKey(10, 910, 403)) }, null, true, "x");
        Assert.NotEqual(a.Signature(), b.Signature());
        var c = new GlobeLayer(); c.Set(new[] { Square(T) }, GlobeStyleNodes.VectorStyle(lineWidth: 5), true, "x");
        Assert.NotEqual(a.Signature(), c.Signature());     // a style change redraws too
    }

    [Fact]
    public void FeatureLayerSkipsNullAndEmptyGeometry()
    {
        var node = new GlobeFeatureLayerNode();
        var features = new IFeature[] { new Feature(Square(T), new AttributesTable()), new Feature(null, new AttributesTable()), new Feature(F.CreatePolygon(), new AttributesTable()) };
        var layer = node.Update(features);
        Assert.Single(layer.Geometries);
    }

    // One Input, layers composed by Group (docs/DESIGN-NODES.md): the globe draws the leaves in order.
    [Fact]
    public void AGroupIsItsLayersInOrderLeftmostFirst()
    {
        var osm = new OpenStreetMapNode().Update();
        var shapes = new GlobeGeometryLayerNode().Update(new[] { Square(T) });
        var hidden = new GlobeGeometryLayerNode().Update(new[] { Square(T) }, enabled: false);
        var inner = new GlobeGroupSpectralNode().Update(new[] { shapes, hidden });
        var pins = global::VL.Lib.Collections.Spread<GlobeLayer>.Empty.ToBuilder(); pins.Add(osm); pins.Add(inner);   // the Group's two inputs
        var group = new GlobeGroupNode().Update(pins.ToSpread());
        var leaves = group.Leaves().ToList();
        Assert.Equal(new[] { osm, shapes }, leaves);
        Assert.NotNull(leaves[0].Raster);
        Assert.Null(leaves[1].Raster);
    }

    [Fact]
    public void AGroupSwitchedOffHidesAllOfIt()
    {
        var osm = new OpenStreetMapNode().Update();
        var group = new GlobeGroupSpectralNode().Update(new[] { osm }, enabled: false);
        Assert.Empty(group.Leaves());
        Assert.Empty(new OpenStreetMapNode().Update(enabled: false).Leaves());
    }

    [Fact]
    public void AnUnwiredStyleIsTheDefault()
    {
        var s = GlobeStyleNodes.VectorStyle();
        Assert.Equal(new Color4(1, 0, 0, 1), s.LineColor);
        Assert.Equal(0.3f, s.FillColor.A, 3);
        Assert.Equal(2f, s.LineWidth);
    }
}
