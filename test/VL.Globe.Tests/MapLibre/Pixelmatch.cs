using System.Runtime.InteropServices;

namespace VL.Globe.Tests.MapLibre;

// A port of pixelmatch 8.0.0 (https://github.com/mapbox/pixelmatch, ISC licence, see
// Pixelmatch.LICENSE.txt), the version MapLibre GL JS v6.19.0's render tests lock, so a "diff" here
// means what it means there. Ported line by line from index.js, lookup tables included (their
// interpolation changes results near black); the windowed mode, the ignore mask and the colour
// cache (which never changes a result) are left out. Checked against pixelmatch's own tests in
// PixelmatchFacts.
internal static class Pixelmatch
{
    public sealed record Options(double Threshold = 0.1, bool IncludeAA = false, bool Checkerboard = true, double Alpha = 0.1);

    /// <summary>The number of mismatched pixels between two straight-RGBA images; writes a diff image when <paramref name="output"/> is given.</summary>
    public static int Count(byte[] img1, byte[] img2, byte[]? output, int width, int height, Options? options = null)
    {
        var o = options ?? new Options();
        int len = width * height;
        if (img1.Length != img2.Length || (output != null && output.Length != img1.Length))
            throw new ArgumentException($"Image sizes do not match. Image 1 size: {img1.Length}, image 2 size: {img2.Length}");
        if (img1.Length != len * 4) throw new ArgumentException($"Image data size does not match width/height. Expecting {len * 4}. Got {img1.Length}");

        var a32 = MemoryMarshal.Cast<byte, uint>(img1.AsSpan()).ToArray();
        var b32 = MemoryMarshal.Cast<byte, uint>(img2.AsSpan()).ToArray();

        bool identical = true;
        for (int i = 0; i < len; i++) if (a32[i] != b32[i]) { identical = false; break; }
        if (identical)
        {
            if (output != null) for (int i = 0, pos = 0; i < len; i++, pos += 4) DrawGrayPixel(output, pos, img1, o.Alpha);
            return 0;
        }

        int diff = 0;
        for (int i = 0, pos = 0; i < len; i++, pos += 4)
        {
            int delta = a32[i] == b32[i] ? 0 : ColorDelta(img1, img2, pos, o.Checkerboard, o.Threshold);
            if (delta != 0)
            {
                int x = i % width, y = i / width;
                bool excludedAA = !o.IncludeAA && (Antialiased(img1, x, y, width, height, a32, b32) || Antialiased(img2, x, y, width, height, b32, a32));
                if (excludedAA)
                {
                    if (output != null) DrawPixel(output, pos, 255, 255, 0);
                }
                else
                {
                    if (output != null) DrawPixel(output, pos, 255, 0, 0);
                    diff++;
                }
            }
            else if (output != null)
            {
                DrawGrayPixel(output, pos, img1, o.Alpha);
            }
        }
        return diff;
    }

    static bool Antialiased(byte[] img, int x1, int y1, int width, int height, uint[] a32, uint[] b32)
    {
        int x0 = x1 > 0 ? x1 - 1 : 0, y0 = y1 > 0 ? y1 - 1 : 0;
        int x2 = x1 < width - 1 ? x1 + 1 : width - 1, y2 = y1 < height - 1 ? y1 + 1 : height - 1;
        int pos4 = (y1 * width + x1) * 4;
        int cr = img[pos4], cg = img[pos4 + 1], cb = img[pos4 + 2], ca = img[pos4 + 3];
        int zeroes = x1 == x0 || x1 == x2 || y1 == y0 || y1 == y2 ? 1 : 0;
        double min = 0, max = 0;
        int minX = 0, minY = 0, maxX = 0, maxY = 0;

        int rowStep = width * 4;
        for (int x = x0; x <= x2; x++)
        {
            int m = (y0 * width + x) * 4;
            for (int y = y0; y <= y2; y++, m += rowStep)
            {
                if (x == x1 && y == y1) continue;
                double delta = BrightnessDelta(img, m, cr, cg, cb, ca);
                if (delta == 0)
                {
                    zeroes++;
                    if (zeroes > 2) return false;
                }
                else if (delta < min) { min = delta; minX = x; minY = y; }
                else if (delta > max) { max = delta; maxX = x; maxY = y; }
            }
        }
        if (min == 0 || max == 0) return false;
        return (HasManySiblings(a32, minX, minY, width, height) && HasManySiblings(b32, minX, minY, width, height)) ||
               (HasManySiblings(a32, maxX, maxY, width, height) && HasManySiblings(b32, maxX, maxY, width, height));
    }

