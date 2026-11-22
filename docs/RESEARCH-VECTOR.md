# Toward a vector globe — research, 2026-11-21

The user, after seeing experiment A (NTS geometry draped into the tiles, Cesium's architecture):
MapLibre GL JS's "globe with a vector map" is a different architecture, and "那是未来，就算现在不做，将来也是要做的".
They asked which .NET frameworks are mature enough to use, and said testing needs more preparation.
This is the survey (web, NuGet, GitHub activity as of 2026-11-21; nothing was built), with the
staged path it suggests.

## The two architectures

| | Cesium-like (what we have) | MapLibre-like (the goal) |
|---|---|---|
| Data | raster tiles; vectors drawn into per-tile images | vector tiles (MVT): geometry + attributes per tile |
| Style | baked into the images | MapLibre Style JSON, evaluated at runtime |
| Drawing | textures on a mesh | GPU per frame: fills triangulated, lines extruded in shaders, SDF text and icons |
| Labels | inside the tile image; collide only within a tile, cut at its edge | screen-space, collision across tiles, always upright |
| Globe | the tile mesh is already on the sphere | tile geometry projected in the vertex shader, subdivided |

## What .NET offers

**Short answer:** no .NET library renders vector maps on the GPU. Two ways in are usable:
- decode tiles and parse styles in C#, then draw them into textures with SkiaSharp — mature enough
  today, with the SkiaSharp version pinned;
- MapLibre Native through its new official C API, which can export a D3D11 shared texture. It is
  pre-1.0, its .NET binding targets net10, and its globe is still an open PR.

Symbol placement and collision exist in no .NET library.

### Reading tiles

| Name | Status | Fit |
|---|---|---|
| NetTopologySuite.IO.VectorTiles(.Mapbox) | 1.1.0 (2024-08), 475k downloads, MIT, netstandard2.0 | **best fit** — NTS is already ours |
| mapbox/vector-tile-cs | BSD-3, lazy decoder, NuGet still 1.0.4-alpha2 | if allocation becomes the problem |
| PMTiles.NET | 0.1.2, 2024-12, MIT | small; port or write our own |
| BruTile.MbTiles 6.0 | Apache-2.0 | BruTile 6 — we are pinned to 5.0.6 |
| DP.VectorMapTileRenderer (core) | created 2026-11-05, MVT + MLT + PMTiles, MIT | promising, unproven — watch |

### Style JSON and expressions

- VexTile's parser covers older Mapbox v8 plus part of the expressions.
- DP.VectorMapTileRenderer covers "a broad subset".
- MapLibre Native's FFI exposes the real evaluator.
- **No .NET port of maplibre-style-spec exists**. That TypeScript package, together with its expression
  tests, is the reference to port.

### Renderers

| Name | Fit |
|---|---|
| VexTile.Renderers (MVT + style → SKBitmap, with labels) | **1.0.0/1.0.1 use SkiaSharp 2.88.7, compatible with vvvv's 2.88.8**; 1.0.2+ needs 2.88.9 / 3.x. Usable now if pinned; licence lineage and quality to check |
| Mapsui.Experimental.VectorTiles 5.2 | SkiaSharp 3 — not for us. Confirms that Mapsui also rasterises vector tiles per tile |
| MapLibre Native + maplibre-native-ffi | BSD-2. The real GPU renderer behind an official C API; FFI v0.202611.0, **pre-1.0, ABI may break**; .NET binding net10-only (we would P/Invoke the header from net8) |

MapLibre Native, in more detail:
- **Windows backends:** Vulkan and OpenGL. There is no D3D11 rendering backend.
- **Export to D3D11:** `MLN_GPU_EXPORT_KIND_DXGI_SHARED_HANDLE`, a D3D11 B8G8R8A8 texture opened with `OpenSharedResource1` and synchronised with fences. That makes a zero-copy path into Stride plausible, but it is untested.
- **Globe:** not released. PR #4533 is open, and only iOS/Android preview tags exist.
- **Prior art:** maplibre-unity uses the FFI with CPU readback.

### Text

- **SDF glyphs.** MapLibre fetches `{fontstack}/{range}.pbf`: 24 px SDF, 3 px buffer. OpenFreeMap and VersaTiles serve them, and decoding the glyph PBFs is the simplest route. SharpMSDF is a C# msdfgen port (MIT).
- **Shaping.** HarfBuzzSharp ships with vvvv; MapLibre GL JS does no HarfBuzz shaping itself.
- **Placement and collision: no .NET library.** They would be ported from GL JS (`GridIndex`, `CollisionIndex`, `placement.ts`, the cross-tile symbol index). **This is the hardest part.**

### Tessellation

- Polygons: Cutear (ISC, 2026-10) or Earcut.NET (MIT, 2026-10).
- Line extrusion: no library; port GL JS `line_bucket.ts`, about 600 lines.
- Globe subdivision: port `subdivision.ts`.

### Testing

- **MapLibre's render tests.** `test/integration/render` holds **1,796 tests, 128 of them under `projection/globe`**.
  - Each test is a `style.json` plus `expected.png`.
  - Defaults are 512×512, `allowed` 0.00025 (the share of pixels that may differ), and the pixelmatch `threshold` 0.1285.
  - The shared assets are about 66 MB.
  - The licence is BSD-3 style.
- **MapLibre Native runs the same fixtures** with per-platform manifests and `metrics/ignores/*.json`. Fixtures + an ignore list + a stated threshold is the model to copy.
- **Golden images** are generated headlessly by GL JS (`UPDATE=1 npm run test-render`, Puppeteer). New street-level cases can be authored the same way.
- **Image diff:** vendor a pixelmatch port (PixelMatch.net, under 300 lines), so our numbers mean the same as MapLibre's.

### Tiles without a key

| Source | Schema | Terms |
|---|---|---|
| OpenFreeMap | OpenMapTiles | no key; OSM/ODbL attribution |
| OSMF vector.openstreetmap.org (since 2025-07) | Shortbread 1.1 | usage policy may change |
| VersaTiles | Shortbread | free; glyphs and sprites served |
| Protomaps daily PMTiles | Protomaps | self-host via HTTP range requests |

## The staged path (proposal)

1. **Vector tiles rasterised per tile into the drape we have.**
   - What: MVT via NTS.IO.VectorTiles, drawn with SkiaSharp 2.88 from a style *subset* — or VexTile 1.0.1 pinned. This is experiment A's machinery with a real data source, and the whole engine (selection, fades, mips, stand-ins) is reused.
   - Check: MapLibre's fill, line and background fixtures on a flat tile, compared with pixelmatch against a stated `allowed` and our own ignore list.
2. **A style-spec expression evaluator in C#,** ported with its expression unit tests.
   - Check: pure xUnit against MapLibre's own JSON test cases.
3. **GPU geometry instead of textures.**
   - What: Cutear for fills, a `line_bucket` port for lines and a `subdivision` port for the globe, camera-relative in Stride.
   - Check: the 128 globe fixtures, rendered with our camera set from each fixture's centre, zoom, pitch and bearing. A GlobeCamera ↔ MapLibre transform equivalence test comes first.
4. **Text.**
   - What: glyph PBFs, an SDF shader, then a port of placement and collision across tiles. This is the largest stage.
   - Check: the `text-*` and `symbol-*` fixtures.
5. **In parallel, a spike on MapLibre Native FFI:** a DXGI shared handle opened on Stride's device. Re-evaluate once Native's globe merges and the FFI reaches 1.0.

**Write ourselves:** the expression evaluator, line extrusion, subdivision, symbol placement and the
test harness.

**Reuse:** NTS.IO.VectorTiles, Cutear/Earcut.NET, a vendored pixelmatch, and MapLibre's fixtures and glyph PBFs.

## Authoring a style in vvvv (survey, 2026-11-22)

The user's question: how to write a style "the way one writes CSS", as nodes, or with an editor
node that writes the JSON. Today `VectorTiles` takes a style URL or a `.json` path, nothing else.

**What a style is, against CSS.** A layer is a rule: `source-layer` + `filter` select, `paint` and
`layout` declare. There is no cascade and no inheritance: the layer order is the drawing order, and
a feature drawn twice (a road's casing and its line) is two layers. Whatever the authoring surface,
its unit is the layer.

**How others expose it**

| who | surface | notes |
|---|---|---|
| Maputnik (MapLibre, MIT) | visual editor in the browser, or self-hosted (Docker, npm) | the open editor of the spec; keeps work in local storage; 2025 (issue #964, FileSystemFileHandle) opens a local file and saves it in place; the old Go `maputnik --watch --file style.json` (desktop v1.0.7, 2020) serves it and writes edits to the file |
| Mapbox Studio | hosted, proprietary | Mapbox's own styles |
| MapTiler Customize | hosted | MapTiler's styles |
| MapLibre Native (Android, iOS) | typed layers: `FillLayer(id, source).withProperties(fillColor(...))`, expressions as builder calls | the one typed API of the spec - a model for nodes |
| VL.Mapsui (sibling) | its own `VectorStyle` nodes on Mapsui's style model | not the MapLibre spec |
| GeometryEditor (VL.NetTopologySuite) | a node plus an editor-extension window (Ctrl+Shift+W), Ctrl+S writes the pin's default into the .vl | the family's proof that an editor node works in vvvv 7.4 |

vvvv 7.4 ships VL.ImGui (Skia and Stride) but not VL.CEF; VL.CEF (Chromium, a NuGet of its own) has
WebBrowser / WebRenderer nodes and ExecuteJavaScript, runs out of process, and has a multi-GPU
"no output" trap.

**Options for an editor**

1. **Maputnik beside vvvv, the file watched.** `VectorTiles` reloads a local style file when it
   changes; Maputnik (browser, or self-hosted) opens that file and saves it in place. Our part is a
   file watch and a reload; the editor is the spec's own, complete. A style edited there may use
   what we do not draw yet - the Status pin already names skipped layers.
2. **Maputnik inside vvvv** (VL.CEF): one window, but a Chromium dependency of hundreds of MB, an
   extra package and its GPU traps, for what option 1 gives already.
3. **Our own editor window** (the GeometryEditor pattern, Skia or VL.ImGui): a layer list,
   visibility, the common paint properties, a colour picker, zoom stops; writes the JSON. Fits vvvv
   and previews on the globe itself, but Maputnik is years of work - it would be a small subset.
4. **Nodes** (no editor): load a style and override it (`SetPaint`, `SetLayout`, `Visibility` by
   layer id - what a patch needs to animate or switch at run time), or typed layer nodes after
   MapLibre Native. Both compile to the same `MapStyleDocument`, so the renderer and the MapLibre
   tests stay the spec.

**Recommended order:** 1 first (small, needed by every other option: a style that changes on disk
must reload), then 4's override nodes for what a patch drives at run time; 3 only if working in
Maputnik beside vvvv proves awkward in use. Unverified: which Maputnik release saves a local file
in place, and in which browsers (the File System Access API is Chromium-only).
