using VL.Globe;

namespace VL.Globe.Tests.Diagnostics;

// ROADMAP 4g: the bench pans as GL JS's panBy([+x, 0]) - the point right of the centre brought to the
// centre, the view moving east - so the two logs measure the same motion.
public class CameraBenchFacts
{
    [Fact]
    public void Bringing_the_point_right_of_the_centre_to_the_centre_moves_the_view_east()
    {
        var v = new GlobeView { Width = 1250, Height_px = 800 };
        Assert.True(v.TryApplyHash("#14.82/35.68778/139.76758/0/0"));
        double before = v.Longitude, latitude = v.Latitude;
        var grabbed = v.PickDir(v.CameraDirectionFromProjection(0.1, 0));
        Assert.NotNull(grabbed);
        v.DragTo(grabbed!.Value, v.CameraDirectionFromProjection(0, 0));
        Assert.True(v.Longitude > before, $"{v.Longitude} not east of {before}");
        Assert.Equal(latitude, v.Latitude, 4);
    }
}
