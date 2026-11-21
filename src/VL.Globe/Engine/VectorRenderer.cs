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
    // LineList: GL JS's fill outline, gl.LINES - one device pixel, the hardware's own line
    internal readonly record struct Item(Buffer Vertices, Buffer Indices, int Start, int Count, Matrix World, Vector3 Centre,
        Vector4 Color, float HalfWidth, bool IsLine, Vector4 Clip, bool LineList = false);

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

    DynamicEffectInstance? _effect;
    MutablePipelineState? _pipeline, _linePipeline;
    bool _broken;
    readonly Dictionary<string, ParameterKey> _keys = new();

    public void Draw(RenderDrawContext context)
    {
        var frame = Current;
        ItemsDrawn = 0;
        if (frame is null || frame.Items.Count == 0 || _broken) return;
        var device = context.GraphicsDevice;
        var commandList = context.CommandList;
        try
        {
            if (_effect is null)
            {
                _effect = new DynamicEffectInstance("GlobeVector");
                _effect.Initialize(context.RenderContext.Services);
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
                l.BlendState = BlendStates.AlphaBlend;
                l.DepthStencilState = DepthStencilStates.DepthRead;
                l.RasterizerState = RasterizerStates.CullNone;
                // no hardware line antialiasing: D3D multiplies its coverage into alpha only, which under
                // premultiplied blending brightened the edge - a white dashed outline round every
                // building and lake (the user, 2026-11-21). GL's gl.LINES are aliased too.
                l.RasterizerState.MultisampleAntiAliasLine = false;
                l.PrimitiveType = PrimitiveType.LineList;
            }
            _effect.UpdateEffect(device);
            if (_effect.Effect is null) return;   // still compiling
            if (_keys.Count == 0)
            {
                foreach (var cb in _effect.Effect.Bytecode.Reflection.ConstantBuffers)
                    foreach (var m in cb.Members)
                        if (m.KeyInfo.Key is { } key) _keys[m.KeyInfo.KeyName] = key;
                // a key not found would leave its value unset in silence: say which were found
                if (!_keys.ContainsKey("GlobeVector.GvWorldView") || !_keys.ContainsKey("GlobeVector.GvProjection"))
                    throw new InvalidOperationException("GlobeVector's parameters not found; the effect has: " + string.Join(", ", _keys.Keys));
            }

            var view = context.RenderContext.RenderView;
            foreach (var p in new[] { _pipeline!, _linePipeline! })
            {
                p.State.RootSignature = _effect.RootSignature;
                p.State.EffectBytecode = _effect.Effect.Bytecode;
                p.State.Output.CaptureState(commandList);
                p.Update();
            }
            bool? lineList = null;

            var parameters = _effect.Parameters;
            float pixel = 2f / (view.Projection.M22 * Math.Max(1f, view.ViewSize.Y));
            Set(parameters, "GvProjection", view.Projection);
            Set(parameters, "GvRadius", frame.Radius);
            Set(parameters, "GvPixel", pixel);
            Set(parameters, "GvLift", Lift);
            Buffer? boundVertices = null;
            foreach (var item in frame.Items)
            {
                if (!ReferenceEquals(item.Vertices, boundVertices))
                {
                    commandList.SetVertexBuffer(0, item.Vertices, 0, VectorVertex.Size);
                    commandList.SetIndexBuffer(item.Indices, 0, true);
                    boundVertices = item.Vertices;
                }
                if (lineList != item.LineList)
                {
                    commandList.SetPipelineState(item.LineList ? _linePipeline!.CurrentState : _pipeline!.CurrentState);
                    lineList = item.LineList;
                }
                Set(parameters, "GvWorldView", item.World * view.View);
                Set(parameters, "GvColor", item.Color);
                Set(parameters, "GvClip", item.Clip);
                Set(parameters, "GvCentre", item.Centre);
                Set(parameters, "GvHalfWidth", item.HalfWidth);
                Set(parameters, "GvIsLine", item.IsLine ? 1f : 0f);
                _effect.Apply(context.GraphicsContext);
                commandList.DrawIndexed(item.Count, item.Start);
                ItemsDrawn++;
            }
        }
        catch (Exception e)
        {
            // a shader that does not compile would throw every frame: say it once, draw nothing
            _broken = true;
            Error = $"GPU vector renderer: {e.GetType().Name} {e.Message}";
            throw;
        }
    }

    void Set<T>(ParameterCollection parameters, string name, T value) where T : struct
    {
        if (!_keys.TryGetValue("GlobeVector." + name, out var key)) return;   // compiled away: unused
        if (key is ValueParameterKey<T> typed) parameters.Set(typed, value);
        else throw new InvalidOperationException($"GlobeVector.{name} is a {key.GetType().Name}, set as {typeof(T).Name}");
    }
}
