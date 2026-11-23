using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace VL.Globe.Engine;

// A SUBSET of the MapLibre Style Specification (https://maplibre.org/maplibre-style-spec/), read
// from style JSON and evaluated on the CPU so a style can be drawn into the draped tile overlays
// (ROADMAP 0.1 items 4b and 4c). What is not in the subset throws StyleUnsupportedException with
// the reason, so a render test can say "unsupported: line-dasharray" instead of drawing something
// else in silence.
//
// The subset: background, fill and line layers; literals, legacy functions (zoom, property and
// zoom-and-property; exponential, interval, categorical, identity) and the expressions listed in
// Expression.Evaluate; colours as CSS strings, interpolated in RGB.

/// <summary>Thrown for any part of a style outside the subset; the message names it.</summary>
internal sealed class StyleUnsupportedException : Exception
{
    public StyleUnsupportedException(string what) : base(what) { }
}

/// <summary>A straight (not premultiplied) RGBA colour, each channel 0..1.</summary>
internal readonly record struct StyleColor(double R, double G, double B, double A)
{
    public static readonly StyleColor Black = new(0, 0, 0, 1);
    public static readonly StyleColor Transparent = new(0, 0, 0, 0);

    public StyleColor WithOpacity(double opacity) => this with { A = A * opacity };

    public static StyleColor Lerp(StyleColor a, StyleColor b, double t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    public override string ToString() => FormattableString.Invariant($"rgba({R * 255:0.#},{G * 255:0.#},{B * 255:0.#},{A:0.###})");

    /// <summary>Parses a CSS colour (hex, rgb[a](), hsl[a](), a named colour); null when it is not one.</summary>
    public static StyleColor? Parse(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (s[0] == '#') return ParseHex(s.Substring(1));
        if (Named.TryGetValue(s, out var hex)) return hex.Length == 0 ? Transparent : ParseHex(hex);
        int open = s.IndexOf('('), close = s.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var fn = s.Substring(0, open).Trim();
        var args = s.Substring(open + 1, close - open - 1)
            .Replace('/', ' ').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (args.Length is < 3 or > 4) return null;
        double alpha = args.Length == 4 ? Number(args[3], 1) : 1;
        if (double.IsNaN(alpha)) return null;
        switch (fn)
        {
            case "rgb":
            case "rgba":
            {
                var c = args.Take(3).Select(a => Number(a, 255)).ToArray();
                if (c.Any(double.IsNaN)) return null;
                return new StyleColor(Clamp(c[0] / 255), Clamp(c[1] / 255), Clamp(c[2] / 255), Clamp(alpha));
            }
            case "hsl":
            case "hsla":
            {
                double h = double.Parse(args[0].Replace("deg", ""), CultureInfo.InvariantCulture);
                double sat = Number(args[1], 100) / 100, l = Number(args[2], 100) / 100;
                if (double.IsNaN(sat) || double.IsNaN(l)) return null;
                return FromHsl(h, Clamp(sat), Clamp(l), Clamp(alpha));
            }
            default:
                return null;
        }
    }

    // "50%" -> 50% of full; "0.5" as is
    static double Number(string s, double full)
    {
        bool pct = s.EndsWith("%");
        if (!double.TryParse(pct ? s.TrimEnd('%') : s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return double.NaN;
        return pct ? v / 100 * full : v;
    }

    static double Clamp(double v) => Math.Max(0, Math.Min(1, v));

    static StyleColor? ParseHex(string h)
    {
        if (h.Length is 3 or 4) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length is not (6 or 8)) return null;
        if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        if (h.Length == 6) v = (v << 8) | 0xFF;
        return new StyleColor((v >> 24 & 0xFF) / 255.0, (v >> 16 & 0xFF) / 255.0, (v >> 8 & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    static StyleColor FromHsl(double h, double s, double l, double a)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        return new StyleColor(Hue(h + 1.0 / 3), Hue(h), Hue(h - 1.0 / 3), a);
    }

    // CSS Color Module Level 4 named colours; "" is transparent
    static readonly Dictionary<string, string> Named = (
        "transparent:,aliceblue:f0f8ff,antiquewhite:faebd7,aqua:00ffff,aquamarine:7fffd4,azure:f0ffff,beige:f5f5dc," +
        "bisque:ffe4c4,black:000000,blanchedalmond:ffebcd,blue:0000ff,blueviolet:8a2be2,brown:a52a2a,burlywood:deb887," +
        "cadetblue:5f9ea0,chartreuse:7fff00,chocolate:d2691e,coral:ff7f50,cornflowerblue:6495ed,cornsilk:fff8dc," +
        "crimson:dc143c,cyan:00ffff,darkblue:00008b,darkcyan:008b8b,darkgoldenrod:b8860b,darkgray:a9a9a9," +
        "darkgreen:006400,darkgrey:a9a9a9,darkkhaki:bdb76b,darkmagenta:8b008b,darkolivegreen:556b2f," +
        "darkorange:ff8c00,darkorchid:9932cc,darkred:8b0000,darksalmon:e9967a,darkseagreen:8fbc8f," +
        "darkslateblue:483d8b,darkslategray:2f4f4f,darkslategrey:2f4f4f,darkturquoise:00ced1,darkviolet:9400d3," +
        "deeppink:ff1493,deepskyblue:00bfff,dimgray:696969,dimgrey:696969,dodgerblue:1e90ff,firebrick:b22222," +
        "floralwhite:fffaf0,forestgreen:228b22,fuchsia:ff00ff,gainsboro:dcdcdc,ghostwhite:f8f8ff,gold:ffd700," +
        "goldenrod:daa520,gray:808080,green:008000,greenyellow:adff2f,grey:808080,honeydew:f0fff0,hotpink:ff69b4," +
        "indianred:cd5c5c,indigo:4b0082,ivory:fffff0,khaki:f0e68c,lavender:e6e6fa,lavenderblush:fff0f5," +
        "lawngreen:7cfc00,lemonchiffon:fffacd,lightblue:add8e6,lightcoral:f08080,lightcyan:e0ffff," +
        "lightgoldenrodyellow:fafad2,lightgray:d3d3d3,lightgreen:90ee90,lightgrey:d3d3d3,lightpink:ffb6c1," +
        "lightsalmon:ffa07a,lightseagreen:20b2aa,lightskyblue:87cefa,lightslategray:778899,lightslategrey:778899," +
        "lightsteelblue:b0c4de,lightyellow:ffffe0,lime:00ff00,limegreen:32cd32,linen:faf0e6,magenta:ff00ff," +
        "maroon:800000,mediumaquamarine:66cdaa,mediumblue:0000cd,mediumorchid:ba55d3,mediumpurple:9370db," +
        "mediumseagreen:3cb371,mediumslateblue:7b68ee,mediumspringgreen:00fa9a,mediumturquoise:48d1cc," +
        "mediumvioletred:c71585,midnightblue:191970,mintcream:f5fffa,mistyrose:ffe4e1,moccasin:ffe4b5," +
        "navajowhite:ffdead,navy:000080,oldlace:fdf5e6,olive:808000,olivedrab:6b8e23,orange:ffa500," +
        "orangered:ff4500,orchid:da70d6,palegoldenrod:eee8aa,palegreen:98fb98,paleturquoise:afeeee," +
        "palevioletred:db7093,papayawhip:ffefd5,peachpuff:ffdab9,peru:cd853f,pink:ffc0cb,plum:dda0dd," +
        "powderblue:b0e0e6,purple:800080,rebeccapurple:663399,red:ff0000,rosybrown:bc8f8f,royalblue:4169e1," +
        "saddlebrown:8b4513,salmon:fa8072,sandybrown:f4a460,seagreen:2e8b57,seashell:fff5ee,sienna:a0522d," +
        "silver:c0c0c0,skyblue:87ceeb,slateblue:6a5acd,slategray:708090,slategrey:708090,snow:fffafa," +
        "springgreen:00ff7f,steelblue:4682b4,tan:d2b48c,teal:008080,thistle:d8bfd8,tomato:ff6347," +
        "turquoise:40e0d0,violet:ee82ee,wheat:f5deb3,white:ffffff,whitesmoke:f5f5f5,yellow:ffff00,yellowgreen:9acd32")
        .Split(',').Select(p => p.Split(':')).ToDictionary(p => p[0], p => p[1]);
}

/// <summary>What an expression is evaluated against: the zoom, one feature's properties, the style's global state.</summary>
internal sealed class StyleContext
{
    public double Zoom;
    // values are double, string, bool or null (a GeoJSON object or array property: its JsonElement)
    public IReadOnlyDictionary<string, object?>? Properties;
    public object? Id;
    public string? GeometryType;    // "Point", "LineString", "Polygon"
    public IReadOnlyDictionary<string, JsonElement> GlobalState = new Dictionary<string, JsonElement>();
    // the style the features are built for: a lenient one leaves out a feature its filter cannot evaluate
    public MapStyleDocument? Document;
}

/// <summary>Evaluates a style property value: a literal, a legacy function or an expression.</summary>
internal static partial class Expression
{
    // A property that evaluates to null (a missing feature property, no matching stop) takes the
    // spec's default, as in MapLibre.

    /// <summary>The value of a paint/layout property as a number; <paramref name="fallback"/> when absent or null.</summary>
    public static double Number(JsonElement? value, StyleContext ctx, double fallback)
        => value is { } v && Evaluate(v, ctx, Kind.Number) is { } r ? ToNumber(r) : fallback;

    /// <summary>The value of a paint/layout property as a colour; <paramref name="fallback"/> when absent or null.</summary>
    public static StyleColor Color(JsonElement? value, StyleContext ctx, StyleColor fallback)
        => value is { } v && Evaluate(v, ctx, Kind.Color) is { } r ? ToColor(r) : fallback;

    /// <summary>The value of a paint/layout property as a string (an enum such as line-cap); <paramref name="fallback"/> when absent or null.</summary>
    public static string String(JsonElement? value, StyleContext ctx, string fallback)
        => value is { } v && Evaluate(v, ctx, Kind.String) is { } r ? r as string ?? throw new StyleUnsupportedException("a string property that is not a string") : fallback;

    /// <summary>A layer filter, expression or legacy syntax: true when the feature is drawn.</summary>
    public static bool Filter(JsonElement filter, StyleContext ctx) => CompiledFilter(filter).Test(ctx);

    /// <summary>
    /// A feature's filter while a layer is built: in a lenient style a feature it cannot evaluate (a number
    /// compared with a string) is left out, as GL JS's evaluate returns the default and the feature goes;
    /// strict (the render tests), it throws.
    /// </summary>
    // One feature's error dropped the whole layer: Liberty's road shields were never drawn ("comparing
    // different types", one ref of another type), 2026-11-23.
    public static bool FilterFeature(JsonElement filter, StyleContext ctx, string layerId)
    {
        try { return Filter(filter, ctx); }
        catch (StyleUnsupportedException e) when (ctx.Document is { Lenient: true } doc)
        {
            doc.Skip($"{layerId}: {e.Message} (a feature its filter could not evaluate left out)");
            return false;
        }
    }

    // MapLibre's isExpressionFilter (style-spec feature_filter): which syntax a filter is written in
    static bool IsExpressionFilter(JsonElement f)
    {
        if (f.ValueKind is JsonValueKind.True or JsonValueKind.False) return true;
        if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() == 0) return false;
        int n = f.GetArrayLength();
        switch (f[0].GetString())
        {
            case "has": return n >= 2 && f[1].GetString() is not ("$id" or "$type");
            case "in": return n >= 3 && (f[1].ValueKind != JsonValueKind.String || f[2].ValueKind == JsonValueKind.Array);
            case "!in": case "!has": case "none": return false;
            case "==": case "!=": case ">": case ">=": case "<": case "<=":
                return n != 3 || f[1].ValueKind == JsonValueKind.Array || f[2].ValueKind == JsonValueKind.Array;
            case "any": case "all":
                return f.EnumerateArray().Skip(1).All(x => IsExpressionFilter(x) || x.ValueKind is JsonValueKind.True or JsonValueKind.False);
            default: return true;
        }
    }

    /// <summary>True when the value depends on the feature (so it is evaluated per feature).</summary>
    public static bool IsFeatureDependent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.TryGetProperty("property", out _),
        JsonValueKind.Array => value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.String &&
            (value[0].GetString() is "get" or "has" or "properties" or "geometry-type" or "id" ||
             value.EnumerateArray().Skip(1).Any(IsFeatureDependent)),
        _ => false,
    };

    // NumberArray: an interpolatable array of numbers (icon-offset, text-offset); otherwise as Any
    public enum Kind { Any, Number, Color, String, NumberArray }

    /// <summary>An expression's value: compiled on first use (StyleExpressionCompiler.cs), then evaluated without walking its JSON.</summary>
    public static object? Evaluate(JsonElement e, StyleContext ctx, Kind want) => Compiled(e).Eval(ctx, want);

    static double[]? Numbers(object? v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } a when a.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number)
            => a.EnumerateArray().Select(e => e.GetDouble()).ToArray(),
        IReadOnlyList<object?> l when l.All(e => e is double) => l.Select(e => (double)e!).ToArray(),
        _ => null,
    };

    static object? FromValue(object? v, Kind want) => v is string s && want == Kind.Color ? StyleColor.Parse(s) : v;

    /// <summary>A JSON value as an expression value: double, string, bool, null, or the element itself.</summary>
    public static object? FromJson(JsonElement e, Kind want) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => want == Kind.Color ? StyleColor.Parse(e.GetString()!) : e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e,
    };

    static object? Coerce(string op, object v) => op switch
    {
        "to-number" => v is string s ? double.Parse(s, CultureInfo.InvariantCulture) : v is bool b ? (b ? 1.0 : 0.0) : v,
        // JavaScript's String(): "true", not .NET's "True"
        "to-string" => v switch { double d => d.ToString(CultureInfo.InvariantCulture), bool b => b ? "true" : "false", _ => v.ToString() },
        "to-boolean" => v switch { bool b => b, double d => d != 0 && !double.IsNaN(d), string s => s.Length > 0, _ => true },
        _ => v,
    };

    static new bool Equals(object? a, object? b) => (a, b) switch
    {
        (double x, double y) => x == y,
        (null, null) => true,
        (null, _) or (_, null) => false,
        _ => a.Equals(b),
    };

    static int Compare(object? a, object? b) => (a, b) switch
    {
        (double x, double y) => x.CompareTo(y),
        (string x, string y) => string.CompareOrdinal(x, y),
        _ => throw new StyleUnsupportedException("comparing different types"),
    };

    static double ToNumber(object? v) => v switch
    {
        double d => d,
        null => throw new StyleUnsupportedException("a number that evaluated to null"),
        _ => throw new StyleUnsupportedException($"a number that evaluated to {v.GetType().Name}"),
    };

    static StyleColor ToColor(object? v) => v switch
    {
        StyleColor c => c,
        string s => StyleColor.Parse(s) ?? throw new StyleUnsupportedException($"colour '{s}'"),
        _ => throw new StyleUnsupportedException($"a colour that evaluated to {v?.GetType().Name ?? "null"}"),
    };
}
