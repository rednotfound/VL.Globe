# VL.Globe — architecture

What exists on 2026-11-21, and why it is built that way. The plan for what comes next is
[ROADMAP.md](ROADMAP.md); the failures that shaped this are [../NOTES.md](../NOTES.md); the scope
decision is `D:\2026_Projects\vl-overworld\docs\GLOBE-SCOPE-PROPOSAL.md` (§3–§5 the surface, §9 the
camera revision, both decided by the user).

## What the package is

Web Mercator raster tiles drawn on a sphere in vvvv gamma's Stride, fetching only the tiles the
camera can see at the zoom its distance earns, plus a camera that moves like a map (grabbed, zoomed
toward the cursor, never underground, north always up) and conversions between longitude/latitude
and positions on the sphere. **It does not depend on VL.Mapsui**: tiles come through BruTile, are
decoded by Stride and drawn on meshes built in C#. It shares two things with VL.Mapsui by
convention only — the tile disk-cache folders and the `Enabled`-off consent rule.

Dependencies: VL.Stride (ships with vvvv 7.4 → Stride 4.2.1.2487), BruTile 5.0.6 (**pinned to
VL.Mapsui's version**: both load into one vvvv process and NuGet unifies to one BruTile),
NetTopologySuite 2.6.0 (for `ToGlobe (Coordinate)`). net8.0. Developed and verified on **vvvv 7.4,
the LTS line** (user, 2026-11-20); `tools\Find-Vvvv.ps1` stays below 8.0.

## The nodes

| node | kind | file | role |
|---|---|---|---|
| `GlobeWindow [Globe]` | **patched** process | `VL.Globe.vl` | the front door: the whole chain below in one node, with its own window; tells Globe the window's real height |
| `OpenStreetMap [Globe]` | process | `TileSources.cs` | an `HttpTileSource` for OSM, built once (an HttpTileSource owns an HttpClient and is not disposable) |
| `XyzTiles [Globe]` | process | `TileSources.cs` | any `{z}/{x}/{y}` server + its required attribution; one source per URL |
| `Globe [Globe]` | process | `GlobeNode.cs` | the engine: select → request → build → draw → fade → drape the `Layers`; outputs one Stride `Entity` |
| `Geometry`, `FeatureLayer [Globe.Layers]` | process | `GlobeLayers.cs` | NTS geometries / features in one style → a layer for `Layers`, VL.Mapsui's shape |
| `VectorStyle [Globe.Styles]` | static | `GlobeLayers.cs` | fill, line colour, line width in pixels; unwired colours default as Mapsui's |
| `GlobeCamera [Globe]` | process | `GlobeCameraNodes.cs` | a geographic camera state and the Stride camera entity it drives, camera-relative |
| `Drag [Globe]` | process | `GlobeCameraNodes.cs` | grab the globe (input `View`, output `Result`) |
| `ZoomByWheel [Globe]` | process | `GlobeCameraNodes.cs` | zoom toward the cursor, eased (input `View`, output `Output`) |
| `DragRotate [Globe]` | process | `GlobeCameraNodes.cs` | right button: turn (sideways) and tilt (up/down) about the view's centre, MapLibre's handler (input `View`, output `Result`) |
| `GlobePositionComponent [Globe]` | process → component | `GlobePosition.cs` | stands an entity on the globe: into its `Components`; its own Transformation is then in metres (+X east, +Y up, −Z north); placed camera-relative at draw time |
| `ToGlobe`, `ToGlobe (Coordinate)`, `FromGlobe [Globe]` | static | `GlobeCoordinates.cs` | lon/lat/height ↔ Vector3, absolute scene coordinates |
| `ScreenToGlobe`, `GlobeToScreen`, `GlobeViewInfo [Globe]` | static | `GlobeViewNodes.cs` | projection-space position ↔ lon/lat (Success: hit the globe; Visible: not on the far side or behind); the view's centre, height, heading, tilt, ground m/px and web-map zoom (Mapsui's) |

