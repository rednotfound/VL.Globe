using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe.Engine;

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
    internal enum Pass { Triangles, LineList, ExtrusionDepth, ExtrusionColor }

    internal readonly record struct Item(Buffer Vertices, Buffer Indices, int Start, int Count, Matrix World, Vector3 Centre,
        Vector4 Color, float HalfWidth, bool IsLine, Vector4 Clip, Pass Pass = Pass.Triangles, float Tiles = 1);

    internal sealed class Frame
    {
        public readonly List<Item> Items = new();
        public float Radius = 1;
    }

    public volatile Frame? Current;

    /// <summary>Why nothing is drawn: the shader failed to compile, say. Empty while all is well.</summary>
    public string Error { get; private set; } = "";
    public int ItemsDrawn { get; private set; }

    // the vertices are lifted this fraction of their distance towards the eye: enough to stay over
    // the raster tiles' facets (they sag up to 0.0012 radii below the sphere at zoom 0-3, seen from
    // two radii away and more; a street-level tile's sag is far below this), too little to see
    public float Lift = 0.0015f;

    // Fill outlines as GL JS draws them (fill_outline: aliased gl.LINES, alpha 1 - smoothstep of the
    // pixel's distance to the line), for MapLibre's render tests. Off in vvvv, where the hardware's
    // antialiased line (blended straight) is drawn: the user judged that one on screen, 2026-11-21.
    public bool GlOutlines;

    DynamicEffectInstance? _effect;
    MutablePipelineState? _pipeline, _linePipeline, _depthPipeline, _extrusionPipeline;
    bool _broken;
    readonly Dictionary<string, ParameterKey> _keys = new();

    public void Draw(RenderDrawContext context)
    {
        var frame = Current;
        ItemsDrawn = 0;
        if (frame is null || frame.Items.Count == 0 || _broken) return;
        try
        {
            if (_effect is null)
            {
                _effect = new DynamicEffectInstance("GlobeVector");
                _effect.Initialize(context.RenderContext.Services);
            }
            _effect.UpdateEffect(context.GraphicsDevice);
            if (_effect.Effect is null) return;   // still compiling
            var view = context.RenderContext.RenderView;
            DrawFrame(context.CommandList, context.GraphicsContext, _effect, view.View, view.Projection, view.ViewSize.Y, frame,
                Lift, linearize: true, flat: false);
        }
        catch (Exception e)
        {
            // a shader that does not compile would throw every frame: say it once, draw nothing
            _broken = true;
            Error = $"GPU vector renderer: {e.GetType().Name} {e.Message}";
            throw;
        }
    }

    /// <summary>
    /// Draws a frame's items with a compiled GlobeVector effect into the targets bound on <paramref name="commandList"/>.
    /// vvvv's Draw above calls it; MapLibre's render tests call it offscreen, flat and without linearising.
    /// </summary>
    // linearize: The colours are sRGB and the target linear (vvvv); false draws in sRGB space, as GL JS blends.
    // flat: The buckets are on the Mercator plane (x east, y north, z up), not the sphere.
    internal void DrawFrame(CommandList commandList, GraphicsContext graphicsContext, EffectInstance effect, Matrix view, Matrix projection,
        float viewHeight, Frame frame, float lift, bool linearize, bool flat)
    {
        ItemsDrawn = 0;
        EnsurePipelines(commandList.GraphicsDevice);
        if (_keys.Count == 0)
        {
            foreach (var cb in effect.Effect.Bytecode.Reflection.ConstantBuffers)
                foreach (var m in cb.Members)
                    if (m.KeyInfo.Key is { } key) _keys[m.KeyInfo.KeyName] = key;
            // a key not found would leave its value unset in silence: say which were found
            if (!_keys.ContainsKey("GlobeVector.GvWorldView") || !_keys.ContainsKey("GlobeVector.GvProjection"))
                throw new InvalidOperationException("GlobeVector's parameters not found; the effect has: " + string.Join(", ", _keys.Keys));
        }

        var pipelines = new[] { _pipeline!, _linePipeline!, _depthPipeline!, _extrusionPipeline! };
        foreach (var p in pipelines)
        {
            p.State.RootSignature = effect.RootSignature;
            p.State.EffectBytecode = effect.Effect.Bytecode;
            p.State.Output.CaptureState(commandList);
            p.Update();
        }
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
        Buffer? boundVertices = null;
        Texture? sceneDepth = null;
        try
        {
            foreach (var item in frame.Items)
            {
                bool extrusion = item.Pass is Pass.ExtrusionDepth or Pass.ExtrusionColor;
                if (extrusion && sceneDepth is null) sceneDepth = UseOwnDepth(commandList);
                else if (item.Pass == Pass.ExtrusionDepth && pass == Pass.ExtrusionColor) { RestoreDepth(commandList, sceneDepth!); sceneDepth = UseOwnDepth(commandList); }
                else if (!extrusion && sceneDepth is not null) { RestoreDepth(commandList, sceneDepth); sceneDepth = null; }
                if (!ReferenceEquals(item.Vertices, boundVertices))
                {
                    commandList.SetVertexBuffer(0, item.Vertices, 0, VectorVertex.Size);
                    commandList.SetIndexBuffer(item.Indices, 0, true);
                    boundVertices = item.Vertices;
                }
                if (pass != item.Pass)
                {
                    commandList.SetPipelineState(pipelines[(int)item.Pass].CurrentState);
                    pass = item.Pass;
                }
                Set(parameters, "GvWorldView", item.World * view);
                Set(parameters, "GvColor", item.Color);
                Set(parameters, "GvClip", item.Clip);
                Set(parameters, "GvCentre", item.Centre);
                Set(parameters, "GvTiles", item.Tiles);
                Set(parameters, "GvHalfWidth", item.HalfWidth);
                Set(parameters, "GvIsLine", item.IsLine ? 1f : 0f);
                Set(parameters, "GvStraight", item.Pass == Pass.LineList && !GlOutlines ? 1f : 0f);
                Set(parameters, "GvGlOutline", item.Pass == Pass.LineList && GlOutlines ? 1f : 0f);
                Set(parameters, "GvExtrusion", extrusion ? 1f : 0f);
                effect.Apply(graphicsContext);
                commandList.DrawIndexed(item.Count, item.Start);
                ItemsDrawn++;
            }
        }
        finally { if (sceneDepth is not null) RestoreDepth(commandList, sceneDepth); }
    }

    void EnsurePipelines(GraphicsDevice device)
    {
        if (_pipeline is not null) return;
        _pipeline = new MutablePipelineState(device);
        var s = _pipeline.State;
        s.SetDefaults();
        s.InputElements = VectorVertex.Layout.CreateInputElements();
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
            own.Flags = (d.Flags & ~TextureFlags.DepthStencilReadOnly) | TextureFlags.DepthStencil;
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

    void Set<T>(ParameterCollection parameters, string name, T value) where T : struct
    {
        if (!_keys.TryGetValue("GlobeVector." + name, out var key)) return;   // compiled away: unused
        if (key is ValueParameterKey<T> typed) parameters.Set(typed, value);
        else throw new InvalidOperationException($"GlobeVector.{name} is a {key.GetType().Name}, set as {typeof(T).Name}");
    }
}
