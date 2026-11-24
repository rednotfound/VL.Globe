using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BruTile;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Extensions;   // ToMeshDraw
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.Import;
using Buffer = Stride.Graphics.Buffer;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe;

// The Globe node's drawing: which tiles are in the scene, the stand-ins for squares not yet loaded, and the fades both ways.
public sealed partial class GlobeNode
{
    // The fade's two weights, keyed so the material can be told them every frame without being
    // rebuilt: own * In + ancestor * Out. A float4 per weight, multiplied into each image.
    static readonly ValueParameterKey<Vector4> FadeInKey = ParameterKeys.NewValue(Vector4.One, "Globe.FadeIn");
    static readonly ValueParameterKey<Vector4> FadeOutKey = ParameterKeys.NewValue(Vector4.Zero, "Globe.FadeOut");

    double _fadeDuration = 0.3;

    // Zooming out: a parent about to replace descendants that are drawn. For one fade duration the
    // descendants stay drawn instead, each fading to its part of the parent's image; then the parent.
    // Never two layers on one spot, so nothing to sort and nothing to z-fight. (MapLibre fades
    // many-to-one both ways; the user, 2026-11-20: "都加呗".)
    sealed class OutFade { public required List<TileKey> From; public double Start; public bool Done; }
    readonly Dictionary<TileKey, OutFade> _outFades = new();

    // DrawSet's StandIns: a square not yet loaded, drawn with its own mesh and the matching part of an
    // arrived ancestor's image - at the surface, never two things on one spot. Kept by square so a
    // square that stays missing for many frames is built once; dropped when no longer drawn.
    sealed class StandIn
    {
        public required Entity Entity;
        public required Buffer Vertices;
        public required Buffer Indices;
        public required D3 Centre;
        public required ModelComponent Model;
        public TileKey From;
        public int FromOverlayVersion;
        public void Dispose() { Vertices.Dispose(); Indices.Dispose(); }
    }
    readonly Dictionary<TileKey, StandIn> _standIns = new();

    void Show(DrawSet.Layers layers)
    {
        var draw = layers.Top;
        var now = _clock.Elapsed.TotalSeconds;
        var before = new HashSet<TileKey>(_shown);
        var borrowed = _standIns.ToDictionary(kv => kv.Key, kv => kv.Value.From);   // what each square showed last frame
        ShowStandIns(layers.StandIns);
        var drawn = new HashSet<TileKey>(draw);
        foreach (var k in _outFades.Keys.Where(k => !drawn.Contains(k)).ToList()) _outFades.Remove(k);
        var display = new List<TileKey>();
        foreach (var t in draw)
        {
            _outFades.TryGetValue(t, out var of);
            if (of is null && _fadeDuration > 0 && !before.Contains(t))
            {
                var under = before.Where(t.IsAncestorOf).ToList();
                if (under.Count > 0 && Fade.Covers(t, under) && under.All(_loaded.ContainsKey))
                    _outFades[t] = of = new OutFade { From = under, Start = now };
            }
            if (of is { Done: false } && Fade.Progress(of.Start, now, _fadeDuration) < 1 && of.From.All(_loaded.ContainsKey))
            {
                display.AddRange(of.From);
                foreach (var d in of.From)
                {
                    var ds = _loaded[d];
                    if (ds.Fading is null || !ds.FadeOut || ds.FadeFrom != t) StartFade(ds, d, t, _loaded[t], outward: true, of.Start);
                }
            }
            else
            {
                if (of is not null) of.Done = true;
                display.Add(t);
            }
        }
        draw = display;
        var next = new HashSet<TileKey>(draw);
        foreach (var t in _shown.Where(t => !next.Contains(t)).ToList())
        {
            if (_loaded.TryGetValue(t, out var s)) { _root.Transform.Children.Remove(s.Entity.Transform); EndFade(s); }
            _shown.Remove(t);
        }
        foreach (var t in draw)
        {
            if (_shown.Contains(t)) continue;
            var s = _loaded[t];
            _root.Transform.Children.Add(s.Entity.Transform);
            _shown.Add(t);
            s.LastUsed = _frame;
            // A finer tile replacing the ancestor drawn here last frame fades in over it. Not the other
            // way (a parent replacing its children) and not over nothing - the pop that shows is the
            // one on the way in.
            // A tile replacing the ancestor drawn here last frame - itself, or as this square's stand-in -
            // fades in over exactly the image that was showing.
            var ancestor = borrowed.TryGetValue(t, out var lent) ? lent : NearestAncestorIn(t, before);
            if (_fadeDuration > 0 && ancestor is { } a && _loaded.TryGetValue(a, out var from))
                StartFade(s, t, a, from, outward: false, now);
        }
    }

