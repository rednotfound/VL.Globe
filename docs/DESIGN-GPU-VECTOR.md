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

**MapLibre's render tests on the GPU path (2026-11-22): 97 of 164 pass, 89 that an empty image would
fail** (the drape: 98 / 90), 2 ignored with reasons, the same 65 unsupported. `MapLibreGpuRenderFacts`
renders offscreen in the test host: a D3D11 device of its own, GlobeVector.sdsl compiled by Stride's
`EffectCompiler` from the repo (with ShaderBase/ShaderBaseStream from the Stride package), the same
VectorBucketBuilder, VectorDrawList and `VectorRenderer.DrawFrame` the globe uses - only with the
buckets on the Mercator plane (`VectorProjection.Mercator`), GL JS's camera (0.6435 rad), sRGB
blending (`GvLinearize` 0) and GL's fill outline (`GlOutlines`). What the fixtures taught:
- **Clip by the pixel, not by the line's point**: GL clips each tile's drawing with the stencil, so a
  line's extruded edge past the square is cut and the neighbour's copy draws the rest. The vertex
  shader now moves uv with the extrusion (a tile is 1/2^z of the world on the plane, 2π cos φ / 2^z
  on the unit sphere). +2 fixtures; the globe looks the same.
- **GL's fill outline** is aliased gl.LINES with alpha 1 − smoothstep of the pixel's distance to the
  line: +9 fixtures. vvvv keeps the hardware's antialiased line the user judged.
- **The extrusion is GL's own bytes** (k / 63, R8G8B8A8_SInt; Stride wants 4-byte elements).
- **An edge exactly on a row of pixel centres** falls to the other side in GL (its framebuffer is y
  up): the test camera is moved down 1/256 px (1/1024 was under the float error and did nothing).
