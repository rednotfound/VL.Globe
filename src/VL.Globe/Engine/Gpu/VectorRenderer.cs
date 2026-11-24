using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe.Engine.Gpu;

// Draws the vector buckets with shaders\GlobeVector.sdsl (docs/DESIGN-GPU-VECTOR.md). Plugged into
// the scene by VL.Stride's EntityRendererComponent, which calls Draw in the transparent stage with
// the scene's RenderView pushed: after the opaque tiles, their depth bound. VL.Stride's own
// WireframeRenderFeature is the template (DynamicEffectInstance, MutablePipelineState).
//
// The node fills a Frame in its Update; Draw reads the latest one. Both run on vvvv's main thread,
// one after the other, but the swap is a single reference anyway.
internal sealed class VectorRenderer : IGraphicsRendererBase
{
    // LineList: GL JS's fill outline, gl.LINES - one device pixel, the hardware's own line.
    // ExtrusionDepth then ExtrusionColor: GL JS draws a fill-extrusion layer's depth first, then its
    // colour where the depth is equal, so a translucent layer is one even layer, its buildings not
    // showing through each other
    // Text: a symbol layer's glyph quads, drawn by GlobeSymbol.sdsl over everything, no depth (GL JS)
    // Circle: a circle layer's quads, drawn by GlobeCircle.sdsl, no depth, as Text
    internal enum Pass { Triangles, LineList, ExtrusionDepth, ExtrusionColor, Text, Circle }

    // what a Text item draws with besides its buffers: the atlas and the layer's paint this frame.
    // Icons are Text items too: IconMode 1 an image, 2 an SDF image; Nearest GL JS's gl.NEAREST
    internal sealed record TextDraw(Texture Atlas, Vector4 Color, Vector4 HaloColor, float Size, bool SizePerFeature, float Opacity,
        float HaloWidth, float HaloBlur, bool IsHalo, bool IsPlain, float CameraToCenter, float PixelRatio, Buffer Placed, Buffer Dynamic,
        int IconMode = 0, bool Nearest = false, bool RotateWithMap = false);

    // what a Circle item draws with: the layer's paint this frame, its colours premultiplied
    internal sealed record CircleDraw(CirclePaint Paint, Vector4 Color, Vector4 StrokeColor, float CameraToCenter, float PixelRatio);

    internal readonly record struct Item(Buffer Vertices, Buffer Indices, int Start, int Count, Matrix World, Vector3 Centre,
        Vector4 Color, float HalfWidth, bool IsLine, Vector4 Clip, Pass Pass = Pass.Triangles, float Tiles = 1, TextDraw? Text = null,
        CircleDraw? Circle = null, TileProjection.Geo Geo = default, int BaseVertex = 0);

    internal sealed class Frame
    {
        public readonly List<Item> Items = new();
        // the atlases the labels and icons sample, with the rows placed since their last upload (AtlasTexture)
        public readonly List<(Texture Texture, IRowAtlas Atlas)> Atlases = new();
        public float Radius = 1;
        // labels to place before drawing (top of the style first), and the distance to the view's centre
        public readonly List<LabelPlacement.Layer> Labels = new();
        public float CameraToCenter = 1;
        // the eye the world matrices are relative to: the view is a rotation only, so a pan at street
        // level can leave it bit for bit the same while every world matrix has moved
        public D3? Eye;
        // device pixels per CSS pixel: labels are laid out and placed in CSS pixels, as GL JS's
        public float PixelRatio = 1;
        // where 3D buildings fade out, from X to Y in scene units from the eye (0 0: they do not): they
        // end where the vector tiles fall below the camera's zoom (GpuVectorTiles.FloorReach)
        public Vector2 ExtrusionFade;
    }

    public volatile Frame? Current;

    /// <summary>Why nothing is drawn: the shader failed to compile, say. Empty while all is well.</summary>
    public string Error { get; private set; } = "";
    public int ItemsDrawn { get; private set; }

    // where a frame's Draw goes (vvvv's render thread): placing the labels on the CPU, uploading the
    // placements, and submitting the draws - max and sum since the last TakeStats, for the node's perf line
    readonly System.Diagnostics.Stopwatch _drawTimer = new();
    double _placeMax, _placeSum, _uploadMax, _uploadSum, _submitMax, _submitSum;
    int _drawFrames, _labelsMax;

    /// <summary>The Draw timings since the last call (ms, max/mean), and resets them.</summary>
    public string TakeStats()
    {
        if (_drawFrames == 0) return "no draws";
        var s = $"place {_placeMax:F1}/{_placeSum / _drawFrames:F1}, upload {_uploadMax:F1}/{_uploadSum / _drawFrames:F1}, " +
                $"submit {_submitMax:F1}/{_submitSum / _drawFrames:F1} ms (max/mean), {ItemsDrawn} draws, {_labelsMax} label tiles";
        _placeMax = _placeSum = _uploadMax = _uploadSum = _submitMax = _submitSum = 0;
        _drawFrames = 0; _labelsMax = 0;
        return s;
    }

    // the vertices are lifted this fraction of their distance towards the eye: enough to stay over
    // the raster tiles' facets (they sag up to 0.0012 radii below the sphere at zoom 0-3, seen from
    // two radii away and more; a street-level tile's sag is far below this), too little to see
    public float Lift = 0.0015f;

    // Fill outlines as GL JS draws them (fill_outline: aliased gl.LINES, alpha 1 - smoothstep of the
    // pixel's distance to the line), for MapLibre's render tests. Off in vvvv, where the hardware's
    // antialiased line (blended straight) is drawn: the user judged that one on screen, 2026-11-21.
    public bool GlOutlines;

    /// <summary>The light the buildings are lit by this frame; null: GL JS's default light.</summary>
    // Read from the scene in Draw (ROADMAP 6b, docs/DESIGN-LIGHT.md): the first directional light of the
    // visibility group's RenderLightCollection, as Stride's ForwardLightingRenderFeature reads it - so
    // SunLight [Globe.Lights] and vvvv's own DirectionalLight both light them. Null in the render tests.
    public SceneLight? Light;

