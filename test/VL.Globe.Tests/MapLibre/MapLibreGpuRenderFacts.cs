using Xunit.Abstractions;

namespace VL.Globe.Tests.MapLibre;

// ROADMAP 4d: the same MapLibre render tests on the GPU path, drawn offscreen (GpuFixtureRenderer)
// - the yardstick the GPU path had none of until now. render-tests-gpu.json is its manifest.
public class MapLibreGpuRenderFacts
{
    readonly ITestOutputHelper _out;
    public MapLibreGpuRenderFacts(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RenderTestsHoldTheirGroundOnTheGpu()
        => RenderHarness.RunAll(_out, "render-tests-gpu.json", "results-gpu", "VL.Globe's GPU path", GpuFixtureRenderer.Render);
}
