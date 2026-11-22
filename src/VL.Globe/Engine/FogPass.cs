using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe.Engine;

/// <summary>The fog's reach and colour for one frame (shaders\GlobeFog.sdsl).</summary>
internal readonly record struct Fog(float Start, float End, float Horizon, Color4 Color)
{
    // ROADMAP 4f step 2: where the fog begins and where it is whole, in multiples of the eye's height
    // above the ground (Cesium's fog goes by height too) - so looking straight down nothing is fogged
    // (the farthest ground is ~1.2 heights off), the whole globe from afar neither (its limb is 1.4-2.8
    // heights off from 2,000 km up), and a view tilted at street level fades out a few kilometres off,
    // where the tiles are coarse and buildings missing. Chosen for the globe; they await the user.
    internal const float StartFactor = 3f, EndFactor = 16f;
    internal const float HorizonAngle = 0.05f;   // radians, about 3 degrees of sky over the limb

    /// <summary>A pale haze, linear and premultiplied.</summary>
    internal static readonly Color4 Haze = new(0.62f, 0.68f, 0.75f, 1f);

    /// <summary>The fog for an eye this high above the ground, in scene units.</summary>
    internal static Fog For(float eyeHeight) => new(StartFactor * eyeHeight, EndFactor * eyeHeight, HorizonAngle, Haze);
}

// One full-screen triangle with GlobeFog, blended premultiplied over what is drawn, no depth: the
// ray's distance to the globe is solved in the shader.
internal sealed class FogPass : IDisposable
{
    MutablePipelineState? _pipeline;
    Buffer? _triangle;
    readonly Dictionary<string, ParameterKey> _keys = new();
    static readonly VertexDeclaration Layout = new(VertexElement.Position<Vector2>());

    public void Draw(CommandList commandList, GraphicsContext graphicsContext, EffectInstance effect, Matrix view, Matrix projection,
        Vector3 globeCentre, float radius, Fog fog)
    {
        var device = commandList.GraphicsDevice;
        if (_pipeline is null)
        {
            _pipeline = new MutablePipelineState(device);
            var s = _pipeline.State;
            s.SetDefaults();
            s.InputElements = Layout.CreateInputElements();
            s.BlendState = BlendStates.AlphaBlend;   // premultiplied
            s.DepthStencilState = DepthStencilStates.None;
            s.RasterizerState = RasterizerStates.CullNone;
            s.PrimitiveType = PrimitiveType.TriangleList;
            _triangle = Buffer.Vertex.New(device, new[] { new Vector2(-1, -1), new Vector2(-1, 3), new Vector2(3, -1) });
            foreach (var cb in effect.Effect.Bytecode.Reflection.ConstantBuffers)
                foreach (var m in cb.Members)
                    if (m.KeyInfo.Key is { } key) _keys[m.KeyInfo.KeyName] = key;
        }
        _pipeline.State.RootSignature = effect.RootSignature;
        _pipeline.State.EffectBytecode = effect.Effect.Bytecode;
        _pipeline.State.Output.CaptureState(commandList);
        _pipeline.Update();

        var p = effect.Parameters;
        Set(p, "GfInverseView", Matrix.Invert(view));
        Set(p, "GfProjection", new Vector4(projection.M11, projection.M22, projection.M31, projection.M32));
        Set(p, "GfCentre", globeCentre);
        Set(p, "GfRadius", radius);
        Set(p, "GfStart", fog.Start);
        Set(p, "GfEnd", fog.End);
        Set(p, "GfHorizon", fog.Horizon);
        Set(p, "GfColor", (Vector4)fog.Color);
        commandList.SetPipelineState(_pipeline.CurrentState);
        commandList.SetVertexBuffer(0, _triangle, 0, Layout.VertexStride);
        effect.Apply(graphicsContext);
        commandList.Draw(3);
    }

    void Set<T>(ParameterCollection parameters, string name, T value) where T : struct
    {
        if (!_keys.TryGetValue("GlobeFog." + name, out var key)) return;   // compiled away: unused
        if (key is ValueParameterKey<T> typed) parameters.Set(typed, value);
        else throw new InvalidOperationException($"GlobeFog.{name} is a {key.GetType().Name}, set as {typeof(T).Name}");
    }

    public void Dispose() => _triangle?.Dispose();
}
