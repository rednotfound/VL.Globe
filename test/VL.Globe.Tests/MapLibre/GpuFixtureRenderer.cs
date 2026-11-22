using Stride.Core.IO;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compiler;
using VL.Globe.Engine;
using Matrix = Stride.Core.Mathematics.Matrix;
using Vector4 = Stride.Core.Mathematics.Vector4;
using Color4 = Stride.Core.Mathematics.Color4;

namespace VL.Globe.Tests.MapLibre;

// MapLibre's render tests drawn by the GPU path (ROADMAP 4d), offscreen in the test host: a D3D11
// device of its own, shaders\GlobeVector.sdsl compiled by Stride's EffectCompiler from the repo,
// the same buckets (VectorBucketBuilder) and draw list (VectorDrawList) and renderer
// (VectorRenderer.DrawFrame) the globe uses - only flat: the buckets on the Mercator plane, seen
// straight down by GL JS's own camera (a 0.6435 rad vertical field of view), and in sRGB, as GL JS
// blends.
internal static class GpuFixtureRenderer
{
    sealed class Gpu
    {
        public required GraphicsDevice Device;
        public required GraphicsContext Context;
        public required EffectInstance Effect, SymbolEffect, CircleEffect;
        public readonly VectorRenderer Renderer = new() { GlOutlines = true };
    }

    static readonly Lazy<Gpu> Shared = new(Create);
    static readonly object Lock = new();
    const double Fov = 0.6435011087932844;   // GL JS's transform: 2 atan(0.75 / ... ) - its default fov

