using System.Text.Json;
using Stride.Core.Mathematics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests.Lighting;

// ROADMAP 6b: the buildings lit by the scene's directional light, drawn offscreen on the GPU path,
// flat (x east, y north, z up) and seen from straight above - only roofs. A light from overhead lights
// a roof fully; one at the horizon leaves it at GL JS's 1 - intensity; GL JS's default sits between.
public class SceneLightFacts
{
    const int Size = 64;

    static double Roof(VectorRenderer.SceneLight? light)
    {
        using var json = JsonDocument.Parse("""
            { "version": 8, "center": [0, 0], "zoom": 16,
              "sources": { "s": { "type": "geojson", "data": { "type": "Polygon",
                "coordinates": [[[-0.01, -0.01], [0.01, -0.01], [0.01, 0.01], [-0.01, 0.01], [-0.01, -0.01]]] } } },
              "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#000000" } },
                { "id": "b", "type": "fill-extrusion", "source": "s", "paint": { "fill-extrusion-color": "#808080", "fill-extrusion-height": 20 } } ] }
            """);
        var doc = MapStyleDocument.Parse(json.RootElement.Clone(), lenient: true);
        var rgba = GpuFixtureRenderer.Render(doc, Size, Size, 1, light);
        int i = (Size / 2 * Size + Size / 2) * 4;
        return rgba[i];   // red, at the centre: the roof
    }

    [Fact]
    public void A_light_overhead_lights_a_roof_more_than_one_at_the_horizon()
    {
        var white = new Vector3(1, 1, 1);
        double overhead = Roof(new VectorRenderer.SceneLight(Vector3.UnitZ, white, 0.5f));
        double horizon = Roof(new VectorRenderer.SceneLight(Vector3.UnitX, white, 0.5f));
        double fallback = Roof(null);
        Assert.True(overhead > horizon + 20, $"overhead {overhead}, horizon {horizon}, default {fallback}");
        Assert.InRange(fallback, horizon, overhead + 1);
    }
}
