using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace VL.Globe.Engine;

// ROADMAP 4g step 4 (2026-11-23): style expressions compiled once, evaluated per feature without walking
// JSON. Interpreted from their JSON on every feature they were most of the 200 MB a Tokyo z14 tile
// allocated while it was built - every operator name a new string (e[0].GetString()), filters in LINQ,
// closures, enumerators - and the workers' garbage paused vvvv's render thread (NOTES.md). GL JS
// compiles each expression once (style-spec createExpression); so do we, on first use, cached by the
// JSON element's identity, so no caller changes.
//
// The semantics are the interpreter's, case by case - MapLibre's render tests hold them - including
// when an error surfaces: an unsupported operator, or a malformed part, throws only when it is
// evaluated, as before (a branch never taken never threw). Constants are boxed once; what is computed
// (an interpolated number, a concatenated string) is still allocated, once per evaluation.
internal static partial class Expression
{
    // a JsonElement is its document and an index into it: the identity compiled expressions are cached by
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_parent")] static extern ref JsonDocument ParentOf(ref JsonElement e);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_idx")] static extern ref int IndexOf(ref JsonElement e);

    static readonly ConditionalWeakTable<JsonDocument, ConcurrentDictionary<int, Node>> Expressions = new();
    static readonly ConditionalWeakTable<JsonDocument, ConcurrentDictionary<int, FilterNode>> Filters = new();

    static readonly object BoxedTrue = true, BoxedFalse = false;
    static object Box(bool b) => b ? BoxedTrue : BoxedFalse;

    /// <summary>The compiled form of an expression (or value), compiled on first use.</summary>
    internal static Node Compiled(JsonElement e)
    {
        var doc = ParentOf(ref e);
        if (doc is null) return Compile(e);
        var byIndex = Expressions.GetValue(doc, static _ => new ConcurrentDictionary<int, Node>());
        int index = IndexOf(ref e);
        return byIndex.TryGetValue(index, out var node) ? node : byIndex.GetOrAdd(index, static (_, el) => Compile(el), e);
    }

    static FilterNode CompiledFilter(JsonElement f)
    {
        var doc = ParentOf(ref f);
        if (doc is null) return CompileFilter(f);
        var byIndex = Filters.GetValue(doc, static _ => new ConcurrentDictionary<int, FilterNode>());
        int index = IndexOf(ref f);
        return byIndex.TryGetValue(index, out var node) ? node : byIndex.GetOrAdd(index, static (_, el) => CompileFilter(el), f);
    }

    internal abstract class Node
    {
        public abstract object? Eval(StyleContext ctx, Kind want);
    }

    // whatever failed to compile throws when it is evaluated, as the interpreter threw then
    sealed class Throws(ExceptionDispatchInfo error) : Node
    {
        public override object? Eval(StyleContext ctx, Kind want) { error.Throw(); return null; }
    }

    static Node Compile(JsonElement e)
    {
        try { return CompileCore(e); }
        catch (Exception ex) { return new Throws(ExceptionDispatchInfo.Capture(ex)); }
    }

    static Node CompileCore(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => new Constant(e.GetDouble()),
        JsonValueKind.True => new Constant(BoxedTrue),
        JsonValueKind.False => new Constant(BoxedFalse),
        JsonValueKind.Null => new Constant(null),
        JsonValueKind.String => new Text(e.GetString()!),
        JsonValueKind.Object => new Function(e),
        // a literal array is the element itself
        JsonValueKind.Array when e.GetArrayLength() == 0 || e[0].ValueKind != JsonValueKind.String => new Constant(e),
        JsonValueKind.Array => Call.Of(e),
        _ => new Throws(ExceptionDispatchInfo.Capture(new StyleUnsupportedException($"value {e.ValueKind}"))),
    };

    sealed class Constant(object? value) : Node
    {
        public readonly object? Value = value;
        public override object? Eval(StyleContext ctx, Kind want) => Value;
    }

    // a string, or a colour where one is wanted (parsed once)
    sealed class Text(string value) : Node
    {
        object? _color;
        public override object? Eval(StyleContext ctx, Kind want)
        {
            if (want != Kind.Color) return value;
            return _color ??= StyleColor.Parse(value) ?? throw new StyleUnsupportedException($"colour '{value}'");
        }
    }

    enum Op
    {
        Literal, Zoom, Get, Has, GeometryType, Id, In, GlobalState, Coerce, Concat, Upcase, Downcase, ToColor, Coalesce,
        Rgb, Rgba, Add, Multiply, Subtract, Divide, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, Not, All, Any,
        Case, Match, Step, Interpolate,
    }

    sealed class Call : Node
    {
        readonly Op _op;
        readonly string _name;      // the operator as written (Coerce's kind, error messages)
        readonly Node[] _a;         // the arguments, compiled; [0] unused
        readonly int _n;
        readonly JsonElement _e;
        string? _key;               // get, has, global-state
        object?[]? _haystack;       // in, when the haystack is a literal array
        object?[][]? _labels;       // match: each branch's labels
        double[]? _stops;           // step: inputs; interpolate: inputs
        double _base = 1;           // interpolate

        Call(JsonElement e, Op op, string name)
        {
            _e = e; _op = op; _name = name; _n = e.GetArrayLength();
            _a = new Node[_n];
            for (int i = 1; i < _n; i++) _a[i] = Compile(e[i]);
        }

        public static Node Of(JsonElement e)
        {
            var name = e[0].GetString()!;
            Op? op = name switch
            {
                "literal" => Op.Literal, "zoom" => Op.Zoom, "get" => Op.Get, "has" => Op.Has, "geometry-type" => Op.GeometryType,
                "id" => Op.Id, "in" => Op.In, "global-state" => Op.GlobalState,
                "string" or "number" or "boolean" or "to-number" or "to-string" or "to-boolean" => Op.Coerce,
                "concat" => Op.Concat, "upcase" => Op.Upcase, "downcase" => Op.Downcase, "to-color" => Op.ToColor, "coalesce" => Op.Coalesce,
                "rgb" => Op.Rgb, "rgba" => Op.Rgba, "+" => Op.Add, "*" => Op.Multiply, "-" => Op.Subtract, "/" => Op.Divide,
                "==" => Op.Equal, "!=" => Op.NotEqual, "<" => Op.Less, "<=" => Op.LessEqual, ">" => Op.Greater, ">=" => Op.GreaterEqual,
                "!" => Op.Not, "all" => Op.All, "any" => Op.Any, "case" => Op.Case, "match" => Op.Match, "step" => Op.Step,
                "interpolate" => Op.Interpolate,
                _ => null,
            };
            if (op is null) return new Throws(ExceptionDispatchInfo.Capture(new StyleUnsupportedException($"expression '{name}'")));
            if (op == Op.Get && e.GetArrayLength() != 2) return new Throws(ExceptionDispatchInfo.Capture(new StyleUnsupportedException("get from an object")));
            var c = new Call(e, op.Value, name);
            switch (op)
            {
                case Op.Get: case Op.Has: case Op.GlobalState: c._key = e[1].GetString()!; break;
                case Op.In:
                {
                    // a literal haystack - [...] or ["literal", [...]] - read once
                    var hay = c._n > 2 ? c._a[2] : null;
                    if (hay is Call { _op: Op.Literal, _n: > 1 } literal) hay = literal._a[1];
                    if (hay is Constant { Value: JsonElement { ValueKind: JsonValueKind.Array } array })
                        c._haystack = array.EnumerateArray().Select(x => FromJson(x, Kind.Any)).ToArray();
                    break;
                }
                case Op.Match:
                {
                    var labels = new List<object?[]>();
                    for (int i = 2; i + 1 < c._n; i += 2)
                    {
                        var l = e[i];
                        labels.Add(l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => FromJson(x, Kind.Any)).ToArray() : new[] { FromJson(l, Kind.Any) });
                    }
                    c._labels = labels.ToArray();
                    break;
                }
                case Op.Step:
                {
                    var stops = new List<double>();
                    for (int i = 3; i + 1 < c._n; i += 2) stops.Add(e[i].GetDouble());
                    c._stops = stops.ToArray();
                    break;
                }
                case Op.Interpolate:
                {
                    var how = e[1];
                    var kind = how[0].GetString();
                    c._base = kind switch
                    {
                        "linear" => 1,
                        "exponential" => how[1].GetDouble(),
                        _ => throw new StyleUnsupportedException($"interpolate {kind}"),
                    };
                    var stops = new List<double>();
                    for (int i = 3; i + 1 < c._n; i += 2) stops.Add(e[i].GetDouble());
                    c._stops = stops.ToArray();
                    break;
                }
            }
            return c;
        }

        object? Arg(int i, StyleContext ctx, Kind k = Kind.Any) => _a[i].Eval(ctx, k);

        public override object? Eval(StyleContext ctx, Kind want)
        {
            int n = _n;
            switch (_op)
            {
                case Op.Literal: return _e[1].ValueKind == JsonValueKind.String && want == Kind.Color ? Arg(1, ctx, want) : Arg(1, ctx);
                case Op.Zoom: return ctx.Zoom;
                case Op.Get: return ctx.Properties != null && ctx.Properties.TryGetValue(_key!, out var p) ? FromValue(p, want) : null;
                case Op.Has: return Box(ctx.Properties?.ContainsKey(_key!) == true);
                case Op.GeometryType: return ctx.GeometryType;
                case Op.Id: return ctx.Id;
                case Op.In:
                {
                    var needle = Arg(1, ctx);
                    if (_haystack is { } hay)
                    {
                        foreach (var x in hay) if (Equals(x, needle)) return BoxedTrue;
                        return BoxedFalse;
                    }
                    return Arg(2, ctx) switch
                    {
                        JsonElement a when a.ValueKind == JsonValueKind.Array => Box(AnyEqual(a, needle)),
                        string s => Box(needle is string n2 && s.Contains(n2)),
                        null => BoxedFalse,
                        _ => throw new StyleUnsupportedException("in on a non-array"),
                    };
                }
                case Op.GlobalState: return ctx.GlobalState.TryGetValue(_key!, out var g) ? FromJson(g, want) : null;
                case Op.Coerce:
                    for (int i = 1; i < n; i++) { var v = Arg(i, ctx, want); if (v != null) return Coerce(_name, v); }
                    return null;
                case Op.Concat:
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 1; i < n; i++) { var v = Arg(i, ctx); if (v != null) sb.Append((string)Coerce("to-string", v)!); }
                    return sb.ToString();
                }
                case Op.Upcase: return Arg(1, ctx, Kind.String) is string up ? up.ToUpperInvariant() : null;
                case Op.Downcase: return Arg(1, ctx, Kind.String) is string down ? down.ToLowerInvariant() : null;
                case Op.ToColor:
                case Op.Coalesce:
                    for (int i = 1; i < n; i++)
                    {
                        var v = _op == Op.ToColor ? Arg(i, ctx, Kind.Color) : Arg(i, ctx, want);
                        if (v is string s && (_op == Op.ToColor || want == Kind.Color)) v = StyleColor.Parse(s);
                        if (v != null) return v;
                    }
                    return null;
                case Op.Rgb: case Op.Rgba:
                    return new StyleColor(ToNumber(Arg(1, ctx)) / 255, ToNumber(Arg(2, ctx)) / 255, ToNumber(Arg(3, ctx)) / 255, _op == Op.Rgba ? ToNumber(Arg(4, ctx)) : 1);
                case Op.Add: { double s = 0; for (int i = 1; i < n; i++) s += ToNumber(Arg(i, ctx)); return s; }
                case Op.Multiply: { double s = 1; for (int i = 1; i < n; i++) s *= ToNumber(Arg(i, ctx)); return s; }
                case Op.Subtract: return n == 2 ? -ToNumber(Arg(1, ctx)) : ToNumber(Arg(1, ctx)) - ToNumber(Arg(2, ctx));
                case Op.Divide: return ToNumber(Arg(1, ctx)) / ToNumber(Arg(2, ctx));
                case Op.Equal: return Box(Equals(Arg(1, ctx), Arg(2, ctx)));
                case Op.NotEqual: return Box(!Equals(Arg(1, ctx), Arg(2, ctx)));
                case Op.Less: return Box(Compare(Arg(1, ctx), Arg(2, ctx)) < 0);
                case Op.LessEqual: return Box(Compare(Arg(1, ctx), Arg(2, ctx)) <= 0);
                case Op.Greater: return Box(Compare(Arg(1, ctx), Arg(2, ctx)) > 0);
                case Op.GreaterEqual: return Box(Compare(Arg(1, ctx), Arg(2, ctx)) >= 0);
                case Op.Not: return Box(Arg(1, ctx) is not true);
                case Op.All: for (int i = 1; i < n; i++) if (Arg(i, ctx) is not true) return BoxedFalse; return BoxedTrue;
                case Op.Any: for (int i = 1; i < n; i++) if (Arg(i, ctx) is true) return BoxedTrue; return BoxedFalse;
                case Op.Case:
                    for (int i = 1; i + 1 < n; i += 2) if (Arg(i, ctx) is true) return Arg(i + 1, ctx, want);
                    return Arg(n - 1, ctx, want);
                case Op.Match:
                {
                    var input = Arg(1, ctx);
                    var labels = _labels!;
                    for (int b = 0, i = 2; i + 1 < n; b++, i += 2)
                        foreach (var l in labels[b])
                            if (Equals(l, input)) return Arg(i + 1, ctx, want);
                    return Arg(n - 1, ctx, want);
                }
                case Op.Step:
                {
                    double x = ToNumber(Arg(1, ctx));
                    int pick = 2;
                    var stops = _stops!;
                    for (int s = 0, i = 3; i + 1 < n; s++, i += 2) if (x >= stops[s]) pick = i + 1; else break;
                    return Arg(pick, ctx, want);
                }
                case Op.Interpolate:
                {
                    double x = ToNumber(Arg(2, ctx));
                    var stops = _stops!;
                    // every stop's output evaluated, as the interpreter did; only two are used
                    var values = ArrayPool<object?>.Shared.Rent(stops.Length);
                    try
                    {
                        for (int s = 0, i = 4; s < stops.Length; s++, i += 2) values[s] = Arg(i, ctx, want);
                        return InterpolateValues(stops, values.AsSpan(0, stops.Length), x, _base, want);
                    }
                    finally { Array.Clear(values, 0, stops.Length); ArrayPool<object?>.Shared.Return(values); }
                }
            }
            throw new StyleUnsupportedException($"expression '{_name}'");
        }
    }

    static bool AnyEqual(JsonElement array, object? needle)
    {
        foreach (var x in array.EnumerateArray()) if (Equals(FromJson(x, Kind.Any), needle)) return true;
        return false;
    }

    // MapLibre's interpolation between the stops around x (InterpolateValues, without the list)
    static object? InterpolateValues(ReadOnlySpan<double> inputs, ReadOnlySpan<object?> values, double x, double b, Kind want)
    {
        if (x <= inputs[0]) return values[0];
        if (x >= inputs[^1]) return values[^1];
        int i = 1;
        while (inputs[i] < x) i++;
        double x0 = inputs[i - 1], x1 = inputs[i];
        object? v0 = values[i - 1], v1 = values[i];
        double d = x1 - x0, p = x - x0;
        double t = d == 0 ? 0 : b == 1 ? p / d : (Math.Pow(b, p) - 1) / (Math.Pow(b, d) - 1);
        return (v0, v1) switch
        {
            (double a, double c) => a + (c - a) * t,
            (StyleColor a, StyleColor c) => StyleColor.Lerp(a, c, t),
            // arrays of numbers (icon-offset, text-offset) element by element, as GL JS's interpolate.array
            _ when Numbers(v0) is { } a && Numbers(v1) is { } c && a.Length == c.Length
                => a.Select((ai, k) => (object?)(ai + (c[k] - ai) * t)).ToList(),
            _ => throw new StyleUnsupportedException($"interpolating {v0?.GetType().Name}"),
        };
    }

    // legacy functions: {stops, base, property, type, default, colorSpace}
    sealed class Function : Node
    {
        readonly string? _badColorSpace;
        readonly double _base = 1;
        readonly string? _type;
        readonly string? _property;
        readonly Node? _default;
        readonly ExceptionDispatchInfo? _stopsError;   // stops that did not read: thrown when they are needed
        readonly bool _hasStops;
        // plain: one curve. Zoom-and-property: one curve per zoom, each over the property
        readonly Stops? _plain;
        readonly double[]? _zooms;
        readonly Stops[]? _byZoom;

        public Function(JsonElement f)
        {
            if (f.TryGetProperty("colorSpace", out var cs) && cs.GetString() != "rgb") _badColorSpace = cs.GetString();
            if (f.TryGetProperty("base", out var bb)) _base = bb.GetDouble();
            if (f.TryGetProperty("type", out var t)) _type = t.GetString()!;
            if (f.TryGetProperty("property", out var prop)) _property = prop.GetString()!;
            if (f.TryGetProperty("default", out var d)) _default = Compile(d);
            if (!(_hasStops = f.TryGetProperty("stops", out var stopsJson))) return;
            try
            {
                var stops = stopsJson.EnumerateArray().Select(s => (key: s[0], output: s[1])).ToList();
                if (stops.Count > 0 && stops[0].key.ValueKind == JsonValueKind.Object)
                {
                    var groups = stops.GroupBy(s => s.key.GetProperty("zoom").GetDouble()).ToList();
                    _zooms = groups.Select(g => g.Key).ToArray();
                    _byZoom = groups.Select(g => new Stops(g.Select(s => (s.key.GetProperty("value"), s.output)).ToList())).ToArray();
                }
                else _plain = new Stops(stops);
            }
            catch (Exception ex) { _stopsError = ExceptionDispatchInfo.Capture(ex); }
        }

        public override object? Eval(StyleContext ctx, Kind want)
        {
            if (_badColorSpace != null) throw new StyleUnsupportedException($"colorSpace {_badColorSpace}");
            bool interpolatable = want is Kind.Number or Kind.Color or Kind.NumberArray;
            string type = _type ?? (interpolatable ? "exponential" : "interval");
            if (type == "identity")
            {
                if (_property is null) return ctx.Zoom;
                return ctx.Properties != null && ctx.Properties.TryGetValue(_property, out var iv) ? FromValue(iv, want) ?? Default() : Default();
            }
            if (!_hasStops) throw new StyleUnsupportedException("a function without stops");
            _stopsError?.Throw();
            if (_byZoom is { } byZoom)
            {
                var input = PropertyInput();
                var values = ArrayPool<object?>.Shared.Rent(byZoom.Length);
                try
                {
                    for (int i = 0; i < byZoom.Length; i++) values[i] = byZoom[i].Evaluate(input, type, _base, ctx, want);
                    // zoom curves of composite functions use base 1 between zoom groups
                    return InterpolateValues(_zooms!, values.AsSpan(0, byZoom.Length), ctx.Zoom, 1, want);
                }
                finally { Array.Clear(values, 0, byZoom.Length); ArrayPool<object?>.Shared.Return(values); }
            }
            return _plain!.Evaluate(_property != null ? PropertyInput() : ctx.Zoom, type, _base, ctx, want) ?? Default();

            object? PropertyInput() => ctx.Properties != null && ctx.Properties.TryGetValue(_property!, out var pv) ? pv : null;
            object? Default() => _default?.Eval(ctx, want);
        }
    }

    // a legacy function's stops: keys as values (categorical) and as numbers (interval, exponential)
    sealed class Stops
    {
        readonly object?[] _keys;
        readonly double[]? _numbers;
        readonly ExceptionDispatchInfo? _notNumbers;
        readonly Node[] _outputs;

        public Stops(List<(JsonElement key, JsonElement output)> stops)
        {
            _keys = stops.Select(s => FromJson(s.key, Kind.Any)).ToArray();
            _outputs = stops.Select(s => Compile(s.output)).ToArray();
            try { _numbers = stops.Select(s => s.key.GetDouble()).ToArray(); }
            catch (Exception ex) { _notNumbers = ExceptionDispatchInfo.Capture(ex); }
        }

        public object? Evaluate(object? input, string type, double b, StyleContext ctx, Kind want)
        {
            if (input is null) return null;
            switch (type)
            {
                case "categorical":
                    for (int i = 0; i < _keys.Length; i++) if (Equals(_keys[i], input)) return _outputs[i].Eval(ctx, want);
                    return null;
                case "interval":
                {
                    if (input is not double x) return null;
                    var pick = _outputs[0];
                    for (int i = 0; i < _outputs.Length; i++) { if (x >= Number(i)) pick = _outputs[i]; else break; }
                    return pick.Eval(ctx, want);
                }
                case "exponential":
                {
                    if (input is not double xe) return null;
                    _notNumbers?.Throw();   // the keys read before the outputs, as the interpreter read them
                    var values = ArrayPool<object?>.Shared.Rent(_outputs.Length);
                    try
                    {
                        for (int i = 0; i < _outputs.Length; i++) values[i] = _outputs[i].Eval(ctx, want);
                        return InterpolateValues(_numbers!, values.AsSpan(0, _outputs.Length), xe, b, want);
                    }
                    finally { Array.Clear(values, 0, _outputs.Length); ArrayPool<object?>.Shared.Return(values); }
                }
            }
            throw new StyleUnsupportedException($"function type {type}");
        }

        double Number(int i)
        {
            if (_notNumbers != null) _notNumbers.Throw();
            return _numbers![i];
        }
    }

    // filters: an expression, or the legacy syntax compiled once ($type the geometry type, $id the id)
    abstract class FilterNode
    {
        public abstract bool Test(StyleContext ctx);
    }

    sealed class FilterThrows(ExceptionDispatchInfo error) : FilterNode
    {
        public override bool Test(StyleContext ctx) { error.Throw(); return false; }
    }

    sealed class ExpressionFilter(Node node) : FilterNode
    {
        public override bool Test(StyleContext ctx) => node.Eval(ctx, Kind.Any) is true;
    }

    static FilterNode CompileFilter(JsonElement f)
    {
        try { return IsExpressionFilter(f) ? new ExpressionFilter(Compile(f)) : CompileLegacy(f); }
        catch (Exception ex) { return new FilterThrows(ExceptionDispatchInfo.Capture(ex)); }
    }

    static FilterNode CompileLegacy(JsonElement f)
    {
        try
        {
            // an expression inside a legacy tree ("any" of both kinds) is evaluated as one
            if (IsExpressionFilter(f)) return new ExpressionFilter(Compile(f));
            return new Legacy(f);
        }
        catch (Exception ex) { return new FilterThrows(ExceptionDispatchInfo.Capture(ex)); }
    }

    sealed class Legacy : FilterNode
    {
        enum Kind2 { All, Any, None, Has, NotHas, In, NotIn, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual }
        enum KeyKind { Type, Id, Property }

        readonly Kind2 _op;
        readonly FilterNode[]? _children;
        readonly string? _key;
        readonly KeyKind _keyKind;
        readonly object?[]? _values;
        readonly object? _value;

        public Legacy(JsonElement f)
        {
            var op = f[0].GetString();
            _op = op switch
            {
                "all" => Kind2.All, "any" => Kind2.Any, "none" => Kind2.None, "has" => Kind2.Has, "!has" => Kind2.NotHas,
                "in" => Kind2.In, "!in" => Kind2.NotIn, "==" => Kind2.Equal, "!=" => Kind2.NotEqual,
                "<" => Kind2.Less, "<=" => Kind2.LessEqual, ">" => Kind2.Greater, ">=" => Kind2.GreaterEqual,
                _ => throw new StyleUnsupportedException($"legacy filter '{op}'"),
            };
            if (_op is Kind2.All or Kind2.Any or Kind2.None)
            {
                _children = f.EnumerateArray().Skip(1).Select(CompileLegacy).ToArray();
                return;
            }
            _key = f[1].GetString()!;
            _keyKind = _key switch { "$type" => KeyKind.Type, "$id" => KeyKind.Id, _ => KeyKind.Property };
            if (_op is Kind2.In or Kind2.NotIn) _values = f.EnumerateArray().Skip(2).Select(x => FromJson(x, Expression.Kind.Any)).ToArray();
            else if (_op is not (Kind2.Has or Kind2.NotHas)) _value = FromJson(f[2], Expression.Kind.Any);
        }

        object? Key(StyleContext ctx) => _keyKind switch
        {
            KeyKind.Type => ctx.GeometryType,
            KeyKind.Id => ctx.Id,
            _ => ctx.Properties != null && ctx.Properties.TryGetValue(_key!, out var v) ? v : null,
        };

        bool Has(StyleContext ctx) => _keyKind switch
        {
            KeyKind.Type => true,
            KeyKind.Id => ctx.Id != null,
            _ => ctx.Properties?.ContainsKey(_key!) == true,
        };

        public override bool Test(StyleContext ctx)
        {
            switch (_op)
            {
                case Kind2.All: foreach (var c in _children!) if (!c.Test(ctx)) return false; return true;
                case Kind2.Any: foreach (var c in _children!) if (c.Test(ctx)) return true; return false;
                case Kind2.None: foreach (var c in _children!) if (c.Test(ctx)) return false; return true;
                case Kind2.Has: return Has(ctx);
                case Kind2.NotHas: return !Has(ctx);
                case Kind2.In:
                case Kind2.NotIn:
                {
                    var v = Key(ctx);
                    bool found = false;
                    foreach (var x in _values!) if (Equals(x, v)) { found = true; break; }
                    return _op == Kind2.In ? found : !found;
                }
                case Kind2.Equal: return Equals(Key(ctx), _value);
                case Kind2.NotEqual: return !Equals(Key(ctx), _value);
                default:
                {
                    var a = Key(ctx);
                    var b = _value;
                    if (a is double x && b is double y) return _op switch { Kind2.Less => x < y, Kind2.LessEqual => x <= y, Kind2.Greater => x > y, _ => x >= y };
                    if (a is string s && b is string t)
                    {
                        int c = string.CompareOrdinal(s, t);
                        return _op switch { Kind2.Less => c < 0, Kind2.LessEqual => c <= 0, Kind2.Greater => c > 0, _ => c >= 0 };
                    }
                    return false;
                }
            }
        }
    }
}
