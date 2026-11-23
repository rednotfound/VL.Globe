using System.Text.Json;

namespace VL.Globe.Tests.Symbols;

// ROADMAP 4e, icons (milestone 4): the sprite rules MapLibre's render tests do not reach - a list of
// sprites, whose images other than "default"'s are named "<id>:<name>", and "@2x" chosen by the pixel
// ratio (GL JS load_sprite.ts). Against MapLibre's own test sprites.
public class IconFacts
{
    static string Sprites()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VL.Globe.nuspec")))
            {
                var dir = Path.Combine(d.FullName, "test", "fixtures", "maplibre", "v6.19.0", "_assets", "sprites");
                Assert.True(Directory.Exists(dir), $"fetch the fixtures first: .\\tools\\Fetch-MapLibreFixtures.ps1 ({dir})");
                return dir;
            }
        throw new DirectoryNotFoundException("no VL.Globe.nuspec above the test assembly");
    }

    static MapStyleDocument Style(string sprite, double pixelRatio, List<string> asked)
    {
        using var json = JsonDocument.Parse($$"""{ "version": 8, "sources": {}, "layers": [], "sprite": {{sprite}} }""");
        var style = MapStyleDocument.Parse(json.RootElement);
        style.PixelRatio = pixelRatio;
        var dir = Sprites();
        style.Load = url => { asked.Add(url); return File.ReadAllBytes(Path.Combine(dir, url.Substring("local://sprites/".Length))); };
        return style;
    }

    [Fact]
    public void ASpriteListNamesItsImagesById()
    {
        var asked = new List<string>();
        var style = Style("""[{ "id": "default", "url": "local://sprites/sprite" }, { "id": "e", "url": "local://sprites/emerald" }]""", 1, asked);
        Assert.True(style.SpriteStore.Ensure(fetch: true, missing: null));
        Assert.Equal(new[] { "local://sprites/sprite.json", "local://sprites/sprite.png", "local://sprites/emerald.json", "local://sprites/emerald.png" }, asked);
        Assert.True(style.SpriteStore.TryGet("dot.sdf", out var dot));
        Assert.True(dot.Image.Sdf);
        Assert.True(style.SpriteStore.TryGet("e:sand_noise", out var sand));
        Assert.False(style.SpriteStore.TryGet("sand_noise", out _));
        // in the atlas with its 1-px padding round it
        Assert.Equal((sand.Image.Width + 2, sand.Image.Height + 2), (sand.PaddedRect.W, sand.PaddedRect.H));
        Assert.Equal((50.0, 50.0), sand.DisplaySize);
    }

    [Fact]
    public void APixelRatioOfTwoLoadsTheDoubleSizedSprite()
    {
        var asked = new List<string>();
        var style = Style("\"local://sprites/emerald\"", 2, asked);
        Assert.True(style.SpriteStore.Ensure(fetch: true, missing: null));
        Assert.Equal(new[] { "local://sprites/emerald@2x.json", "local://sprites/emerald@2x.png" }, asked);
        Assert.True(style.SpriteStore.TryGet("sand_noise", out var sand));
        // twice the pixels, the same size on screen
        Assert.Equal((100, 2.0), (sand.Image.Width, sand.Image.PixelRatio));
        Assert.Equal((50.0, 50.0), sand.DisplaySize);
    }

    [Fact]
    public void ASpriteImageIsPremultipliedOnce()
    {
        var asked = new List<string>();
        var style = Style("\"local://sprites/sprite\"", 1, asked);
        Assert.True(style.SpriteStore.Ensure(fetch: true, missing: null));
        Assert.True(style.SpriteStore.TryGet("building-12", out var icon));
        // against Skia's own premultiplied decode of the sprite, within rounding: premultiplied twice,
        // a half-transparent edge pixel came out at a quarter of its colour - dark edges (2026-11-22)
        using var reference = SkiaSharp.SKBitmap.Decode(Path.Combine(Sprites(), "sprite.png"));
        Assert.Equal(SkiaSharp.SKAlphaType.Premul, reference.AlphaType);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Sprites(), "sprite.json")));
        var at = json.RootElement.GetProperty("building-12");
        int x0 = at.GetProperty("x").GetInt32(), y0 = at.GetProperty("y").GetInt32();
        var p = icon.Image.Pixels;
        int soft = 0;
        for (int y = 0; y < icon.Image.Height; y++)
            for (int x = 0; x < icon.Image.Width; x++)
            {
                int i = (y * icon.Image.Width + x) * 4;
                var c = reference.GetPixelSpan().Slice(((y0 + y) * reference.Width + x0 + x) * 4, 4);   // the platform's order: compare the alpha and the brightest channel
                byte a = p[i + 3];
                Assert.Equal(c[3], a);
                Assert.True(Math.Abs(Math.Max(p[i], Math.Max(p[i + 1], p[i + 2])) - Math.Max(c[0], Math.Max(c[1], c[2]))) <= 1, $"pixel {x},{y}");
                if (a is > 32 and < 224) soft++;
            }
        Assert.True(soft > 4, "the icon has soft edges to check");
    }
}
