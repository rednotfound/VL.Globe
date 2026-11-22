using Stride.Core.Mathematics;
using VL.Globe.Engine;

namespace VL.Globe.Tests;

// When the renderer places labels, walks the glyphs along lines again, or leaves them (VectorRenderer.NextLabelStep).
public class LabelStepFacts
{
    [Fact]
    public void A_pan_that_leaves_the_rotation_only_view_unchanged_still_moves_the_glyphs_along_lines()
    {
        // camera-relative: a pan at street level moves the eye, and the view (a rotation) can stay bit
        // for bit the same. The glyphs along lines stood still in such frames and caught up in steps.
        var renderer = new VectorRenderer();
        var viewProjection = Matrix.RotationX(0.5f) * Matrix.PerspectiveFovRH(0.8f, 1.5f, 0.01f, 10);
        var size = new Vector2(1200, 800);
        var eye = new D3(0.1, 0.2, 0.97);
        Assert.Equal(VectorRenderer.LabelStep.Place, renderer.NextLabelStep(viewProjection, size, eye, tiles: 1, now: 0));
        Assert.Equal(VectorRenderer.LabelStep.None, renderer.NextLabelStep(viewProjection, size, eye, 1, 16));

        var panned = eye with { X = eye.X + 1e-7 };   // about half a metre on the unit globe
        Assert.Equal(VectorRenderer.LabelStep.UpdateLines, renderer.NextLabelStep(viewProjection, size, panned, 1, 33));
        // and placed again 300 ms after the last placement while it keeps moving, once more at rest
        Assert.Equal(VectorRenderer.LabelStep.Place, renderer.NextLabelStep(viewProjection, size, panned with { X = panned.X + 1e-7 }, 1, 320));
        Assert.Equal(VectorRenderer.LabelStep.UpdateLines, renderer.NextLabelStep(viewProjection, size, panned, 1, 336));
        Assert.Equal(VectorRenderer.LabelStep.Place, renderer.NextLabelStep(viewProjection, size, panned, 1, 352));
        Assert.Equal(VectorRenderer.LabelStep.None, renderer.NextLabelStep(viewProjection, size, panned, 1, 368));
    }
}
