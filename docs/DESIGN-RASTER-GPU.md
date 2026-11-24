# Raster tile layers on the GPU (ROADMAP 6c)

Asked by the user 2026-11-24, after shapes became GPU layers: "如果可以的话应该把栅格图层也做成GPU图层". Asked to
judge between keeping the globe's tiles as the bottom imagery (A) and every raster layer on the GPU (B),
the user weighed one architecture for maintenance, the same pins and behaviour everywhere, and COG/COPC
later; **B, in three steps** (my judgement, asked for: "需要你给我判断").

## Why

Today a raster layer is drawn two ways: the Input's first opaque one is the globe tiles' own image (a
Stride material: mipmaps, fades both ways, stand-ins, pole caps), every other one is drawn by Skia into
each tile's overlay image. Shapes and VectorTiles are GPU layers drawn after all of them, so a raster
layer above a shape cannot cover it, and Opacity, fades and fallback are code twice. After B:

- **one path**: every surface layer is drawn by `VectorRenderer` in the Input's order - raster, fill,
  line, extrusion, symbol - as MapLibre's painter does with its style's layers;
- the globe's tiles keep the sphere's shape and depth (shadows, the user's models) and the Background
  colour, nothing else;
- a new raster source (COG: a tiled pyramid) is one more source for the same layer; a point cloud (COPC)
  is a GPU layer with depth, as the buildings are; terrain (0.2) drapes one stack of layers per tile.

## What the references do (read from source 2026-11-24; quotes in the session's survey)

| | MapLibre GL JS (`src/webgl/draw/draw_raster.ts`) | CesiumJS (`GlobeSurfaceTileProvider.js`, `TileImagery.js`) |
|---|---|---|
| layers | one draw per layer per tile, in style order, interleaved with vector layers | all imagery layers of a tile in one draw (multi-texture, `TEXTURE_UNITS`), more passes past the limit; vectors after |
| tiles drawn | the source cache's renderable tiles: ideal ones, else loaded children, else a loaded parent | every globe tile; its imagery the finest READY ancestor while the right one loads |
| overlap of parent and child | stencil per zoom (`GEQUAL`, highest zoom first); on the globe two passes with `GREATER` - "any pixel drawn at most once" | none: imagery belongs to the globe tile, an ancestor sampled through a texture rectangle (`textureTranslationAndScale`) |
| fade | `raster-fade-duration` 300 ms, a parent and its children cross-faded in the shader: `u_image0` own, `u_image1` the parent with `u_tl_parent`/`u_scale_parent`, `mix` by `u_fade_t`; off under terrain | none on imagery |
| filtering | mipmaps (`generateMipmap`), min `LINEAR_MIPMAP_NEAREST`, anisotropic max when pitch > 20° | per layer LINEAR by default |
| per-layer paint | opacity 1, hue-rotate 0, brightness-min 0 / max 1, saturation 0, contrast 0, resampling linear, fade 300, layer-blend normal | alpha 1, brightness 1, contrast 1, hue 0, saturation 1, gamma 1, split, colour-to-alpha, cutout |
| globe mesh | per tile, subdivided (`create_tile_mesh.ts`: 128 at z0, 32 from z2), the top and bottom rows to the pole | the terrain/ellipsoid tile mesh |
| terrain | render-to-texture per terrain tile of each stack of 2D layers (background, fill, line, raster, hillshade), mipmapped, cached by fingerprint, draped on the terrain mesh | imagery in the globe shader, on the terrain mesh |

## The design

- **A `Raster` pass in `VectorRenderer`**, an item per (layer, shown globe tile) in the Input's order
  among the vector items. The layer list of `GpuVectorTiles` becomes the Input's list with raster
  layers in their places, instead of a service list after the drape.
- **Per globe tile, not per source tile** (Cesium's way, which our `DrawSet` already is): the globe
  tiles shown never overlap (DrawSet draws none over its own ancestor), so no stencil is needed. The
  tile's mesh is the globe tile's own (`TileGeometry`: segments by zoom, `CapRows` to the poles).
- **The texture**: the source tile of the globe tile's square at min(zoom, the source's maxzoom); else
  the finest loaded ancestor, sampled through a rectangle (GL JS's `u_tl_parent`/`u_scale_parent`,
  Cesium's `textureTranslationAndScale`) - the stand-in and the overscale are the same case.
- **The fade**: GL JS's cross-fade, two textures in one draw (own and ancestor, `mix` by the fade) - the
  globe material's `Fading` today (`ancestor * (1 - f) + own * f`); both ways, as `Fade.cs` does now.
- **Depth**: the ground layers' (`GlobeVector.sdsl`: the sphere's depth at the pixel, the lift, the
  slope-scaled offset of 2026-11-24), read-only.
- **Textures**: fetched and decoded on workers from the source's HTTP client and disk cache (shared with
  VL.Mapsui), mip chain built there (`MipChain`, sRGB, straight alpha), uploaded a few a frame nearest
  first; one LRU per source, zoom 3 and coarser never evicted (as `GlobeNode.Evict`). Anisotropic
  sampling when tilted (GL JS: past 20°).
- **Paint**: `Opacity` (the pin today). Brightness/contrast/saturation/hue (both references have them)
  only when asked.
- **Not in this item**: render-to-texture stacks for terrain (0.2 decides); COG/COPC sources.

## The steps

1. **GPU raster layers for the raster layers above the first** - all of the design above. Then the
   Skia overlay (`Engine/Draped`, `GlobeNode.Overlays.cs`) is removed: no drape left. Tests offscreen:
   a raster layer over a shape layer and under another (order), an ancestor through its rectangle
   (stand-in, overscale), the pole cap, multisampled.
2. **As good as the globe's imagery**: the same layer drawn both ways, side by side - stills at street
   level and from space, the poles, and moving (fades, stand-ins, no flicker far off, frame times in
   the `Globe perf` lines). **The user judges** before step 3.
3. **The first layer too**: the globe tiles keep shape, depth and the Background colour; their imagery
   material, fades and stand-in entities are removed (`GlobeNode.Tiles/Show`, `Fade.cs`'s material use).

Each step its own commits, tests and rung 4; the help patches (`Stack tile layers`, `Open a globe
window`) unchanged in their wiring.