    /// <summary>How dark a wall is at its foot, 0-1 (ambient occlusion over its lowest 3 m); 0: none, as GL JS.</summary>
    // On in vvvv (the user, 2026-11-24: on by default); off in MapLibre's render tests, which GL JS draws without
    public float Ao = 0.35f;
    /// <summary>What the last draw found of the scene's lights, for the Globe node's log.</summary>
    public volatile string LightInfo = "";

    internal readonly record struct SceneLight(Vector3 TowardsLight, Vector3 Color, float Intensity, bool Shadows = false);

    /// <summary>The scene's first directional light: towards it in the globe's axes, its sRGB colour, GL JS's intensity (Stride's 1 = 0.5).</summary>
    internal static SceneLight? ReadLight(RenderContext context, out string info)
    {
        // the visibility group being drawn, else the scene instance's groups (whichever holds the lights)
        var key = global::Stride.Rendering.Lights.ForwardLightingRenderFeature.CurrentLights;
        var lights = context.VisibilityGroup?.Tags.Get(key);
        string where = context.VisibilityGroup is null ? "no visibility group" : lights is null ? "no lights in the visibility group" : "the visibility group";
        if (lights is null && global::Stride.Engine.SceneInstance.GetCurrent(context) is { } scene)
            foreach (var group in scene.VisibilityGroups)
                if (group.Tags.Get(key) is { } found) { lights = found; where += ", the scene's"; break; }
        if (lights is null) { info = where + ": no light collection"; return null; }
        var kinds = new List<string>();
        foreach (var l in lights) kinds.Add(l.Type?.GetType().Name ?? "null");
        info = $"{where}: {kinds.Count} light(s) [{string.Join(", ", kinds)}]";
        foreach (var l in lights)
        {
            if (l.Type is not global::Stride.Rendering.Lights.LightDirectional directional || l.Intensity <= 0) continue;
            var linear = l.Color * (1f / l.Intensity);   // RenderLight.Color is linear and times the intensity
            var srgb = new Vector3(MathUtil.LinearToSRgb(linear.R), MathUtil.LinearToSRgb(linear.G), MathUtil.LinearToSRgb(linear.B));
            // the light's own shadow switch (Stride's, as vvvv's DirectionalLight sets it): the buildings cast then too
            bool shadows = directional.Shadow is { Enabled: true };
            info += shadows ? ", shadows on" : ", shadows off";
            return new SceneLight(-Vector3.Normalize(l.Direction), srgb, Math.Clamp(0.5f * l.Intensity, 0f, 1f), shadows);
        }
        return null;
    }

