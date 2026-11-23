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
transparent overlay per tile (`Engine/Draped/TileOverlay.cs`), laid over the imagery with `BinaryOperator.Over`
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
found by content (FromWKT and Cons rebuild objects every frame). **`Pick` and `Graticule` built 2026-11-23**
(Engine/Camera/Picking.cs; help patches `HowTo Pick a feature`, `HowTo Draw a graticule`; seen drawing in vvvv, the
pick's hover still to be tried by the user). Shapes after VectorTiles are drawn by the GPU (GeometryStyle).

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
- The renderer is new engine code, not FeatureLayer's: `Engine\Styling\MapStyle.cs` (a style-spec subset:
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
- `Engine\VectorTiles\VectorTile.cs`, an MVT decoder written by hand instead of NTS.IO.VectorTiles: that one
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
- `Engine\VectorTiles\VectorTileService.cs`: the style, each source's TileJSON, tiles on demand (6 in flight),
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
- `Engine/Gpu/VectorBucket.cs`: a vector tile's fills (NTS `PolygonTriangulator`) and lines (the same
  `LineTessellator`, now emitting the unextruded vertex) as vertices on the unit sphere relative to
  the tile's centre; one range per layer; paint that depends on the feature in the vertices.
- `shaders/GlobeVector.sdsl`: lines extruded in pixels in the vertex shader, GL JS's edge ramp in
  the pixel shader, clipping to the tile's square by discard, lifted towards the eye over the tiles.
