using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stride.Core.Mathematics;

namespace VL.Globe.Engine;

// Label fades (ROADMAP 4e, DESIGN-GPU-VECTOR.md "Next" 1): GL JS's symbol fading, from style.ts
// _updatePlacement, pauseable_placement.ts and placement.ts commit / updateBucketOpacities.
// - Every symbol has a cross-tile ID (CrossTileSymbolIndex), given when its tile comes.
// - A placement (LabelPlacement) begins from the camera of its frame and goes on over frames, 2 ms a
//   frame (unless fades are off: then whole, every frame, so a new tile shows its labels at once).
//   A new one begins when the last is done, 300 ms (the fade) after the last commit, and the camera
//   or the tiles have changed since it began.
// - Its commit turns each ID's placement into an opacity state: the opacity it has reached, and
//   whether it is placed (fading in) or not (fading out); the IDs no longer placed fade out from where
//   they were. A label with no state yet: shown at once if it may overlap, else hidden until placed.
// - Each symbol vertex holds its state (Pack); the shader moves it by GsFadeChange, the time since the
//   commit over the fade's duration.
// Not ported: GL JS's zoomAdjustment (shorter fades while zooming out fast), variable anchors' offsets.
internal sealed class LabelFades
{
    /// <summary>GL JS's fadeDuration, ms: 300 by default, 0 in MapLibre's render tests.</summary>
    public double FadeDuration = 300;
    /// <summary>GL JS's budget for a placement in one frame, ms (pauseable_placement.ts).</summary>
    public double FrameBudget = 2;

    readonly CrossTileSymbolIndex _index = new();
    readonly LabelPlacement _placement = new();
    Dictionary<int, JointOpacity> _opacities = new(), _next = new();
    double _commitTime = double.NegativeInfinity;
    bool _begun, _committed, _stale;
    long _tiles;
    // the camera the placement under way (or last done) began with, and last frame's camera
    (Matrix viewProjection, Vector2 size, D3? eye) _placedCamera, _lastCamera;

    internal readonly record struct Opacity(float Value, bool Placed)
    {
        // GL JS OpacityState
        public static Opacity From(Opacity? previous, double increment, bool placed, bool skipFade = false)
            => previous is { } p
                ? new Opacity((float)Math.Clamp(p.Value + (p.Placed ? increment : -increment), 0, 1), placed)
                : new Opacity(skipFade && placed ? 1 : 0, placed);
        public bool Hidden => Value == 0 && !Placed;
    }

    internal readonly record struct JointOpacity(Opacity Text, Opacity Icon)
    {
        public static JointOpacity From(JointOpacity? previous, double increment, bool text, bool icon, bool skipFade = false)
            => new(Opacity.From(previous?.Text, increment, text, skipFade), Opacity.From(previous?.Icon, increment, icon, skipFade));
        public bool Hidden => Text.Hidden && Icon.Hidden;
    }

    [Flags]
    internal enum Did { None = 0, Began = 1, Placed = 2, Committed = 4, Opacities = 8, Lines = 16 }

    /// <summary>The opacity states, by cross-tile ID (for tests).</summary>
    internal IReadOnlyDictionary<int, JointOpacity> Opacities => _opacities;

    /// <summary>GL JS symbolFadeChange: how far the states have moved since the last commit, in fades; 1 with fades off.</summary>
    public float FadeChange(double now) => FadeDuration == 0 || !_committed ? 1 : (float)((now - _commitTime) / FadeDuration);

    /// <summary>
    /// One frame of labels: the tiles indexed, the placement begun or continued, its commit, the vertices'
    /// states written, and the glyphs along lines walked when the camera moved or a state changed.
    /// </summary>
    public Did Step(IReadOnlyList<LabelPlacement.Layer> layers, Matrix view, Matrix projection, float width, float height,
        float cameraToCenter, bool flat, D3? eye, double now)
    {
        var did = Did.None;
        // the tiles: indexed again when any came or went (GL JS does it every frame; the same tiles are a no-op there)
        long tiles = 17;
        foreach (var layer in layers)
            foreach (var (tile, _) in layer.Tiles) tiles = tiles * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tile);
        bool bucketsChanged = false;
        if (tiles != _tiles || !_begun)
        {
            _tiles = tiles;
            var used = new HashSet<int>();
            foreach (var layer in layers)
            {
                used.Add(layer.Index);
                var list = new List<GpuTile>(layer.Tiles.Count);
                foreach (var (tile, _) in layer.Tiles) list.Add(tile);
                bucketsChanged |= _index.AddLayer(layer.Index, list);
            }
            _index.PruneUnusedLayers(used);
        }
        if (bucketsChanged) _stale = true;

        // moved: the view, the viewport or the eye. With the eye left out, a pan at street level kept
        // the rotation-only view bit for bit in many frames: the glyphs along lines stood still while
        // the roads under them moved, and caught up in steps ("一顿一顿", the user, 2026-11-22)
        var camera = (view * projection, new Vector2(width, height), eye);
        bool moved = camera != _lastCamera;
        _lastCamera = camera;

