namespace VL.Globe.Tests.Nodes;

// The Globe takes the height of the view it is drawn in (2026-11-25): no window height wired back through a
// FrameDelay. The pin still overrides; before the first draw, the GlobeCamera's window, else 1080.
public class ScreenHeightFacts
{
    [Fact]
    public void ThePinWinsWhenSet() => Assert.Equal(600f, GlobeNode.PickScreenHeight(600, 839, 720).height);

    [Fact]
    public void UnsetItIsTheViewItWasDrawnIn() => Assert.Equal(839f, GlobeNode.PickScreenHeight(0, 839, 720).height);

    [Fact]
    public void BeforeTheFirstDrawTheCamerasWindow() => Assert.Equal(720f, GlobeNode.PickScreenHeight(0, 0, 720).height);

    [Fact]
    public void WithNothingKnown1080() => Assert.Equal(1080f, GlobeNode.PickScreenHeight(0, 0, 0).height);
}