- GeoJSON sources are drawn by the GPU path too (geojson-vt's grid: 8192 steps a tile, maxzoom 18),
  on the globe as well.

## Labels (ROADMAP 4e), 2026-11-22 - milestones 1 and 2

The fixtures first, as for the lines: the text-* groups (134 fixtures) and Open Sans Semibold's glyph
ranges fetched; MapLibre's symbol-shaping expectations copied into the tests. Then GL JS's pieces, each
ported against them:
- `GlyphPbf` (glyph ranges), `GlyphStore` (ranges loaded through the style's Load/Peek/Pending, so a
  missing range rebuilds the tile when it comes), `GlyphAtlas` (one per style, shelves, a glyph never
  moves: buckets keep their texture coordinates; the image is uploaded again when it grows).
- `Shaping.cs` - shapeText for horizontal text, ported by a subagent against the 8 expected JSONs
  (vertical text, images in text and unspaced scripts' dictionary breaking throw unsupported).
- `SymbolLayout.cs` - symbol_layout.ts and quads.ts for point labels: text-field (tokens and
  expressions, concat), text-font stacks, sizes at the tile's zoom and the next, anchors (points, line
  starts, polygons' poles of inaccessibility), only inside the tile; glyph quads x 32 as GL JS stores them.
- `GlobeSymbol.sdsl` - symbol_sdf: the anchor projected (on the sphere or the plane), the quad in
  screen pixels, GL JS's perspective ratio, its SDF smoothstep and halo, halo and text in one pass or
  two. No depth test, as GL JS; the globe's far side hidden.
- `LabelPlacement.cs` (milestone 2) - placement.ts / collision_index.ts / grid_index.ts for viewport
  point labels, every frame in the renderer: layers top first, boxes at the layout size with
  text-padding times the perspective ratio, a 25-px grid with 100 px round the view, the 0.6 cutoff.
  Each tile's per-vertex placement is a second vertex stream.

**MapLibre's fixtures on the GPU path: 154 pass, 146 non-vacuous** (97 / 89 before labels): 56 text
fixtures, and visibility/text/visible - dozens of POIs - placed exactly as GL JS places them.
**Seen in vvvv on Liberty at Kyoto Station**: Latin and Japanese names, halos, transit labels in
blue italics, no overlaps.

**Milestone 3, labels along lines (2026-11-22):** `LineAnchors.cs` (getAnchors, getCenterAnchor,
checkMaxAngle, clipLine; a subagent's port, GL JS's 24 unit tests) run at GL JS's 8192 units so anchors
round alike; `LineLabels.cs` places each glyph along the projected line every frame
(placeGlyphsAlongLine, keep-upright flipping) and the placement collides a row of circles along the
label (placeCollisionCircles); GlobeSymbol.sdsl reads the placed glyphs from a third vertex slot.
GeoJSON line-center labels sit at the middle of their geojson-vt piece (128 px buffer).
**158 fixtures pass, 150 non-vacuous.** Seen in vvvv: river and canal names along the water.
Not yet: text-offset across a line (findOffsetIntersectionPoint), line labels aligned to the viewport.

## Icons (milestone 4) - the plan, 2026-11-22

Ready: `tools\Fetch-MapLibreFixtures.ps1` fetches the 12 icon-* groups (111 fixtures; all
"unsupported" now) and the sprites they load (`_assets\sprites\<name>.json/.png`, `@2x` beside).
GL JS sources to port (download as for the labels, into the scratchpad's maplibre-src):
`src/style/load_sprite.ts`, `src/render/image_atlas.ts` (IMAGE_PADDING 1), `src/render/image_manager.ts`,
`src/symbol/shaping.ts` shapeIcon / fitIconToText, `src/symbol/quads.ts` getIconQuads (stretch zones),
`src/webgl/draw/draw_symbol.ts` (icon program), `symbol_icon.vertex/fragment.glsl` (non-SDF) and the
SDF program for sdf icons, `placement.ts` (icon/text joint placement: icon-optional, text-optional,
icon-allow-overlap / ignore-placement), `collision_feature.ts` (icon boxes).
Steps, each against the fixtures:
1. Sprites: the style's `sprite` (a URL or a list of {id, url}), `.json` + `.png` (decode with
   SkiaSharp, which the library already has), `@2x` when the pixel ratio is 2; an image atlas per style
   (RGBA, 1-px padding, premultiplied), uploaded as the glyph atlas is.
2. Layout: icon-image (tokens / expressions), icon-size, icon-anchor, icon-offset, icon-rotate,
   shapeIcon and getIconQuads; icons in the same SymbolVertex stream as text (a flag for the
   fragment path: RGBA sample vs SDF), drawn before the layer's text.
3. Shader: GlobeSymbol gains the icon path - non-SDF: the atlas colour x opacity; SDF: as text, with
   icon-color / icon-halo-*.
4. Placement: icon boxes, the joint rule (both placed or neither, unless one is optional).
5. icon-text-fit (shields): fitIconToText, then stretch zones (the `stretch` sprite's 17 fixtures).
6. Liberty in vvvv: POI icons, road shields (line placement, viewport-aligned: needs "line labels
   aligned to the viewport" too), one-way arrows.

**As built, 2026-11-22 - steps 1 to 4 (point icons):**
- `SpriteStore.cs` - the style's `sprite` (a URL or a list of {id, url}; "<id>:<name>" for a sprite
  other than "default"), `.json` + `.png` (`@2x` when `MapStyleDocument.PixelRatio` is above 1)
  through Load / Peek / Pending as the glyphs; decoded unpremultiplied with SkiaSharp, premultiplied
  as GL JS's texture.ts (`Math.round(c * a / 255)`); `ImageAtlas` - RGBA, shelves 2048 wide, 1-px
  padding, one per style, an image never moves.
- `SymbolLayout.cs` - icon-image (tokens, expressions), icon-size (layout at zoom + 1; per feature in
  the vertex), icon-anchor, icon-offset, icon-rotate, icon-padding (1-4 numbers, CSS order); shapeIcon
  and getIconQuads' one quad (the padding inside it), in the same SymbolVertex stream as text, an icon
  range before the layer's text range. The bucket's first icon decides SDF or not (GL JS's
  `sdfIcons`) and GL JS's `iconsNeedLinear` is kept per layer. Overlap-able layers are drawn sorted by
  anchor y (`sortFeatures`).
- `GlobeSymbol.sdsl` - `GsIconMode` 1: the atlas colour x opacity (unpremultiplied, linearised and
  premultiplied again in vvvv); 2: the SDF path with the field in alpha and the font scale the
  icon-size itself. `GsNearest` - gl.NEAREST where GL JS uses it (icon-size 1, the device pixel ratio,
  no rotation), in the render tests only: on the globe always linear.
- `LabelPlacement.cs` - a point symbol has a text box and an icon box; each placed, then GL JS's joint
  rule (both or neither unless *-optional), then inserted (unless *-ignore-placement); shown when
  placed or by alwaysShowText / alwaysShowIcon.
- Not yet: icon-text-fit (step 5), icons along lines (6), icon-size by zoom and feature (one size
  per vertex, as text-size). A lenient style leaves those icons out and keeps the layer's text.

**MapLibre's fixtures on the GPU path: 252 pass, 243 non-vacuous** (158 / 150 before icons): 54 of
the 111 icon-* fixtures. Not passing: icon-text-fit (45 unsupported, 1 passes), the two composite
icon-size fixtures (ignored), and 10 unsupported for other reasons - an icon halo per feature (3), the
`image` expression, circle layers (2), pitch, collision debug boxes (3).
**Seen in vvvv on Liberty at Kyoto Station (700 m)**: bus, rail, hospital, school and shop icons with
their names beside them, no overlaps; road names along the roads kept (their shields left out).

**Step 5, the same day: icon-text-fit, stretch zones - and circle layers to see them.**
- `IconShaping.cs` - shapeIcon, fitIconToText, applyTextFit (textFitWidth / textFitHeight), the icon
  collision box with the content padding, getIconQuads with stretchX / stretchY / content (up to 15
  quads an icon). `SymbolVertex` is 36 bytes now: GL JS's a_pixeloffset (x 16) and a_minFontScale
  (x 256), the shader's `offset / 32 * max(minFontScale, fontScale) + pixelOffset / 16`.
- Most icon-text-fit fixtures mark their anchors with a **circle layer**, so circles came first:
  `CircleLayout.cs` (circle_bucket.ts: a quad at every point inside the tile; the seven paint
  properties per feature in a 36-byte `CircleVertex`, or uniforms) and `GlobeCircle.sdsl` (circle
  shaders, pitch-alignment viewport, pitch-scale map or viewport; antialiasing, blur and stroke).
  circle-translate and pitch-alignment map are refused; circle-sort-key is not read (strict: refused).
- **Labels in CSS pixels**: offsets in the shader and LabelPlacement's grid are CSS pixels, times the
  device pixel ratio on the target (`Frame.PixelRatio`, 1 in vvvv) - the 2x-screen fixtures.
- A legacy filter with an expression inside ("any" of both) is evaluated part by part, as GL JS.
- **304 pass, 295 non-vacuous**; the icon-text-fit group: all but variable anchors, vertical text and
  the shield along a line. Liberty uses neither icon-text-fit nor circles: nothing new to see there.

**Step 6, the same day: symbols on lines.**
- `LinePlacements` (SymbolLayout.cs): the anchors along a feature's lines (getAnchors / getCenterAnchor,
  anchorIsTooClose), shared by text along lines and the two new cases:
  - text and icon upright at the line's anchors (text- and icon-rotation-alignment viewport; Liberty's
    shields): point symbols there - one line of text, the joint placement of point symbols;
  - an icon alone along the line (icon-rotation-alignment map or auto; Liberty's one-way arrows): a
    `LineLabel` whose "glyphs" are its quads at offset 0, turned to the line every frame
    (icon-keep-upright), colliding as GL JS's icon box round the anchor; sampled linear (pitched with
    the map in GL JS, so "transformed"); sorted by anchor y with the point symbols where they may overlap.
  - not yet: an icon beside text along a line (strict refuses; lenient keeps the text), icon-offset along a line.
- Found on the way: GL JS v6.19's pole of inaccessibility prefers the centroid when it is inside and
  within the precision (symbol-placement/point-polygon); anchors are truncated to GL JS's Int16 units.
- Fetched: symbol-placement, symbol-spacing, text-/icon-rotation-alignment, icon-pitch-alignment (the
  alignment groups all need `bearing`). **311 pass, 302 non-vacuous.** Not passing: the two
  *-overscaled fixtures (ignored) - below.

**Symbols past the source's maxzoom, the same day.** GL JS (default options) lays the maxzoom tile's
symbols out again at the overscaled zoom: one bucket, `tilePixelRatio = 8192 / (512 x overscaling)`,
layout properties at that zoom (MapLibre slices real sub-tiles only with the experimental
`zoomLevelsToOverscale`). Here: `Build(..., overscale)`; `GpuVectorTiles` builds a tile's symbol
layers as an entry of their own keyed by the overscale (the finest of the globe tiles it serves), so
fills and lines are not rebuilt when zooming in, and while a new layout builds the nearest one built
is shown. Seen in vvvv at Kyoto Station (700 m, overscale 4): street names along Karasuma, Muromachi,
Higashinotoin, Shinmachi and the North-South Free Passage, where only Shiokoji and Hachijo had one.
The two *-overscaled fixtures are much closer (26% -> 20%, 5.5% -> 4.9% of pixels) but stay ignored:
on some lines the anchors sit about half the spacing off GL JS's, cause not found (getAnchors and
clipLine match line by line; integer coordinates changed nothing; overscaling 1 in getAnchors came
closer, 5.7% - yet GL JS's own tests pass it as we do).

## Next

1. Fades (300 ms) and labels across tiles of two zooms (CrossTileSymbolIndex), the `format`
   expression, text-size by zoom and feature, text-offset across a line, an icon beside text along a line.
2. Seen in vvvv at Kyoto Station: the one-way arrows along the roads. Shields not yet seen (no road
   with a ref in that view).
2. Labels: fades (300 ms) and labels across tiles of two zooms (CrossTileSymbolIndex), the `format`
   expression, text-size by zoom and feature, text-offset across a line.
3. Labels and icons (RESEARCH-VECTOR stage 4: glyph PBFs, SDF, placement and collision) - the
   largest piece left before 0.1's vector map reads like MapLibre's.
3. 16-bit indices where a range allows, and dashes (line-dasharray) - drawn solid today.
4. MapLibre's fixtures rendered by the GPU path offscreen, the same harness as the drape's.