Help patches: `HowTo Open a globe window` (OpenStreetMap → GlobeWindow, the front door),
`HowTo Put OpenStreetMap on a globe` (the separate nodes, for a window shared with other things) and
`HowTo Place a point on the globe` (ToGlobe/FromGlobe, with an OrbitCamera — absolute, see below).

### GlobeWindow, and the wiring a patch does without it

GlobeWindow is patched in `VL.Globe.vl` like VL.Mapsui's `MapWindow`: outer pins in its Create/Update
patches reach the inner nodes by hidden links. Inside, and in `HowTo Put OpenStreetMap on a globe`:

```
OpenStreetMap ──Tile Source──► Globe ──Entity──► RootScene ──► SceneWindow
GlobeCamera ──Camera──► Globe.Camera, RootScene, SceneWindow.Camera, Mouse.Camera
GlobeCamera ──View──► Drag ──Result──► ZoomByWheel
Mouse [Stride.Input] ──Output──► RootScene        (it hears the window only inside the scene)
Mouse ──Position in Projection──► Drag.Position, ZoomByWheel.Position
Mouse ──Left Pressed──► Drag.Dragging ;  Mouse ──Mouse Wheel Delta──► ZoomByWheel.Wheel Delta
SceneWindow ──Client Bounds──► Height (2D.Rectangle) ──► FrameDelay ──► Globe.Screen Height
SceneWindow: Enable Default Post Effects = False, Present Interval = One, Enable Default Camera = False
```

- **Screen Height** is how many pixels a tile texel covers, so it decides the detail. It comes from
  the window, one frame late (SceneWindow is downstream of Globe; FrameDelay breaks the loop). The
  Height node is `2D.Rectangle`'s — SceneWindow's `Client Bounds` is RectangleF; Stride's `Rectangle`
  Height was dropped by the compiler without a word.
- **Post effects off**: SceneWindow's defaults tone-map and anti-alias the frame — map colours came
  out darker (10,26,56 shown as 0,7,50) and the text soft. Map tiles are finished pixels.
- **Present Interval One**: the pin has no default and ran `Immediate`; a fast drag tore.
- GlobeWindow also takes **`Layers`** (vector layers) and outputs **`Cursor`** (its Mouse's position in
  projection space, for ScreenToGlobe) — it hides its Mouse.

## Coordinates and units

- **Axes are Stride's `Sphere`'s**: y is north, longitude 0 / latitude 0 is +z, 90°E is +x.
  `GlobeCoordinates` and `TileGeometry.OnSphere` share the formula; four tests pin it.
- **The sphere is Web Mercator's**: WGS84's equatorial radius, 6,378,137 m (`EarthRadius`), drawn
  at `Radius` scene units (default 1). Heights are metres above it.
- **Tiles are OSM-numbered** (row 0 at the north), `TileKey(Zoom, X, Y)`.
- **Rendering is camera-relative** (ROADMAP item 1, Cesium's RTC). GlobeCamera's view matrix is
  rotation only — the eye at the origin — and the eye itself, in double, rides on the camera entity
  in an internal `GlobeCameraFrame` component, snapshotted when the view is built. Globe reads it from
  the `Camera` it is given and places every tile at `(centre × R − eye)`, subtracted in double; each
  tile's vertices are offsets from its own centre (`TileGeometry.RelativeVertices`). At street level
  a vertex lands within 0.008 mm of the truth; the absolute path was 0.54 m off
  (`CameraRelativeFacts`). **A camera without the component (OrbitCamera) is drawn absolutely**, as
  before. `ToGlobe` stays absolute (right under OrbitCamera). **Your own entities** carry a
  `GlobePosition` component instead: its processor sets a `TransformLink`, which Stride asks for the
  parent matrix in `TransformProcessor.Draw` — after the patch ran — and which finds GlobeCamera's
  frame in the scene, so no pin carries the camera and GlobeWindow's `Input` has no cycle (a
  Camera-taking transformation node was built first and dropped for that cycle).

