using SkiaSharp;

namespace VL.Globe.Tests.MapLibre;

// PNG <-> straight RGBA bytes, as pngjs reads and writes them: no colour-space conversion, no
// premultiplication.
internal static class Png
{
    public static (byte[] rgba, int width, int height) Read(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException($"not an image: {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"{result}: {path}");
        return (bitmap.Bytes, info.Width, info.Height);
    }

    public static void Write(string path, byte[] rgba, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, rgba);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
