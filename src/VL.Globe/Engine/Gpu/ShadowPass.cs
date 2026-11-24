using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe.Engine.Gpu;

// Cast shadows of the buildings (ROADMAP 6b, C; docs/DESIGN-LIGHT.md). Two halves:
// - casting: VectorRenderer draws the fill-extrusion depth items once more, into this shadow map,
//   with GlobeVector's projection replaced by view space -> the sun's orthographic clip space;
// - receiving: a full-screen triangle (GlobeShadow / GlobeShadowMS) after the buildings, on the depth
//   they were drawn on, multiplies what is drawn by 1 - strength where the sun's view is blocked.
// Two maps (2048² each, Mapbox's count; Cesium draws four): a near one over the view out to a split
// distance and a far one out to the whole distance, both set by the eye's height - from 1.2 km up the
// near texels are ~1 m instead of ~3 m. Buildings outside the camera's tiles cast nothing (they are not
// in the frame).
internal sealed class ShadowPass : IDisposable
{
    public const int Size = 2048;
    public const int Cascades = 2;
    /// <summary>How dark a shadow is: the target multiplied by 1 - this.</summary>
    public float Strength = 0.45f;

    readonly Texture?[] _maps = new Texture?[Cascades];
    MutablePipelineState? _receiver;
    Buffer? _triangle;
    readonly Dictionary<string, ParameterKey> _keys = new();
    object? _keysOf;
    static readonly VertexDeclaration Layout = new(VertexElement.Position<Vector2>());
    readonly Texture[] _targets = new Texture[8];
    int _targetCount;
    Texture? _depth;
    Viewport _viewport;

    /// <summary>One map's place: view space to its clip space, its depth bias and its normal offset (view units).</summary>
    internal readonly record struct Cascade(Matrix ViewToShadow, float Bias, float Offset);

    /// <summary>One frame's fit: the cascades, where the near one hands over (view distances), where the shadow fades, the light in view space.</summary>
    internal readonly record struct Fit(Cascade Near, Cascade Far, Vector2 Split, Vector2 Fade, Vector3 LightView);

