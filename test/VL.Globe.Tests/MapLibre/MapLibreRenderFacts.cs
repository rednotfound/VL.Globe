using VL.Globe.Engine;
using Xunit.Abstractions;

namespace VL.Globe.Tests.MapLibre;

// ROADMAP 0.1 item 4b: MapLibre GL JS's render tests as the yardstick for the draped vector path
// (RenderHarness has the rules; render-tests.json the manifest).
public class MapLibreRenderFacts
{
    readonly ITestOutputHelper _out;
    public MapLibreRenderFacts(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RenderTestsHoldTheirGround()
        => RenderHarness.RunAll(_out, "render-tests.json", "results", "VL.Globe's draped path", MapStylePainter.RenderStraight);
}
