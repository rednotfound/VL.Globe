using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests.Probes;

// The 45 N parallel drawn dotted west of France (NOTES 2026-11-24, the graticule help, whole globe): the
// Graticule's GPU layer drawn offscreen, multisampled as vvvv draws, into a PNG to look at.
// VLGLOBE_PROBE=1 VLGLOBE_VIEW=#1.5/45/-5/0/0 VLGLOBE_OUT=<png>
public class GraticuleProbe
{
    [Probe]
    public void DrawTheGraticule()
    {
        string hash = Environment.GetEnvironmentVariable("VLGLOBE_VIEW") ?? "#1.5/45/-5/0/0";
        string output = Environment.GetEnvironmentVariable("VLGLOBE_OUT") ?? Path.Combine(Path.GetTempPath(), "graticule.png");
        const int W = 900, H = 700;
        const double FovY = Math.PI / 4;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / ((double)W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)W / H, near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);

        var layer = new GlobeGraticuleNode().Update(spacing: double.TryParse(Environment.GetEnvironmentVariable("VLGLOBE_SPACING"), out var sp) ? sp : 15);
        using var service = new VectorTileService("graticule", System.Text.Encoding.UTF8.GetBytes(Engine.Styling.GeometryStyle.Json(layer.Geometries, layer.Style)));
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
        var px = GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, W, H, false, tiles, MultisampleCount.X4);
        Png.Write(output, px, W, H);
        Console.WriteLine($"{tiles.Count} tiles, zoom {tiles.Min(t => t.Zoom)}..{tiles.Max(t => t.Zoom)}, written {output}");
    }
}