    static bool HasManySiblings(uint[] img, int x1, int y1, int width, int height)
    {
        int pos1 = y1 * width + x1;
        uint val = img[pos1];
        if (x1 > 0 && x1 < width - 1 && y1 > 0 && y1 < height - 1)
        {
            int c = (val == img[pos1 - width - 1] ? 1 : 0) + (val == img[pos1 - 1] ? 1 : 0) + (val == img[pos1 + width - 1] ? 1 : 0) +
                    (val == img[pos1 - width] ? 1 : 0) + (val == img[pos1 + width] ? 1 : 0) +
                    (val == img[pos1 - width + 1] ? 1 : 0) + (val == img[pos1 + 1] ? 1 : 0) + (val == img[pos1 + width + 1] ? 1 : 0);
            return c > 2;
        }
        int x0 = x1 > 0 ? x1 - 1 : 0, y0 = y1 > 0 ? y1 - 1 : 0;
        int x2 = x1 < width - 1 ? x1 + 1 : width - 1, y2 = y1 < height - 1 ? y1 + 1 : height - 1;
        int zeroes = x1 == x0 || x1 == x2 || y1 == y0 || y1 == y2 ? 1 : 0;
        for (int x = x0; x <= x2; x++)
        {
            int pos = y0 * width + x;
            for (int y = y0; y <= y2; y++, pos += width)
            {
                if (x == x1 && y == y1) continue;
                zeroes += val == img[pos] ? 1 : 0;
                if (zeroes > 2) return true;
            }
        }
        return false;
    }

