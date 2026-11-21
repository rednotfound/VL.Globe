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

## As built, 2026-11-21 night — what changed after the user looked

The design held ("大方向完全没有问题"); these are the corrections, each from something seen:

| seen | cause | now |
|---|---|---|
| dark globe while tiles load | the style's background lived in the data buckets | a data-free background grid per needed tile, at once; zooming out, built descendants stand in before an ancestor |
| a chequerboard from afar | 8 background cells at zoom 1 are 22°, sagging 1.8% under the globe | cells, fills and lines cut to ≤ 2° |
| then discs of background in the sea at zoom 8 | an absolute 2° bound: an ocean square was two flat triangles 1 km deep, seen from 300 km where the lift is 450 m | cut to an eighth of a tile up to zoom 10 — **the sag bound is relative to the distance the zoom is seen from** |
| dotted cracks at tile borders | two tiles clipping one edge each discarded some pixels | clip rectangles overlap by 2e-4; the background grid reaches past its square |
| buildings vanish past z14 | Liberty's `building` stops at 14, `building-3d` (fill-extrusion) takes over and is not drawn | next: fill-extrusion flat, then 3D |
| white dashed outlines, then jagged ones | D3D line antialiasing puts coverage into alpha only; under premultiplied blending that brightens | outlines blended straight with hardware AA |
| "偶尔…会卡一下" while loading | builds on ProcessorCount − 2 pool threads; 4 tiles of 15 MB uploaded in a frame; MB arrays dropped per tile | MapLibre's worker count below normal priority; 8 MB a frame; pooled arrays; outlines as a line list (385k → 201k vertices a tile) |

Measured after: a second of 80 tiles / 170 MB uploaded kept every frame under 27 ms. Updates of
200-330 ms remained, each in a second with a gen-2 collection but with 2 ms of counted pause: the
stages named the draw list - a new large-object-heap list every frame, which waited on the
background collection it fed. Two frames are reused in turn now. Build time single-threaded, Liberty around Kyoto Station: 118 → 66 ms a tile.

**3D buildings (fill-extrusion), the same night:** GL JS's fill_extrusion_bucket and shader. The roof is
the polygon raised to `fill-extrusion-height`; each ring edge is a wall from `-base` to the height,
its normal outward (by the ring's winding, flipped for holes). The light is GL JS's default
(viewport-anchored [1.15, 210°, 30°], intensity 0.5, ambient 0.03, the vertical gradient computed per
vertex on the CPU), lit in sRGB and then linearised. A layer is drawn in two passes, depth written
then colour where equal, so a translucent layer of buildings is one even layer. Only lenient parses
keep it: the harness renders with the drape. Cost: a street-level Liberty tile is ~22 MB now.
The depth pass writes to a copy of the scene's depth: in vvvv's transparent stage the scene's depth
is bound read-only, and the first build's buildings showed their far walls (the user).

**A 28-byte vertex** (from 40): only the position stays float; extrusion snorm16, uv unorm16 over
-0.5..1.5, N and width half floats. Two Kyoto Station tiles 44 → 33 MB, the picture unchanged.
The user tilted the 3D buildings: "确实好很多了".

## Next

1. MapLibre's fixtures (fill, line, fill-extrusion) rendered by the GPU path offscreen, the same
   harness and pixelmatch as the drape's: the GPU path has no measured yardstick yet.
2. Labels and icons (RESEARCH-VECTOR stage 4: glyph PBFs, SDF, placement and collision) - the
   largest piece left before 0.1's vector map reads like MapLibre's.
3. 16-bit indices where a range allows, and dashes (line-dasharray) - drawn solid today.
4. MapLibre's fixtures rendered by the GPU path offscreen, the same harness as the drape's.