## The engine (`Globe`, `Engine\`)

Each frame, on vvvv's main thread:

1. **Camera pose.** The view matrix is the custom one when the camera uses it (GlobeCamera,
   OrbitCamera), else the transform; with a `GlobeCameraFrame`, the eye is put back in
   (`Translation(−eye) × view`) for selection only — culling tolerates float, drawing does not.
2. **Select** (`TileSelector`, pure): a quadtree walk from zoom 0. A tile is dropped when outside the
   frustum (Stride 4.2 tests boxes: the box round the tile's bounding sphere) or wholly behind the
   horizon (a 5 × 5 sample). It is refined while one texel would cover more than
   `Screen Space Error` pixels: the texel's width **at the tile's equator-most row** (cos φ — Cesium's
   `latitudeClosestToEquator`), over the distance **to the tile's nearest surface point** (camera
   lon/lat clamped into the tile, not its bounding sphere), × screen height / (2 tan(fov/2)).
   Default **1.41 = √2: the ideal level rounded**, as MapLibre (raster `roundZoom`) and Cesium
   (imagery `Math.round`) choose imagery; tiles show at 0.71–1.41×. Bounded by `Max Zoom` and 400.
3. **Request** (`DrawSet.Requests`, pure): every wanted tile's zoom-2 ancestor first, then the tile,
   skipping loaded/pending/failed; at most `Max Requests In Flight` at once, on the thread pool via
   `HttpTileSource.GetTileAsync` (which reads and writes the disk cache itself).
4. **Build** what arrived, at most 8 per frame: decode (`Image.Load`, sRGB), a **mip chain built on
   the CPU in linear light** (`MipChain`: 2 × 2 boxes; averaging sRGB bytes darkens thin lines and
   text), sampled trilinear and clamped; a mesh of offsets from the tile centre (rows and columns
   linear in Mercator, `SegmentsFor(zoom) = max(8, 64 >> zoom)`); an emissive-only material.
5. **Draw** (`DrawSet.Resolve`, pure): each wanted tile, or its nearest arrived ancestor; never a tile
   together with one of its ancestors (coplanar → z-fight).
6. **Fade** (`Fade`, MapLibre's raster fade, `Fade Duration` 0.3 s): a tile that replaces a drawn
   ancestor gets a second material, `own × f + ancestor-part × (1 − f)`, the ancestor sampled at the
   child's part (`uv × scale + offset`); zooming out, the drawn descendants fade to their parts of the
   parent before the parent replaces them — only when they cover it (`Fade.Covers`). One layer per
   spot throughout. The weights are keyed `ComputeFloat4`s set every frame, **alpha 1**: Stride's
   `BinaryOperator.Add` is `ComputeColorAdd3ds`, an alpha-weighted blend. An ancestor under a running
   fade is never evicted.
7. **Drape the layers** (`TileOverlay`, Cesium's and Mapsui's own way): every shown tile whose overlay
   is out of date gets one, at most 6 per frame, nearest first — the layers' geometry in its Web
   Mercator square drawn by SkiaSharp into a transparent 256 px image, mipped with colour weighted by
   alpha, and laid over the imagery with `BinaryOperator.Over`. The same look is used by the tile's
   fades and by stand-ins borrowing it. A replaced overlay is disposed 60 frames later, since a fade or
   stand-in may still sample it. Layers change by content, not identity.
8. **Every tile's translation** is set from this frame's eye; the **background sphere** (radius
   0.995, `Background` colour, a 1 × 1 sRGB texture — an emissive `ComputeColor` drew black) too,
   and it is drawn whenever there is a camera, Enabled or not.
9. **Evict** least-recently-used tiles beyond 600; **report** `Tiles Visible`, `Tiles Loading`,
   `Requests Sent`, `Bytes Received`, `Attribution`, `Status` — logging `Status` when its head changes.

`Enabled` off requests nothing and draws only the background. A source change resets everything.
A one-level seam where neighbours differ stays visible on a still globe; MapLibre and Cesium accept
it too — rounding makes its two sides 0.71× and 1.41× instead of 1× and 2×.

## The camera (`GlobeView`, `GlobeCameraNodes.cs`)

- **State is geographic and centred, MapLibre's** (since 2026-11-21): `Longitude`, `Latitude` of the
  point at the centre of the view, `Height` the distance from the eye to it, `Heading`, `Tilt`
  (0 = straight down, at most `Max Tilt`, 60°). At tilt 0 this is exactly the old state (the point
  below, the height above it). Rotating and tilting turn about the centre. The floor applies to the
  eye's altitude (`EyeAltitude`, GlobeCamera's `Height` output): `SetHeight` keeps
  Height ≥ Min Height / cos(tilt). Clip planes follow the eye's altitude.
