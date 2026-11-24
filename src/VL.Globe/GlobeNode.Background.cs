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

// The Globe node's background sphere, just below the tiles.
public sealed partial class GlobeNode
{
    // The globe before any tile: a sphere just below the tiles, drawn by this node because only it
    // knows the eye a camera-relative camera renders from - a Sphere placed by the patch sat at the
    // origin, which is now the camera (ROADMAP item 1). 0.995: no tile facet sags deeper than 0.997
    // (TileGeometryFacts), so it never shows through a tile.
    const float BackgroundRadius = 0.995f;
    Entity? _background;
    GeometricPrimitive? _backgroundMesh;
    Texture? _backgroundTexture;
    Color4 _backgroundColor;
    bool _backgroundShown;

    void ShowBackground(GraphicsDevice? device, Color4 color)
    {
        bool want = device is not null && color.A > 0;
        if (want && (_background is null || color != _backgroundColor))
        {
            _backgroundMesh ??= GeometricPrimitive.Sphere.New(device!, 1f, 64);
            var draw = _backgroundMesh.ToMeshDraw();
            // The colour as a 1x1 sRGB texture, the tiles' own path: a ComputeColor emissive rendered
            // pure black in vvvv (2026-11-20). An sRGB texel also reads the colour the way the tiles'
            // pixels are read, so the two match.
            _backgroundTexture?.Dispose();
            _backgroundTexture = Texture.New2D(device!, 1, 1, PixelFormat.R8G8B8A8_UNorm_SRgb, new[] { (Color)color });
            var material = Material.New(device!, new MaterialDescriptor
            {
                // CullMode None as for the tiles. Not the cause of the black sphere, but a camera that
                // ever ends up inside it (a bad eye) then shows it rather than hiding the fault.
                Attributes = { Emissive = new MaterialEmissiveMapFeature(new ComputeTextureColor(_backgroundTexture)), CullMode = CullMode.None },
            });
            var mesh = new Mesh { Draw = draw, MaterialIndex = 0, BoundingBox = new BoundingBox(new Vector3(-1), new Vector3(1)), BoundingSphere = new BoundingSphere(Vector3.Zero, 1) };
            var model = new global::Stride.Rendering.Model { mesh };
            model.Materials.Add(new MaterialInstance(material));
            model.BoundingBox = mesh.BoundingBox;
            model.BoundingSphere = mesh.BoundingSphere;
            if (_background is not null && _backgroundShown) _root.Transform.Children.Remove(_background.Transform);
            _background = new Entity("Globe background") { new ModelComponent(model) };
            _backgroundShown = false;
            _backgroundColor = color;
        }
        if (want != _backgroundShown || (want && _background is null))
            _logger.LogInformation("Globe: background {State}, colour {Color}, device {Device}", want ? "shown" : "hidden", color, device is not null);
        if (_background is null) return;
        if (want != _backgroundShown)
        {
            if (want) _root.Transform.Children.Add(_background.Transform);
            else _root.Transform.Children.Remove(_background.Transform);
            _backgroundShown = want;
        }
        _background.Transform.Scale = new Vector3(_radius * BackgroundRadius);
        _background.Transform.Position = TileGeometry.Translation(default, _radius, _relativeEye);
    }
}
