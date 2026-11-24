using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using VL.Core.Import;

namespace VL.Globe;

// Lights as vvvv's own are (docs/DESIGN-LIGHT.md): a Stride entity with a LightComponent, wired into the
// scene beside the models - Stride lights the user's models by it, and the Globe's buildings read the
// same light back from the scene's light collection (VectorRenderer). Category Globe.Lights as
// Stride.Lights; pins in the order of vvvv's DirectionalLight: what sets the direction, Color,
// Intensity, Enabled last. Intensity is Stride's (1, as vvvv's DirectionalLight); the buildings take 1
// as GL JS's 0.5.

/// <summary>The sun as a directional light, where it stands at a date and time.</summary>
/// <remarks>Put Entity into the scene beside your models. Unconnected Date Time follows the clock.</remarks>
[ProcessNode(Name = "SunLight", Category = "Globe.Lights")]
public sealed class SunLightNode
{
    readonly LightDirectional _type = new();
    readonly LightComponent _light;
    readonly Entity _entity = new("Sun");

    public SunLightNode()
    {
        _light = new LightComponent { Type = _type };
        _entity.Add(_light);
    }

    /// <param name="dateTime">The moment, in UTC (a local time is converted). Unconnected: now.</param>
    /// <param name="color">The light's colour. Unconnected: white.</param>
    /// <param name="intensity">As Stride's lights: 1 is vvvv's DirectionalLight's default.</param>
    /// <param name="enabled">Off: the scene has no sun.</param>
    [return: Pin(Name = "Entity")]
    public Entity Update(DateTime? dateTime = null, Color4? color = null, float intensity = 1f, bool enabled = true)
    {
        var c = color ?? Color4.White;
        if (_type.Color is not ColorRgbProvider rgb || rgb.Value != c.ToColor3())
            _type.Color = new ColorRgbProvider(c.ToColor3());
        _light.Intensity = Math.Max(0, intensity);
        _light.Enabled = enabled;
        _entity.Transform.Rotation = Towards(SunPosition.Direction(dateTime ?? DateTime.UtcNow));
        return _entity;
    }

    /// <summary>The rotation that turns a light's -Z (the way Stride's light travels) away from <paramref name="towardsLight"/>.</summary>
    internal static Quaternion Towards(Vector3 towardsLight)
    {
        var to = Vector3.Normalize(towardsLight);
        float d = Vector3.Dot(Vector3.UnitZ, to);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f) return Quaternion.RotationY(MathUtil.Pi);
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, to));
        return Quaternion.RotationAxis(axis, MathF.Acos(Math.Clamp(d, -1f, 1f)));
    }
}
