using System.Collections.Generic;
using System.Text.Json;

namespace VL.Globe.Tests.Styling;

// ROADMAP 4g step 4: style expressions compiled once and evaluated per feature without allocating
// (interpreted from JSON they were most of what a tile allocated while it was built, and the garbage
// paused vvvv's render thread). MapLibre's render tests hold the semantics; this holds the cost.
public class ExpressionCompilerFacts
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void An_expression_is_compiled_once()
    {
        var doc = Json("""{ "a": ["==", ["get", "class"], "main"], "b": ["==", ["get", "class"], "main"] }""");
        var a = doc.GetProperty("a");
        Assert.Same(Expression.Compiled(a), Expression.Compiled(a));
        Assert.Same(Expression.Compiled(a), Expression.Compiled(doc.GetProperty("a")));   // the same element, found again
        Assert.NotSame(Expression.Compiled(a), Expression.Compiled(doc.GetProperty("b")));   // equal text, another element
    }

    [Fact]
    public void Typeof_names_a_value_s_type_as_GL_JS_does()
    {
        // Liberty's boundary_3 (prefecture and state borders) filters on it; dropped until 2026-11-23
        var doc = Json("""
            { "n": ["typeof", ["get", "admin_level"]], "s": ["typeof", "a"], "b": ["typeof", true], "z": ["typeof", ["get", "none"]],
              "a": ["typeof", ["literal", [1, 2]]], "m": ["typeof", ["literal", [1, "x"]]],
              "f": ["all", ["==", ["typeof", ["get", "admin_level"]], "number"], [">=", ["get", "admin_level"], 3]] }
            """);
        var ctx = new StyleContext { Zoom = 8, Properties = new Dictionary<string, object?> { ["admin_level"] = 4.0 } };
        string T(string k) => (string)Expression.Evaluate(doc.GetProperty(k), ctx, Expression.Kind.Any)!;
        Assert.Equal("number", T("n"));
        Assert.Equal("string", T("s"));
        Assert.Equal("boolean", T("b"));
        Assert.Equal("null", T("z"));
        Assert.Equal("array<number, 2>", T("a"));
        Assert.Equal("array<value, 2>", T("m"));
        Assert.True(Expression.Filter(doc.GetProperty("f"), ctx));
    }

    [Fact]
    public void A_filter_and_a_value_evaluate_per_feature_without_allocating()
    {
        // Liberty-like: a filter, a match, an interpolate by zoom, a legacy filter
        var doc = Json("""
            {
              "filter": ["all", ["==", ["geometry-type"], "LineString"], ["in", ["get", "class"], ["literal", [1, 2, 3]]], ["!=", ["get", "brunnel"], "tunnel"]],
              "legacy": ["all", ["==", "$type", "LineString"], ["in", "class", "minor", "service"], ["!has", "tunnel"]],
              "color": ["match", ["get", "class"], ["minor", "service"], "#ffffff", "#cccccc"],
              "width": ["interpolate", ["exponential", 1.2], ["zoom"], 12, 0.5, 20, ["case", ["==", ["get", "class"], "minor"], 13, 8]]
            }
            """);
        var ctx = new StyleContext { Zoom = 15, GeometryType = "LineString", Properties = new Dictionary<string, object?> { ["class"] = "minor", ["brunnel"] = "bridge" } };
        var filter = doc.GetProperty("filter");
        var legacy = doc.GetProperty("legacy");
        var color = doc.GetProperty("color");
        var width = doc.GetProperty("width");
        Assert.False(Expression.Filter(filter, ctx));   // "minor" is not 1, 2 or 3
        Assert.True(Expression.Filter(legacy, ctx));
        Assert.Equal(StyleColor.Parse("#ffffff"), Expression.Color(color, ctx, StyleColor.Black));
        Assert.InRange(Expression.Number(width, ctx, 0), 0.5, 13);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Expression.Filter(filter, ctx);
            Expression.Filter(legacy, ctx);
            Expression.Color(color, ctx, StyleColor.Black);
        }
        long filtersAndColours = GC.GetAllocatedBytesForCurrentThread() - before;
        // the interpreter allocated kilobytes for each of these; compiled, only a boxed zoom or number may remain
        Assert.True(filtersAndColours < 1000 * 64, $"{filtersAndColours / 1000.0:F0} bytes a feature");
    }
}
