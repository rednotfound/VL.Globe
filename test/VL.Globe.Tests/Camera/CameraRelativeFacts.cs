using Stride.Core.Mathematics;
using Xunit.Abstractions;

namespace VL.Globe.Tests.Camera;

// ROADMAP item 1: a tile vertex seen from 20 m up, carried to camera space the way Stride does it -
// in float, world matrix times view matrix - against the same vertex in double. The absolute path is
// kept beside the new one as the contrast: it is the 0.4 m lattice the camera snapped to.
public class CameraRelativeFacts(ITestOutputHelper output)
{
    const double Lon = 139.7671, Lat = 35.6812;   // Tokyo Station
    const double Height = 20;                     // metres: street level
    const int Zoom = 19;

    static TileKey TileUnder(double lon, double lat, int zoom)
    {
        int n = 1 << zoom;
        return new TileKey(zoom, (int)((lon + 180) / 360 * n), (int)(TileGeometry.MercatorY(lat) * n));
    }

    static GlobeView Camera()
    {
        var v = new GlobeView { MinHeight = 1, Longitude = Lon, Latitude = Lat, Heading = 30 };
        v.SetHeight(Height);
        return v;
    }

    // Camera space in double, as Matrix.LookAtRH builds it: z = -forward, x = up x z, y = z x x.
    static D3 TruthInCamera(D3 world, D3 eye, D3 fwd, D3 up)
    {
        var z = -fwd;
        var x = D3.Cross(up, z).Normalized;
        var y = D3.Cross(z, x);
        var d = world - eye;
        return new D3(D3.Dot(d, x), D3.Dot(d, y), D3.Dot(d, z));
    }

    static IEnumerable<D3> TruthVertices(TileKey t)
    {
        int seg = TileGeometry.SegmentsFor(t.Zoom);
        double global = (double)t.TilesPerSide * seg;
        for (int j = 0; j <= seg; j++)
            for (int i = 0; i <= seg; i++)
            {
                var (x, y, z) = TileGeometry.OnSphere(TileGeometry.LongitudeOf((t.X * seg + i) / global), TileGeometry.LatitudeOf((t.Y * seg + j) / global));
                yield return new D3(x, y, z);
            }
    }

    static double Metres(Vector3 got, D3 truth) => new D3(got.X - truth.X, got.Y - truth.Y, got.Z - truth.Z).Length * GlobeCoordinates.EarthRadius;

    (double relative, double absolute) WorstErrors()
    {
        var view = Camera();
        var (eye, fwd, up) = view.Pose();
        var tile = TileUnder(Lon, Lat, Zoom);
        var truth = TruthVertices(tile).Select(p => TruthInCamera(p, eye, fwd, up)).ToArray();

        // New: offsets from the tile centre, the entity at (centre - eye) subtracted in double, rotation-only view.
        var (centre, offsets, _, _) = TileGeometry.RelativeVertices(tile);
        var world = Matrix.Scaling(1f) * Matrix.Translation(TileGeometry.Translation(centre, 1f, eye));
        var wvRelative = world * GlobeView.ViewRotation(fwd, up);
        double rel = offsets.Select((o, i) => Metres(Vector3.TransformCoordinate(o, wvRelative), truth[i])).Max();

        // Before: absolute vertices, absolute view, both float.
        var (positions, _) = TileGeometry.Vertices(tile);
        var wvAbsolute = Matrix.Scaling(1f) * view.ViewMatrix();
        double abs = positions.Select((p, i) => Metres(Vector3.TransformCoordinate(p, wvAbsolute), truth[i])).Max();

        output.WriteLine($"zoom {Zoom} tile {tile} seen from {Height} m: camera-relative {rel * 1000:F4} mm, absolute {abs:F3} m");
        return (rel, abs);
    }

    [Fact]
    public void AVertexSeenFrom20MetresIsWithinAMillimetre() => Assert.True(WorstErrors().relative < 1e-3);

    [Fact]
    public void TheAbsolutePathWasOffByTenthsOfAMetre()
    {
        // The contrast, so the first fact cannot pass by measuring nothing: the same harness finds the
        // old lattice. Measured 2026-11-20 at Tokyo Station, zoom 19: camera-relative 0.0078 mm,
        // absolute 0.538 m.
        Assert.True(WorstErrors().absolute > 0.1);
    }

    [Fact]
    public void WithoutAnEyeATileIsPlacedAbsolutely()
    {
        var t = TileUnder(Lon, Lat, Zoom);
        var c = TileGeometry.Centre(t);
        Assert.Equal((c * 2).ToVector3(), TileGeometry.Translation(c, 2f, null));
    }
}