- **All camera maths is double** (`D3`, with a hand-written `ToString` — the generated one recursed
  and crashed vvvv).
- **One primitive does both gestures, north up**: `BringUnderCursor(wanted, camDir)` takes the rigid
  rotation now → wanted only as a first guess, restores `Heading`, then solves longitude/latitude by
  Gauss-Newton until the point is under the cursor again. The plain rotation turned the heading by
  31° over one dragged circle (a sphere's holonomy); Cesium constrains to the polar axis and
  MapLibre's globe pan keeps the bearing.
- **The cursor arrives in projection space** (Mouse's `Position in Projection`, −1..1, y assumed up)
  and becomes a camera-space direction in doubles via GlobeCamera's projection scale. The Mouse's
  world `Ray` is not used: unprojected in float, it jittered by metres near the ground.
- **Wheel**: one notch is `Mouse Wheel Delta` 1. Each notch adds `Levels Per Notch` (0.25) to a
  pending zoom that `WheelSmoother` plays out exponentially over `Smoothing` (0.15 s), frame-rate
  independent; what is left is dropped at Min/Max Height. (MapLibre 0.151 eased 200 ms.)
- **Clip planes follow the height**: near = 0.1 × height, far = 1.1 × horizon distance + 0.01 R.
- **Tiles are filtered anisotropically**: a tilted view sees the ground at a grazing angle, where
  trilinear picks the blurriest mip.
- **Off the globe** (the cursor in space) the camera does not move — ROADMAP item 5.

## Threading and lifetime

Process nodes only (a static node runs 60×/s). Network work runs on the thread pool and only
enqueues bytes; every GPU resource is created and every entity added or removed in `Update`, on the
main thread. A tile from a source that has since been replaced is dropped on arrival.

## Verification

- **Tests** (`test\VL.Globe.Tests`, 145, no network, no GPU — SkiaSharp runs in the test host): coordinates; tile geometry (exact
  seams, sag); camera-relative precision (0.008 mm vs 0.54 m); selection — the tile below at
  0.6–1.42×, **every sampled pixel of the screen** at ≤ 1.5× over seven poses (`TileCoverageFacts`;
  the tile-below test could not see a stretched row), a wrong Screen Height making text small; draw
  set; camera invariants incl. a looped drag and corner zooms leaving north up; wheel easing; mip
  chain in linear light; fade transforms and coverage. The test project references
  VL.Stride.Runtime **without** `ExcludeAssets`, or Stride's module initializer kills every test.
- **`tools\Compile-HelpPatches.ps1`** compiles every help patch with vvvvc and reads the generated C#
  for each node and for dropped links. **It cannot see inside GlobeWindow**: the package's `.vl`
  compiles to `VL.Globe.vl.dll` with no source. Decompile it (`ilspycmd`) and read
  `GlobeWindow_P.Update` after every change to the definition.
- **Rung 4 is the user's screen**: the jitter, the heading drift, the stretched row, the dark fades
  and the tearing were all seen there first.
