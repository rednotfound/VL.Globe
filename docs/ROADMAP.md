# VL.Globe — roadmap

Written 2026-11-09 at the end of the session that built the package. Revised 2026-11-21 after the
camera-relative session. **Re-planned 2026-11-21 around releases**, from the research in
[RESEARCH-0.1.md](RESEARCH-0.1.md) and two decisions by the user (below).

Read [ARCHITECTURE.md](ARCHITECTURE.md) for what exists and [../NOTES.md](../NOTES.md) for what
already failed. Items are in the order they should be done; each says how it is verified. Nothing is
published. Branch `feature/camera-relative` is on `github.com/rednotfound/VL.Globe`; the user merges,
pushes and releases.

**The user's standard (2026-11-09): street level.** "我们肯定还是要做到很细才行". Judge every item
at the lowest height, not the comfortable one. The verification host is **vvvv 7.4 (LTS)**.

## The releases, decided 2026-11-21

- **0.1 = VL.Mapsui's basics on the globe, on a smooth sphere.** The user: "0.1版本的最终目标至少是
  mapsui能够涵盖的基础功能". Its coordinate and picking nodes are designed **terrain-ready**:
  - heights mean above sea level;
  - a `Height Reference` (above sea level / on the ground / above the ground) is reserved in the
    surface;
  - 0.1 implements only sea level.
- **0.2 = terrain** (the user chose "0.1 先平地，地形进 0.2"). Stages are in section 0.2 below.
- **How lines and polygons are drawn is decided by an experiment**, not up front. The user: "先做实验再定，
  因为我再考虑如何支持raster tiles". Draping vectors into tile textures and compositing several raster
  layers are the same machinery, so one experiment (item 3) answers both.
- **Vectors are draped** (decided 2026-11-21 from experiment A, "问题不大"): drawn by SkiaSharp into each
  tile's overlay image, laid over the map in the tile material. **A MapLibre-like GPU vector renderer is
  the future** (the user: "那是未来，就算现在不做，将来也是要做的"); if it cannot share the raster engine it
  becomes **a separate node** ("能够集成在一起固然好…如果不行，我们是可以提供不同的节点，渲染raster的和vector的").
  The .NET survey and the staged path: [RESEARCH-VECTOR.md](RESEARCH-VECTOR.md). **Vector tiles as a
  draped source join 0.1**, terrain stays 0.2, and MapLibre's render tests become our yardstick first.
- **GeoLibre** (`rednotfound/GeoLibre`, a fork of opengeos/GeoLibre) is studied for its **feature
  set** and two small algorithms: the Terrarium provider and zoom ↔ distance. Its engines and app
  shell are not copied (RESEARCH-0.1 §3).

## Done 2026-11-20/21 (branch `feature/camera-relative`)

Each was found or confirmed on the user's screen. Details and numbers are in NOTES.md.

| what | why it was needed |
|---|---|
| **Camera-relative rendering** | float at radius 1 snapped everything to a 0.4 m lattice. It is now 0.008 mm at 20 m (`CameraRelativeFacts`) |
| Background sphere drawn by Globe (`Background`, drawn while off) | a patch Sphere sat on the camera once the camera was at the origin |
| **North-up drag and zoom** (Gauss-Newton on lon/lat) | rotating the rig drifted the heading 31° per dragged circle: "拖动一下就是会拖歪掉" |
| **`GlobeWindow`** + `HowTo Open a globe window` | Globe needs the window's real height; a 1080 default made text small |
| Selection by distance to the tile's surface, with the texel width taken at its equator-most row | the bounding-sphere distance refined a level early; one row was stretched to 2.55× |
| **Rounded levels (SSE 1.41), mipmaps in linear light** | MapLibre and Cesium both round; a 2× vs 1× seam read as unloaded |
| Post effects off, VSync on | the defaults darkened and blurred the map, and the view tore while dragging |
| **Fades both ways**, 0.3 s | MapLibre's raster fade. The first try dipped dark: Stride's Add is an alpha-weighted 3ds blend |
| Wheel at 0.25 level per notch, eased 0.15 s | "滚一个就拉近了很多" |
| **Zooming out, loaded children stand in; zoom ≤ 3 never evicted** | a fast zoom out showed the zoom-2 fallback, white and blurred (MapLibre `_retainLoadedChildren`) |

