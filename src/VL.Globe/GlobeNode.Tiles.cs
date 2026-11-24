using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe;

// The Globe node's tiles: the sphere's surface, cut by the zoom the camera earns - its shape (camera-relative,
// segments by zoom, caps to the poles), its depth (the scene's entities and the buildings' shadows are tested
// against it) and the Background colour. Nothing else: every layer, OSM's tiles included, is drawn on it by the
// GPU (ROADMAP 6c step 3, the user: "地球globe就是蓝色的球就结束"). Built on the main thread, a few a frame, evicted.
public sealed partial class GlobeNode
{
    sealed class Slot
    {
        public required Entity Entity;
        public required Buffer Vertices;
        public required Buffer Indices;
        public required D3 Centre;       // on the unit sphere, in double: the vertices are offsets from it
        public required ModelComponent Model;
        public int LastUsed;
        public void Dispose() { Vertices.Dispose(); Indices.Dispose(); }
    }

    const int MaxCreatedPerFrame = 8;
    const int MaxSlots = 600;
    const int KeepZoom = 3;

    int _created;

    // One material for every tile and stand-in: the Background colour as a 1x1 sRGB texture (an emissive
    // ComputeColor drew black in vvvv, 2026-11-20). Emissive only: the colour reads the same in any scene.
    Material? _plain;
    Texture? _plainTexture;
    Color4 _plainColor;

    Material Plain(GraphicsDevice device, Color4 color)
    {
        if (_plain is not null && color == _plainColor) return _plain;
        var old = _plainTexture;
        _plainTexture = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm_SRgb, new[] { (Color)color });
        _plain = Material.New(device, new MaterialDescriptor
        {
            Attributes = { Emissive = new MaterialEmissiveMapFeature(new ComputeTextureColor(_plainTexture)), CullMode = CullMode.None },
        });
        _plainColor = color;
        foreach (var s in _loaded.Values) s.Model.Materials[0] = _plain;
        foreach (var si in _standIns.Values) si.Model.Materials[0] = _plain;
        if (old is not null) _retiredTextures.Add((old, _frame));   // a frame may still draw with it
        return _plain;
    }
    readonly List<(Texture texture, int frame)> _retiredTextures = new();

    Slot Build(GraphicsDevice device, TileKey key, Color4 color)
    {
        var (vb, ib, mesh, tileCentre) = BuildMesh(device, key);
        var (entity, modelComponent) = MakeEntity(key, mesh, Plain(device, color), tileCentre);
        return new Slot { Entity = entity, Vertices = vb, Indices = ib, Centre = tileCentre, Model = modelComponent, LastUsed = _frame };
    }

    (Entity, ModelComponent) MakeEntity(TileKey key, Mesh mesh, Material material, D3 centre)
    {
        var model = new global::Stride.Rendering.Model { mesh };   // inside namespace VL, "Model" and "Stride" bind to VL.*
        model.Materials.Add(new MaterialInstance(material));
        model.BoundingBox = mesh.BoundingBox;
        model.BoundingSphere = mesh.BoundingSphere;
        var modelComponent = new ModelComponent(model);
        var entity = new Entity(key.ToString()) { modelComponent };
        entity.Transform.Scale = new Vector3(_radius);
        entity.Transform.Position = TileGeometry.Translation(centre, _radius, _relativeEye);
        return (entity, modelComponent);
    }

    static (Buffer vb, Buffer ib, Mesh mesh, D3 centre) BuildMesh(GraphicsDevice device, TileKey key)
    {
        var (tileCentre, offsets, normals, uv) = TileGeometry.RelativeVertices(key);
        var vertices = new VertexPositionNormalTexture[offsets.Length];
        for (int i = 0; i < offsets.Length; i++) vertices[i] = new VertexPositionNormalTexture(offsets[i], normals[i], uv[i]);
        var vb = Buffer.Vertex.New(device, vertices, GraphicsResourceUsage.Immutable);
        var indices = TileGeometry.IndicesFor(key);
        var ib = Buffer.Index.New(device, indices, GraphicsResourceUsage.Immutable);

        var (boundsCentre, r) = TileGeometry.Bounds(key);
        var centre = (new D3(boundsCentre.X, boundsCentre.Y, boundsCentre.Z) - tileCentre).ToVector3();   // in the mesh's own space
        var mesh = new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                DrawCount = indices.Length,
                IndexBuffer = new IndexBufferBinding(ib, false, indices.Length),
                VertexBuffers = new[] { new VertexBufferBinding(vb, VertexPositionNormalTexture.Layout, vertices.Length) },
            },
            MaterialIndex = 0,
            BoundingBox = new BoundingBox(centre - new Vector3(r), centre + new Vector3(r)),
            BoundingSphere = new BoundingSphere(centre, r),
        };
        return (vb, ib, mesh, tileCentre);
    }

    void Evict()
    {
        foreach (var r in _retiredTextures.Where(r => _frame - r.frame > 2).ToList()) { r.texture.Dispose(); _retiredTextures.Remove(r); }
        if (_loaded.Count <= MaxSlots) return;
        // Zoom 3 and coarser are never evicted: at most 85 tiles, and the stand-ins of last resort.
        var borrowed = _standIns.Values.Select(si => si.From).ToHashSet();
        foreach (var (k, s) in _loaded.Where(kv => kv.Key.Zoom > KeepZoom && !_shown.Contains(kv.Key) && !borrowed.Contains(kv.Key)).OrderBy(kv => kv.Value.LastUsed).Take(_loaded.Count - MaxSlots).ToList())
        {
            s.Dispose();
            _loaded.Remove(k);
        }
    }

    void Reset()
    {
        foreach (var t in _shown) if (_loaded.TryGetValue(t, out var s)) _root.Transform.Children.Remove(s.Entity.Transform);
        _shown.Clear();
        foreach (var s in _loaded.Values) s.Dispose();
        _loaded.Clear();
        _failed.Clear();
        foreach (var si in _standIns.Values) { _root.Transform.Children.Remove(si.Entity.Transform); si.Dispose(); }
        _standIns.Clear();
        _failedCount = 0;
        _lastError = "";
    }
}
