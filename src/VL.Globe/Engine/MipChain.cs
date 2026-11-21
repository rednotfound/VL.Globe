using System;
using System.Collections.Generic;

namespace VL.Globe.Engine;

// The smaller copies of a tile image the GPU samples when a tile is drawn smaller than its pixels.
// Both MapLibre (LINEAR_MIPMAP_NEAREST) and Cesium (generateMipmap, trilinear) mipmap their imagery;
// without it, choosing levels by rounding (texels down to 0.71 px) makes small map text shimmer.
//
// Built on the CPU from the decoded pixels, a 2x2 box per level, averaged in LINEAR light: the
// bytes are sRGB, and averaging them as numbers darkens and thickens thin dark lines and text - the
// very thing a map is made of. Pure, so it is tested without a GPU.
internal static class MipChain
{
    static readonly float[] ToLinear = BuildToLinear();

    static float[] BuildToLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

    // linear -> sRGB byte through a 4096-step table: Math.Pow per channel per pixel cost tens of ms a
    // tile once overlays were redrawn per tile (experiment/vectors). 4096 steps resolve every sRGB byte
    // above the darkest few, where the steps are finer than a byte anyway.
    const int Steps = 4096;
    static readonly byte[] FromLinear = BuildFromLinear();

    static byte[] BuildFromLinear()
    {
        var t = new byte[Steps + 1];
        for (int i = 0; i <= Steps; i++)
        {
            double l = (double)i / Steps;
            double c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            t[i] = (byte)Math.Round(c * 255);
        }
        return t;
    }

    static byte ToSrgb(float linear) => FromLinear[(int)(Math.Clamp(linear, 0f, 1f) * Steps + 0.5f)];

    /// <summary>The number of levels down to 1x1, as Stride's MipMapCount.Auto counts them.</summary>
    public static int LevelCount(int width, int height)
    {
        int n = 1;
        for (int s = Math.Max(width, height); s > 1; s >>= 1) n++;
        return n;
    }

    /// <summary>Level 0 (the given 4-byte pixels, rows tightly packed) and every smaller level.</summary>
    // srgbColour: channels 0-2 are sRGB colour, channel 3 linear alpha (RGBA or BGRA alike).
    // straightAlpha: colour is weighted by alpha - an overlay's transparent pixels are black with
    // alpha 0, and a plain average would draw a dark halo round every line at the coarser levels.
    public static List<(byte[] pixels, int width, int height)> Build(byte[] level0, int width, int height, bool srgbColour = true, bool straightAlpha = false)
    {
        if (level0.Length != width * height * 4) throw new ArgumentException("4 bytes per pixel, rows tightly packed", nameof(level0));
        var levels = new List<(byte[], int, int)> { (level0, width, height) };
        var (src, w, h) = (level0, width, height);
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var dst = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    // the 2x2 block, clamped at an odd or 1-pixel edge
                    int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                    int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                    int ia = (y0 * w + x0) * 4, ib = (y0 * w + x1) * 4, id = (y1 * w + x0) * 4, ie = (y1 * w + x1) * 4;
                    float wa = src[ia + 3], wb = src[ib + 3], wd = src[id + 3], we = src[ie + 3], wsum = wa + wb + wd + we;
                    for (int c = 0; c < 4; c++)
                    {
                        int a = ia + c, b = ib + c, d = id + c, e = ie + c;
                        if (straightAlpha && c < 3)
                        {
                            float lin = wsum <= 0 ? 0 : (ToLinear[src[a]] * wa + ToLinear[src[b]] * wb + ToLinear[src[d]] * wd + ToLinear[src[e]] * we) / wsum;
                            dst[(y * nw + x) * 4 + c] = srgbColour ? ToSrgb(lin) : (byte)Math.Round(Math.Clamp(lin, 0f, 1f) * 255);
                            continue;
                        }
                        bool linearChannel = c == 3 || !srgbColour;
                        dst[(y * nw + x) * 4 + c] = linearChannel
                            ? (byte)((src[a] + src[b] + src[d] + src[e] + 2) / 4)
                            : ToSrgb((ToLinear[src[a]] + ToLinear[src[b]] + ToLinear[src[d]] + ToLinear[src[e]]) / 4);
                    }
                }
            levels.Add((dst, nw, nh));
            (src, w, h) = (dst, nw, nh);
        }
        return levels;
    }
}