---

## 0.1 — Mapsui's basics on a smooth globe

### 1. PlaceOnGlobe, then the 20 m floor — done 2026-11-21, the user judged it ("似乎ok")

**Built as `GlobePositionComponent`**, not a transformation node: the Camera-taking node formed a
cycle in GlobeWindow (see ARCHITECTURE "Coordinates"). `HowTo Stand a model on the globe`: a 100 m
ball at Tokyo Station, seen at its computed size (67 px) in vvvv 7.4. Floor 20 m, Max Zoom 19.

The plan as written before building:


1. **`PlaceOnGlobe [Globe]`**. It gives the **transformation relative to the camera's frame** for a
   longitude, latitude and height, oriented east-north-up so that a model stands on the surface.
   - Inputs: `Camera` or `View`, `Longitude`, `Latitude`, `Height`, and the reserved
     `Height Reference`. Output: `Transformation`.
   - Before fixing the name and pins, measure how VL.Stride patches usually pass a transformation
     (family rule: follow the usual pattern).
   - `HowTo Place a point on the globe` moves to it, and GlobeWindow's `Input` becomes useful.
   - Until this exists, anything a patch places is drawn relative to an eye at the origin, which is
     wrong.
2. **Lower the floor.**
   - Default `Min Height` from 100 m to ~20 m, `Max Zoom` 19 (OSM's maximum).
   - Look at the text at that height with the user.

**Verify:** a test that a placed point lands within 1 mm at 20 m. In vvvv, a model on Tokyo stays put
while dragging at Min Height.

**Open:** depth precision once tilt exists (after 0.1).

### 1b. Tilt and turn — done 2026-11-21, the user judged it ("问题不大")

The user, having stood a model on the globe: "因为是3d的场景了…还是想要调整camera的俯仰角度从侧面看你放在地图上
的3d模型". Gesture chosen by the user: **right-drag** (MapLibre): sideways turns, up/down tilts, about
the view's centre, `Max Tilt` 60°. The view state became centred (ARCHITECTURE "The camera"); at tilt 0
nothing changed (all 105 earlier tests, and the ball's 66–67 px). Tiles now filter anisotropically.
The turn direction was flipped after the user found it backwards (drag right now looks right, as
MapLibre, Mapbox, deck.gl and Cesium do), and stand-ins became squares at the surface after a sunk
ancestor showed as dropping tiles and black cracks under tilt (NOTES 2026-11-21).

**Loading under tilt, checked 2026-11-21** ("请确保加载不出现问题"): `TileCoverageFacts` now samples
eight tilted poses (20 m to 1,000 km, 30–60°). No hole anywhere; at most 179 tiles, far under the 400
cap; the selector's magnification ≤ 1.6 (the 0.1 over the straight-down bound is the centre-of-view
pixel density it uses, 1/cos² more toward the corners; MapLibre and Cesium ignore it too), except at
zoom 19 where OSM has nothing finer. Fixed on the way: Globe's frustum took a 16:9 aspect (a wider
window lost its side tiles), requests now go nearest first, and stand-ins sink by their distance (a
sink by the camera's height z-fought at the horizon). **Still open:** if the 400 cap is ever reached,
the depth-first walk stops refining wherever it happens to be (make it priority-ordered then); depth
precision toward the horizon has not been measured on screen; a Compass widget.

### 2. Coordinates and reading the view — done 2026-11-21, the user judged it ("看起来不错")

`ScreenToGlobe` (Success), `GlobeToScreen` (Visible), `GlobeViewInfo` (centre, height, heading, tilt,
ground resolution, web-map zoom by GeoLibre's ground-resolution formula), static nodes in Mapsui's
shape taking the View and a projection-space position. GlobeCamera gained `Screen Height` (GlobeWindow
wires the window's height into it too) and GlobeWindow a `Cursor` output, the mouse in projection
space, since it hides its Mouse. Help patch `HowTo Read the place under the cursor`. 11 tests.

The plan as written:


- **`ScreenToGlobe`**: the cursor in projection space → longitude/latitude, plus `Success`, because
  the cursor can miss the globe.
- **`GlobeToScreen`**: longitude/latitude/height → projection space, plus `Visible`, because the
  point can be on the far side.
- **`GlobeViewInfo`**: centre longitude/latitude, height, zoom level, heading.
  - The zoom ↔ height mapping is by ground resolution (GeoLibre `cesium-camera.ts`), so the zoom
    matches VL.Mapsui's and a 2D map can be synced to the globe.
- All of these are absolute maths and camera-relative-safe. Heights are above sea level, as decided.

### 3. Experiment: how vectors and extra raster layers reach the globe — decided 2026-11-21: draped

Prototype (a) was built and judged by the user ("问题不大"): SkiaSharp draws NTS geometry into a
transparent overlay per tile (`Engine/TileOverlay.cs`), laid over the imagery with `BinaryOperator.Over`
— in the tile, its fades and the stand-ins that borrow it, so lines do not blink while tiles change.
Overlay mips weight colour by alpha (no dark halo). Prototype (b), geometry, was not built: several
times A's cost for a picture the user did not ask for; the GPU path belongs to the future vector node.
An extra raster layer (opacity) goes the same way as a second texture under the overlay — item 6.

The plan as written:


The user decides this from what they see. Build the two smallest prototypes:

- **(a) Draped.**
  - Each visible tile gets a second texture, rendered with VL.Skia from NTS geometry in that tile's
    Web Mercator square.
  - It is composited over the imagery in the tile material, with the same machinery as the fades.
  - A **second raster layer with an opacity** (for example a transparent XYZ overlay) uses the same
    path.
  - Measure:
    - line crispness at street level;
    - labels cut at tile edges;
    - re-render cost when the data changes;
    - GPU memory.
- **(b) Geometry.**
  - A line and a polygon densified **by metres** (the trap: densifying by degrees facets at 20 m).
  - They are placed camera-relative (needs item 1), with a screen-pixel line width.
  - Measure:
    - crispness;
    - z-fighting with the tiles;
    - how it would have to follow terrain in 0.2.

Show both at 20 m and at country scale, side by side, and record the decision in this file. Points
and labels are screen-facing 3D in both prototypes.

### 4. FeatureLayer, styles, Graticule — partly done 2026-11-21

**Built and seen in vvvv:** `Geometry`, `FeatureLayer` [Globe.Layers] and `VectorStyle` [Globe.Styles]
in VL.Mapsui's shape (Features/Geometries + Style + Enabled + Name → a layer), into Globe's and
GlobeWindow's **`Layers`**, drawn in order. Help patch `HowTo Draw shapes on the globe`. Changes are
found by content (FromWKT and Cons rebuild objects every frame). **Still to do:** `Pick`, `Graticule`.

The plan as written:


- **`FeatureLayer [Globe]`**: NTS points, lines and polygons, drawn the way item 3 decided.
- **`VectorStyle`, `SymbolStyle`, `LabelStyle`**: the same shape as VL.Mapsui's. Whether they *are*
  Mapsui's is a family question: VL.Globe must not depend on VL.Mapsui.
- **`Graticule`**: it exercises the line path from street to globe.
- **`Pick`**: cursor → longitude/latitude → the features within a tolerance in metres. Same outputs
  as Mapsui's Pick.

**Verify:** a GeoJSON of Tokyo's wards stays on OSM's boundaries from 20 m to the whole globe.

### 4b. A test harness on MapLibre's render tests — done 2026-11-21

The yardstick for every vector stage (RESEARCH-VECTOR "Testing"): MapLibre GL JS's
`test/integration/render` (1,796 fixtures, 128 for the globe; `style.json` + `expected.png`,
pixelmatch `threshold` 0.1285, `allowed` 0.00025), run as MapLibre Native runs them — the fixtures, a
stated threshold, and an ignore list of known failures.

**Built:**
- `tools\Fetch-MapLibreFixtures.ps1` fetches 11 groups at **v6.19.0** (background-color/-opacity,
  fill-antialias/-color/-opacity/-outline-color, line-cap/-color/-join/-opacity/-width; 81 fixtures)
  into the git-ignored `test\fixtures\maplibre`.
- `test\VL.Globe.Tests\MapLibre\Pixelmatch.cs`: pixelmatch **8.0.0** (the version v6.19.0 locks)
  ported line by line, checked against pixelmatch's own tests.
- `MapLibreRenderFacts`: each fixture is passed, failed or unsupported (with the reason). Every
  supported fixture passes or is in `render-tests.json`'s ignores; an ignored one that passes must
  leave; `passedAtLeast` may only go up, and does not count passes an empty image would also make.
  Results and `report.md` in `test\fixtures\maplibre\results`.
- The renderer is new engine code, not FeatureLayer's: `Engine\MapStyle.cs` (a style-spec subset:
  CSS colours, legacy functions, the common expressions), `MapStylePainter.cs` (background, fill,
  line; inline GeoJSON; a viewport that is a test's canvas or a tile's square) and
  `LineTessellator.cs` (GL JS's `line_bucket.ts` joins and its shader's antialiasing).

**Result:** 48 of 48 supported fixtures pass (47 counted); 33 unsupported — 15 need a vector source
(4c), the rest raster sources, patterns, dasharray, `lineMetrics`, filters, non-RGB colour spaces,
a symbol layer, terrain, operations. FeatureLayer still drapes with `TileOverlay`'s Skia stroke;
moving it onto the painter is part of 4c.

### 4c. `VectorTiles` — a draped vector-tile source — built, judged "不太行"

MVT via NetTopologySuite.IO.VectorTiles, drawn per tile into the overlay from a MapLibre Style JSON
*subset* (fill, line, background; filters; zoom stops) — stage 1 of RESEARCH-VECTOR's path, the same
machinery as FeatureLayer. Keyless sources: OpenFreeMap (OpenMapTiles), VersaTiles / OSMF (Shortbread).
Verified by 4b's fixtures and, at street level, by the user. Labels drawn this way collide only within
a tile and are cut at its edge — the reason for the later vector node.

**Step 1 done 2026-11-21 — the painter reads vector tiles:**
- `Engine\VectorTile.cs`, an MVT decoder written by hand instead of NTS.IO.VectorTiles: that one
  needs protobuf-net (not shipped by vvvv; another package could load another version) and converts
  to lon/lat, which the painter would convert back. Geometry is kept in Web Mercator world
  coordinates (0..1) for GeoJSON and tiles alike.
- Vector sources (`tiles`, min/maxzoom, `tileSize`, `scheme: tms`), drawn tile by tile with a hard
  clip to the tile's square, overscaled past maxzoom; GeoJSON by URL; filters in both syntaxes
  (MapLibre's `isExpressionFilter` decides), `in`, `id`.
- The harness: 25 groups, 164 fixtures, 17 assets; **98 pass (90 counted), 1 ignored (earcut),
  65 unsupported** (circle and symbol layers, operations, bearing, raster, clustering, patterns,
  dasharray, colour spaces).

**Step 2 built 2026-11-21, not yet seen in vvvv — the node `VectorTiles [Globe.Layers]`** (the user
chose: a layer into Layers, the style by URL):
- Style (URL or file, default OpenFreeMap Liberty), Enabled (off), Name → Output, Attribution, Status.
- `Engine\VectorTileService.cs`: the style, each source's TileJSON, tiles on demand (6 in flight),
  disk cache `%LOCALAPPDATA%\VL.Globe\vector-tiles`, 7 days.
- A **lenient** parse: unsupported sources and layers are left out, unsupported properties ignored,
  except those that would make a layer something else (`fill-pattern`, `line-gap-width`, offsets,
  translates, gradients: the layer is left out); a layer that throws while drawing is dropped. All
  counted in Status. Liberty: 84 of 111 layers drawable; 17 of them dashed, drawn solid.
- Overlays now render on **worker threads** (`OverlayRenderer`: FeatureLayer/Geometry and styles into
  one image, in order) and upload 6 a frame; one drawn while a vector tile was missing is redrawn
  when tiles have arrived. FeatureLayer still draws with TileOverlay.
- Help patch `HowTo Draw vector tiles` (Tokyo Station, 1500 m); rungs 1-3 pass.

**Rung 4, 2026-11-21 (a copy of the help patch with Enabled on, no input driven):** Tokyo Station and
Osaka Station at 1500 m drawn in Liberty's colours - moats, parks, roads by class, rail - with no
seam at tile edges and nothing in the log. Status: 81 layers drawn, 49 parts skipped. The first
run fetched **118 tiles, 31 MB** for one view: the ancestors shown while the raster loads asked for
low-zoom vector tiles (up to 1.3 MB). Now only the wanted tiles fetch; a stand-in draws what is
already here (`Peek`). Cold cache at Osaka afterwards: **4 tiles, 2.8 MB**, all z14.

**The user's first look (2026-11-21), two bugs, fixed the same day:**
1. *"不连 OpenStreetMap 就看不到 vector tiles"*: tiles were built only when their raster arrived. A
   globe with layers and no Tile Source now builds plain tiles in the background colour at once;
   with a source, the overlay still waits for the tile it lies on.
2. *"vector tiles 总在加载，似乎没有保持"*: every arrival redrew every incomplete overlay, stand-ins
   that would never get their tile included, on 4 threads; and each overlay took 631 ms in vvvv
   because it processed the whole vector tile (16 globe tiles' worth), and 3 threads decoded the
   same tile. Now: an overlay is redrawn only when a tile it was drawn without has come; wanted
   tiles first, `ProcessorCount - 2` threads; features off the canvas are skipped; one decode per
   tile; round caps sliced by radius; line layers drawn in one call. Liberty around Kyoto Station:
   172 → 60 ms an overlay single-threaded; 229 ms in vvvv with the threads contending. Status
   reports it.

**The user's judgement (2026-11-21): "我感觉不太行"** — a texture caches pixels, so every new view
costs CPU drawing, and a line's width is baked at one zoom. Asked why MapLibre does it well: it
caches *geometry* and draws pixels on the GPU every frame. Decided the same evening: **move to
RESEARCH-VECTOR stage 3, the GPU** ("同意，开始做吧，stride也是有强大的shader系统的").

### 4d. `VectorTiles` on the GPU — built 2026-11-21, the user judged it ("我觉得还行")

Design: [DESIGN-GPU-VECTOR.md](DESIGN-GPU-VECTOR.md). Built the same night:
- `Engine/VectorBucket.cs`: a vector tile's fills (NTS `PolygonTriangulator`) and lines (the same
  `LineTessellator`, now emitting the unextruded vertex) as vertices on the unit sphere relative to
  the tile's centre; one range per layer; paint that depends on the feature in the vertices.
- `shaders/GlobeVector.sdsl`: lines extruded in pixels in the vertex shader, GL JS's edge ramp in
  the pixel shader, clipping to the tile's square by discard, lifted towards the eye over the tiles.
- `Engine/VectorRenderer.cs` (an `IGraphicsRendererBase` in VL.Stride's `EntityRendererComponent`,
  transparent stage) and `Engine/GpuVectorTiles.cs` (which tiles, built on workers, uploaded 4 a
  frame, stand-ins by the nearest built ancestor).
- `VectorTiles` gained `Rendering`: GPU (default) or Draped.
- 9 xUnit facts (GpuVectorFacts), 170 green. The shader has never compiled yet: **rung 4 is next**
  (the user's vvvv was open on the help patch, so dist was not rebuilt).

**Rung 4 (22:37, Osaka Station, with OpenStreetMap, no input driven):** GlobeVector compiled on
the first run (one warning: `pow` of a negative); 4 vector tiles, 229 draws; roads, rail, water,
parks and buildings where they belong, colours right. A bucket takes ~1 s to build on a worker
(NTS triangulation, not yet measured by stage).

**The user's judgement while moving (2026-11-21 night): "大方向完全没有问题", "我已经很满意了".** Fixed
the same night: dark gaps while loading, a chequerboard and then discs of background from afar,
dotted cracks at tile borders, the globe shaking with the pole dragged into view (NOTES).

**Next, in this order:**
1. **`fill-extrusion` drawn flat** (its colour and opacity on the ground): Liberty's `building` fill
   stops at z14 and `building-3d` takes over, so buildings vanish past z14 (the user asked why).
2. **"有的时候还是会稍微有点卡"** — measured and largely done the same night (DESIGN-GPU-VECTOR.md,
   "As built"): MapLibre's worker count below normal priority, 8 MB uploads a frame, pooled arrays,
   outlines as a line list; frames stay under 27 ms while 170 MB a second load. The 200-330 ms
   Updates were the draw list, a new large-object-heap list every frame (fixed, two frames reused).
   **Left:** frames of 60–140 ms outside the node after heavy uploads (a smaller vertex should help).
3. **3D buildings** (`fill-extrusion` for real: walls, roofs, light, depth written) — built the
   same night (DESIGN-GPU-VECTOR.md), judged by the user tilting ("确实好很多了"); then a 28-byte
   vertex (-25% GPU memory). Then MapLibre's render tests on the GPU path, offscreen in `dotnet
   test`: 97 pass (89 non-vacuous; the drape 98/90).

### 4e. Labels — milestones 1 (point text) and 2 (collision) done 2026-11-22

Ported from GL JS against its fixtures and shaping expectations (DESIGN-GPU-VECTOR.md, "Labels"):
154 fixtures pass on the GPU path (146 non-vacuous). Seen in vvvv on Liberty, not yet judged by the
user. **Milestone 3, labels along lines, done the same day: 158 fixtures (150 non-vacuous).**
**Milestone 4, icons, steps 1-6 done the same day: 311 fixtures (302 non-vacuous)** - sprites,
point icons, icon-text-fit and stretch zones, circle layers, shields and icons along lines.
**Next: symbols past the source's maxzoom (DESIGN-GPU-VECTOR.md, "Next" 1), then fades.**

### 4f. The distance in a tilted view — asked by the user 2026-11-22, to do after 4e

"斜视的时候，远处的建筑就很少加载了…缺的会比较明显". Why: far tiles are chosen coarser (screen-space
error), the vector tile one level below the globe tile, and OpenMapTiles has buildings only from z13
(Liberty's building-3d from z14) - so wherever the far vector tiles fall to z12 or coarser there is
no building data at all. Limiting the tilt alone does not cure it: the gap shows at our default
`MaxTilt` 60. Surveyed (2026-11-22): Google's vector maps cap the tilt by zoom (30° at z10, 45° at
z14, 60° at z15, 67.5° from z15.5); MapLibre / Mapbox allow 85° and load fewer, coarser tiles far
away, Mapbox hiding the distance in fog; Cesium's fog coarsens and culls horizon tiles (35% fewer).
**Agreed order, cheapest first:**
1. The tilt capped by zoom, as Google's (in `GlobeCamera`; `MaxTilt` stays the ceiling).
2. Fog / a fade towards the horizon, as Mapbox and Cesium - softens the edge and loads fewer tiles.
3. A z13 floor for vector tiles within some distance while the camera is at street level - fills
   the buildings in; measure GPU memory and frame time tilted in vvvv before choosing the distance.

### 5. Navigation

- `CenterOn`, `ZoomToLevel`, `ZoomIn`, `ZoomOut`.
- `FlyTo`: a great-circle path with a height arc, the globe's DragBetween.
- `ZoomToBounds`: fit an NTS envelope or geometry, using the bounding cap and angular radius θ;
  eye distance ≈ R·cos θ + R·sin θ / tan(fov/2).
- The drag that leaves the globe: today the camera stops when the cursor is off the sphere.
  - Cesium switches to a trackball above 7,500 km.
  - MapLibre 6 bends the ray past the horizon.
  - Choose one; the ray must not reach the Newton solve when it misses.

### 6. Widgets and imagery layers

- `ScaleBar`: TerriaJS's method — the ground distance across one pixel at a probe point, then the
  largest round length under 100 px. Hidden when the probe misses the globe.
- `ZoomButtons`, `Click`, the attribution shown in the window.
- **Several imagery layers with `Opacity`**, if item 3's draped path did not already deliver them.

### 7. Gestures and tidying (before the release)

- **Ask the user about the vertical drag direction.** `Position in Projection` is assumed y-up and
  nobody has asked. Then move the Drag and ZoomByWheel logs from Information to Debug.
- **Teach `Compile-HelpPatches.ps1` to decompile `VL.Globe.vl.dll`** and run the dropped-link check
  on patched definitions. The first GlobeWindow's height link was silently dropped.
- `Test-VLPatch.ps1` and `HelpPatchGen.ps1` still carry VL.Mapsui's node lists: run
  `Test-VLPackage.ps1` and fix what it reports.
- Help patches:
  - XyzTiles has one;
  - every node has an F1 HelpFocus;
  - restyle to the family's rules (memory: help patch cells, community style, layout).
- `README.md` in the vvvv library shape, and fresh `docs\` screenshots.
- `Globe`'s own frustum still assumes 16:9 unless the camera sets an aspect. Read the projection
  Stride used.

**0.1 ships** once the node surface has gone a week without moving (family rule).

---

## 0.2 — terrain (RESEARCH-0.1 §4)

Start with **AWS Terrain Tiles, Terrarium PNG**:
- no key, no account;
- Web Mercator z0–15, like our imagery;
- bare earth;
- already decoded in vl-overworld.

Credit USGS SRTM/3DEP/GMTED and the other sources. Cesium World Terrain and Mapbox Terrain-DEM are
ruled out by their terms.

1. **Heights on the CPU mesh.**
   - A `TerrainTiles` source, `Enabled` off by default.
   - DEM level = `min(L − 2, 15)`, with bilinear over-zoom beyond that (MapLibre's coupling; our
     64-segment tile at L+2 matches a 256-px DEM at L).
   - Heights sampled by longitude/latitude across tile borders, so neighbours compute identical
     edges (GeoLibre's `cesium-terrarium.ts`).
   - Radial displacement in double before the centre is subtracted.
   - **Skirts**.
   - Bounds grown by each tile's min/max height.
   - A tile is rebuilt, with a crossfade, when its DEM arrives.
2. **The ground governs the camera and picking.**
   - The floor is the ground plus Min Height, smoothed as Cesium does (≤ 10 % per frame unless the
     user moved).
   - `ScreenToGlobe` and `Pick` march against the heights.
   - `PlaceOnGlobe` and `FeatureLayer` gain the `On the ground` Height Reference.
   - Terrain is attached before the camera is seeded (GeoLibre's ordering lesson).
3. **Light.**
   - Normals from the DEM.
   - Hillshade.
   - An `Exaggeration` that scales the clamps.
4. **Later.**
   - GPU displacement.
   - Keyed sources (MapTiler, Terrain-RGB).
   - A Copernicus GLO-30 pyramid, labelled as a DSM.
   - An elevation profile and terrain-surface measurement (GeoLibre's plugins).

**Verify:** street level in the Alps and on a coastline, judged against reality by the user. No
crack visible at a level boundary. The camera never enters a hillside while dragging uphill.

## After — each waiting for a consumer

- **`VectorGlobe`: a MapLibre-like GPU vector renderer**, a separate node if it cannot share Globe —
  RESEARCH-VECTOR stages 2–4: a style-spec expression evaluator ported with its tests, GPU fills
  (Cutear/Earcut.NET), line extrusion and globe subdivision ported from GL JS, then SDF text with
  cross-tile placement — the largest stage. In parallel, a spike on MapLibre Native's FFI (D3D11
  shared texture) once its globe merges and the FFI reaches 1.0.

- A Compass (tilt and turn exist since 0.1 item 1b).
- Great-circle arcs.
- Extrusion.
- GPU picking.
- Atmosphere and sun.
- Polar caps in the Background colour.
- Measurement.
- `StyleByValue`, `StyleByGeometry`, `VisibleRange` by height.
- Inertia.
- Vector tiles.
- PMTiles.
- 3D Tiles via cesium-native on .NET 10 is a different package.
- The course (vl-overworld's `Experiment globe view.vl`) is rebuilt on GlobeWindow when a chapter
  uses the globe.