    DynamicEffectInstance? _effect, _symbolEffect, _circleEffect, _fogEffect, _shadowEffect, _shadowEffectMS;
    readonly FogPass _fog = new();
    readonly ShadowPass _shadow = new();
    MutablePipelineState? _castPipeline;
    /// <summary>Fog towards the horizon over everything the globe draws (GlobeFog.sdsl).</summary>
    // off: the user, 2026-11-22, of the fog whole at 5 eye heights - "这么夸张得雾的话，我宁愿没有呢"
    // (MapLibre has none by default). Kept, unused, until a thin haze at the limb is asked for.
    public bool Fog = false;
    MutablePipelineState? _pipeline, _linePipeline, _depthPipeline, _extrusionPipeline, _textPipeline, _circlePipeline;
    readonly Dictionary<string, ParameterKey> _symbolKeys = new(), _circleKeys = new();
    // labels: GL JS's placement and fades - a placement 2 ms a frame, begun again 300 ms after the last
    // commit when the camera or the tiles have changed; between, only the glyphs along lines follow
    // the camera, and the shader moves each label's opacity by the time since the commit (LabelFades)
    readonly LabelFades _fades = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The labels' fade, ms (GL JS's fadeDuration, 300 by default); 0: no fades, a whole placement every frame.</summary>
    public double FadeDuration { get => _fades.FadeDuration; set => _fades.FadeDuration = value; }
    bool _broken;
    readonly Dictionary<string, ParameterKey> _keys = new();

    // Each vector draw's values in one buffer a frame (GlobeVector.sdsl's GvDraws), found by its index - a
    // per-instance attribute from _drawIds, offset by the draw's start instance. One Apply a pass, not
    // one a draw: at 4000-6000 draws zoomed out the per-draw sets and Applies were 5 ms of a missed
    // frame (ROADMAP 4g, 2026-11-23).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct DrawData
    {
        public Matrix WorldView;
        public Vector4 Color, Clip, TileTrig, CentreHalfWidth, Misc;   // Misc: Mercator y, tiles a side, is a line, -
        public const int Size = 64 + 5 * 16;
    }
    DrawData[] _drawData = new DrawData[256];
    Buffer? _draws, _drawIds;
    int _drawCapacity;

    void EnsureDrawBuffers(GraphicsDevice device, int count)
    {
        if (_draws is not null && count <= _drawCapacity) return;
        int capacity = Math.Max(256, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
        _draws?.Dispose(); _drawIds?.Dispose();
        _draws = Buffer.Structured.New(device, capacity, DrawData.Size);
        var ids = new float[capacity];
        for (int i = 0; i < capacity; i++) ids[i] = i;
        _drawIds = Buffer.Vertex.New(device, ids, GraphicsResourceUsage.Immutable);
        _drawCapacity = capacity;
        if (_drawData.Length < capacity) _drawData = new DrawData[capacity];
    }

    public void Draw(RenderDrawContext context)
    {
        var frame = Current;
        ItemsDrawn = 0;
        // the fog is drawn with no vector layer too: it covers the raster tiles as well
        if (frame is null || _broken || frame.Items.Count == 0 && frame.Eye is null) return;
        try
        {
            if (_effect is null)
            {
                _effect = new DynamicEffectInstance("GlobeVector");
                _effect.Initialize(context.RenderContext.Services);
                _symbolEffect = new DynamicEffectInstance("GlobeSymbol");
                _symbolEffect.Initialize(context.RenderContext.Services);
                _circleEffect = new DynamicEffectInstance("GlobeCircle");
                _circleEffect.Initialize(context.RenderContext.Services);
                _fogEffect = new DynamicEffectInstance("GlobeFog");
                _fogEffect.Initialize(context.RenderContext.Services);
                _shadowEffect = new DynamicEffectInstance("GlobeShadow");
                _shadowEffect.Initialize(context.RenderContext.Services);
                _shadowEffectMS = new DynamicEffectInstance("GlobeShadowMS");
                _shadowEffectMS.Initialize(context.RenderContext.Services);
            }
            _effect.UpdateEffect(context.GraphicsDevice);
            _symbolEffect!.UpdateEffect(context.GraphicsDevice);
            _circleEffect!.UpdateEffect(context.GraphicsDevice);
            _fogEffect!.UpdateEffect(context.GraphicsDevice);
            _shadowEffect!.UpdateEffect(context.GraphicsDevice);
            _shadowEffectMS!.UpdateEffect(context.GraphicsDevice);
            if (_effect.Effect is null || _symbolEffect.Effect is null || _circleEffect.Effect is null || _fogEffect.Effect is null) return;   // still compiling
            var view = context.RenderContext.RenderView;
            Light = ReadLight(context.RenderContext, out var lightInfo);
            if (frame.Items.Count > 0)
                DrawFrame(context.CommandList, context.GraphicsContext, _effect, _symbolEffect, _circleEffect, view.View, view.Projection, view.ViewSize.Y, frame,
                    Lift, linearize: true, flat: false,
                    _shadowEffect.Effect is null ? null : _shadowEffect, _shadowEffectMS.Effect is null ? null : _shadowEffectMS);
            // after the draw, which reads the effect's parameter names
            LightInfo = (Light is null ? "the default light - " : $"the scene's directional light, towards ({Light.Value.TowardsLight.X:F1}, {Light.Value.TowardsLight.Y:F1}, {Light.Value.TowardsLight.Z:F1}) - ") + lightInfo
                + (_keys.Count == 0 ? "" : _keys.ContainsKey("GlobeVector.GvLight") ? "; GvLight in the effect" : "; GvLight NOT in the compiled effect");
            // last, over the tiles, the vector layers and the labels (ROADMAP 4f step 2)
            // by the eye's own height, in double: CameraToCenter comes from the tile selection, which does
            // not run while the Globe is off - it was 1e-9 then, and the fog covered the whole window
            if (Fog && FogOf(frame) is { } fog)
                _fog.Draw(context.CommandList, context.GraphicsContext, _fogEffect, view.View, view.Projection,
                    (-frame.Eye!.Value).ToVector3(), frame.Radius, fog);
        }
        catch (Exception e)
        {
            // a shader that does not compile would throw every frame: say it once, draw nothing
            _broken = true;
            Error = $"GPU vector renderer: {e.GetType().Name} {e.Message}";
            throw;
        }
    }

    /// <summary>The fog for a frame: by the eye's height above the globe; none without an eye or below the ground.</summary>
    internal static Fog? FogOf(Frame frame)
        => frame.Eye is { } eye && eye.Length - frame.Radius is var height && height > 0 ? Gpu.Fog.For((float)height) : null;

    /// <summary>
    /// One frame of labels, as DrawFrame does it: <paramref name="now"/> in ms. Gives back the fade change
    /// to draw this frame with - taken after the step, from the commit it may have made.
    /// </summary>
    // Taken before the step, the first frame of every commit drew the new states with the time since the
    // commit before: labels fading in from 0 drawn whole for that frame, those fading out gone - every
    // fade began with a blink (the user, 2026-11-22: "淡入的标签都闪了一下")
    internal float StepLabels(Frame frame, Matrix view, Matrix projection, float width, float height, bool flat, double now, out LabelFades.Did did)
    {
        did = frame.Labels.Count > 0
            ? _fades.Step(frame.Labels, view, projection, width, height, frame.CameraToCenter, flat, frame.Eye, now)
            : LabelFades.Did.None;
        return _fades.FadeChange(now);
    }

    /// <summary>
    /// Draws a frame's items with a compiled GlobeVector effect into the targets bound on <paramref name="commandList"/>.
    /// vvvv's Draw above calls it; MapLibre's render tests call it offscreen, flat and without linearising.
    /// </summary>
    // linearize: The colours are sRGB and the target linear (vvvv); false draws in sRGB space, as GL JS blends.
    // flat: The buckets are on the Mercator plane (x east, y north, z up), not the sphere.
    internal void DrawFrame(CommandList commandList, GraphicsContext graphicsContext, EffectInstance effect, EffectInstance symbolEffect,
        EffectInstance circleEffect, Matrix view, Matrix projection, float viewHeight, Frame frame, float lift, bool linearize, bool flat,
        EffectInstance? shadowEffect = null, EffectInstance? shadowEffectMS = null)
    {
        ItemsDrawn = 0;
        _stamp++;   // every accessor fetched again this frame, before any parameter is set
        AtlasTexture.Flush(commandList, frame.Atlases);
        EnsurePipelines(commandList.GraphicsDevice);
        // An effect compiled again (its source changed: vvvv may first hand out the cached one) lays out
        // other parameters: its names are read again. Read once per renderer, a parameter added to the
        // shader stayed "compiled away" in vvvv and was never set (GvLight, 2026-11-24).
        if (!ReferenceEquals(_keysOf, effect.Effect)) { _keys.Clear(); _resolved.Clear(); _keysOf = effect.Effect; }
        if (!ReferenceEquals(_symbolKeysOf, symbolEffect.Effect)) { _symbolKeys.Clear(); _symbolResolved.Clear(); _symbolKeysOf = symbolEffect.Effect; }
        if (!ReferenceEquals(_circleKeysOf, circleEffect.Effect)) { _circleKeys.Clear(); _circleResolved.Clear(); _circleKeysOf = circleEffect.Effect; }
        if (_circleKeys.Count == 0)
            foreach (var cb in circleEffect.Effect.Bytecode.Reflection.ConstantBuffers)
                foreach (var m in cb.Members)
                    if (m.KeyInfo.Key is { } key) _circleKeys[m.KeyInfo.KeyName] = key;
        if (_symbolKeys.Count == 0)
        {
            foreach (var cb in symbolEffect.Effect.Bytecode.Reflection.ConstantBuffers)
                foreach (var m in cb.Members)
                    if (m.KeyInfo.Key is { } key) _symbolKeys[m.KeyInfo.KeyName] = key;
            foreach (var r in symbolEffect.Effect.Bytecode.Reflection.ResourceBindings)
                if (r.KeyInfo.Key is { } key) _symbolKeys[r.KeyInfo.KeyName] = key;
        }
        if (_keys.Count == 0)
        {
            foreach (var cb in effect.Effect.Bytecode.Reflection.ConstantBuffers)
                foreach (var m in cb.Members)
                    if (m.KeyInfo.Key is { } key) _keys[m.KeyInfo.KeyName] = key;
            foreach (var r in effect.Effect.Bytecode.Reflection.ResourceBindings)
                if (r.KeyInfo.Key is { } key) _keys[r.KeyInfo.KeyName] = key;
            // a key not found would leave its value unset in silence: say which were found
            if (!_keys.ContainsKey("GlobeVector.GvDraws") || !_keys.ContainsKey("GlobeVector.GvProjection"))
                throw new InvalidOperationException("GlobeVector's parameters not found; the effect has: " + string.Join(", ", _keys.Keys));
        }

        var pipelines = new[] { _pipeline!, _linePipeline!, _depthPipeline!, _extrusionPipeline!, _textPipeline!, _circlePipeline! };
        foreach (var p in pipelines)
        {
            var e = p == _textPipeline ? symbolEffect : p == _circlePipeline ? circleEffect : effect;
            p.State.RootSignature = e.RootSignature;
            p.State.EffectBytecode = e.Effect.Bytecode;
            p.State.Output.CaptureState(commandList);
            p.Update();
        }
        // labels: placed from this camera, then the changed placements uploaded
        _drawTimer.Restart();
        double placeMs = 0;
        float labelWidth = commandList.Viewport.Width / frame.PixelRatio, labelHeight = commandList.Viewport.Height / frame.PixelRatio;
        float fadeChange = StepLabels(frame, view, projection, labelWidth, labelHeight, flat, _clock.Elapsed.TotalMilliseconds, out _);
        if (frame.Labels.Count > 0)
        {
            _labelsMax = Math.Max(_labelsMax, frame.Labels.Sum(l => l.Tiles.Count));
            placeMs = _drawTimer.Elapsed.TotalMilliseconds;
            foreach (var layer in frame.Labels)
                foreach (var (tile, _) in layer.Tiles)
                {
                    if (tile.PlacedDirty && tile.PlacedBuffer is { } placedBuffer && tile.Placed is { } placed)
                    {
                        placedBuffer.SetData(commandList, (ReadOnlySpan<float>)placed);
                        tile.PlacedDirty = false;
                    }
                    if (tile.DynamicDirty && tile.DynamicBuffer is { } dynamicBuffer && tile.Dynamic is { } dynamic)
                    {
                        // the part written since the last upload, or all of it when that is not known (4g step 4)
                        int from = Math.Clamp(tile.DynamicFrom, 0, dynamic.Length), to = Math.Clamp(tile.DynamicTo, from, dynamic.Length);
                        if (tile.DynamicFrom == int.MaxValue) { from = 0; to = dynamic.Length; }
                        if (to > from) dynamicBuffer.SetData(commandList, ((ReadOnlySpan<Vector4>)dynamic)[from..to], from * 16);
                        tile.DynamicDirty = false;
                        tile.DynamicFrom = int.MaxValue; tile.DynamicTo = 0;
                    }
                }
        }
        double uploadMs = _drawTimer.Elapsed.TotalMilliseconds - placeMs;
        var symbolParameters = symbolEffect.Parameters;
        SetText(symbolParameters, "GsProjection", projection);
        SetText(symbolParameters, "GsLift", lift);
        SetText(symbolParameters, "GsFlat", flat ? 1f : 0f);
        SetText(symbolParameters, "GsLinearize", linearize ? 1f : 0f);
        var circleParameters = circleEffect.Parameters;
        SetCircle(circleParameters, "GcProjection", projection);
        SetCircle(circleParameters, "GcLift", lift);
        SetCircle(circleParameters, "GcFlat", flat ? 1f : 0f);
        SetCircle(circleParameters, "GcLinearize", linearize ? 1f : 0f);
        Pass? pass = null;

        var parameters = effect.Parameters;
        float pixel = 2f / (projection.M22 * Math.Max(1f, viewHeight));
        Set(parameters, "GvProjection", projection);
        Set(parameters, "GvRadius", frame.Radius);
        Set(parameters, "GvPixel", pixel);
        Set(parameters, "GvLift", lift);
        Set(parameters, "GvLinearize", linearize ? 1f : 0f);
        Set(parameters, "GvFlat", flat ? 1f : 0f);
        var viewport = commandList.Viewport;
        Set(parameters, "GvViewport", new Vector2(viewport.Width, viewport.Height));
        Set(parameters, "GvFade", frame.ExtrusionFade);
        Set(parameters, "GvAo", Ao);
        Set(parameters, "GvLight", Light is { } light ? new Vector4(light.TowardsLight, 1) : Vector4.Zero);
        Set(parameters, "GvLightColor", Light is { } lit ? new Vector4(lit.Color, lit.Intensity) : new Vector4(1, 1, 1, 0.5f));
        // the sphere ground layers take their depth from (ROADMAP 4h): its centre in view space and
        // |eye - centre|^2 - R^2, formed in double - at street level it is a small difference of large values
        if (frame.Eye is { } eye)
            Set(parameters, "GvSphere", new Vector4(Vector3.TransformNormal((-eye).ToVector3(), view),
                (float)(eye.X * eye.X + eye.Y * eye.Y + eye.Z * eye.Z - (double)frame.Radius * frame.Radius)));
        else
        {
            var centre = view.TranslationVector;   // the globe stands at the origin, the view moves the eye
            Set(parameters, "GvSphere", new Vector4(centre, centre.LengthSquared() - frame.Radius * frame.Radius));
        }
        Buffer? boundVertices = null;
        Texture? sceneDepth = null;
        // every vector draw's values, written once (GvDraws); text and circle items leave their element unused
        int itemCount = frame.Items.Count;
        EnsureDrawBuffers(commandList.GraphicsDevice, itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            var it = frame.Items[i];
            if (it.Text is not null || it.Circle is not null) continue;
            _drawData[i] = new DrawData
            {
                WorldView = Matrix.Transpose(it.World * view), Color = it.Color, Clip = it.Clip, TileTrig = it.Geo.Trig,
                CentreHalfWidth = new Vector4(it.Centre, it.HalfWidth),
                Misc = new Vector4(it.Geo.MercatorY, it.Tiles, it.IsLine ? 1f : 0f, 0),
            };
        }
        if (itemCount > 0) _draws!.SetData(commandList, new ReadOnlySpan<DrawData>(_drawData, 0, itemCount));
        parameters.Set((ObjectParameterKey<Buffer>)_keys["GlobeVector.GvDraws"], _draws!);
        bool vectorApplied = false;
        Pass? appliedPass = null;
        // the buildings' shadows (ROADMAP 6b, C): cast into the sun's map now, received after the buildings
        ShadowPass.Fit? shadowFit = null;
        if (Light is { Shadows: true } sun && frame.Eye is { } shadowEye && !flat && (shadowEffect is not null || shadowEffectMS is not null)
            && frame.Items.Exists(x => x.Pass == Pass.ExtrusionDepth))
            shadowFit = ShadowPass.FitFor(view, projection, shadowEye, frame.Radius, sun.TowardsLight);
        if (shadowFit is { } castFit)
        {
            CastShadows(commandList, graphicsContext, effect, frame, castFit);
            Set(parameters, "GvProjection", projection);
        }
        bool shadowsReceived = false;
        void ReceiveShadows()
        {
            if (shadowsReceived || shadowFit is not { } fit || _ownDepth is null) return;
            shadowsReceived = true;
            var receiver = _ownDepth.MultisampleCount != MultisampleCount.None ? shadowEffectMS : shadowEffect;
            if (receiver is null) return;
            _shadow.Receive(commandList, graphicsContext, receiver, _ownDepth, projection, fit);
            pass = null; boundVertices = null; vectorApplied = false;   // its pipeline and buffers replaced ours
        }
        try
        {
            Array.Clear(_submitTicks);
            for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
            {
                var item = frame.Items[itemIndex];
                long tItem = System.Diagnostics.Stopwatch.GetTimestamp();
                bool extrusion = item.Pass is Pass.ExtrusionDepth or Pass.ExtrusionColor;
                if (extrusion && sceneDepth is null) sceneDepth = UseOwnDepth(commandList);
                else if (item.Pass == Pass.ExtrusionDepth && pass == Pass.ExtrusionColor) { RestoreDepth(commandList, sceneDepth!); sceneDepth = UseOwnDepth(commandList); }
                else if (!extrusion && sceneDepth is not null) { RestoreDepth(commandList, sceneDepth); sceneDepth = null; ReceiveShadows(); }
                if (!ReferenceEquals(item.Vertices, boundVertices))
                {
                    commandList.SetVertexBuffer(0, item.Vertices, 0, item.Pass switch { Pass.Text => SymbolVertex.Size, Pass.Circle => CircleVertex.Size, _ => VectorVertex.Size });
                    // a vector bucket's indices are 16-bit, each range's from its BaseVertex; labels' and circles' 32-bit
                    commandList.SetIndexBuffer(item.Indices, 0, item.Pass is Pass.Text or Pass.Circle);
                    boundVertices = item.Vertices;
                }
                if (pass != item.Pass)
                {
                    commandList.SetPipelineState(pipelines[(int)item.Pass].CurrentState);
                    pass = item.Pass;
                }
                if (item.Circle is { } c)
                {
                    var p = c.Paint;
                    SetCircle(circleParameters, "GcWorldView", item.World * view);
                    SetCircle(circleParameters, "GcCentre", item.Centre);
                    SetCircle(circleParameters, "GcViewport", new Vector2(commandList.Viewport.Width, commandList.Viewport.Height));
                    SetCircle(circleParameters, "GcCameraToCenter", c.CameraToCenter);
                    SetCircle(circleParameters, "GcScaleWithMap", p.ScaleWithMap ? 1f : 0f);
                    SetCircle(circleParameters, "GcDevicePixelRatio", c.PixelRatio);
                    SetCircle(circleParameters, "GcColor", c.Color);
                    SetCircle(circleParameters, "GcStrokeColor", c.StrokeColor);
                    SetCircle(circleParameters, "GcRadius", (float)p.Radius);
                    SetCircle(circleParameters, "GcBlur", (float)p.Blur);
                    SetCircle(circleParameters, "GcOpacity", (float)p.Opacity);
                    SetCircle(circleParameters, "GcStrokeWidth", (float)p.StrokeWidth);
                    SetCircle(circleParameters, "GcStrokeOpacity", (float)p.StrokeOpacity);
                    float V(CirclePerFeature bit) => (p.PerFeature & bit) != 0 ? 1f : 0f;
                    SetCircle(circleParameters, "GcColorV", V(CirclePerFeature.Color));
                    SetCircle(circleParameters, "GcStrokeColorV", V(CirclePerFeature.StrokeColor));
                    SetCircle(circleParameters, "GcRadiusV", V(CirclePerFeature.Radius));
                    SetCircle(circleParameters, "GcBlurV", V(CirclePerFeature.Blur));
                    SetCircle(circleParameters, "GcOpacityV", V(CirclePerFeature.Opacity));
                    SetCircle(circleParameters, "GcStrokeWidthV", V(CirclePerFeature.StrokeWidth));
                    SetCircle(circleParameters, "GcStrokeOpacityV", V(CirclePerFeature.StrokeOpacity));
                    circleEffect.Apply(graphicsContext);
                    vectorApplied = false;
                    commandList.DrawIndexed(item.Count, item.Start);
                    ItemsDrawn++;
                    continue;
                }
                if (item.Text is { } t)
                {
                    commandList.SetVertexBuffer(1, t.Placed, 0, sizeof(float));
                    commandList.SetVertexBuffer(2, t.Dynamic, 0, 16);
                    vectorApplied = false;
                    SetText(symbolParameters, "GsWorldView", item.World * view);
                    SetText(symbolParameters, "GsCentre", item.Centre);
                    SetText(symbolParameters, "GsViewport", new Vector2(commandList.Viewport.Width, commandList.Viewport.Height));
                    SetText(symbolParameters, "GsTexSize", new Vector2(t.Atlas.Width, t.Atlas.Height));
                    SetText(symbolParameters, "GsColor", t.Color);
                    SetText(symbolParameters, "GsHaloColor", t.HaloColor);
                    SetText(symbolParameters, "GsSize", t.Size);
                    SetText(symbolParameters, "GsSizeFromVertex", t.SizePerFeature ? 1f : 0f);
                    SetText(symbolParameters, "GsOpacity", t.Opacity);
                    SetText(symbolParameters, "GsFadeChange", fadeChange);
                    SetText(symbolParameters, "GsHaloWidth", t.HaloWidth);
                    SetText(symbolParameters, "GsHaloBlur", t.HaloBlur);
                    SetText(symbolParameters, "GsIsHalo", t.IsHalo ? 1f : 0f);
                    SetText(symbolParameters, "GsIsPlain", t.IsPlain ? 1f : 0f);
                    SetText(symbolParameters, "GsCameraToCenter", t.CameraToCenter);
                    SetText(symbolParameters, "GsDevicePixelRatio", t.PixelRatio);
                    SetText(symbolParameters, "GsIconMode", (float)t.IconMode);
                    SetText(symbolParameters, "GsNearest", t.Nearest ? 1f : 0f);
                    SetText(symbolParameters, "GsRotateWithMap", t.RotateWithMap ? 1f : 0f);
                    if (_symbolKeys.TryGetValue("GlobeSymbol.GsAtlas", out var atlasKey)) symbolParameters.Set((ObjectParameterKey<Texture>)atlasKey, t.Atlas);
                    long tApply = System.Diagnostics.Stopwatch.GetTimestamp();
                    symbolEffect.Apply(graphicsContext);
                    long tDraw = System.Diagnostics.Stopwatch.GetTimestamp();
                    commandList.DrawIndexed(item.Count, item.Start);
                    Tally(1, tItem, tApply, tDraw);
                    ItemsDrawn++;
                    continue;
                }
                // the draw's index: slot 1 at its element with stride 0, so every vertex reads it. A per-instance
                // element offset by the start instance (DrawIndexedInstanced) was read per vertex instead -
                // Stride's input layout lost the classification (2026-11-23, colours by index in a fixture)
                commandList.SetVertexBuffer(1, _drawIds!, itemIndex * sizeof(float), 0);
                long tApplyV = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!vectorApplied || appliedPass != item.Pass)
                {
                    // what a pass shares; the rest is the draw's own, in GvDraws
                    Set(parameters, "GvStraight", item.Pass == Pass.LineList && !GlOutlines ? 1f : 0f);
                    Set(parameters, "GvGlOutline", item.Pass == Pass.LineList && GlOutlines ? 1f : 0f);
                    Set(parameters, "GvExtrusion", extrusion ? 1f : 0f);
                    tApplyV = System.Diagnostics.Stopwatch.GetTimestamp();
                    effect.Apply(graphicsContext);
                    vectorApplied = true; appliedPass = item.Pass;
                }
                long tDrawV = System.Diagnostics.Stopwatch.GetTimestamp();
                commandList.DrawIndexed(item.Count, item.Start, item.BaseVertex);
                Tally(0, tItem, tApplyV, tDrawV);
                ItemsDrawn++;
            }
            if (sceneDepth is not null) { RestoreDepth(commandList, sceneDepth); sceneDepth = null; ReceiveShadows(); }
        }
        finally
        {
            if (sceneDepth is not null) RestoreDepth(commandList, sceneDepth);
            double submitMs = _drawTimer.Elapsed.TotalMilliseconds - placeMs - uploadMs;
            _placeMax = Math.Max(_placeMax, placeMs); _placeSum += placeMs;
            _uploadMax = Math.Max(_uploadMax, uploadMs); _uploadSum += uploadMs;
            _submitMax = Math.Max(_submitMax, submitMs); _submitSum += submitMs;
            _drawFrames++;
            Last = new DrawTiming(Last.Serial + 1, placeMs, uploadMs, submitMs, ItemsDrawn, _fades.Last, SubmitPartsMs());
        }
    }

    /// <summary>The latest Draw's parts, ms: GlobeNode logs them for a frame that missed its vsync (ROADMAP 4g step 4).</summary>
    // 4g step 4: where a submit's time goes, vector and text items apart - state and parameters, Apply
    // (the constant buffers uploaded), DrawIndexed - ms
    readonly long[] _submitTicks = new long[6];
    void Tally(int kind, long start, long apply, long draw)
    {
        long end = System.Diagnostics.Stopwatch.GetTimestamp();
        _submitTicks[3 * kind] += apply - start; _submitTicks[3 * kind + 1] += draw - apply; _submitTicks[3 * kind + 2] += end - draw;
    }
    SubmitParts SubmitPartsMs()
    {
        double Ms(int i) => _submitTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return new SubmitParts(Ms(0), Ms(1), Ms(2), Ms(3), Ms(4), Ms(5));
    }
    internal readonly record struct SubmitParts(double VectorSets, double VectorApply, double VectorDraw, double TextSets, double TextApply, double TextDraw);

    internal readonly record struct DrawTiming(long Serial, double Place, double Upload, double Submit, int Items, LabelFades.StepTiming Labels = default, SubmitParts Parts = default)
    {
        public double Total => Place + Upload + Submit;
    }

    internal DrawTiming Last;

    void EnsurePipelines(GraphicsDevice device)
    {
        if (_pipeline is not null) return;
        _pipeline = new MutablePipelineState(device);
        var s = _pipeline.State;
        s.SetDefaults();
        // the vertices in slot 0, the draw's index in slot 1 - bound with stride 0: one a draw
        var drawElement = new InputElementDescription
        {
            SemanticName = "TEXCOORD", SemanticIndex = 4, Format = PixelFormat.R32_Float, InputSlot = 1,
            AlignedByteOffset = 0, InputSlotClass = InputClassification.Vertex, InstanceDataStepRate = 0,
        };
        s.InputElements = VectorVertex.Layout.CreateInputElements().Append(drawElement).ToArray();
        s.BlendState = BlendStates.AlphaBlend;   // premultiplied
        s.DepthStencilState = DepthStencilStates.DepthRead;
        s.RasterizerState = RasterizerStates.CullNone;
        s.PrimitiveType = PrimitiveType.TriangleList;
        _linePipeline = new MutablePipelineState(device);
        var l = _linePipeline.State;
        l.SetDefaults();
        l.InputElements = s.InputElements;
        // Hardware line antialiasing multiplies its coverage into alpha only: under premultiplied
        // blending that brightened the edge into a white dashed outline round every building
        // and lake; without it the outline was jagged (the user, 2026-11-21, both). So these
        // lines blend straight (SrcAlpha, InvSrcAlpha) and the shader writes straight colour
        // for them: the coverage then weighs the colour as it should.
        l.BlendState = GlOutlines ? BlendStates.AlphaBlend : BlendStates.NonPremultiplied;
        l.DepthStencilState = DepthStencilStates.DepthRead;
        l.RasterizerState = RasterizerStates.CullNone;
        l.RasterizerState.MultisampleAntiAliasLine = !GlOutlines;
        l.PrimitiveType = PrimitiveType.LineList;
        // fill-extrusion: depth only (written), then colour where it is equal (not written)
        _depthPipeline = new MutablePipelineState(device);
        var d = _depthPipeline.State;
        d.SetDefaults();
        d.InputElements = s.InputElements;
        d.BlendState = BlendStates.ColorDisabled;
        d.DepthStencilState = DepthStencilStates.Default;
        d.RasterizerState = RasterizerStates.CullNone;
        d.PrimitiveType = PrimitiveType.TriangleList;
        _extrusionPipeline = new MutablePipelineState(device);
        var x = _extrusionPipeline.State;
        x.SetDefaults();
        x.InputElements = s.InputElements;
        x.BlendState = BlendStates.AlphaBlend;
        x.DepthStencilState = DepthStencilStates.DepthRead;
        x.RasterizerState = RasterizerStates.CullNone;
        x.PrimitiveType = PrimitiveType.TriangleList;
        // labels: over everything, no depth, as GL JS draws symbols without terrain
        _textPipeline = new MutablePipelineState(device);
        var t = _textPipeline.State;
        t.SetDefaults();
        // the glyph quads in slot 0, whether each is placed in slot 1 (LabelPlacement, every frame)
        var placedElement = new InputElementDescription
        {
            SemanticName = "TEXCOORD", SemanticIndex = 2, Format = PixelFormat.R32_Float, InputSlot = 1,
            AlignedByteOffset = 0, InputSlotClass = InputClassification.Vertex, InstanceDataStepRate = 0,
        };
        // ... and in slot 2 where a glyph along a line was placed (LineGlyphs, every frame)
        var lineElement = new InputElementDescription
        {
            SemanticName = "TEXCOORD", SemanticIndex = 3, Format = PixelFormat.R32G32B32A32_Float, InputSlot = 2,
            AlignedByteOffset = 0, InputSlotClass = InputClassification.Vertex, InstanceDataStepRate = 0,
        };
        t.InputElements = SymbolVertex.Layout.CreateInputElements().Append(placedElement).Append(lineElement).ToArray();
        t.BlendState = BlendStates.AlphaBlend;
        t.DepthStencilState = DepthStencilStates.None;
        t.RasterizerState = RasterizerStates.CullNone;
        t.PrimitiveType = PrimitiveType.TriangleList;
        // circles: as labels, over everything, premultiplied
        _circlePipeline = new MutablePipelineState(device);
        var c = _circlePipeline.State;
        c.SetDefaults();
        c.InputElements = CircleVertex.Layout.CreateInputElements();
        c.BlendState = BlendStates.AlphaBlend;
        c.DepthStencilState = DepthStencilStates.None;
        c.RasterizerState = RasterizerStates.CullNone;
        c.PrimitiveType = PrimitiveType.TriangleList;
    }

    // fill-extrusion needs to write depth, and vvvv's forward renderer binds the scene's depth
    // READ-ONLY in the transparent stage (VLForwardRenderer.BindDepthAsResourceDuringTransparentRendering,
    // true by default): the writes were dropped in silence and every building showed its far walls
    // through its near ones (the user, 2026-11-21: "所有的建筑都看到了其内部"). So an extrusion layer is
    // drawn on a depth buffer of our own, a copy of the scene's - the globe still hides what is behind
    // it - with the scene's colour targets; the scene's depth goes back after.
    Texture? _ownDepth;
    readonly Texture[] _targets = new Texture[8];
    int _targetCount;

    Texture UseOwnDepth(CommandList commandList)
    {
        var scene = commandList.DepthStencilBuffer ?? throw new InvalidOperationException("no depth buffer bound for the vector tiles");
        var texture = scene;   // a read-only view shares its texture's resource: copying it copies that
        var d = texture.Description;
        if (_ownDepth is null || _ownDepth.Width != d.Width || _ownDepth.Height != d.Height || _ownDepth.Format != d.Format
            || _ownDepth.Description.MultisampleCount != d.MultisampleCount)
        {
            _ownDepth?.Dispose();
            var own = d;
            // readable too: the shadows are received from it (ShadowPass)
            own.Flags = (d.Flags & ~TextureFlags.DepthStencilReadOnly) | TextureFlags.DepthStencil | TextureFlags.ShaderResource;
            _ownDepth = Texture.New(commandList.GraphicsDevice, own);
        }
        _targetCount = commandList.RenderTargetCount;
        for (int i = 0; i < _targetCount; i++) _targets[i] = commandList.RenderTargets[i];
        commandList.UnsetRenderTargets();
        commandList.Copy(texture, _ownDepth);
        commandList.SetRenderTargets(_ownDepth, _targetCount, _targets);
        return scene;
    }

    void RestoreDepth(CommandList commandList, Texture scene) => commandList.SetRenderTargets(scene, _targetCount, _targets);

    // The buildings' depth items once more, into the sun's shadow map: GlobeVector with view space -> the
    // map's clip space as its projection, depth only (ShadowPass).
    void CastShadows(CommandList commandList, GraphicsContext graphicsContext, EffectInstance effect, Frame frame, ShadowPass.Fit fit)
    {
        CastShadows(commandList, graphicsContext, effect, frame, 0, fit.Near);
        CastShadows(commandList, graphicsContext, effect, frame, 1, fit.Far);
    }

    void CastShadows(CommandList commandList, GraphicsContext graphicsContext, EffectInstance effect, Frame frame, int index, ShadowPass.Cascade cascade)
    {
        _shadow.BeginCasting(commandList, index);
        try
        {
            if (_castPipeline is null)
            {
                _castPipeline = new MutablePipelineState(commandList.GraphicsDevice);
                var c = _castPipeline.State;
                c.SetDefaults();
                c.InputElements = _depthPipeline!.State.InputElements;
                c.BlendState = BlendStates.ColorDisabled;
                c.DepthStencilState = DepthStencilStates.Default;
                c.RasterizerState = RasterizerStates.CullNone;
                c.PrimitiveType = PrimitiveType.TriangleList;
            }
            _castPipeline.State.RootSignature = effect.RootSignature;
            _castPipeline.State.EffectBytecode = effect.Effect.Bytecode;
            _castPipeline.State.Output.CaptureState(commandList);
            _castPipeline.Update();
            var parameters = effect.Parameters;
            Set(parameters, "GvProjection", cascade.ViewToShadow);
            Set(parameters, "GvStraight", 0f);
            Set(parameters, "GvGlOutline", 0f);
            Set(parameters, "GvExtrusion", 1f);
            commandList.SetPipelineState(_castPipeline.CurrentState);
            effect.Apply(graphicsContext);
            Buffer? bound = null;
            for (int i = 0; i < frame.Items.Count; i++)
            {
                var item = frame.Items[i];
                if (item.Pass != Pass.ExtrusionDepth) continue;
                if (!ReferenceEquals(item.Vertices, bound))
                {
                    commandList.SetVertexBuffer(0, item.Vertices, 0, VectorVertex.Size);
                    commandList.SetIndexBuffer(item.Indices, 0, false);
                    bound = item.Vertices;
                }
                commandList.SetVertexBuffer(1, _drawIds!, i * sizeof(float), 0);
                commandList.DrawIndexed(item.Count, item.Start, item.BaseVertex);
            }
        }
        finally { _shadow.EndCasting(commandList); }
    }

    void SetCircle<T>(ParameterCollection parameters, string name, T value) where T : struct
        => SetBy(parameters, _circleKeys, _circleResolved, "GlobeCircle.", name, value);

    void SetText<T>(ParameterCollection parameters, string name, T value) where T : struct
        => SetBy(parameters, _symbolKeys, _symbolResolved, "GlobeSymbol.", name, value);

    void Set<T>(ParameterCollection parameters, string name, T value) where T : struct
        => SetBy(parameters, _keys, _resolved, "GlobeVector.", name, value);

    // A parameter's key, found once per name. Every draw set a dozen parameters, each by a new string
    // ("GlobeVector." + name) looked up in the effect's keys: 3000 draws a frame, ~40,000 strings made
    // and hashed a frame (ROADMAP 4g step 4, found 2026-11-23 when two more per-draw parameters cost
    // the frame 1.3 ms). The names are literals, one object each: looked up by reference. The effect's
    // keys are read once (the first Draw), so a key found stays right.
    // Then (the same day) a missed frame's submit taken apart: of ~5.5 ms, ~4 were the vector items'
    // parameters - Set(key, value) finds the key in the collection's layout every call. Stride's own
    // renderers hold an accessor (GetAccessor: the slot's place) and set through it; so do we, the
    // accessor fetched again each frame (a recompiled effect lays its parameters out anew).
    readonly Dictionary<object, Slot?> _resolved = new(ReferenceEqualityComparer.Instance),
        _symbolResolved = new(ReferenceEqualityComparer.Instance), _circleResolved = new(ReferenceEqualityComparer.Instance);
    object? _keysOf, _symbolKeysOf, _circleKeysOf;   // the compiled effects the key tables were read from
    int _stamp;   // DrawFrame's count: an accessor older than this frame is fetched again

    abstract class Slot { }

    sealed class Slot<T> : Slot where T : struct
    {
        public required ValueParameterKey<T> Key;
        public ValueParameter<T> Accessor;
        public ParameterCollection? For;
        public int Stamp = -1;
    }

    void SetBy<T>(ParameterCollection parameters, Dictionary<string, ParameterKey> keys, Dictionary<object, Slot?> resolved,
        string prefix, string name, T value) where T : struct
    {
        if (!resolved.TryGetValue(name, out var slot))
        {
            if (keys.Count == 0) return;   // the effect not read yet: nothing to set, nothing remembered
            if (!keys.TryGetValue(prefix + name, out var found)) slot = null;   // compiled away: unused
            else if (found is ValueParameterKey<T> typed) slot = new Slot<T> { Key = typed };
            else throw new InvalidOperationException($"{prefix}{name} is a {found.GetType().Name}, set as {typeof(T).Name}");
            resolved[name] = slot;
            // a name made at run time would be a new object every call, and this table would grow
            if (resolved.Count > 256) throw new InvalidOperationException($"{prefix}{name}: parameter names must be string literals");
        }
        if (slot is null) return;
        if (slot is not Slot<T> s) throw new InvalidOperationException($"{prefix}{name} set as {typeof(T).Name}, first as another type");
        if (s.Stamp != _stamp || !ReferenceEquals(s.For, parameters))
        {
            s.Accessor = parameters.GetAccessor(s.Key);
            s.For = parameters;
            s.Stamp = _stamp;
        }
        parameters.Set(s.Accessor, value);
    }
}
