using NetTopologySuite.Geometries;
using Stride.Core.Mathematics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests.Gpu;

// FeatureLayer, Geometry and Graticule are GPU layers of their own (2026-11-24, the user: one way to draw
// them, with or without VectorTiles): each a style made by GeometryStyle, drawn in the Input's order.
public class ShapeLayerFacts
{
    static readonly GeometryFactory F = new();

    static Geometry Box(double w, double s, double e, double n) =>
        F.CreatePolygon(new[] { new Coordinate(w, s), new Coordinate(e, s), new Coordinate(e, n), new Coordinate(w, n), new Coordinate(w, s) });

    [Fact]
    public void ALaterShapeLayerIsDrawnOverAnEarlierOneAndNoVectorTilesAreNeeded()
    {
        const double FovY = Math.PI / 4, W = 200, H = 150;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash("#14/35.68/139.76/0/0"));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);

        // the same square twice, blue first, red second - as two Geometry nodes in a Group
        var square = new[] { Box(139.6, 35.5, 139.9, 35.8) };
        using var blue = new VectorTileService("blue", System.Text.Encoding.UTF8.GetBytes(
            Engine.Styling.GeometryStyle.Json(square, new GlobeVectorStyle(new Color4(0, 0, 1, 1), new Color4(0, 0, 1, 1), 1))));
        using var red = new VectorTileService("red", System.Text.Encoding.UTF8.GetBytes(
            Engine.Styling.GeometryStyle.Json(square, new GlobeVectorStyle(new Color4(1, 0, 0, 1), new Color4(1, 0, 0, 1), 1))));
        blue.Start(); red.Start();
        using var gpu = new GpuVectorTiles();
        double height = eye.Length - 1;
        double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / H * 6378137, v.Latitude) - 1;
        var wanted = tiles.ToHashSet();
        var until = DateTime.UtcNow.AddSeconds(30);
        int frame = 0, still = 0, lastBuilt = -1;
        while (DateTime.UtcNow < until && still < 10)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, new[] { blue, red }, 1f, eye, zoom, ++frame, height); return 0; });
            if (blue.Document != null && red.Document != null && gpu.Building == 0 && gpu.TilesBuilt == lastBuilt) still++; else still = 0;
            lastBuilt = gpu.TilesBuilt;
            Thread.Sleep(30);
        }
        var px = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false, tiles);
        int centre = ((int)(H / 2) * (int)W + (int)(W / 2)) * 4;
        Assert.True(px[centre] > 240 && px[centre + 2] < 15, $"centre {px[centre]},{px[centre + 1]},{px[centre + 2]}: red over blue");
    }
}