    static Gpu Create()
    {
        var device = GraphicsDevice.New(DeviceCreationFlags.None, GraphicsProfile.Level_11_0);
        // GlobeVector and the two Stride shaders it inherits, from the repo and the Stride package
        string repo = RenderHarness.RepoRoot();
        var dir = Path.Combine(Path.GetTempPath(), "vlglobe-shaders-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "GlobeVector.sdsl", "GlobeSymbol.sdsl", "GlobeCircle.sdsl" }) File.Copy(Path.Combine(repo, "shaders", f), Path.Combine(dir, f), overwrite: true);
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var assets = Path.Combine(nuget, "stride.graphics", "4.2.1.2487", "stride", "Assets");
        foreach (var f in new[] { "ShaderBase.sdsl", "ShaderBaseStream.sdsl" }) File.Copy(Path.Combine(assets, f), Path.Combine(dir, f), overwrite: true);
        var compiler = new EffectCompiler(new FileSystemProvider(null, dir)) { UseFileSystem = true };
        compiler.SourceDirectories.Add(dir);
        var parameters = new CompilerParameters();
        parameters.EffectParameters.Platform = GraphicsPlatform.Direct3D11;
        parameters.EffectParameters.Profile = GraphicsProfile.Level_11_0;
        EffectInstance Compile(string name)
        {
            var result = compiler.Compile(new ShaderMixinSource { Name = name, Mixins = { new ShaderClassSource(name) } },
                parameters.EffectParameters, parameters).WaitForResult();
            if (result.CompilationLog.HasErrors)
                throw new InvalidOperationException($"{name} does not compile: " + string.Join("; ", result.CompilationLog.Messages.Select(m => m.Text)));
            var effect = new EffectInstance(new Effect(device, result.Bytecode));
            effect.UpdateEffect(device);
            return effect;
        }
        return new Gpu { Device = device, Context = new GraphicsContext(device), Effect = Compile("GlobeVector"), SymbolEffect = Compile("GlobeSymbol"), CircleEffect = Compile("GlobeCircle") };
    }

    public static byte[] Render(MapStyleDocument style, int width, int height, double pixelRatio)
    {
        lock (Lock) return RenderCore(style, width, height, pixelRatio);
    }

    static byte[] RenderCore(MapStyleDocument style, int width, int height, double pixelRatio)
    {
        var gpu = Shared.Value;
        style.PixelRatio = pixelRatio;   // the sprite's @2x
        int w = (int)Math.Floor(width * pixelRatio), h = (int)Math.Floor(height * pixelRatio);
        double zoom = style.Zoom;
        double worldSize = 512 * Math.Pow(2, zoom) * pixelRatio;                // device px per world (1 wide)
        double cx = (style.CenterLon + 180) / 360, cy = TileGeometry.MercatorY(style.CenterLat);
        double distance = h / 2.0 / Math.Tan(Fov / 2) / worldSize;                // the eye above the plane, in world units
        double halfW = w / 2.0 / worldSize, halfH = h / 2.0 / worldSize;

        var layers = Enumerable.Range(0, style.Layers.Count).Select(i => GpuStyleLayer.Of(style, i)).Where(l => l != null).Select(l => l!).ToList();
        var ctx = new StyleContext { Zoom = zoom, GlobalState = style.GlobalState };
        var tiles = new List<GpuTile>();
        // per source: the tiles GL JS would pick (the floored zoom for the source's tile size, clamped,
        // overscaled past maxzoom), wrapped copies shifted by whole worlds
        var bySource = new Dictionary<string, List<(GpuTile tile, double shift)>>();
        List<(GpuTile tile, double shift)> Cover(StyleSource source, IReadOnlyList<GpuStyleLayer> sourceLayers, bool background)
        {
            var (minZoom, maxZoom, tileSize) = source switch
            {
                VectorSource v => (v.MinZoom, v.MaxZoom, v.TileSize),
                _ => (0, 18, 512),
            };
            var list = new List<(GpuTile, double)>();
            int ideal = Math.Max(0, (int)Math.Floor(zoom + Math.Log2(512.0 / tileSize)));
            if (ideal < minZoom) return list;
            int z = Math.Min(ideal, maxZoom), n = 1 << z;
            int y0 = Math.Max(0, (int)Math.Floor((cy - halfH) * n)), y1 = Math.Min(n - 1, (int)Math.Floor((cy + halfH) * n - 1e-9));
            int x0 = (int)Math.Floor((cx - halfW) * n), x1 = (int)Math.Floor((cx + halfW) * n - 1e-9);
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    var key = new TileKey(z, ((tx % n) + n) % n, ty);
                    var bucket = background
                        ? VectorBucketBuilder.BackgroundBucket(key, VectorProjection.Mercator)
                        : VectorBucketBuilder.Build(style, source, sourceLayers, false, key, true, null, out _, VectorProjection.Mercator,
                            overscale: 1 << (ideal - z));   // GL JS's overscaling past the source's maxzoom
                    if (bucket is null) continue;
                    var tile = GpuTile.Upload(gpu.Device, bucket);
                    tiles.Add(tile);
                    list.Add((tile, Math.Floor((double)tx / n)));
                }
            return list;
        }
        foreach (var group in layers.Where(l => l.Type != "background").GroupBy(l => l.Layer.Source!))
            bySource[group.Key] = Cover(style.Sources[group.Key], group.ToList(), false);
        List<(GpuTile tile, double shift)>? backgrounds = null;

        var frame = new VectorRenderer.Frame { Radius = 1, CameraToCenter = (float)distance, PixelRatio = (float)pixelRatio };
        // the glyphs the labels placed and the icons, one atlas each for the style, made as the globe makes them
        Texture? atlas = layers.Any(l => l.IsSymbol)
            ? AtlasTexture.Refresh(gpu.Device, null, style.GlyphStore.Atlas, GlyphAtlas.Width, PixelFormat.R8_UNorm, frame) : null;
        Texture? images = layers.Any(l => l.IconImage != null)
            ? AtlasTexture.Refresh(gpu.Device, null, style.SpriteStore.Atlas, ImageAtlas.Width, PixelFormat.R8G8B8A8_UNorm, frame) : null;
        foreach (var layer in layers)
        {
            if (layer.IsCircle)
            {
                if (layer.CircleUniforms(ctx) is not { } circlePaint || !bySource.TryGetValue(layer.Layer.Source!, out var circled)) continue;
                foreach (var (tile, shift) in circled)
                {
                    var at = Matrix.Translation((float)(tile.Bucket.Centre.X + shift - cx), (float)(tile.Bucket.Centre.Y + cy), (float)(tile.Bucket.Centre.Z - distance));
                    VectorDrawList.AddCircleLayer(frame.Items, layer, circlePaint, new[] { (tile, new Vector4(0, 0, 1, 1)) }, _ => at,
                        linearize: false, distance, pixelRatio);
                }
                continue;
            }
            if (layer.IsSymbol)
            {
                if (!layer.SymbolUniforms(ctx, out var paint) || !bySource.TryGetValue(layer.Layer.Source!, out var labelled)) continue;
                var iconPaint = layer.IconUniforms(ctx);
                var placing = new List<(GpuTile, Matrix)>();
                foreach (var (tile, shift) in labelled)
                {
                    var at = Matrix.Translation((float)(tile.Bucket.Centre.X + shift - cx), (float)(tile.Bucket.Centre.Y + cy), (float)(tile.Bucket.Centre.Z - distance));
                    VectorDrawList.AddSymbolLayer(frame.Items, layer, paint, new[] { (tile, new Vector4(0, 0, 1, 1)) }, _ => at,
                        linearize: false, atlas, distance, pixelRatio, iconPaint, images, allowNearest: true);
                    placing.Add((tile, at));
                }
                // placed from the top of the style down
                frame.Labels.Insert(0, new LabelPlacement.Layer(layer.Index, placing, (float)paint.Size));
                continue;
            }
            if (!layer.Uniforms(ctx, out var color, out var lineWidth, out var outline)) continue;
            List<(GpuTile tile, double shift)> list;
            if (layer.Type == "background")
                list = backgrounds ??= Cover(new GeoJsonSource(), Array.Empty<GpuStyleLayer>(), true);
            else if (!bySource.TryGetValue(layer.Layer.Source!, out list!)) continue;
            // one tile at a time: a wrapped copy is the same buffers with another translation
            foreach (var (tile, shift) in list)
            {
                var world = Matrix.Translation((float)(tile.Bucket.Centre.X + shift - cx), (float)(tile.Bucket.Centre.Y + cy), (float)(tile.Bucket.Centre.Z - distance));
                VectorDrawList.AddLayer(frame.Items, layer, color, lineWidth * pixelRatio, outline,
                    new[] { (tile, new Vector4(0, 0, 1, 1)) }, _ => world, linearize: false);
            }
        }

        var target = Texture.New2D(gpu.Device, w, h, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        var depth = Texture.New2D(gpu.Device, w, h, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
        try
        {
            var cl = gpu.Context.CommandList;
            cl.SetRenderTargetAndViewport(depth, target);
            cl.Clear(target, new Color4(0, 0, 0, 0));
            cl.Clear(depth, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil, 1, 0);
            // the eye at the origin looking down -z, north up: the view is the identity, as the globe's
            // camera-relative view is a rotation only
            var projection = Matrix.PerspectiveFovRH((float)Fov, (float)w / h, (float)(distance * 0.01), (float)(distance * 100));
            // GL's framebuffer is y up, D3D's y down: an edge exactly through a row of pixel centres goes
            // to the other side (the top-left rule, read back flipped). A bevel's flat edges came out a
            // row off, the top one row higher, the bottom one lower (line-miter-limit/default). Moved
            // down 1/256 px - more than this chain's float error, which hid 1/1024 - ties fall as in GL.
            projection.M32 += 2f / 256 / h;
            gpu.Renderer.DrawFrame(cl, gpu.Context, gpu.Effect, gpu.SymbolEffect, gpu.CircleEffect, Matrix.Identity, projection, h, frame, lift: 0, linearize: false, flat: true);
            var px = target.GetData<byte>(cl);
            // render.test.ts un-premultiplies by division into a Uint8Array, which truncates
            for (int i = 0; i < px.Length; i += 4)
            {
                double a = px[i + 3] / 255.0;
                if (a == 0) continue;
                for (int k = 0; k < 3; k++) px[i + k] = (byte)((int)(px[i + k] / a) & 0xFF);
            }
            return px;
        }
        finally
        {
            target.Dispose();
            depth.Dispose();
            atlas?.Dispose();
            images?.Dispose();
            foreach (var t in tiles) t.Dispose();
        }
    }
}
