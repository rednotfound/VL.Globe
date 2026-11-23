using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace VL.Globe.Tests.Camera;

// ROADMAP 0.1 item 4: Pick (Engine/Camera/Picking.cs) and Graticule.
public class PickFacts
{
    static readonly GeometryFactory F = new();
    static Polygon Square(double lon, double lat, double half) => F.CreatePolygon(new[]
    {
        new Coordinate(lon - half, lat - half), new Coordinate(lon + half, lat - half), new Coordinate(lon + half, lat + half),
        new Coordinate(lon - half, lat + half), new Coordinate(lon - half, lat - half),
    });

    [Fact]
    public void APointInsideAPolygonHitsIt()
    {
        var layer = new GlobeGeometryLayerNode().Update(new Geometry[] { Square(139.76, 35.68, 0.01) });
        Assert.NotNull(Picking.Pick(layer, 139.765, 35.685, 0));
        Assert.Null(Picking.Pick(layer, 139.80, 35.68, 0));
    }

    [Fact]
    public void ALineIsHitWithinTheToleranceInMetres()
    {
        var line = F.CreateLineString(new[] { new Coordinate(139.70, 35.68), new Coordinate(139.80, 35.68) });
        var layer = new GlobeGeometryLayerNode().Update(new Geometry[] { line });
        double tenMetresNorth = 35.68 + 10 / 110574.0;
        Assert.Null(Picking.Pick(layer, 139.75, tenMetresNorth, 5));
        Assert.NotNull(Picking.Pick(layer, 139.75, tenMetresNorth, 20));
    }

    [Fact]
    public void TheTopmostIsPickedAndAFeatureComesBackWithItsAttributes()
    {
        var below = new GlobeGeometryLayerNode().Update(new Geometry[] { Square(139.76, 35.68, 0.05) }, name: "below");
        var attributes = new AttributesTable { { "name", "Marunouchi" } };
        var above = new GlobeFeatureLayerNode().Update(new IFeature[] { new Feature(Square(139.76, 35.68, 0.01), attributes) }, name: "above");
        var group = new GlobeGroupSpectralNode().Update(new[] { below, above });
        var hit = Picking.Pick(group, 139.76, 35.68, 0)!.Value;
        Assert.Same(above, hit.layer);
        Assert.Equal("Marunouchi", hit.layer.Features![hit.index].Attributes["name"]);
        // where only the one below is
        Assert.Same(below, Picking.Pick(group, 139.80, 35.70, 0)!.Value.layer);
    }

    [Fact]
    public void AGraticuleEveryTenDegrees()
    {
        var lines = Picking.Graticule(10);
        // 36 meridians; parallels at 0, +-10 .. +-80
        Assert.Equal(36 + 1 + 2 * 8, lines.Count);
        Assert.Empty(Picking.Graticule(0));
    }
}
