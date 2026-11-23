using System.Text.Json;
using NetTopologySuite.Geometries;
using Stride.Core.Mathematics;

namespace VL.Globe.Tests.Styling;

// Shapes over vector tiles (2026-11-23): a Geometry layer after VectorTiles is drawn by the GPU from a
// style of its own (GeometryStyle). The style must parse strictly and build into fill and line ranges.
public class GeometryStyleFacts
{
    static readonly GeometryFactory F = new();

    [Fact]
    public void AShapesStyleParsesStrictlyAndBuildsFillsAndLines()
    {
        var square = F.CreatePolygon(new[] { new Coordinate(139.75, 35.67), new Coordinate(139.77, 35.67), new Coordinate(139.77, 35.69), new Coordinate(139.75, 35.69), new Coordinate(139.75, 35.67) });
        var line = F.CreateLineString(new[] { new Coordinate(139.74, 35.68), new Coordinate(139.78, 35.685) });
        var point = F.CreatePoint(new Coordinate(139.76, 35.68));
        var json = GeometryStyle.Json(new Geometry[] { square, line, point }, new GlobeVectorStyle(new Color4(1, 0, 0, 0.3f), new Color4(1, 0, 0, 1), 3));
        using var parsed = JsonDocument.Parse(json);
        var doc = MapStyleDocument.Parse(parsed.RootElement.Clone(), lenient: false);
        Assert.Equal(3, doc.Layers.Count);
        Assert.Empty(doc.Skipped);

        var (sourceId, source) = doc.Sources.First();
        var layers = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null).Select(l => l!).ToList();
        Assert.Equal(3, layers.Count);
        // the z14 tile with Tokyo Station in it
        var key = new TileKey(14, 14552, 6451);
        var bucket = VectorBucketBuilder.Build(doc, source, layers, false, key, true, null, out bool complete);
        Assert.True(complete);
        Assert.NotNull(bucket);
        Assert.Contains(bucket!.Ranges, r => r.Kind == RangeKind.Fill);   // the fill
        Assert.Contains(bucket.Ranges, r => r.Kind == RangeKind.Line);         // the outline and the line
        Assert.NotEmpty(bucket.CircleVertices);                                 // the point
        bucket.Release();
    }
}
