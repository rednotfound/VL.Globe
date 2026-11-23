using System.Text.Json;
using Stride.Core.Mathematics;
using VL.Globe.Engine;
using VL.Globe.Tests.MapLibre;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// ROADMAP 4h (docs/DESIGN-OVERSCALED.md): shown far past its source's maxzoom, a fill's triangles - cut
// by the source tile's zoom - sag under the sphere by more than the lift, and the globe's own tiles,
// cut finely, hid them by depth: the dark globe showed through the style (the user, style 13, maxzoom 6).
// Drawn here offscreen over the globe tiles' depth, as vvvv draws it; without that depth no probe could
// fail this way.
public class OverscaledGroundFacts(ITestOutputHelper output)
{
    static string Tiles()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VL.Globe.nuspec")))
            {
                var assets = Path.Combine(d.FullName, "test", "fixtures", "maplibre", "v6.19.0", "_assets", "tiles");
                Assert.True(Directory.Exists(assets), $"fetch the fixtures first: .\\tools\\Fetch-MapLibreFixtures.ps1 ({assets})");
                return assets;
            }
        throw new DirectoryNotFoundException("no VL.Globe.nuspec above the test assembly");
    }

    // a source that stops at z0 (the fixtures' world tile), its water filled pure red, nothing else
    static byte[] Style() => System.Text.Encoding.UTF8.GetBytes($$"""
        {
          "version": 8,
          "sources": { "world": { "type": "vector", "maxzoom": 0, "tiles": [{{JsonSerializer.Serialize(Path.Combine(Tiles(), "{z}-{x}-{y}.mvt"))}}] } },
          "layers": [ { "id": "water", "type": "fill", "source": "world", "source-layer": "water", "paint": { "fill-color": "#ff0000" } } ]
        }
        """);

    [Theory]
    [InlineData("#11/30/-40/0/0")]      // mid-Atlantic, eye ~10 km
    [InlineData("#14/30/-40/0/40")]     // mid-Atlantic, eye ~1 km, tilted
    [InlineData("#17/30/-40/20/60")]    // mid-Atlantic, street level, tilted far
    public void AFillPastItsSourcesMaxzoomIsNotHiddenByTheGlobesTiles(string hash)
    {
        const double FovY = Math.PI / 4, W = 400, H = 300;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);

        using var service = new VectorTileService("overscaled", Style());
        service.Start();
        using var gpu = new GpuVectorTiles();
        double height = eye.Length - 1;
        double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / H * 6378137, v.Latitude) - 1;
        var wanted = tiles.ToHashSet();
        var until = DateTime.UtcNow.AddSeconds(30);
        int frame = 0, still = 0, lastBuilt = -1;
        while (DateTime.UtcNow < until && still < 10)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, new[] { service }, 1f, eye, zoom, ++frame, height); return 0; });
            if (service.Document != null && gpu.Building == 0 && gpu.TilesBuilt == lastBuilt) still++; else still = 0;
            lastBuilt = gpu.TilesBuilt;
            Thread.Sleep(30);
        }
        Assert.True(gpu.Frame!.Items.Count > 0, gpu.Status);

        int Red(byte[] px)
        {
            int n = 0;
            for (int i = 0; i < px.Length; i += 4) if (px[i] > 200 && px[i + 1] < 50 && px[i + 2] < 50) n++;
            return n;
        }
        int sky = 0;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (v.Pick(x + 0.5, y + 0.5) is null) sky++;
        int ground = (int)(W * H) - sky;
        int alone = Red(GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false));
        int overGlobe = Red(GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false, tiles));
        output.WriteLine($"{hash}: eye {height * 6378137:F0} m, {tiles.Count} globe tiles z{tiles.Min(t => t.Zoom)}..{tiles.Max(t => t.Zoom)}, ground {ground} px; red alone {alone}, over the globe's depth {overGlobe}");
        // the sea is all water: every pixel of ground red, with or without the globe's tiles in the depth
        Assert.True(alone > ground * 0.99, $"alone {alone} of {ground}");
        Assert.True(overGlobe > ground * 0.99, $"over the globe's tiles {overGlobe} of {ground}");
    }
    static string Box(double w, double s, double e, double n) => $"[[[{w},{s}],[{e},{s}],[{e},{n}],[{w},{n}],[{w},{s}]]]";

    [Fact]
    public void TheFarSideOfTheGlobeIsNotDrawn()
    {
        // the depth is the sphere's at the pixel, so the far side would pass the globe tiles' depth:
        // it is cut by the shader. A red box on the far side, a green one facing the eye
        var style = System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "version": 8,
              "sources": {
                "far": { "type": "geojson", "data": { "type": "Polygon", "coordinates": {{Box(110, 0, 170, 50)}} } },
                "near": { "type": "geojson", "data": { "type": "Polygon", "coordinates": {{Box(-50, 20, -30, 40)}} } } },
              "layers": [
                { "id": "far", "type": "fill", "source": "far", "paint": { "fill-color": "#ff0000" } },
                { "id": "near", "type": "fill", "source": "near", "paint": { "fill-color": "#00ff00" } } ]
            }
            """);
        var (alone, overGlobe) = Draw(style, "#0.5/30/-40/0/0", 400, 300);
        output.WriteLine($"red alone {Count(alone, 0)}, over the globe {Count(overGlobe, 0)}; green {Count(alone, 1)}, {Count(overGlobe, 1)}");
        Assert.Equal(0, Count(alone, 0));
        Assert.Equal(0, Count(overGlobe, 0));
        Assert.True(Count(overGlobe, 1) > 1000);
    }

    static int Count(byte[] px, int channel)
    {
        int n = 0;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i + channel] > 200 && px[i + (channel + 1) % 3] < 50 && px[i + (channel + 2) % 3] < 50) n++;
        return n;
    }

    static (byte[] alone, byte[] overGlobe) Draw(byte[] style, string hash, double W, double H)
    {
        const double FovY = Math.PI / 4;
        var v = new GlobeView { MinHeight = 1, Width = W, Height_px = H, FovY = FovY, TiltByZoomLimit = false };
        v.ProjX = 1 / Math.Tan(FovY / 2) / (W / H); v.ProjY = 1 / Math.Tan(FovY / 2);
        Assert.True(v.TryApplyHash(hash));
        var (eye, forward, up) = v.Pose();
        var (near, far) = v.ClipPlanes();
        var view = GlobeView.ViewRotation(forward, up);
        var projection = Matrix.PerspectiveFovRH((float)FovY, (float)(W / H), near, far);
        var vp = v.ViewMatrix() * projection;
        var tiles = new TileSelector { MaxZoom = 19 }.Select(eye.ToVector3(), new BoundingFrustum(ref vp), FovY, H, 1f);
        using var service = new VectorTileService("probe", style);
        service.Start();
        using var gpu = new GpuVectorTiles();
        double height = eye.Length - 1;
        double zoom = GlobeView.ZoomOf(height * 2 * Math.Tan(FovY / 2) / H * 6378137, v.Latitude) - 1;
        var wanted = tiles.ToHashSet();
        var until = DateTime.UtcNow.AddSeconds(30);
        int frame = 0, still = 0, lastBuilt = -1;
        while (DateTime.UtcNow < until && still < 10)
        {
            GpuFixtureRenderer.Locked(() => { gpu.Update(GpuFixtureRenderer.Device, tiles, wanted, new[] { service }, 1f, eye, zoom, ++frame, height); return 0; });
            if (service.Document != null && gpu.Building == 0 && gpu.TilesBuilt == lastBuilt) still++; else still = 0;
            lastBuilt = gpu.TilesBuilt;
            Thread.Sleep(30);
        }
        Assert.True(gpu.Frame!.Items.Count > 0, gpu.Status);
        return (GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false),
            GpuFixtureRenderer.RenderGlobe(gpu.Frame!, view, projection, (int)W, (int)H, false, tiles));
    }
}
