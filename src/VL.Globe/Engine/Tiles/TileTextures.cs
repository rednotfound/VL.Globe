using System.IO;
using Stride.Graphics;

namespace VL.Globe.Engine.Tiles;

// A raster tile's image on the GPU with its whole mip chain (MipChain), for the globe's own imagery and
// the GPU raster layers (ROADMAP 6c) alike. Safe on a worker thread: D3D11's device is free-threaded
// (vvvv's creation flags: None), and an immutable texture made with its data touches only the device.
internal static class TileTextures
{
    // Formats other than 4-byte RGBA/BGRA load as before, without mips - an unknown format draws soft or
    // shimmering, never not at all.
    // straightAlpha: averaged weighted by alpha (a translucent PNG layer), else as opaque pixels
    public static Texture LoadMipmapped(GraphicsDevice device, byte[] bytes, bool straightAlpha = false)
    {
        using var stream = new MemoryStream(bytes);
        using var source = Image.Load(stream, loadAsSRGB: true);
        var pb = source.PixelBuffer[0];
        var format = pb.Format;
        bool fourBytes = format is PixelFormat.R8G8B8A8_UNorm_SRgb or PixelFormat.R8G8B8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb or PixelFormat.B8G8R8A8_UNorm;
        if (!fourBytes || pb.Width < 2)
            return Texture.New(device, source, TextureFlags.ShaderResource, GraphicsResourceUsage.Immutable);

        int w = pb.Width, h = pb.Height;
        var level0 = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(pb.DataPointer + y * pb.RowStride, level0, y * w * 4, w * 4);
        bool srgb = format is PixelFormat.R8G8B8A8_UNorm_SRgb or PixelFormat.B8G8R8A8_UNorm_SRgb;
        return FromLevels(device, level0, w, h, format, srgb, straightAlpha);
    }

    public static Texture FromLevels(GraphicsDevice device, byte[] level0, int w, int h, PixelFormat format, bool srgb, bool straightAlpha)
    {
        var levels = MipChain.Build(level0, w, h, srgb, straightAlpha);

        using var image = Image.New2D(w, h, MipMapCount.Auto, format);
        for (int m = 0; m < levels.Count && m < image.Description.MipLevels; m++)
        {
            var dst = image.GetPixelBuffer(0, 0, m);
            var (pixels, lw, lh) = levels[m];
            for (int y = 0; y < lh; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * lw * 4, dst.DataPointer + y * dst.RowStride, lw * 4);
        }
        return Texture.New(device, image, TextureFlags.ShaderResource, GraphicsResourceUsage.Immutable);
    }
}