    // sRGB [0..255] -> linear [0..1], padded with a 257th entry for interpolation
    static readonly double[] Lin = BuildLin();
    static double[] BuildLin()
    {
        var t = new double[257];
        for (int i = 0; i < 256; i++) { double c = i / 255.0; t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        t[256] = t[255];
        return t;
    }

    static readonly double[] LR = Scaled(0.4122214708), MR = Scaled(0.2119034982), SR = Scaled(0.0883024619);
    static readonly double[] LG = Scaled(0.5363325363), MG = Scaled(0.6806995451), SG = Scaled(0.2817188376);
    static readonly double[] LB = Scaled(0.0514459929), MB = Scaled(0.1073969566), SB = Scaled(0.6299787005);
    static double[] Scaled(double k) { var t = new double[256]; for (int i = 0; i < 256; i++) t[i] = k * Lin[i]; return t; }

    static double LinLUT(double x) { int i = (int)x; return Lin[i] + (Lin[i + 1] - Lin[i]) * (x - i); }

    const int CbrtN = 4096, CbrtExact = 8;
    static readonly double[] Cbrt = BuildCbrt();
    static double[] BuildCbrt() { var t = new double[CbrtN + 2]; for (int i = 0; i <= CbrtN + 1; i++) t[i] = Math.Cbrt((double)i / CbrtN); return t; }
    static double CbrtLUT(double x)
    {
        double t = x * CbrtN;
        int i = (int)t;
        if (i < CbrtExact) return Math.Cbrt(x);
        return Cbrt[i] + (Cbrt[i + 1] - Cbrt[i]) * (t - i);
    }

    const double ToeK1 = 0.206, ToeK2 = 0.03, ToeK3 = (1 + ToeK1) / (1 + ToeK2);

    static int ColorDelta(byte[] img1, byte[] img2, int k, bool checkerboard, double maxDelta)
    {
        int r1 = img1[k], g1 = img1[k + 1], b1 = img1[k + 2], a1 = img1[k + 3];
        int r2 = img2[k], g2 = img2[k + 1], b2 = img2[k + 2], a2 = img2[k + 3];
        if (a1 == 255 && a2 == 255)
        {
            double l1 = CbrtLUT(LR[r1] + LG[g1] + LB[b1]), m1 = CbrtLUT(MR[r1] + MG[g1] + MB[b1]), s1 = CbrtLUT(SR[r1] + SG[g1] + SB[b1]);
            double l2 = CbrtLUT(LR[r2] + LG[g2] + LB[b2]), m2 = CbrtLUT(MR[r2] + MG[g2] + MB[b2]), s2 = CbrtLUT(SR[r2] + SG[g2] + SB[b2]);
            return OklabHyabDelta(Lightness(l1, m1, s1) - Lightness(l2, m2, s2), l1 - l2, m1 - m2, s1 - s2, maxDelta);
        }
        return ColorDeltaTransparent(r1, g1, b1, a1, r2, g2, b2, a2, k, checkerboard, maxDelta);
    }

    static int ColorDeltaTransparent(double r1, double g1, double b1, int a1, double r2, double g2, double b2, int a2, int k, bool checkerboard, double maxDelta)
    {
        double rb = 255, gb = 255, bb = 255;
        if (checkerboard)
        {
            uint h = unchecked((uint)((k >> 2) * (int)0x9e3779b1));
            rb = 48 + 159 * (h >> 31);
            gb = 48 + 159 * ((h >> 30) & 1);
            bb = 48 + 159 * ((h >> 29) & 1);
        }
        r1 = (r1 * a1 + rb * (255 - a1)) / 255;
        g1 = (g1 * a1 + gb * (255 - a1)) / 255;
        b1 = (b1 * a1 + bb * (255 - a1)) / 255;
        r2 = (r2 * a2 + rb * (255 - a2)) / 255;
        g2 = (g2 * a2 + gb * (255 - a2)) / 255;
        b2 = (b2 * a2 + bb * (255 - a2)) / 255;
        double lr1 = LinLUT(r1), lg1 = LinLUT(g1), lb1 = LinLUT(b1);
        double lr2 = LinLUT(r2), lg2 = LinLUT(g2), lb2 = LinLUT(b2);
        double l1 = CbrtLUT(0.4122214708 * lr1 + 0.5363325363 * lg1 + 0.0514459929 * lb1);
        double m1 = CbrtLUT(0.2119034982 * lr1 + 0.6806995451 * lg1 + 0.1073969566 * lb1);
        double s1 = CbrtLUT(0.0883024619 * lr1 + 0.2817188376 * lg1 + 0.6299787005 * lb1);
        double l2 = CbrtLUT(0.4122214708 * lr2 + 0.5363325363 * lg2 + 0.0514459929 * lb2);
        double m2 = CbrtLUT(0.2119034982 * lr2 + 0.6806995451 * lg2 + 0.1073969566 * lb2);
        double s2 = CbrtLUT(0.0883024619 * lr2 + 0.2817188376 * lg2 + 0.6299787005 * lb2);
        return OklabHyabDelta(Lightness(l1, m1, s1) - Lightness(l2, m2, s2), l1 - l2, m1 - m2, s1 - s2, maxDelta);
    }

    static int OklabHyabDelta(double dLr, double dl, double dm, double ds, double maxDelta)
    {
        double rest = maxDelta - Math.Abs(dLr);
        if (rest >= 0)
        {
            double da = 1.9779984951 * dl - 2.4285922050 * dm + 0.4505937099 * ds;
            double db = 0.0259040371 * dl + 0.7827717662 * dm - 0.8086757660 * ds;
            if (da * da + db * db <= rest * rest) return 0;
        }
        return dLr > 0 ? -1 : 1;
    }

    static double Lightness(double l, double m, double s)
    {
        double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double x = ToeK3 * L - ToeK1;
        return 0.5 * (x + Math.Sqrt(x * x + 4 * ToeK2 * ToeK3 * L));
    }

    static double BrightnessDelta(byte[] img, int m, int r1, int g1, int b1, int a1)
    {
        int r2 = img[m], g2 = img[m + 1], b2 = img[m + 2], a2 = img[m + 3];
        double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        int da = a1 - a2;
        if (dr == 0 && dg == 0 && db == 0 && da == 0) return 0;
        if (a1 < 255 || a2 < 255)
        {
            dr = (r1 * a1 - r2 * a2 - 255.0 * da) / 255;
            dg = (g1 * a1 - g2 * a2 - 255.0 * da) / 255;
            db = (b1 * a1 - b2 * a2 - 255.0 * da) / 255;
            double d = dr * 0.29889531 + dg * 0.58662247 + db * 0.11448223;
            return d == 0 && da != 0 ? da / 2.0 : d;
        }
        return dr * 0.29889531 + dg * 0.58662247 + db * 0.11448223;
    }

    static void DrawPixel(byte[] output, int pos, byte r, byte g, byte b)
    {
        output[pos] = r; output[pos + 1] = g; output[pos + 2] = b; output[pos + 3] = 255;
    }

    static void DrawGrayPixel(byte[] output, int i, byte[] img, double alpha)
    {
        double val = 255 + (img[i] * 0.29889531 + img[i + 1] * 0.58662247 + img[i + 2] * 0.11448223 - 255) * alpha * img[i + 3] / 255;
        byte v = (byte)Math.Clamp((int)val, 0, 255);   // a Uint8Array assignment truncates
        DrawPixel(output, i, v, v, v);
    }
}