- `Engine/Gpu/VectorRenderer.cs` (an `IGraphicsRendererBase` in VL.Stride's `EntityRendererComponent`,
  transparent stage) and `Engine/Gpu/GpuVectorTiles.cs` (which tiles, built on workers, uploaded 4 a
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
   Not done after all: the user, 2026-11-22 night, "远远不如 GL JS 流畅" - see **4g**.
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
Symbols past the source's maxzoom laid out at the overscaled zoom, the same day. **Next: fades.**

**Authoring a style — decided 2026-11-22 (the user):** no editor and no style nodes of our own.
Maputnik, MapLibre's own editor, already writes the style JSON ("社区已经做到的事情我们可能就不用做了");
it is named in the README and the help patch. The style is a value of its own, as a texture is
FileTexture's (the user: "vector tiles是一个节点，style json这种加载设置是一个节点"): `MapLibreStyle (URL)`
and `MapLibreStyle (File) [Globe.Styles]` make a `MapLibreStyle`, `VectorTiles` takes it. The file
node reads again on `Read` (vvvv's FileReader and our ReadOSM name it so), not on a file watch (only
Stride's project loader has an `Auto Reload`). A first try put a `Reload` pin on VectorTiles itself -
an application's shortcut, not a library's shape. Nodes that change a style (SetPaint, Visibility)
would take one and give one. Survey: RESEARCH-VECTOR.md, "Authoring a style in vvvv".

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

**Steps 1 and 2 built 2026-11-22, not yet seen by the user moving:** `GlobeView.TiltLimit` (Google's
numbers, `MaxTilt` the ceiling, zooming out lowers the tilt); `GlobeFog.sdsl` + `Engine/Gpu/FogPass.cs`,
one full-screen pass after everything the globe draws, the ray's distance to the sphere solved per
pixel (no depth), from 3 to 16 eye heights, the sky within ~3 degrees of the limb fogged too
(`FogFacts` render it offscreen). Step 3 waits for the user's look: with the fog whole at 16 eye
heights (2.4 km tilted 60 degrees from 300 m), the vector tiles inside it are z14 already.

**Step 3 built the same evening, from the user's look** ("有雾没雾完全没有关系啊，tiles被截得很厉害",
`#13.82/35.69555/139.75982/-9.9/57.3`, Tokyo, 2.3 km up): the 3D buildings ended in a straight line
6 km off, where the vector tiles fell from z14 to z13 - and z13's buildings are merged, without
heights. Not a floor at z13 but at the camera's own zoom: inside the fog's reach no vector tile is
coarser (`GpuVectorTiles.VectorKeys`, parts of a coarse globe tile only where they are themselves in
reach); the fog now 2 to 5 eye heights, so it is whole where the floor ends. Rendered offscreen from
the user's view (`ViewProbe`): no line, buildings into the haze. Cost there: 39 z14 tiles with
buildings, 853 MB on the GPU (658 before) - over the 768 MB budget; the compact extrusion vertex
(C) is what brings it down.

### 4g. As smooth as MapLibre GL JS — asked by the user 2026-11-22 night, before the rest of 4e

The user: "我们的系统跟 MapLibre GL JS 相比，还是远远不如人家那么流畅，还是卡卡的" - and asked where
the difference is. An acceptance criterion of 0.1, not a polish item. 4d's "有点卡" entry read
"largely done"; it was not.

**Why it was not seen: the instrument.** `Globe perf` logs the slowest frame and the frames over
33 ms. At 60 Hz a frame over 16.7 ms misses the vsync and judders. The user's log of 22:51 at rest,
nothing uploaded: slowest frame 23 ms, "0 over 33 ms", submit 5.5 ms mean, 2876 draws. "60 fps
while no tiles arrive" (4e) was read off that instrument and does not hold.

**Where we differ from GL JS** (measured here; GL JS's numbers from its source, to be checked):
1. *Bytes per tile.* Our vertex is 28 bytes (three floats on the unit sphere), indices 32-bit,
   roads built twice (casing and line). GL JS keeps Int16 tile coordinates - a fill 4 bytes, a line
   8, an extrusion 12 - with 16-bit indices, and projects onto the globe in the shader. A z14 tile
   at Kyoto is 6.7 MB here; uploads run 130-150 MB/s while loading, single tiles above the 8 MB a
   frame budget (13-18 MB in one frame).
2. *Tiles in a tilted view.* GL JS chooses coarser tiles with distance; 4f step 3 keeps the camera's
   zoom to 5 eye heights for the 3D buildings (853 MB tilted over Tokyo).
3. *Draw submission.* 2300-3100 draws, submit 5.5-10 ms mean - a third to a half of the frame.
   GL JS draws as many (per layer per tile), each cheap. Suspected, not measured: our per-draw
   parameter setting through Stride's EffectInstance.
4. *Feel, not verified:* GL JS's drag inertia and easing; ours eases only the wheel.

**Order:**
1. The instrument: mean frame time, frames over 16.7 ms, percentiles, per second in `Globe perf`;
   the same view (a view hash) measured in GL JS in Chrome - frame time, tiles, GPU memory - as the
   yardstick.
2. Lighter tiles, GL JS's layout: Int16 tile coordinates projected in the shader, 16-bit indices,
   roads built once.
3. Coarser tiles far away, as GL JS; 4f's far buildings weighed again against the cost.
4. Draw submission timed by part, then batched or its parameters cut, whichever the timing names.
5. Drag inertia, if the feel is still behind once the frames are.

**Step 1, half built 2026-11-22 night:** `Engine/Diagnostics/FrameStats.cs` (`FrameStatsFacts`): `Globe perf` now
reads `N frames, fps, mean p50 p95 p99 max ms, M over 20 ms, moving|still`, and every second the
camera moved is logged, smooth or not. `tools/GLJS-Yardstick.html`: GL JS 6.19.0 on the globe with
Liberty, the same summary each second as `GLJS perf:` (on the page and in the console), a view
hash as ours, "Pan 10 s" at 300 px/s.

**First numbers, the same night** (`tools/Summarize-Perf.py`, seconds the camera moved):
- *VL.Globe, the user moving by hand at Tokyo* (23:32-23:34, `HowTo Draw vector tiles`, 1250 x 800):
  68 s, 52.9 fps, **17.4% of frames over 20 ms**, p95 median 23.9 ms, uploads 52 MB/s mean, submit
  5.8 ms mean, 2330 draws.
  OpenStreetMap's raster tiles were still under it; the user took them out of the help patch after.
- *The bench, the same motion in both* - the user's view `#14.82/35.68778/139.76758/-78.6/54.5`,
  1250 x 800, Liberty alone, 25 s to load, then two pans of 10 s at 300 px/s (each 6.58-6.59 km
  north on both; the same streets at the end, screenshots compared):

  | | frames over 20 ms | worst frame | p95 (median s) |
  |---|---|---|---|
  | **GL JS 6.19.0** (Chrome 157, device pixel ratio 1.26) | **0 of 1326 (0.0%)** | 17 ms | 16.8 ms |
  | **VL.Globe**, run 1 (23:48) | 49 of 1292 (3.8%) | 43 ms | 17.5 ms |
  | **VL.Globe**, run 2 (23:52) | 45 of 1286 (3.5%) | 287 ms | 17.0 ms |

- **Where ours go:** a second with no tile uploaded misses 0-2 frames; a second uploading 30-70 MB
  misses 3-9, its submit max 13-25 ms (7 without) - the frames after a tile arrives. One Update of
  271 ms was the upload alone ("finish+upload 269"): one tile's buffers made in a frame. GL JS loads
  tiles along the same path and misses nothing: loading never costs it a frame. Steps 2-4 stand,
  step 2 (bytes) first.

**A false proof on the way** (NOTES.md): GL JS's first run read 60 fps while panning - the map had
not moved. `panBy(..., {animate: false})` each frame left it in place for 20 s; seen only by setting
its last screenshot beside vvvv's, 13 km apart. Both benches now log the centre at each pan's edges
and how far it went.

Run with `tools/Yardstick-Server.py <log>` (serves tools/, takes the page's POSTs - no browser
extension) and a separate Chrome profile: `--user-data-dir=<temp> --app=http://127.0.0.1:8765/GLJS-Yardstick.html?bench=1&w=1250&h=800#<hash>`.
The same motion in vvvv: `Engine/Diagnostics/CameraBench.cs`, started by `VLGLOBE_BENCH=pan VLGLOBE_VIEW=#...`
on a copy of the help patch with Enabled on; `CameraBenchFacts` checks it pans as GL JS's does.
Sum either log up with `tools/Summarize-Perf.py <log> --from <pan 1 begins> --to <pan 2 ends> --moving`.

**Step 2 built 2026-11-23 - the bytes halved, the misses not** (the same bench):
- A vertex holds its place in the tile, 16 bytes (was 28); GlobeVector.sdsl projects it
  (`Engine/Tiles/TileProjection.cs`: only small quantities subtracted, small angles by polynomial - the
  GPU's sin is exact to ~1e-6 absolute, metres at z14; 0.22 mm at z14, `TileProjectionFacts`).
- A line layer the same as an earlier one in every byte draws its span (casings and roads).
- 16-bit indices in segments of 65536 vertices with a base vertex (GL JS's SegmentVector).
- Kyoto z13 tile 12.2 -> 4.9 MB; the Tokyo view's 102 tiles 479 -> 240 MB; uploads while panning
  22 -> 10 MB/s. MapLibre's fixtures unchanged (329 / 320); the view drawn offscreen unchanged.
- **Frames over 20 ms: old 3.8, 3.5, 4.2% (three runs); new 6.7, 5.2%.** Submit 6.0-6.2 -> 7.3-7.8 ms
  mean: two more parameters a draw, 4.5% more draws (segments). The GPU (RTX 3060 Ti) is not it:
  the projection is ~0.15 ms of arithmetic a frame.
- Found by that: every parameter set made a string (`"GlobeVector." + name`) and looked it up -
  ~40,000 a frame. Keys now found once per name (by the literal's reference): **4.5%**, submit 7.0 ms.
- So the misses at tile arrival are not the bytes. What a frame that misses spends its time on is
  not known yet: the perf line sums a second. **Next: step 4 first** - every frame over 20 ms logged
  with its parts (Update, place, upload, submit, and outside the node), then whatever that names.

**Step 4, the root cause found 2026-11-23: garbage from building tiles, collected on every thread.**
- `Globe frame:` lines (`Engine/Diagnostics/FrameAnatomy.cs`) take each missed frame apart. In one bench, 40
  missed frames overran by 354 ms in all and were paused by gen0 collections for 397 ms; in 26 of 40
  the pause alone was more than the overrun. 111 collections in those frames, 3.6 ms each.
- The perf line now says who allocates: 90-955 MB a second while panning, ~50 of it on vvvv's main
  thread (Update, Draw, the patch), the rest on the workers building tiles. A gen0 every ~8.5 MB.
- A 64-MB gen0 (`DOTNET_GCgen0size`, vvvv started with it) made 1/8 of the collections, each 15-30
  ms: the pause follows what survives, not the count. Misses unchanged. Not a setting to turn.
- `AllocationProbe` (by hand), one Tokyo z14 tile: decode 17 MB, shapes 54-80 MB, symbols 122-137 MB
  allocated for a few MB of result; a z13 tile's shapes 206 MB. One layer, `poi_r20`, 65-76 MB.
  By type: strings, string arrays, JSON enumerators, closures, `JsonElement[]` - the style
  expressions, interpreted from their JSON on every feature (`e[0].GetString()` allocates each call,
  LINQ in filters, numbers boxed) - and `PositionedGlyph` objects from shaping; for lines, a new
  `(ExtrudedVertex, bool)[]` per line.
- **Why GL JS does not stutter:** its workers have heaps of their own - their garbage never stops
  the page's thread - and its expressions are compiled once per layer (style-spec `createExpression`),
  evaluated per feature without allocating. Here every worker's garbage stops vvvv's main thread.
- **Fix order** (each measured with the bench and `AllocationProbe`): (a) the expressions compiled
  once per layer to an evaluator that does not allocate per feature; (b) shaping's glyphs as structs,
  the line tessellator's arrays reused; (c) the main thread's ~50 MB a second; then whatever the
  probe names next. (Out of process tile building, GL JS's own design, only if (a)-(c) fall short.)

**Step 4 so far (2026-11-23), each on the same bench; MapLibre's reports line for line unchanged:**
- (a) Expressions compiled once (`StyleExpressionCompiler.cs`): a Tokyo z14 tile's shapes allocate
  54 -> 6 MB, a z13's 206 -> 22 MB. Misses unchanged (3.1-4.7%): fewer collections, each longer.
- The perf line now logs what the ephemeral collections promoted: a pause is ~1-1.5 ms a collection
  plus ~1.5 ms a promoted MB. ~14 MB promoted a second - one decoded tile (8.6 MB) for each tile
  decoded. Not the cache: with no decoded tile cached it promoted twice that (decoded twice). A tile
  takes longer to build than the ~50 ms between collections, so all it is made of is copied.
- A decoded tile as a few arrays (`FeatureGeometry.cs`: a layer's points one array, rings and parts
  offsets; a feature's properties a view of the layer's key/value tables and one shared tag array):
  decode 16.7 -> 9.5 MB allocated, 8.6 -> 3.4 MB alive; **promoted 7.1-7.6 MB a second, misses 1.6%
  and 0.8%** (two runs), worst frame 28-31 ms. GL JS: 0%.
- Left: ~17 collections a second (166 MB/s, most of it symbol layout: ~10 KB a label - shaping's
  object a glyph) and 7 MB/s still promoted. Next: what is still promoted, then shaping's glyphs.
- Then the misses left were no longer GC's: frames of 20-24 ms (one 31), Update + Draw + a little
  pause. A tile's buffers made in Update were 5-8 ms of six of eleven. **Now the workers make them**
  (`GpuTile.Upload` in the job): D3D11's device is free-threaded unless created `SingleThreaded` -
  vvvv's flags are `None` (logged, `Globe device:`) - and Stride 4.2.1's buffer creation touches only
  the device, its resource list under a lock (read in its source). A job dropped while running
  releases what it made. Misses 0.8%, **worst frame 24 ms** (was 28-34), Update under 5 ms in all.
- Left in the missed frames: label placement spikes of 6-9 ms (its budget is 2 ms a frame), submit
  6-11 ms (3300 draws), GC pauses of 2-6 ms. Next: the placement spikes.
- The label step timed by part in each missed frame: the spikes were the glyphs along lines, walked
  every frame the camera moved - every shown line label, a list a glyph for paths only collisions use.
  Now GL JS's isVisible (an anchor past the viewport and 256 px is hidden, not walked) and no paths
  but for collisions: lines 7.4 -> 0.2 ms, the main thread's allocation 50 -> 37 MB/s. **Bench: misses
  0.6%, worst frame 23 ms** (GL JS 0%, 17 ms). Fixture reports unchanged.
- Left: GC pauses of 4-7.6 ms (2-4 collections in one frame, ~17 a second), submit 5-13 ms (3300
  draws), the opacities rewritten for every label tile when a tile comes (5.4 ms once; GL JS skips
  unchanged buckets).
- Submit taken apart (state and parameters / Apply / DrawIndexed, vector and text apart): of ~5.5 ms,
  **~4 were setting the vector items' parameters** - Stride's `Set(key, value)` finds the key in the
  collection's layout every call; Apply 0.5 ms, DrawIndexed 0.1. Now set through accessors
  (`GetAccessor`, Stride's own renderers' way), fetched again each frame. **Submit 5.5 -> ~2 ms mean.**
- **Bench: 0.1% (one frame) and 0.0%, worst frame 20 and 19 ms - GL JS's 0% and 17 ms.** On this pan
  the gap the user felt is closed. Not yet judged by the user; not yet benched tilting or zooming.
- **Judged by the user (2026-11-23): "测试下来好很多了，帧数也稳定很多了".** Their own session's log
  showed what a pan does not: zooming, the label index spiked 10-44 ms.
- **A zoom bench** (`VLGLOBE_BENCH=zoom` / the page's `mode=zoom`: 5 levels out at one a second and
  back, twice; GL JS seen zoomed out in a screenshot): **ours 4.0% and 3.3%, worst 38-41 ms; GL JS
  0.2% and 0.4%, worst 850 and 67 ms.** Zooming builds tens of tiles a second: 41 collections a
  second, 370 MB/s, 14-20 MB/s promoted - in the seconds that build 70 tiles, 85 MB.
- Zoomed-out tiles' garbage was lines (a tessellator and strip list a line: now one a thread - z10
  shapes 17.5 -> 2.6 MB, z12 22.8 -> 0.3) and one-way arrows (icons shaped once per input set per
  layer, anchors without LINQ: 13.8 -> 6.7 MB). Off-screen line labels hidden again every frame,
  and every such tile's whole label buffer uploaded: now only a change, only the changed range.
  Zoom bench: 3.4%, worst 31 ms; collections 41 -> 27 a second, but promoted 14 -> 22 MB/s.
- Left in zooming: GC (273 of 270 ms overrun), the promoted bytes following the tiles built (about
  half their uploaded size).
- **What survives, seen** (dotnet-gcdump, installed 2026-11-23 with the user's yes; two dumps 22 s
  apart during the zoom bench): by count, 218k strings (the decoded tiles' value tables), 163-181k
  `StyleFeature`s, as many `TagProperties` and 206k boxed doubles (ids), and 51k `LineLabel`s with
  a float[] and a Vector3[] each. Bytes grow only in vertex arrays (17 MB, large-object heap). Done:
  a feature is its own property view and keeps its id unboxed - the 181k views and ~180k doubles
  gone; zoom bench 3.4% -> 2.8% (37 of 1306 frames), worst 39 ms. Then features as rows of their
  layer's arrays (`FeatureTable`, enumerated through one reused cursor - no object a feature): zoom
  bench **1.2%** twice (16 of 1314 frames), worst 31-35 ms; Tokyo offscreen unchanged (22-26 pixels
  of 255k differ, as between two runs of the same code). Then line labels as structs, their lines and
  glyph offsets ranges of two arrays a tile (no three objects a label): three runs 0.6%, 2.2%, 1.1% -
  no clear gain over 1.2%; the bench's spread between runs is now larger than the step. What the
  missed frames hold now is mixed, not GC alone (4-8 ms of it a frame): label opacities 5-10 ms in
  single frames, submit 7-9 ms at 6000 draws zoomed out, 800 label tiles. Left: the value strings as
  bytes (218k strings, the largest count), and these - measured before chosen.
- **Measured** (2026-11-23: every frame's parts summed a second, made and missed apart - the perf
  line's "frames by part"; three zoom benches, 1.9 / 0.8 / 0.9%, 48 missed of ~3950). Mean ms,
  made -> missed: GC 0.9 -> 6.4, submit 1.6 -> 5.4, Update 1.7 -> 6.5 (its gpu vectors 1.0 -> 4.1:
  the draw list's layers, 2.2 ms in every missed frame), label place 0.4 -> 3.9 (opacities 0.2 ->
  2.1), upload 0.1 -> 1.6; time outside the node 12.9 -> 8.7 (the vsync slack used up). GC alone
  is as long as the overrun in 14 of 48, and at most 63% of all of it; a pause lands inside the
  other parts, so theirs include some. 19 of 48 had a tile uploaded in them; 2 a gen1/gen2.
  Submit in the missed frames grows with items: 5.4 ms at 2000-6000, 6.0 above.
- Value strings left in the tile's bytes until a property is first read (`ValueTable`), keys shared
  across tiles: strings grown in 22 s 218k -> 88k, all new objects 706k -> 328k; zoom bench 1.1 /
  0.8 / 0.8% (was 1.9 / 0.8 / 0.9). Seen in the same dump: 18k Stride `ShaderClassSource` (and 9k
  each of its lists) grew and stayed - from the raster engine's Material.New per tile slot (GlobeNode).
- Each vector draw's values in one structured buffer a frame (GlobeVector.sdsl's GvDraws), its index
  in slot 1 bound at its element with stride 0: one Apply a pass instead of 13 sets and an Apply a
  draw. Submit 1.54 -> 1.15 ms in a made frame, 5.2 -> 4.1 in a missed one; zoom bench 0.5 / 0.9 /
  0.7% (was 1.1 / 0.8 / 0.8). Fixture reports identical, Tokyo offscreen within the probe's spread.
  Not yet: merging draws across tiles (the draw calls themselves are 0.1 ms).
- **Stopped here (the user, 2026-11-23: "如果你评估下来差不多了，那我们就可以停在这里了").** Pan
  0.0-0.1% missed (GL JS 0%), zoom ~0.7% (from 3.4%; GL JS 0.2-0.4%). Left, by cost: pooling the
  decoded tiles' arrays (ownership on eviction; ~0.1-0.3 points), spreading a tile's arrival over
  frames and label opacities only where changed (small each), the tiles' data in native memory
  (what GL JS's worker heaps amount to; every access by hand, crashes on a mistake), building in a
  separate process (IPC, shared GPU memory). `DOTNET_GCgen0size` is the whole vvvv's, not a package's
  to set. The one heap .NET threads share is the difference that stays. A dump stalls vvvv ~0.8 s: never bench while dumping.

### 4h. Ground layers sinking under the globe past a source's maxzoom — built and seen by the user 2026-11-23 ("似乎成功了")

**Built** (the user chose B', GL JS's way): ground passes take the true sphere's depth at their pixel
(`GlobeVector.sdsl`, `GvSphere`), the far side cut along the ray; `OverscaledGroundFacts` over the globe
tiles' depth (red before: 0 of 120000 px), 315 green, fixture reports unchanged. Seen by the user in
vvvv: "似乎成功了". Zoom bench after it (Tokyo #14.82/35.68778/139.76758/-78.6/54.5, 2026-11-23 19:16):
0.1% missed (1 of 1324 frames), worst 21 ms - no cost seen. Done. (Was: the 4g bench (SV_Depth turns off early depth for these draws).


**The whole plan, the user's words and the evidence: [DESIGN-OVERSCALED.md](DESIGN-OVERSCALED.md).**
The user, testing styles: "zoom in之后就会把底下的深蓝色的球也给露出来" (style 13, MapLibre's demo
tiles, maxzoom 6; seen with others too), then "还是非常明显，我觉得这个问题需要仔细想一下对策".
Cause: a bucket is cut by its vector tile's zoom; shown far past the source's maxzoom its chords sag
under the sphere (z6: ~80 m) more than the lift (0.0015 of the distance), and the globe's tiles hide
it by depth. The background is fixed (7b278f4, per shown square); fills and lines are not (picture 4:
the land gone at 2.5 km). Expected at street level on every style too (z14 sources, §2.5). Next:
read how GL JS's globe does it, choose A (per-shown-tile clipped buckets) or B (no depth test against
the globe; horizon in the shader) with the user, first a probe that can fail (globe depth in
`RenderGlobe`).

### 5. Navigation — nodes built 2026-11-23, tested (294 green); not yet seen in vvvv

**Built** (`GlobeNavigateNodes.cs`, `Engine/Camera/Navigation.cs`, category `Globe.Navigate`, VL.Mapsui's
shape): `CenterOn`, `ZoomToLevel`, `ZoomIn`, `ZoomOut`, `FlyTo`, `ZoomToBounds`. Each acts on a
Trigger's rising edge (the view is also moved by Drag and the wheel every frame: a node setting it
every frame would hold it), passes the view on (Output) and logs the view's hash. FlyTo is MapLibre's:
van Wijk and Nuij's path (rho 1.42) along the great circle, CSS ease, Duration 0 = S / 1.2 s (at most
10); a drag or the wheel ends it. ZoomToBounds finds the height on the screen by bisection (a cap's
radius left a box at 0.64 of the window). Help patch `HowTo Fly to a place`. The drag that leaves the
globe: MapLibre's (the user's choice, 2026-11-23) - the cursor in space holds the horizon's nearest
point (`GlobeView.HorizonToward`), so the drag goes on; only drags do, ScreenToGlobe still misses.

The plan as written:

- `CenterOn`, `ZoomToLevel`, `ZoomIn`, `ZoomOut`.
- `FlyTo`: a great-circle path with a height arc, the globe's DragBetween.
- `ZoomToBounds`: fit an NTS envelope or geometry, using the bounding cap and angular radius θ;
  eye distance ≈ R·cos θ + R·sin θ / tan(fov/2).
- The drag that leaves the globe: today the camera stops when the cursor is off the sphere.
  - Cesium switches to a trackball above 7,500 km.
  - MapLibre 6 bends the ray past the horizon.
  - Choose one; the ray must not reach the Newton solve when it misses.

### 6. Widgets and imagery layers — imagery layers built 2026-11-23

**Several imagery layers with Opacity — built**, seen in vvvv (OpenTopoMap at 0.5-0.6 over OpenStreetMap,
Tokyo and Mount Fuji). OpenStreetMap and XyzTiles have an `Opacity`; in Group the leftmost tile layer is
the globe's own imagery (mipmaps, fades, stand-ins) while opaque - below Opacity 1 it goes into the
overlay too, over plain tiles in the Background colour (the user: "用户知道自己再做什么") - every layer above it is drawn into
each tile's overlay in Group order (`RasterOverlay`: the source's disk cache, or fetched on the overlay
worker for a wanted tile; a stand-in draws only what is cached and is marked incomplete). GPU vector
tiles stay above all tile layers whatever their place in Group. Help patch `HowTo Stack tile layers`
(XyzTiles' first). Still to do: ScaleBar, ZoomButtons, Click.

**The attribution in the window — built 2026-11-23** (the user: "别忘了支持Attribution的信息等等的呈现"), seen
in vvvv by me (Tokyo, Liberty: "OpenFreeMap © OpenMapTiles Data from OpenStreetMap" bottom right), not yet
by the user. As MapLibre's AttributionControl and Mapsui's renderer: no node, every Globe draws it while
Enabled - `Engine/Tiles/AttributionLayer.cs`, a VL.Skia layer put in the scene by VL.Stride's `SkiaRenderer`
(made in C#, `AfterScene`), 12 px on a half-white box, cut with an ellipsis when wider than the view.
Globe's `Attribution` output is now every enabled layer's credit once, in order, joined by " | " (was:
the imagery's only). FeatureLayer and Geometry have an `Attribution` pin for the user's own data (Mapsui's
layers each have one; the user chose it 2026-11-23), and a style's geojson sources give theirs too.
`AttributionFacts`.

**The poles — built 2026-11-23** (the user: "南极和北极的圆洞", GL JS's way chosen): as MapLibre GL JS's
globe (`getMeshFromTileID` extendToNorthPole/SouthPole for raster, background, hillshade), the top and
bottom rows of tiles reach the pole, the texture's edge row stretched over the cap - 20 rows of 0.25°
(`TileGeometry.CapRows`; one row 85°-90° would sag ~6 km). The selector samples the cap for the
horizon (over a pole it chose no tile at all) but picks the zoom by the square. A style's background
reaches the pole too (`VectorBucket` Background, the vertex's spare byte `Cap`, `GlobeVector.sdsl`
PoleCap); fills do not, as in GL JS - Liberty's sea stops at 85°, the cap is its background colour.
Seen in vvvv by me (OSM + OpenTopoMap and Liberty over the North Pole), not yet by the user.
`PoleCapFacts`, incl. multisampled (below).

**The help patches reorganised (2026-11-23, the user: "为什么是放在coordiates分类下面的呢？感觉有很多不佳而离的地方"):**
topics and subfolders as VL.Stride's and VL.Skia's (Overview, Layers, Camera, Input, Objects); "Place a point" and
"Stand a model" merged into `Objects\HowTo Place objects on the globe` (GlobePositionComponent the main cell,
ToGlobe/FromGlobe a cell of numbers below - absolute, so not drawn under GlobeWindow); "Put OpenStreetMap on a
globe" is `Overview\HowTo Build your own globe scene`. All 11 compile (Compile-HelpPatches); the merged one seen
in vvvv's editor. F1 flags (none before - F1 on a VL.Globe node opened nothing): one High per node, Low where it
also helps, by `tools/Set-HelpFlags.py` (its table is the record); all 31 nodes - FeatureLayer (Draw shapes, with its
Attribution), GlobeToScreen (Read the place) and ToGlobe (Coordinate) (Place objects) given cells, Group (Spectral) one in Stack tile layers. F1 tried by the user: works. Open: an off switch (MapLibre's `attributionControl: false`) -
not added until asked.

The plan as written:

- `ScaleBar`: TerriaJS's method — the ground distance across one pixel at a probe point, then the
  largest round length under 100 px. Hidden when the probe misses the globe.
- `ZoomButtons`, `Click`, the attribution shown in the window.
- **Several imagery layers with `Opacity`**, if item 3's draped path did not already deliver them.

### 7. Gestures and tidying (before the release)

**The drag near the poles — fixed 2026-11-23** (the user: "视野在南北极附近拖拽的时候还是会抖动，甚至会闪动到北极去";
the user chose GL JS's way): below MapLibre zoom 12 the drag is GL JS's globe pan (computeGlobePanCenter: the
pixel move times the centre's degrees per pixel, latitude within ±85.051129, longitude speed capped at 60° far
out), above it the exact grab; chosen at the press (`GlobeView.DragSession`). The grab with north up had no
answer past the pole and took the far side's - 125-180° in one frame (`PoleDragFacts`). Packed; the user's
look in vvvv still to come.

- **Ask the user about the vertical drag direction.** `Position in Projection` is assumed y-up and
  nobody has asked. Then move the Drag and ZoomByWheel logs from Information to Debug.
- **Teach `Compile-HelpPatches.ps1` to decompile `VL.Globe.vl.dll`** and run the dropped-link check
  on patched definitions. The first GlobeWindow's height link was silently dropped.
- ~~`Test-VLPatch.ps1` and `HelpPatchGen.ps1` still carry VL.Mapsui's node lists: run
  `Test-VLPackage.ps1` and fix what it reports.~~ 2026-11-24: both read `tools\Nodes.psd1`;
  HelpPatchGen.ps1 (VL.Mapsui's generator) removed.
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
