using Stride.Graphics;

namespace VL.Globe.Tests.Gpu;

// The glyph atlas on the GPU (AtlasTexture): made again only when the atlas grows taller, the rows
// placed since uploaded into the same texture in the Draw. Read back from a D3D11 device of the test's own.
public class AtlasTextureFacts
{
    static Glyph Filled(int id, int size, byte value)
    {
        int side = size + 2 * Glyph.Border;
        return new Glyph(id, Enumerable.Repeat(value, side * side).ToArray(), size, size, 0, 0, size);
    }

    static byte[] Read(GraphicsContext context, Texture texture) => texture.GetData<byte>(context.CommandList);

    [Fact]
    public void Rows_placed_after_the_texture_was_made_reach_it_without_a_new_texture()
    {
        using var device = GraphicsDevice.New(DeviceCreationFlags.None, GraphicsProfile.Level_11_0);
        var context = new GraphicsContext(device);
        var atlas = new GlyphAtlas();
        atlas.Place("a", Filled(1, 10, 40));

        var frame = new VectorRenderer.Frame();
        var texture = AtlasTexture.Refresh(device, null, atlas, GlyphAtlas.Width, PixelFormat.R8_UNorm, frame);
        AtlasTexture.Flush(context.CommandList, frame.Atlases);

        // a second glyph on the same shelf, then one on a new shelf: the same height, the same texture
        atlas.Place("a", Filled(2, 12, 200));
        atlas.Place("a", Filled(3, 20, 90));
        Assert.Equal(64, atlas.Height);
        frame.Atlases.Clear();
        var same = AtlasTexture.Refresh(device, texture, atlas, GlyphAtlas.Width, PixelFormat.R8_UNorm, frame);
        Assert.Same(texture, same);
        AtlasTexture.Flush(context.CommandList, frame.Atlases);
        Assert.Equal(atlas.Snapshot().pixels, Read(context, same));

        // grown taller: a new texture holding every row
        for (int id = 4; atlas.Height == 64; id++) atlas.Place("a", Filled(id, 30, (byte)id));
        frame.Atlases.Clear();
        var taller = AtlasTexture.Refresh(device, same, atlas, GlyphAtlas.Width, PixelFormat.R8_UNorm, frame);
        Assert.NotSame(same, taller);
        Assert.Equal(atlas.Height, taller.Height);
        AtlasTexture.Flush(context.CommandList, frame.Atlases);
        Assert.Equal(atlas.Snapshot().pixels, Read(context, taller));
        taller.Dispose();
    }
}