    /// <summary>
    /// The shadow maps' places for a view: orthographic boxes along the light round the view's frustum - the
    /// near one to a split, the far one to a distance, both from the eye's height - deep enough towards the
    /// light for tall buildings, their texels fixed to the ground (snapped in absolute coordinates) so they do
    /// not swim as the camera moves. Null where no shadow is drawn: the light below the horizon or the eye too high.
    /// </summary>
    // view: the camera's view (camera-relative: a rotation; absolute: the eye is its inverse's translation).
    // eye: the eye in absolute scene units (double). Metres -> scene units by radius / EarthRadius.
    internal static Fit? FitFor(Matrix view, Matrix projection, D3 eye, float radius, Vector3 towardsLight)
    {
        double unitsPerMetre = radius / GlobeCoordinates.EarthRadius;
        double heightM = (eye.Length - radius) / unitsPerMetre;
        if (heightM > 15000 || heightM < -100) return null;
        var up = (eye * (1 / eye.Length)).ToVector3();
        if (Vector3.Dot(up, towardsLight) < 0.02f) return null;   // the sun at or below the horizon here
        double distanceM = Math.Clamp(4 * heightM + 1000, 1000, 6000);
        // the split: ~350 m at street level (100 m up), ~2 km from 1.2 km up; never past 40 % of the distance
        double splitM = Math.Clamp(1.5 * Math.Max(heightM, 0) + 200, 300, 0.4 * distanceM);
        float distance = (float)(distanceM * unitsPerMetre), split = (float)(splitM * unitsPerMetre);

        var inverseView = Matrix.Invert(view);
        var lightDir = -Vector3.Normalize(towardsLight);
        var lightUp = Math.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var light = Matrix.LookAtRH(Vector3.Zero, lightDir, lightUp);   // a rotation: the sun's axes
        float tanX = 1 / Math.Max(projection.M11, 1e-6f), tanY = 1 / Math.Max(projection.M22, 1e-6f);
        var camera = inverseView.TranslationVector;                     // 0 for a camera-relative view
        var eyeAbs = new D3(eye.X - camera.X, eye.Y - camera.Y, eye.Z - camera.Z);   // the world's origin, absolute
        double ex = light.M11 * eyeAbs.X + light.M21 * eyeAbs.Y + light.M31 * eyeAbs.Z;
        double ey = light.M12 * eyeAbs.X + light.M22 * eyeAbs.Y + light.M32 * eyeAbs.Z;
        // towards the light: buildings beyond the frustum's box cast into it (1.5 km of margin)
        float margin = (float)(1500 * unitsPerMetre);

        Cascade For(float from, float to)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (float z in new[] { from, to })
                foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                {
                    var world = Vector3.TransformCoordinate(new Vector3(sx * tanX * z, sy * tanY * z, -z), inverseView);
                    var l = Vector3.TransformCoordinate(world, light);
                    min = Vector3.Min(min, l); max = Vector3.Max(max, l);
                }
            // square texels, snapped to the ground: the world here is relative to the eye when the camera is
            // camera-relative, so the eye's absolute place (in the sun's axes, double) is what the grid follows
            float texel = Math.Max(max.X - min.X, max.Y - min.Y) / Size;
            float Snap(float v, double offset) => (float)(Math.Floor((v + offset) / texel) * texel - offset);
            float left = Snap(min.X, ex), bottom = Snap(min.Y, ey);
            float right = left + texel * Size, top = bottom + texel * Size;
            float zNear = -max.Z - margin, zFar = -min.Z + margin * 0.1f;
            var ortho = Matrix.OrthoOffCenterRH(left, right, bottom, top, zNear, zFar);
            // the lookup moved 1.5 texels along the surface's normal (normal offset), the depth compared with
            // 0.3 m to spare: a wall facing the sun at a grazing angle drew stripes of its own shadow without
            float bias = (float)(0.3 * unitsPerMetre / (zFar - zNear));
            return new Cascade(inverseView * light * ortho, bias, 1.5f * texel);
        }

