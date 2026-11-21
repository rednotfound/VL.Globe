using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using VL.Core.Import;
using VL.Globe.Engine;

namespace VL.Globe;

// The /// text is the tooltip; the reasoning is here.
//
// Standing your own things on the globe (ROADMAP 0.1 item 1). Under GlobeCamera the eye is at the
// origin (camera-relative rendering), so anything placed in absolute coordinates lands on the camera.
// The first design was a transformation node taking the Camera - fine with the separate nodes, but in
// GlobeWindow the camera comes OUT of the node the placed things must go INTO: a cycle that only a
// FrameDelay breaks, and one frame late is exactly the jitter camera-relative rendering removed.
//
// So this is Cesium's way instead: the thing carries where it is (double longitude, latitude,
// height), and the subtraction of the eye happens at DRAW time. Stride computes world matrices in
// TransformProcessor.Draw, after the whole VL patch has run this frame; a TransformLink set on the
// entity is asked for its parent matrix there (TransformComponent.UpdateWorldMatrixInternal:
// World = Local x link). The link finds GlobeCamera's GlobeCameraFrame in the entity's scene - the
// camera entity is in the scene, as it must be for the Mouse - so no pin carries the camera.
//
// Shaped as VL.Stride shapes components: a process node named ...Component whose output goes into an
// entity's Components (Sphere, Model, Entity all have that pin). The entity's own Transformation is
// then the model's local transformation IN METRES: +X east, +Y up, -Z north.

/// <summary>Where on the globe an entity stands; put it into the entity's Components.</summary>
[DefaultEntityComponentProcessor(typeof(GlobePositionProcessor))]
public sealed class GlobePosition : EntityComponent
{
    internal double Longitude, Latitude, Height;
    internal float Radius = 1f;
}

/// <summary>Stands an entity on the globe at a longitude and latitude; its own Transformation is then in metres, +Y up, -Z north.</summary>
/// <remarks>Put Output into the entity's Components (Sphere, Model, Entity). Height is above sea level.</remarks>
[ProcessNode(Name = "GlobePositionComponent", Category = "Globe")]
public sealed class GlobePositionComponentNode
{
    readonly GlobePosition _component = new();

    /// <param name="longitude">Degrees east.</param>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="height">Metres above sea level.</param>
    /// <param name="radius">The globe's radius in scene units - the same as Globe's.</param>
    [return: Pin(Name = "Output")]
    public GlobePosition Update(double longitude = 0, double latitude = 0, double height = 0, float radius = 1f)
    {
        _component.Longitude = longitude;
        _component.Latitude = latitude;
        _component.Height = height;
        _component.Radius = radius > 0 ? radius : 1f;
        return _component;
    }
}

/// <summary>Gives every entity with a GlobePosition the link that places it at draw time.</summary>
internal sealed class GlobePositionProcessor : EntityProcessor<GlobePosition>
{
    protected override void OnEntityComponentAdding(Entity entity, GlobePosition component, GlobePosition data)
        => entity.Transform.TransformLink = new GlobePositionLink(component);

    protected override void OnEntityComponentRemoved(Entity entity, GlobePosition component, GlobePosition data)
    {
        if (entity.Transform.TransformLink is GlobePositionLink link && link.Position == component)
            entity.Transform.TransformLink = null;
    }
}

/// <summary>The parent matrix of a GlobePosition entity: its place relative to this frame's eye.</summary>
internal sealed class GlobePositionLink(GlobePosition position) : TransformLink
{
    public GlobePosition Position { get; } = position;
    GlobeCameraFrame? _frame;   // found once, searched again when it leaves the scene

    public override void ComputeMatrix(bool recursive, out Matrix matrix)
    {
        var p = Position;
        matrix = GlobeCoordinates.Place(Matrix.Identity, FindFrame()?.Eye, p.Longitude, p.Latitude, p.Height, p.Radius);
    }

    // The GlobeCameraFrame in the same root scene; null (absolute placement, as under OrbitCamera)
    // when there is none. Called from Stride's parallel transform update: reads only, and a race on
    // the cache field at worst repeats the search.
    GlobeCameraFrame? FindFrame()
    {
        var cached = _frame;
        if (cached?.Entity?.Scene is not null) return cached;
        var scene = Position.Entity?.Scene;
        while (scene?.Parent is { } parent) scene = parent;
        return _frame = scene is null ? null : Search(scene);
    }

    internal static GlobeCameraFrame? Search(Scene scene)
    {
        foreach (var e in scene.Entities)
            if (Search(e) is { } f) return f;
        foreach (var child in scene.Children)
            if (Search(child) is { } f) return f;
        return null;
    }

    static GlobeCameraFrame? Search(Entity e)
    {
        if (e.Get<GlobeCameraFrame>() is { } f) return f;
        foreach (var t in e.Transform.Children)
            if (Search(t.Entity) is { } g) return g;
        return null;
    }
}
