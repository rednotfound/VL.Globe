using NetTopologySuite.Geometries;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests.Gpu;

// A line seen edge-on, near the globe's limb, was narrower on the screen than a pixel and broke into dots
// (the 45 N parallel west of France, graticule help, whole globe; found 2026-11-25 with GraticuleProbe).
// Now held a pixel wide and faded: every row the line crosses has a lit pixel.
public class EdgeOnLineFacts
{
    [Fact]
    public void AMeridianAtTheLimbIsNotBrokenIntoDots()
    {
        const int W = 400, H = 400;
        const double FovY = Math.PI / 4;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash("#1.2/0/0/0/0"));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, 1f, near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);

        // a meridian 3 degrees inside the limb (seen from |eye| radii the limb is acos(1/|eye|) from the
        // centre), from 50 S to 50 N, a point a degree, as the Graticule's lines
        double lon = Math.Acos(1 / eye.Length) * 180 / Math.PI - 3;
        var points = Enumerable.Range(-50, 101).Select(lat => new Coordinate(lon, lat)).ToArray();
        var line = new GeometryFactory().CreateLineString(points);
        var white = new Color4(1, 1, 1, 1);
        using var service = new VectorTileService("meridian", System.Text.Encoding.UTF8.GetBytes(
            Engine.Styling.GeometryStyle.Json(new Geometry[] { line }, new GlobeVectorStyle(white, white, 1))));
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

        // where the line is on the screen: the rows between its projections at 30 S and 30 N
        int Row(double lat)
        {
            var p = GlobeCoordinates.ToGlobe(lon, lat, 0, 1f) - eye.ToVector3();
            var c = Vector3.TransformCoordinate(Vector3.TransformCoordinate(p, view), projection);
            return (int)Math.Round((1 - c.Y) / 2 * H);
        }
        int top = Math.Min(Row(30), Row(-30)), bottom = Math.Max(Row(30), Row(-30));
        Assert.True(bottom - top > 50, $"the line at {lon:F1} E spans rows {top}..{bottom}");
        var broken = new List<int>();
        for (int y = top; y <= bottom; y++)
        {
            int brightest = 0;
            for (int x = W / 2; x < W; x++) brightest = Math.Max(brightest, px[(y * W + x) * 4]);
            // the background is 51; faint is right - edge-on the line covers little of a pixel - but never nothing
            if (brightest <= 52) broken.Add(y);
        }
        Assert.True(broken.Count == 0, $"{broken.Count} of {bottom - top + 1} rows without the line, e.g. {string.Join(",", broken.Take(10))}");
    }
}