        var lightView = Vector3.Normalize(Vector3.TransformNormal(towardsLight, view));
        // the near map hands over to the far one over the last tenth of the split
        return new Fit(For(split * 0.001f, split), For(split * 0.001f, distance), new Vector2(split * 0.9f, split),
            new Vector2(distance * 0.8f, distance), lightView);
    }

    /// <summary>Binds cascade <paramref name="index"/>'s map as the only target, cleared; EndCasting puts the scene's targets back.</summary>
    public void BeginCasting(CommandList commandList, int index)
    {
        var device = commandList.GraphicsDevice;
        var map = _maps[index] ??= Texture.New2D(device, Size, Size, PixelFormat.D32_Float, TextureFlags.DepthStencil | TextureFlags.ShaderResource);
        _targetCount = commandList.RenderTargetCount;
        for (int i = 0; i < _targetCount; i++) _targets[i] = commandList.RenderTargets[i];
        _depth = commandList.DepthStencilBuffer;
        _viewport = commandList.Viewport;
        commandList.SetRenderTarget(map, (Texture?)null);
        commandList.SetViewport(new Viewport(0, 0, Size, Size));
        commandList.Clear(map, DepthStencilClearOptions.DepthBuffer, 1f);
    }

    public void EndCasting(CommandList commandList)
    {
        commandList.SetRenderTargets(_depth, _targetCount, _targets);
        commandList.SetViewport(_viewport);
    }

    /// <summary>Darkens what is drawn where the sun is blocked, reading <paramref name="sceneDepth"/> (not bound as a target).</summary>
    public void Receive(CommandList commandList, GraphicsContext graphicsContext, EffectInstance effect, Texture sceneDepth,
        Matrix projection, Fit fit)
    {
        if (_maps[0] is null || _maps[1] is null) return;
        var device = commandList.GraphicsDevice;
        if (_receiver is null)
        {
            _receiver = new MutablePipelineState(device);
            var s = _receiver.State;
            s.SetDefaults();
            s.InputElements = Layout.CreateInputElements();
            // target x source colour, its alpha kept
            s.BlendState = new BlendStateDescription(Blend.Zero, Blend.SourceColor);
            s.BlendState.RenderTarget0.AlphaSourceBlend = Blend.Zero;
            s.BlendState.RenderTarget0.AlphaDestinationBlend = Blend.One;
            s.DepthStencilState = DepthStencilStates.None;
            s.RasterizerState = RasterizerStates.CullNone;
            s.PrimitiveType = PrimitiveType.TriangleList;
            _triangle = Buffer.Vertex.New(device, new[] { new Vector2(-1, -1), new Vector2(-1, 3), new Vector2(3, -1) });
        }
        if (!ReferenceEquals(_keysOf, effect.Effect))
        {
            _keys.Clear();
            foreach (var cb in effect.Effect.Bytecode.Reflection.ConstantBuffers)
                foreach (var m in cb.Members)
                    if (m.KeyInfo.Key is { } key) _keys[m.KeyInfo.KeyName] = key;
            foreach (var r in effect.Effect.Bytecode.Reflection.ResourceBindings)
                if (r.KeyInfo.Key is { } key) _keys[r.KeyInfo.KeyName] = key;
            _keysOf = effect.Effect;
        }
        _receiver.State.RootSignature = effect.RootSignature;
        _receiver.State.EffectBytecode = effect.Effect.Bytecode;
        _receiver.State.Output.CaptureState(commandList);
        _receiver.Update();

        var p = effect.Parameters;
        Set(p, "GhProjection", new Vector4(projection.M11, projection.M22, projection.M31, projection.M32));
        Set(p, "GhDepth", new Vector2(projection.M33, projection.M43));
        Set(p, "GhViewToShadow0", fit.Near.ViewToShadow);
        Set(p, "GhViewToShadow1", fit.Far.ViewToShadow);
        Set(p, "GhViewport", new Vector2(commandList.Viewport.Width, commandList.Viewport.Height));
        Set(p, "GhTexel", 1f / Size);
        Set(p, "GhBias", new Vector2(fit.Near.Bias, fit.Far.Bias));
        Set(p, "GhOffset", new Vector2(fit.Near.Offset, fit.Far.Offset));
        Set(p, "GhSplit", fit.Split);
        Set(p, "GhStrength", Strength);
        Set(p, "GhFade", fit.Fade);
        Set(p, "GhLightView", fit.LightView);
        Set(p, "GhSamples", Math.Max(1, (int)sceneDepth.MultisampleCount));   // None = 1, X2 = 2, X4 = 4, X8 = 8
        SetTexture(p, "GhShadowMap0", _maps[0]!);
        SetTexture(p, "GhShadowMap1", _maps[1]!);
        SetTexture(p, "GhSceneDepth", sceneDepth);
        commandList.SetPipelineState(_receiver.CurrentState);
        commandList.SetVertexBuffer(0, _triangle, 0, Layout.VertexStride);
        effect.Apply(graphicsContext);
        commandList.Draw(3);
    }

    void Set<T>(ParameterCollection parameters, string name, T value) where T : struct
    {
        if (!_keys.TryGetValue("GlobeShadowBase." + name, out var key)) return;   // compiled away: unused
        if (key is ValueParameterKey<T> typed) parameters.Set(typed, value);
        else throw new InvalidOperationException($"GlobeShadowBase.{name} is a {key.GetType().Name}, set as {typeof(T).Name}");
    }

    void SetTexture(ParameterCollection parameters, string name, Texture texture)
    {
        foreach (var (k, key) in _keys)
            if (k.EndsWith("." + name, StringComparison.Ordinal) && key is ObjectParameterKey<Texture> typed) { parameters.Set(typed, texture); return; }
        throw new InvalidOperationException($"{name} not among the shadow effect's parameters: " + string.Join(", ", _keys.Keys));
    }

    public void Dispose() { foreach (var m in _maps) m?.Dispose(); _triangle?.Dispose(); }
}
