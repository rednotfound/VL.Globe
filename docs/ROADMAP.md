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

### 1. PlaceOnGlobe, then the 20 m floor — built 2026-11-21, the user's look pending

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

### 1b. Tilt and turn — pulled into 0.1 by the user, 2026-11-21, built, the user's look pending

The user, having stood a model on the globe: "因为是3d的场景了…还是想要调整camera的俯仰角度从侧面看你放在地图上
的3d模型". Gesture chosen by the user: **right-drag** (MapLibre): sideways turns, up/down tilts, about
the view's centre, `Max Tilt` 60°. The view state became centred (ARCHITECTURE "The camera"); at tilt 0
nothing changed (all 105 earlier tests, and the ball's 66–67 px). Tiles now filter anisotropically.
**Still open:** `TileCoverageFacts` has no tilted poses yet; the 400-tile cap and the far plane under
tilt; depth precision toward the horizon; a Compass widget.

### 2. Coordinates and reading the view

- **`ScreenToGlobe`**: the cursor in projection space → longitude/latitude, plus `Success`, because
  the cursor can miss the globe.
- **`GlobeToScreen`**: longitude/latitude/height → projection space, plus `Visible`, because the
  point can be on the far side.
- **`GlobeViewInfo`**: centre longitude/latitude, height, zoom level, heading.
  - The zoom ↔ height mapping is by ground resolution (GeoLibre `cesium-camera.ts`), so the zoom
    matches VL.Mapsui's and a 2D map can be synced to the globe.
- All of these are absolute maths and camera-relative-safe. Heights are above sea level, as decided.

### 3. Experiment: how vectors and extra raster layers reach the globe

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

### 4. FeatureLayer, styles, Graticule

- **`FeatureLayer [Globe]`**: NTS points, lines and polygons, drawn the way item 3 decided.
- **`VectorStyle`, `SymbolStyle`, `LabelStyle`**: the same shape as VL.Mapsui's. Whether they *are*
  Mapsui's is a family question: VL.Globe must not depend on VL.Mapsui.
- **`Graticule`**: it exercises the line path from street to globe.
- **`Pick`**: cursor → longitude/latitude → the features within a tolerance in metres. Same outputs
  as Mapsui's Pick.

**Verify:** a GeoJSON of Tokyo's wards stays on OSM's boundaries from 20 m to the whole globe.

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