        bool force = FadeDuration == 0;
        bool settled = _placement.Done && !StillRecent(now);
        if (force || !_begun || (settled && (camera != _placedCamera || _stale)))
        {
            _placement.Begin(layers, view, projection, width, height, cameraToCenter, flat);
            _placedCamera = camera;
            _begun = true;
            _stale = false;
            did |= Did.Began;
        }
        if (!_placement.Done)
        {
            long start = Stopwatch.GetTimestamp();
            bool done = _placement.Continue(force ? null : () => Stopwatch.GetElapsedTime(start).TotalMilliseconds > FrameBudget);
            did |= Did.Placed;
            if (done) { Commit(now); did |= Did.Committed; }
        }
        if ((did & Did.Committed) != 0 || bucketsChanged)
        {
            UpdateOpacities(layers);
            did |= Did.Opacities;
        }
        if (moved || (did & Did.Opacities) != 0)
        {
            _placement.UpdateLines(layers, view, projection, width, height, cameraToCenter, flat);
            did |= Did.Lines;
        }
        return did;
    }

    // GL JS Placement.stillRecent, without its zoom adjustment
    bool StillRecent(double now) => _committed && _commitTime + FadeDuration > now;

    // GL JS Placement.commit: the placement's results become opacity states, carried on from the last ones
    void Commit(double now)
    {
        double increment = FadeChange(now);
        _next.Clear();
        foreach (var (id, joint) in _placement.Placements)
            _next[id] = _opacities.TryGetValue(id, out var previous)
                ? JointOpacity.From(previous, increment, joint.Text, joint.Icon)
                : JointOpacity.From(null, increment, joint.Text, joint.Icon, joint.SkipFade);
        // not placed this time (gone, or a duplicate now): fading out from where they were
        foreach (var (id, previous) in _opacities)
        {
            if (_next.ContainsKey(id)) continue;
            var state = JointOpacity.From(previous, increment, false, false);
            if (!state.Hidden) _next[id] = state;
        }
        (_opacities, _next) = (_next, _opacities);
        _commitTime = now;
        _committed = true;
    }

    /// <summary>A vertex's state: placed, 1 + the opacity reached; not placed, minus it. 0 is hidden and staying so.</summary>
    internal static float Pack(Opacity o) => o.Placed ? 1 + o.Value : -o.Value;

    /// <summary>The opacity a vertex is drawn with: GlobeSymbol.sdsl's fade, the same formula (for tests).</summary>
    internal static float Shown(float state, float fadeChange)
        => state > 0.5f ? Math.Clamp(state - 1 + fadeChange, 0, 1) : Math.Clamp(-state - fadeChange, 0, 1);

    static readonly JointOpacity Duplicate = default;

    // GL JS updateLayerOpacities / updateBucketOpacities: every symbol vertex gets its ID's state; a
    // symbol whose ID a finer tile of the layer holds too is a duplicate, hidden
    void UpdateOpacities(IReadOnlyList<LabelPlacement.Layer> layers)
    {
        var seen = new Dictionary<int, GpuTile>();
        var done = new HashSet<GpuTile>(ReferenceEqualityComparer.Instance);
        var tiles = new List<GpuTile>();
        foreach (var layer in layers)
        {
            seen.Clear(); done.Clear(); tiles.Clear();
            foreach (var (tile, _) in layer.Tiles) if (done.Add(tile)) tiles.Add(tile);
            tiles.Sort(LabelPlacement.Order);
            foreach (var tile in tiles)
            {
                if (tile.Placed is not { } placed) continue;
                var (labelStart, labelCount) = tile.LabelRange(layer.Index);
                if (tile.LabelIds is { } labelIds)
                    for (int i = labelStart; i < labelStart + labelCount; i++)
                    {
                        var label = tile.Labels[i];
                        var state = StateOf(labelIds[i], tile, (label.Flags & SymbolFlags.AlwaysShowText) != 0, (label.Flags & SymbolFlags.AlwaysShowIcon) != 0);
                        Write(tile, placed, label.TextStart, label.TextCount, Pack(state.Text));
                        Write(tile, placed, label.IconStart, label.IconCount, Pack(state.Icon));
                    }
                var (lineStart, lineCount) = tile.LineLabelRange(layer.Index);
                if (tile.LineLabelIds is { } lineIds)
                    for (int i = lineStart; i < lineStart + lineCount; i++)
                    {
                        var line = tile.LineLabels[i];
                        var state = StateOf(lineIds[i], tile, line.AlwaysOverlap, line.AlwaysOverlap);
                        Write(tile, placed, line.VertexStart, line.GlyphOffsets.Length * 4, Pack(line.IconBox is null ? state.Text : state.Icon));
                    }
            }
        }

        JointOpacity StateOf(int id, GpuTile tile, bool alwaysText, bool alwaysIcon)
        {
            if (seen.TryGetValue(id, out var by) && !ReferenceEquals(by, tile)) return Duplicate;
            seen[id] = tile;
            if (_opacities.TryGetValue(id, out var state)) return state;
            // GL JS defaultOpacityState: shown before any placement only where it may overlap; kept,
            // so the next placement starts from it
            return _opacities[id] = JointOpacity.From(null, 0, alwaysText, alwaysIcon, skipFade: true);
        }
    }

    static void Write(GpuTile tile, float[] placed, int start, int count, float value)
    {
        for (int v = start; v < start + count; v++)
            if (placed[v] != value) { placed[v] = value; tile.PlacedDirty = true; }
    }
}
