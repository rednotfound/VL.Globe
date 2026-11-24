using NetTopologySuite.Geometries;
using SkiaSharp;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Globe.Tests.MapLibre;

namespace VL.Globe.Tests.Gpu;

// ROADMAP 6c (docs/DESIGN-RASTER-GPU.md): every raster layer is a GPU layer (the globe is its Background colour), drawn
// in the Input's order among the vector layers, each globe tile from its own image or an ancestor's
// through a rectangle. No network: the source's disk cache holds the only images, its URL refuses
// connections (port 9 on this machine), so every other tile fails at once and an ancestor stands in.
public class RasterLayerFacts
{
    static readonly GeometryFactory F = new();
    const double FovY = Math.PI / 4;

    static Geometry Box(double w, double s, double e, double n) =>
        F.CreatePolygon(new[] { new Coordinate(w, s), new Coordinate(e, s), new Coordinate(e, n), new Coordinate(w, n), new Coordinate(w, s) });

    // a source whose zoom-2 tiles (the fallback every wanted square asks for) are the image drawn by paint
    static GlobeTileSource SourceAtZoom2(Action<SKBitmap, int, int> paint, out string folder)
    {
        var key = "http://127.0.0.1:9/" + Guid.NewGuid().ToString("N") + "/{z}/{x}/{y}.png";
        var source = TileSourceFactory.Create(key, "test", key);
        folder = source.CacheFolder;
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
            {
                using var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                paint(bitmap, x, y);
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                source.Http.PersistentCache!.Add(new BruTile.TileIndex(x, y, 2), png.ToArray());
            }
        return source;
    }

    static VectorTileService Shape(string name, Color4 colour)
    {
        var square = new[] { Box(139.6, 35.5, 139.9, 35.8) };
        var service = new VectorTileService(name, System.Text.Encoding.UTF8.GetBytes(
            Engine.Styling.GeometryStyle.Json(square, new GlobeVectorStyle(colour, colour, 1))));
        service.Start();
        return service;
    }

