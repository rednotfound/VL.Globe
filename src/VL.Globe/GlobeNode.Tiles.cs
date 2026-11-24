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

// The Globe node's tiles: requested, built (texture, mesh, material, entity) on the main thread, evicted.
public sealed partial class GlobeNode
{
    sealed class Slot
    {
        public required Entity Entity;
        public required Texture Texture;
        public required Buffer Vertices;
        public required Buffer Indices;
        public required D3 Centre;       // on the unit sphere, in double: the vertices are offsets from it
        public required ModelComponent Model;
        public required Material Plain;  // its own image only; a fade swaps in another and back
        public Material? Fading;         // ancestor * (1 - f) + own * f, while the fade runs
        public TileKey FadeFrom;         // the ancestor mixed in
        public bool FadeOut;             // false: own image fades IN over the ancestor; true: fades OUT to it
        public double FadeStart;
        public int LastUsed;
        public void Dispose() { Texture.Dispose(); Vertices.Dispose(); Indices.Dispose(); }
    }

    const int MaxCreatedPerFrame = 8;
    const int MaxSlots = 600;
    const int KeepZoom = 3;

    void Request(TileKey t, GlobeTileSource source)
    {
        _pending.Add(t);
        _requests++;
        var info = new TileInfo { Index = new TileIndex(t.X, t.Y, t.Zoom) };
        Task.Run(async () =>
        {
            byte[]? bytes = null;
            try { bytes = await source.Http.GetTileAsync(info).ConfigureAwait(false); }
            catch (Exception e) { _lastError = $"{t}: {e.GetType().Name} {e.Message}"; }   // an answer, counted, not retried
            _arrived.Enqueue((t, bytes, source));
        });
    }

    void CreateArrived(GraphicsDevice device)
    {
        for (int n = 0; n < MaxCreatedPerFrame && _arrived.TryDequeue(out var a); n++)
        {
            if (!ReferenceEquals(a.source, _source)) continue;      // from a source since replaced
            _pending.Remove(a.key);
            if (a.bytes is not { Length: > 0 }) { _failed.Add(a.key); _failedCount++; continue; }
            _bytes += a.bytes.Length;
            try { _loaded[a.key] = Build(device, a.key, a.bytes); }
            catch (Exception e) { _failed.Add(a.key); _failedCount++; _lastError = $"{a.key}: {e.GetType().Name} {e.Message}"; _logger.LogWarning(e, "Globe: building tile {Tile} failed", a.key); }
        }
    }

    int _created;

    // bytes null: a tile without imagery, one pixel of the plain colour
    Slot Build(GraphicsDevice device, TileKey key, byte[]? bytes, Color4 plain = default)
    {
        var texture = bytes is null
            ? Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm_SRgb, new[] { (Color)plain })
            : TileTextures.LoadMipmapped(device, bytes);
        var (vb, ib, mesh, tileCentre) = BuildMesh(device, key);

        // Emissive only, as in the lab: the map is the light, so the globe reads the same in any scene.
        var material = Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                // Anisotropic between the mip levels (MipChain) - a tilted view sees the ground at a grazing
                // angle, where trilinear picks the blurriest level - clamped at the tile's edge: wrapping
                // blended a tile's opposite edge into its border once mips were sampled.
                Emissive = new MaterialEmissiveMapFeature(Sampled(texture, Vector2.One, Vector2.Zero)),
                CullMode = CullMode.None,
            },
        });
        var (entity, modelComponent) = MakeEntity(key, mesh, material, tileCentre);
        return new Slot { Entity = entity, Texture = texture, Vertices = vb, Indices = ib, Centre = tileCentre, Model = modelComponent, Plain = material, LastUsed = _frame };
    }

    // What a tile looks like: its imagery - the same for the tile itself, the fades and the stand-ins that
    // borrow it. (The raster layers above it were laid over it here as an overlay drawn by Skia until ROADMAP 6c.)
    static IComputeColor Look(Slot s, Vector2 scale, Vector2 offset) => Sampled(s.Texture, scale, offset);

    static IComputeColor Sampled(Texture texture, Vector2 scale, Vector2 offset) => new ComputeTextureColor(texture)
    {
        Filtering = TextureFilter.Anisotropic, AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
        Scale = scale, Offset = offset,
    };

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
        if (_loaded.Count <= MaxSlots) return;
        var fadingFrom = _shown.Select(t => _loaded.TryGetValue(t, out var f) && f.Fading is not null ? f.FadeFrom : (TileKey?)null).Where(k => k is not null).Select(k => k!.Value).ToHashSet();
        // Zoom 3 and coarser are never evicted: at most 85 tiles, and the stand-ins of last resort.
        // Least-recently-used evicted them first while the camera sat low, and a fast zoom out then
        // found nothing but the zoom-2 fallback still loading (the user, 2026-11-21).
        fadingFrom.UnionWith(_standIns.Values.Select(si => si.From));   // borrowed by a stand-in
        foreach (var (k, s) in _loaded.Where(kv => kv.Key.Zoom > KeepZoom && !_shown.Contains(kv.Key) && !fadingFrom.Contains(kv.Key)).OrderBy(kv => kv.Value.LastUsed).Take(_loaded.Count - MaxSlots).ToList())
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
        _pending.Clear();
        _failed.Clear();
        _outFades.Clear();
        foreach (var si in _standIns.Values) { _root.Transform.Children.Remove(si.Entity.Transform); si.Dispose(); }
        _standIns.Clear();
        _requests = 0;
        _failedCount = 0;
        _lastError = "";
        _bytes = 0;
    }
}
