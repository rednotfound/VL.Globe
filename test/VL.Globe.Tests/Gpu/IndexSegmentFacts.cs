
namespace VL.Globe.Tests.Gpu;

// ROADMAP 4g step 2: a vector bucket's indices are 16-bit, each range cut into segments whose vertices
// lie within 65536 of the segment's BaseVertex, as GL JS's SegmentVector.
public class IndexSegmentFacts
{
    static (List<VectorRange> ranges, ushort[] target, List<VectorVertex> vertices) Cut(uint[] source, params VectorRange[] ranges)
    {
        var vertices = new List<VectorVertex>();
        uint top = source.Length == 0 ? 0 : source.Max();
        for (int i = 0; i <= top; i++) { var v = new VectorVertex(); v.SetUv(i % 1000 / 1000.0, i / 1000 / 1000.0); vertices.Add(v); }
        var target = new ushort[source.Length];
        var result = VectorBucketBuilder.Segments(source, target, ranges.ToList(), vertices);
        return (result, target, vertices);
    }

    [Fact]
    public void Triangles_over_many_vertices_come_back_whole_from_their_segments()
    {
        // a strip of 100000 triangles over 100002 vertices: two segments at least
        int triangles = 100000;
        var source = new uint[triangles * 3];
        for (int t = 0; t < triangles; t++) { source[3 * t] = (uint)t; source[3 * t + 1] = (uint)t + 1; source[3 * t + 2] = (uint)t + 2; }
        var original = (uint[])source.Clone();
        var (ranges, target, _) = Cut(source, new VectorRange(0, RangeKind.Line, 0, source.Length));
        Assert.True(ranges.Count >= 2);
        int at = 0;
        foreach (var r in ranges)
        {
            Assert.Equal(at, r.Start);
            Assert.Equal(0, r.Count % 3);   // cut between triangles
            for (int i = r.Start; i < r.Start + r.Count; i++) Assert.Equal(original[i], (uint)(r.BaseVertex + target[i]));
            at += r.Count;
        }
        Assert.Equal(source.Length, at);
    }

    [Fact]
    public void A_span_two_layers_share_is_cut_once_for_both()
    {
        var source = new uint[] { 0, 1, 2, 70000, 70001, 70002 };
        var (ranges, _, _) = Cut(source, new VectorRange(0, RangeKind.Line, 0, 6), new VectorRange(1, RangeKind.Line, 0, 6));
        var first = ranges.Where(r => r.Layer == 0).ToList();
        var second = ranges.Where(r => r.Layer == 1).ToList();
        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(r => (r.Start, r.Count, r.BaseVertex)), second.Select(r => (r.Start, r.Count, r.BaseVertex)));
    }

    [Fact]
    public void A_triangle_too_wide_for_16_bits_gets_copies_of_its_vertices()
    {
        // one triangle whose corners are 70000 vertices apart (a coarse tile's huge coastline polygon)
        var source = new uint[] { 0, 1, 70000 };
        var (ranges, target, vertices) = Cut(source, new VectorRange(0, RangeKind.Fill, 0, 3));
        var r = Assert.Single(ranges);
        // the same three places, now side by side at the end
        Assert.Equal(70001 + 3, vertices.Count);
        var corners = Enumerable.Range(0, 3).Select(i => vertices[r.BaseVertex + target[i]].Uv).ToList();
        Assert.Equal(new[] { vertices[0].Uv, vertices[1].Uv, vertices[70000].Uv }, corners);
    }
}