    // the view of a MapLibre hash, its tiles drawn by the GPU until nothing changes, then rendered offscreen
    static byte[] Render(string hash, IReadOnlyList<GpuLayer> layers, int w = 200, int h = 150, MultisampleCount samples = MultisampleCount.None)
    {
        var v = new GlobeView { MinHeight = 1, Width = w, Height_px = h, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / ((double)w / h); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)w / h, near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, h, 1f);
        using var gpu = new GpuVectorTiles { RasterFade = 0 };
        double height = eye.Length - 1;
        double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / h * 6378137, v.Latitude) - 1;
        var wanted = tiles.ToHashSet();
        var until = DateTime.UtcNow.AddSeconds(30);
        int frame = 0, still = 0;
        string last = "";
        while (DateTime.UtcNow < until && still < 10)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, layers, 1f, eye, zoom, ++frame, height); return 0; });
            var items = gpu.Frame!.Items;
            string now = gpu.Status + "|" + items.Count + "|" + items.Count(i => i.Raster is not null);
            bool ready = layers.All(l => l.Vector is null || l.Vector.Document != null) && gpu.Building == 0 && !gpu.Status.Contains("GPU rasters: 0 loaded")
                && !System.Text.RegularExpressions.Regex.IsMatch(gpu.Status, @"[1-9]\d* loading");
            still = ready && now == last ? still + 1 : 0;
            last = now;
            Thread.Sleep(30);
        }
        Assert.True(still >= 10, "not settled: " + gpu.Status);
        return GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, w, h, false, tiles, samples);
    }

    static (byte r, byte g, byte b) Centre(byte[] px, int w = 200, int h = 150)
    {
        int i = (h / 2 * w + w / 2) * 4;
        return (px[i], px[i + 1], px[i + 2]);
    }

    static void Fill(SKBitmap b, SKColor c) => b.Erase(c);

    [Fact]
    public void ARasterLayerIsDrawnInItsPlaceAmongTheShapeLayers()
    {
        var red = SourceAtZoom2((b, _, _) => Fill(b, SKColors.Red), out var folder);
        using var blue = Shape("blue", new Color4(0, 0, 1, 1));
        try
        {
            // over the shape: red
            var (r, g, b) = Centre(Render("#14/35.68/139.76/0/0", new[] { new GpuLayer(blue), new GpuLayer(null, red) }));
            Assert.True(r > 240 && b < 15, $"raster over shape: {r},{g},{b}");
            // under it: blue
            (r, g, b) = Centre(Render("#14/35.68/139.76/0/0", new[] { new GpuLayer(null, red), new GpuLayer(blue) }));
            Assert.True(b > 240 && r < 15, $"shape over raster: {r},{g},{b}");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ARasterLayerIsDrawnAtItsOpacity()
    {
        var red = SourceAtZoom2((b, _, _) => Fill(b, SKColors.Red), out var folder);
        using var blue = Shape("blue", new Color4(0, 0, 1, 1));
        try
        {
            // blended in sRGB, as the render tests' pipeline draws: half of each
            var (r, g, b) = Centre(Render("#14/35.68/139.76/0/0", new[] { new GpuLayer(blue), new GpuLayer(null, red, 0.5f) }));
            Assert.InRange(r, 120, 135);
            Assert.InRange(b, 120, 135);
        }
        finally { Directory.Delete(folder, true); }
    }

    // A street-level square drawn from a zoom-2 image (the finest one here): its part of it, through the
    // rectangle - the stand-in and the overscale are the same case. The image says where it is sampled:
    // red = u, green = v, in the zoom-2 tile.
    [Fact]
    public void AnAncestorsImageIsSampledThroughTheSquaresRectangle()
    {
        var gradient = SourceAtZoom2((b, _, _) =>
        {
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    b.SetPixel(x, y, new SKColor((byte)x, (byte)y, 0));
        }, out var folder);
        try
        {
            const double lat = 35.68, lon = 139.76;
            var (r, g, _) = Centre(Render($"#14/{lat}/{lon}/0/0", new[] { new GpuLayer(null, gradient) }));
            double u = (lon + 180) / 360 * 4, mercator = Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360));
            double v = (1 - mercator / Math.PI) / 2 * 4;
            double expectR = (u - Math.Floor(u)) * 256 - 0.5, expectG = (v - Math.Floor(v)) * 256 - 0.5;
            Assert.InRange(r, expectR - 3, expectR + 3);
            Assert.InRange(g, expectG - 3, expectG + 3);
        }
        finally { Directory.Delete(folder, true); }
    }

    // GL JS extends the tiles' edge rows to the poles; so do the raster layers, on the background's grid
    [Fact]
    public void ARasterLayerCoversThePoleCap()
    {
        var red = SourceAtZoom2((b, _, _) => Fill(b, SKColors.Red), out var folder);
        try
        {
            var (r, g, b) = Centre(Render("#2/89.5/0/0/0", new[] { new GpuLayer(null, red) }));
            Assert.True(r > 240 && g < 15 && b < 15, $"the pole: {r},{g},{b}");
        }
        finally { Directory.Delete(folder, true); }
    }

    // vvvv's SceneWindow draws multisampled
    [Fact]
    public void ARasterLayerOverAShapeIsDrawnMultisampled()
    {
        var red = SourceAtZoom2((b, _, _) => Fill(b, SKColors.Red), out var folder);
        using var blue = Shape("blue", new Color4(0, 0, 1, 1));
        try
        {
            var (r, g, b) = Centre(Render("#14/35.68/139.76/0/0", new[] { new GpuLayer(blue), new GpuLayer(null, red) }, samples: MultisampleCount.X4));
            Assert.True(r > 240 && b < 15, $"multisampled: {r},{g},{b}");
        }
        finally { Directory.Delete(folder, true); }
    }
}