    void ShowStandIns(List<(TileKey Square, TileKey From)> wanted)
    {
        var keep = new HashSet<TileKey>();
        foreach (var (square, fromKey) in wanted)
        {
            if (_device is null || !_loaded.TryGetValue(fromKey, out var from)) continue;
            keep.Add(square);
            if (!_standIns.TryGetValue(square, out var si))
            {
                var (vb, ib, mesh, centre) = BuildMesh(_device, square);
                var (entity, model) = MakeEntity(square, mesh, StandInMaterial(square, fromKey, from), centre);
                si = new StandIn { Entity = entity, Vertices = vb, Indices = ib, Centre = centre, Model = model, From = fromKey, FromOverlayVersion = from.OverlayStamp };
                _standIns[square] = si;
                _root.Transform.Children.Add(entity.Transform);
            }
            else if (si.From != fromKey || si.FromOverlayVersion != from.OverlayStamp)
            {
                si.Model.Materials[0] = StandInMaterial(square, fromKey, from);   // a nearer ancestor arrived, or its overlay changed
                si.From = fromKey;
                si.FromOverlayVersion = from.OverlayStamp;
            }
        }
        foreach (var square in _standIns.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var si = _standIns[square];
            _root.Transform.Children.Remove(si.Entity.Transform);
            si.Dispose();
            _standIns.Remove(square);
        }
    }

    Material StandInMaterial(TileKey square, TileKey fromKey, Slot from)
    {
        var (scale, offset) = Fade.AncestorTransform(square, fromKey);
        return Material.New(_device!, new MaterialDescriptor
        {
            Attributes = { Emissive = new MaterialEmissiveMapFeature(Look(from, scale, offset)), CullMode = CullMode.None },
        });
    }

    static TileKey? NearestAncestorIn(TileKey t, HashSet<TileKey> set)
    {
        for (var a = t; a.Zoom > 0;) { a = a.Parent; if (set.Contains(a)) return a; }
        return null;
    }

    void StartFade(Slot s, TileKey t, TileKey a, Slot from, bool outward, double start)
    {
        var (scale, offset) = Fade.AncestorTransform(t, a);
        try
        {
            s.Fading = Material.New(_device!, new MaterialDescriptor
            {
                Attributes =
                {
                    Emissive = new MaterialEmissiveMapFeature(new ComputeBinaryColor(
                        new ComputeBinaryColor(Look(from, scale, offset), new ComputeFloat4(Vector4.One) { Key = FadeOutKey }, BinaryOperator.Multiply),
                        new ComputeBinaryColor(Look(s, Vector2.One, Vector2.Zero), new ComputeFloat4(new Vector4(0, 0, 0, 1)) { Key = FadeInKey }, BinaryOperator.Multiply),
                        BinaryOperator.Add)),
                    CullMode = CullMode.None,
                },
            });
        }
        catch (Exception e) { _logger.LogWarning(e, "Globe: fade material for {Tile} failed; drawn without a fade", t); s.Fading = null; return; }
        s.FadeFrom = a;
        s.FadeOut = outward;
        s.FadeStart = start;
        SetFade(s.Fading, OwnWeight(s, _clock.Elapsed.TotalSeconds));
        s.Model.Materials[0] = s.Fading;
    }

    static void SetFade(Material m, float f)
    {
        var p = m.Passes[0].Parameters;
        // Alpha stays 1. BinaryOperator.Add is Stride's ComputeColorAdd3ds, a 3ds Max blend that weighs
        // each side by its ALPHA (BlendUtils.BasicBlend); weights of (f, f, f, f) made a half-way fade
        // half as bright - every tile dipped dark as it changed (the user, 2026-11-20).
        p.Set(FadeInKey, new Vector4(f, f, f, 1));
        p.Set(FadeOutKey, new Vector4(1 - f, 1 - f, 1 - f, 1));
    }

    void AdvanceFades()
    {
        var now = _clock.Elapsed.TotalSeconds;
        foreach (var t in _shown)
        {
            if (!_loaded.TryGetValue(t, out var s) || s.Fading is null) continue;
            // the ancestor's image must outlive the fade; if it was evicted anyway, stop at once
            if (!_loaded.ContainsKey(s.FadeFrom)) { EndFade(s); continue; }
            // A fade in ends on its own image; a fade out holds the ancestor's until Show swaps the
            // ancestor itself in next frame - ending it here would flash the old image for a frame.
            if (!s.FadeOut && Fade.Progress(s.FadeStart, now, _fadeDuration) >= 1) EndFade(s);
            else SetFade(s.Fading, OwnWeight(s, now));
        }
    }

    float OwnWeight(Slot s, double now)
    {
        var p = Fade.Progress(s.FadeStart, now, _fadeDuration);
        return s.FadeOut ? 1 - p : p;
    }

    static void EndFade(Slot s)
    {
        if (s.Fading is null) return;
        s.Model.Materials[0] = s.Plain;
        s.Fading = null;
    }
}
