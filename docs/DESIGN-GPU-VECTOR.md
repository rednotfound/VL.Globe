# GPU vector tiles — design, 2026-11-21

The user judged the draped VectorTiles ("我感觉不太行") and agreed to move to RESEARCH-VECTOR's stage 3:
geometry on the GPU, MapLibre's architecture. **Cache geometry, not pixels**: the slow work
(decode, filter, triangulate) is done once per vector tile on a worker thread; every frame the GPU
draws it again from the current camera, which is cheap.

## How it plugs into Stride (found in vvvv 7.4, not guessed)

- `VL.Stride.Rendering.EntityRendererComponent` (VL.Stride.Runtime) holds an `IGraphicsRendererBase`;
  its render feature calls `Draw(RenderDrawContext)` with the scene's `RenderView` pushed, in the
  stage asked for (`DrawerRenderStage.Transparent`: after the opaque tiles, their depth bound).
- A package folder with a `shaders\` subfolder is made visible to Stride's effect system by
  `VLGame.UpdateShaderPaths`, as VL.Audio's `shaders\FFTQueue_ComputeFX.sdsl` is. VL.Globe ships
  `shaders\GlobeVector.sdsl`; `DynamicEffectInstance("GlobeVector")` compiles it at run time
  (VL.Stride's `WireframeRenderFeature` is the template: a `MutablePipelineState`, own vertex
  buffers, `Apply`, `Draw`).
- Parameter keys are taken from the compiled effect's reflection by name, not declared by hand.

## The data, per vector tile (Engine/VectorBucket.cs, CPU, testable)

One vertex buffer and one index buffer per vector tile, every style layer a range in them (one
draw per layer per tile, as GL JS draws a bucket).

- **Vertices are on the unit sphere, relative to the vector tile's centre** (float is fine there;
  the tile entity's translation is computed in double from the camera-relative eye, as the raster
  tiles' is). Long edges are subdivided at coarse zooms so chords follow the sphere.
- **Lines**: `LineTessellator` (the line_bucket port that passes MapLibre's fixtures) emits the
  base point, the extrusion vector and the edge coordinate N separately. The extrusion is turned
  into the sphere's tangent plane (east / north). The vertex shader extrudes in *pixels* (half width
  + 0.5 at the vertex's depth), so a line keeps its screen width at every zoom; the fragment shader
  makes GL's edge ramp `clamp(outset · (1 − |N|))` — what the CPU did by cutting bands.
- **Fills**: polygons triangulated once (NTS `PolygonTriangulator`, ear clipping with holes), plus
  GL's antialiasing outline drawn as a 1 px line.
- **Paint**: a property that depends on the feature is evaluated per vertex at the tile's zoom; a
  property that depends only on the zoom (most of them) is evaluated once per layer per frame at
  the camera's zoom and passed as a uniform. MapLibre interpolates "composite" properties between
  two zoom stops on the GPU; not yet done here.

## Drawing (Engine/VectorRenderer.cs)

- For each style layer, for each vector tile: one draw. Depth test against the globe (back side
  hidden), no depth write, so later layers paint over earlier ones; the vertices are lifted a
  fraction of their distance towards the eye so they never z-fight the raster tiles.
- **Clipping** without a stencil: every vertex carries its position in the tile's square; the
  fragment shader discards outside a clip rectangle (the whole tile, or the part a coarser tile
  stands in for).
- **Which tiles**: the vector tiles that cover the globe tiles shown (zoom − 1 for 512-px vector
  tiles, clamped to the source's maxzoom, overscaled beyond). A tile not built yet is stood in for
  by its nearest built ancestor, clipped to the missing tile's square. A tile with a selected
  descendant is drawn once per globe tile it serves, so nothing is drawn twice.
- Colours are linearised (vvvv renders in linear space); blending is premultiplied.

## The node

`VectorTiles [Globe.Layers]` gains `Rendering`: **GPU** (default) or **Draped** (the old path,
kept for comparison and as the fallback).

## Verification

1. xUnit: buckets of the Berlin fixture tile — vertex counts, every vertex on the unit sphere,
   extrusions tangent, ranges cover the buffers.
2. Rung 4 in vvvv: the shader compiles (Status says so, errors are logged), Liberty at Kyoto Station
   and Nagoya with and without OpenStreetMap; compared side by side with Draped.
3. The user's judgement while moving: frame time, waiting, sharpness, country scale.
4. Later: MapLibre's fixtures rendered on the GPU (offscreen), the same harness.

Not in this step: labels and icons (stage 4), dashes, patterns, composite interpolation, fill
extrusion.
