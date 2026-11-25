using System.Globalization;
using NetTopologySuite.Features;

namespace VL.Globe.Tests.Camera;

// ROADMAP 6d (docs/DESIGN-PICK.md): Pick finds what a VectorTiles layer draws, as GL JS's queryRenderedFeatures.
// Styles with inline GeoJSON sources, so no network; the view's centre is picked.
public class VectorPickFacts
{
    const double Lon = 139.7615, Lat = 35.681;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    static GlobeView View(double zoom, double pitch = 0)
    {
        const double FovY = Math.PI / 4;
        var v = new GlobeView { MinHeight = 1, Width = 800, Height_px = 600, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (800.0 / 600); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(string.Format(C, "#{0}/{1}/{2}/0/{3}", zoom, Lat, Lon, pitch)));
        return v;
    }

    static string Box(double lon, double lat, double half, string properties) => string.Format(C,
        "{{\"type\":\"Feature\",\"properties\":{{{4}}},\"geometry\":{{\"type\":\"Polygon\",\"coordinates\":[[[{0},{1}],[{2},{1}],[{2},{3}],[{0},{3}],[{0},{1}]]]}}}}",
        lon - half, lat - half, lon + half, lat + half, properties);

    static string Line(double lat, string properties) => string.Format(C,
        "{{\"type\":\"Feature\",\"properties\":{{{1}}},\"geometry\":{{\"type\":\"LineString\",\"coordinates\":[[139.75,{0}],[139.77,{0}]]}}}}", lat, properties);

    static GlobeLayer Layer(string features, string layers)
    {
        var json = "{\"version\":8,\"sources\":{\"s\":{\"type\":\"geojson\",\"data\":{\"type\":\"FeatureCollection\",\"features\":[" + features + "]}}},\"layers\":[" + layers + "]}";
        var service = new VectorTileService("pick", System.Text.Encoding.UTF8.GetBytes(json));
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (service.Document is null && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.NotNull(service.Document);
        return new GlobeLayer { Name = "tiles", Vector = service };
    }

    static Picking.Found? PickCentre(GlobeView view, GlobeLayer input, float tolerance = 0) => Picking.Pick(view, input, 0, 0, tolerance, new VectorPicker());

    [Fact]
    public void AFillUnderTheCursorComesBackWithItsProperties()
    {
        var input = Layer(Box(Lon, Lat, 0.001, "\"name\":\"Marunouchi\",\"class\":\"office\""),
            "{\"id\":\"buildings\",\"type\":\"fill\",\"source\":\"s\",\"paint\":{\"fill-color\":\"#f00\"}}");
        var hit = PickCentre(View(16), input);
        Assert.NotNull(hit);
        Assert.Equal("buildings", hit!.Layer);
        Assert.Equal("Marunouchi", hit.Feature.Attributes["name"]);
        Assert.Equal("office", hit.Feature.Attributes["class"]);
        Assert.Equal("buildings", hit.Feature.Attributes["$layer"]);
        Assert.True(hit.Feature.Geometry.Contains(new NetTopologySuite.Geometries.Point(Lon, Lat)), "its geometry in degrees");
    }

    [Fact]
    public void NothingOutsideThePolygon()
    {
        var input = Layer(Box(Lon + 0.01, Lat, 0.001, ""), "{\"id\":\"b\",\"type\":\"fill\",\"source\":\"s\"}");
        Assert.Null(PickCentre(View(16), input));
    }

    [Fact]
    public void ALineIsHitWithinItsWidthAndMissedPastIt()
    {
        // 20 px wide at zoom 16: half of it is 10 px; a pixel there is ~1.5 m
        var view = View(16);
        double metresPerPixel = view.GroundResolution();
        double near = Lat + 6 * metresPerPixel / 110574.0, far = Lat + 20 * metresPerPixel / 110574.0;
        string layer = "{\"id\":\"road\",\"type\":\"line\",\"source\":\"s\",\"paint\":{\"line-width\":20}}";
        Assert.NotNull(PickCentre(view, Layer(Line(near, "\"name\":\"a\""), layer)));
        Assert.Null(PickCentre(view, Layer(Line(far, "\"name\":\"a\""), layer)));
    }

    [Fact]
    public void ALayersFilterAndZoomRangeAreRespected()
    {
        var both = Box(Lon, Lat, 0.001, "\"kind\":\"park\"");
        Assert.Null(PickCentre(View(16), Layer(both,
            "{\"id\":\"b\",\"type\":\"fill\",\"source\":\"s\",\"filter\":[\"==\",[\"get\",\"kind\"],\"school\"]}")));
        Assert.NotNull(PickCentre(View(16), Layer(both,
            "{\"id\":\"b\",\"type\":\"fill\",\"source\":\"s\",\"filter\":[\"==\",[\"get\",\"kind\"],\"park\"]}")));
        Assert.Null(PickCentre(View(14), Layer(both, "{\"id\":\"b\",\"type\":\"fill\",\"source\":\"s\",\"minzoom\":15}")));
        Assert.Null(PickCentre(View(16), Layer(both, "{\"id\":\"b\",\"type\":\"fill\",\"source\":\"s\",\"layout\":{\"visibility\":\"none\"}}")));
    }

    [Fact]
    public void TheUpperOfTwoLayersFirst()
    {
        var input = Layer(Box(Lon, Lat, 0.001, ""),
            "{\"id\":\"lower\",\"type\":\"fill\",\"source\":\"s\"},{\"id\":\"upper\",\"type\":\"line\",\"source\":\"s\",\"paint\":{\"line-width\":4000}}");
        Assert.Equal("upper", PickCentre(View(16), input)!.Layer);
    }

    // Tilted at street level: a tall building between the eye and the ground point under the cursor is hit by its
    // wall, before a lower one standing on that point
    [Fact]
    public void ATallBuildingInFrontIsHitByItsWallTheNearerFirst()
    {
        var view = View(17, pitch: 60);
        var (eye, _, _) = view.Pose();
        double eyeLon = Math.Atan2(eye.X, eye.Z) * 180 / Math.PI, eyeLat = Math.Asin(eye.Y / eye.Length) * 180 / Math.PI;
        double eyeHeight = (eye.Length / view.Radius - 1) * GlobeCoordinates.EarthRadius;
        // where the cursor's ray is 40 m up: that fraction of the way from the ground point to under the eye
        double t = 40 / eyeHeight;
        double frontLon = Lon + (eyeLon - Lon) * t, frontLat = Lat + (eyeLat - Lat) * t;
        string Building(double lon, double lat, double height, string name) => Box(lon, lat, 0.00008, $"\"h\":{height.ToString(C)},\"name\":\"{name}\"");
        string layer = "{\"id\":\"3d\",\"type\":\"fill-extrusion\",\"source\":\"s\",\"paint\":{\"fill-extrusion-height\":[\"get\",\"h\"]}}";

        var behind = Layer(Building(Lon, Lat, 10, "behind"), layer);
        Assert.Equal("behind", PickCentre(view, behind)!.Feature.Attributes["name"]);   // alone, the ground point's roof
        var both = Layer(Building(Lon, Lat, 10, "behind") + "," + Building(frontLon, frontLat, 80, "front"), layer);
        Assert.Equal("front", PickCentre(view, both)!.Feature.Attributes["name"]);
        var low = Layer(Building(frontLon, frontLat, 20, "low"), layer);   // too low to stand in the ray there
        Assert.Null(PickCentre(view, low));
    }
}
