using Stride.Core.Mathematics;
using VL.Globe.Engine;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// ROADMAP 0.1 item 1: PlaceOnGlobe stands a model, in metres, on the globe relative to GlobeCamera's
// eye. Judged as Stride judges it: the output matrix times the camera's rotation-only view, in
// float, against the camera-space truth in double.
public class PlaceOnGlobeFacts(ITestOutputHelper output)
{
    const double Lon = 139.7671, Lat = 35.6812, Up = 20;   // a camera 20 m above Tokyo Station
    const double MetresPerUnit = 6378137.0;

    static (GlobeView view, D3 eye, Matrix viewMatrix) Camera()
    {
        var v = new GlobeView { MinHeight = 1, Longitude = Lon, Latitude = Lat };
        v.SetHeight(Up);
        var (eye, fwd, up) = v.Pose();
        return (v, eye, GlobeView.ViewRotation(fwd, up));
    }

    // camera space in metres: +x right, +y up the screen, -z ahead
    static Vector3 InCamera(Matrix placed, Matrix view, Vector3 local)
        => Vector3.TransformCoordinate(local, placed * view) * (float)MetresPerUnit;

    static void Near(Vector3 expected, Vector3 got, double mm, string what)
    {
        var d = (expected - got).Length() * 1000;
        Assert.True(d < mm, $"{what}: {d:F3} mm off ({got})");
    }

    [Fact]
    public void AModelBelowTheCameraIsTwentyMetresAhead()
    {
        var (_, eye, view) = Camera();
        var placed = GlobeCoordinates.Place(Matrix.Identity, eye, Lon, Lat, 0, 1f);
        var origin = InCamera(placed, view, Vector3.Zero);
        output.WriteLine($"origin in camera space: {origin} m");
        Near(new Vector3(0, 0, -20), origin, 1, "the model's origin");
    }

    [Fact]
    public void ItsAxesAreEastUpAndNorthInMetres()
    {
        // heading 0: the top of the screen is north, the right is east
        var (_, eye, view) = Camera();
        var placed = GlobeCoordinates.Place(Matrix.Identity, eye, Lon, Lat, 0, 1f);
        Near(new Vector3(1, 0, -20), InCamera(placed, view, new Vector3(1, 0, 0)), 1, "1 m east");
        Near(new Vector3(0, 1, -20), InCamera(placed, view, new Vector3(0, 0, -1)), 1, "1 m north (-Z)");
        Near(new Vector3(0, 0, -19), InCamera(placed, view, new Vector3(0, 1, 0)), 1, "1 m up, toward the camera");
    }

    [Fact]
    public void InputIsAppliedFirstInMetres()
    {
        // a model already moved 5 m east by its own transformation
        var (_, eye, view) = Camera();
        var placed = GlobeCoordinates.Place(Matrix.Translation(5, 0, 0), eye, Lon, Lat, 0, 1f);
        Near(new Vector3(5, 0, -20), InCamera(placed, view, Vector3.Zero), 1, "5 m east by Input");
    }

    [Fact]
    public void AnUnwiredInputIsIdentityNotZero()
    {
        var (_, eye, view) = Camera();
        var unwired = GlobeCoordinates.Place(default, eye, Lon, Lat, 0, 1f);
        Near(new Vector3(1, 0, -20), InCamera(unwired, view, new Vector3(1, 0, 0)), 1, "unwired Input");
    }

    [Fact]
    public void WithoutAGlobeCameraItIsAbsoluteAndAgreesWithToGlobe()
    {
        // OrbitCamera has no GlobeCameraFrame: placed in absolute scene coordinates, where ToGlobe puts it
        var placed = GlobeCoordinates.Place(Matrix.Identity, null, 10, -1.5, 5000, 2f);
        var origin = Vector3.TransformCoordinate(Vector3.Zero, placed);
        Assert.True((origin - GlobeCoordinates.ToGlobe(10, -1.5, 5000, 2f)).Length() < 1e-6f);
    }

    [Fact]
    public void TheLinkFindsTheCameraInItsSceneAndPlacesAtDrawTime()
    {
        // the component's real path: a scene holding GlobeCamera's entity (with its frame) and the
        // placed entity; Stride asks the link for the parent matrix when it computes world matrices
        var (_, eye, view) = Camera();
        var scene = new global::Stride.Engine.Scene();
        var frame = new GlobeCameraFrame { Eye = eye };
        scene.Entities.Add(new global::Stride.Engine.Entity { frame });
        var position = new GlobePosition { Longitude = Lon, Latitude = Lat, Height = 0, Radius = 1f };
        var placed = new global::Stride.Engine.Entity { position };
        scene.Entities.Add(placed);
        var link = new GlobePositionLink(position);
        link.ComputeMatrix(false, out var m);
        Near(new Vector3(0, 0, -20), InCamera(m, view, Vector3.Zero), 1, "via the scene");
        Assert.Same(frame, GlobePositionLink.Search(scene));
    }

    [Fact]
    public void WithoutAGlobeCameraInTheSceneTheLinkPlacesAbsolutely()
    {
        var scene = new global::Stride.Engine.Scene();
        var position = new GlobePosition { Longitude = 10, Latitude = -1.5, Height = 5000, Radius = 2f };
        scene.Entities.Add(new global::Stride.Engine.Entity { position });
        new GlobePositionLink(position).ComputeMatrix(false, out var m);
        Assert.True((Vector3.TransformCoordinate(Vector3.Zero, m) - GlobeCoordinates.ToGlobe(10, -1.5, 5000, 2f)).Length() < 1e-6f);
    }
}
