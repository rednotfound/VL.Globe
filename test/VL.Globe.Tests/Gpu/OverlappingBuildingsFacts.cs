using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests.Gpu;

// Translucent buildings whose faces coincide - two parts of one height overlapping, roof on roof - were
// blended twice where they overlap, a darker patch (the user, 2026-11-24, Liberty's buildings at 80%). A
// layer's buildings now reach each pixel once: the colour pass writes depth and passes only nearer than
// what it has drawn there.
public class OverlappingBuildingsFacts
{
    // Whether two coinciding roofs come out at exactly one depth depends on the view's rounding: at some
    // views they did (doubled), at others one was a hair nearer (once) - patches, as seen. Several views.
    [Theory]
    [InlineData(15.5)]
    [InlineData(16)]
    [InlineData(16.5)]
    [InlineData(17)]
    public void TwoTranslucentBuildingsOfOneHeightAreNotDarkerWhereTheyOverlap(double mapZoom)
    {
        const int W = 240, H = 160;
        const double FovY = Math.PI / 4;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / ((double)W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(FormattableString.Invariant($"#{mapZoom}/35.681/139.7615/0/0")));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)W / H, near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);

        // the same share of the screen at every zoom: a unit of 0.0008 degrees at 15.5
        double u = 0.0008 * Math.Pow(2, 15.5 - mapZoom);
        string Box(double w, double e) => FormattableString.Invariant(
            $"{{\"type\":\"Feature\",\"properties\":{{}},\"geometry\":{{\"type\":\"Polygon\",\"coordinates\":[[[{w},{35.681 - u}],[{e},{35.681 - u}],[{e},{35.681 + u}],[{w},{35.681 + u}],[{w},{35.681 - u}]]]}}}}");
        string style = "{\"version\":8,\"sources\":{\"b\":{\"type\":\"geojson\",\"data\":{\"type\":\"FeatureCollection\",\"features\":["
            + Box(139.7615 - 1.5 * u, 139.7615 + 0.5 * u) + "," + Box(139.7615 - 0.5 * u, 139.7615 + 1.5 * u)
            + "]}}},\"layers\":[{\"id\":\"buildings\",\"type\":\"fill-extrusion\",\"source\":\"b\",\"paint\":{\"fill-extrusion-color\":\"#ff0000\",\"fill-extrusion-height\":40,\"fill-extrusion-opacity\":0.5}}]}";
        using var service = new VectorTileService("buildings", System.Text.Encoding.UTF8.GetBytes(style));
        service.Start();
        using var gpu = new GpuVectorTiles();
        double height = eye.Length - 1;
        double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / H * 6378137, v.Latitude) - 1;
        var wanted = tiles.ToHashSet();
        var until = DateTime.UtcNow.AddSeconds(30);
        int frame = 0, still = 0, last = -1;
        while (DateTime.UtcNow < until && still < 10)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, new[] { service }, 1f, eye, zoom, ++frame, height); return 0; });
            still = service.Document != null && gpu.Building == 0 && gpu.TilesBuilt == last ? still + 1 : 0;
            last = gpu.TilesBuilt;
            Thread.Sleep(30);
        }
        var px = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, W, H, false, tiles);

        // the roofs at 40 m: one building only, both, the other only
        int Red(double lon)
        {
            var p = GlobeCoordinates.ToGlobe(lon, 35.681, 40, 1f) - eye.ToVector3();
            var c = Vector3.TransformCoordinate(Vector3.TransformCoordinate(p, view), projection);
            int x = (int)Math.Round((c.X + 1) / 2 * W), y = (int)Math.Round((1 - c.Y) / 2 * H);
            Assert.InRange(x, 0, W - 1); Assert.InRange(y, 0, H - 1);
            return px[(y * W + x) * 4];
        }
        int left = Red(139.7615 - u), both = Red(139.7615), right = Red(139.7615 + u);
        Assert.True(left > 90, $"the roof is drawn: {left}");   // the background is 51
        Assert.InRange(right, left - 3, left + 3);
        Assert.True(Math.Abs(both - left) <= 3, $"where they overlap {both}, alone {left} and {right}: blended twice");
    }
}
