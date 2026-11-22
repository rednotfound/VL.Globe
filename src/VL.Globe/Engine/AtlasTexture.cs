using System;
using System.Collections.Generic;
using Stride.Graphics;

namespace VL.Globe.Engine;

// An atlas on the GPU. Made again only when the atlas has grown taller (it doubles, so a few times a
// style); the rows placed since go into the same texture in the Draw. Before, a new texture was made
// from a copy of the whole image whenever a glyph was added: a large-object-heap array of megabytes,
// and the "atlases" stage of 150 ms, twice in a second, the first time a tilt brought ~400 label
// tiles (2026-11-22).
internal static class AtlasTexture
{
    /// <summary>The texture to draw <paramref name="atlas"/> with this frame; the rows it lacks are queued on <paramref name="frame"/>.</summary>
    public static Texture Refresh(GraphicsDevice device, Texture? texture, IRowAtlas atlas, int width, PixelFormat format, VectorRenderer.Frame frame)
    {
        if (texture is null || texture.Height != atlas.Height)
        {
            texture?.Dispose();
            texture = atlas.Upload((pixels, height) => Texture.New2D(device, width, height, format, pixels, TextureFlags.ShaderResource, GraphicsResourceUsage.Default));
        }
        frame.Atlases.Add((texture, atlas));
        return texture;
    }

    /// <summary>The rows placed since the last upload, into the frame's atlas textures: before anything is drawn.</summary>
    public static void Flush(CommandList commandList, List<(Texture texture, IRowAtlas atlas)> atlases)
    {
        foreach (var (texture, atlas) in atlases)
        {
            int rowBytes = texture.Width * texture.Format.SizeInBytes();
            atlas.UploadChanges(texture.Height, (pixels, from, rows) =>
                texture.SetData(commandList, new Span<byte>(pixels, from * rowBytes, rows * rowBytes), 0, 0,
                    new ResourceRegion(0, from, 0, texture.Width, from + rows, 1)));
        }
    }
}
