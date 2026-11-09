# VL.Globe — architecture

What exists on 2026-11-09, and why it is built that way. The plan for what comes next is
[ROADMAP.md](ROADMAP.md); the failures that shaped this are [../NOTES.md](../NOTES.md); the scope
decision is `D:\2026_Projects\vl-overworld\docs\GLOBE-SCOPE-PROPOSAL.md` (§3–§5 the surface, §9 the
camera revision, both decided by the user).

## What the package is

Web Mercator raster tiles drawn on a sphere in vvvv gamma's Stride, fetching only the tiles the
camera can see at the zoom its distance earns, plus a camera that moves like a map (grabbed, zoomed
toward the cursor, never underground) and conversions between longitude/latitude and positions on
the sphere. **It does not depend on VL.Mapsui**: tiles come through BruTile, are decoded by Stride
and drawn on meshes built in C#. It shares two things with VL.Mapsui by convention only — the tile
disk-cache folders and the `Enabled`-off consent rule.

Dependencies: VL.Stride (ships with vvvv 7.4 → Stride 4.2.1.2487), BruTile 5.0.6 (**pinned to
VL.Mapsui's version**: both load into one vvvv process and NuGet unifies to one BruTile),
NetTopologySuite 2.6.0 (for `ToGlobe (Coordinate)`). net8.0.

## The nodes

| node | kind | file | role |
|---|---|---|---|
| `OpenStreetMap [Globe]` | process | `TileSources.cs` | an `HttpTileSource` for OSM, built once (an HttpTileSource owns an HttpClient and is not disposable) |
| `XyzTiles [Globe]` | process | `TileSources.cs` | any `{z}/{x}/{y}` server + its required attribution; one source per URL |
| `Globe [Globe]` | process | `GlobeNode.cs` | the engine: select → request → build → draw; outputs one Stride `Entity` |
| `GlobeCamera [Globe]` | process | `GlobeCameraNodes.cs` | a geographic camera state and the Stride camera entity it drives |
| `Drag [Globe]` | process | `GlobeCameraNodes.cs` | grab the globe (input `View`, output `Result`) |
| `ZoomByWheel [Globe]` | process | `GlobeCameraNodes.cs` | zoom toward the cursor (input `View`, output `Output`) |
| `ToGlobe`, `ToGlobe (Coordinate)`, `FromGlobe [Globe]` | static | `GlobeCoordinates.cs` | lon/lat/height ↔ Vector3, absolute scene coordinates |

The help patches: `HowTo Put OpenStreetMap on a globe` (the whole chain) and `HowTo Place a point
on the globe` (ToGlobe/FromGlobe, still with an OrbitCamera).

### The wiring a patch does

```
OpenStreetMap ──Tile Source──► Globe ──Entity──► RootScene ──► SceneWindow
GlobeCamera ──Camera──► Globe.Camera, RootScene, SceneWindow.Camera, Mouse.Camera
GlobeCamera ──View──► Drag ──Result──► ZoomByWheel
Mouse [Stride.Input] ──Output──► RootScene        (it hears the window only inside the scene)
Mouse ──Position in Projection──► Drag.Position, ZoomByWheel.Position
Mouse ──Left Pressed──► Drag.Dragging ;  Mouse ──Mouse Wheel Delta──► ZoomByWheel.Wheel Delta
```

The interaction stays in the patch, as in VL.Mapsui (`Map` → `Drag` → `ZoomByWheel`): no node reads
the mouse behind the reader's back.

## Coordinates and units

- **Axes are Stride's `Sphere`'s**: y is north, longitude 0 / latitude 0 is +z, 90°E is +x.
  `GlobeCoordinates` and `TileGeometry.OnSphere` share the formula; four tests pin it.
- **The sphere is Web Mercator's**: WGS84's equatorial radius, 6,378,137 m (`EarthRadius`), drawn
  at `Radius` scene units (default 1). Heights are metres above it.
- **Tiles are OSM-numbered** (row 0 at the north), `TileKey(Zoom, X, Y)`.
- **Today every position is absolute** (centre of the earth at the origin) and handed to Stride as
  float. That is the precision limit ROADMAP item 1 removes.

## The engine (`Globe`, `Engine\`)

Each frame, on vvvv's main thread:

1. **Camera pose.** If the camera's `CameraComponent` uses a custom view matrix (GlobeCamera and
   vvvv's OrbitCamera both do), the eye is `Invert(ViewMatrix)`; otherwise the transform. The
   projection is rebuilt from the camera's field of view, aspect (16:9 unless set — a gap) and
   clip planes into a `BoundingFrustum`.
2. **Select** (`TileSelector`, pure): a quadtree walk from zoom 0. A tile is dropped when it is
   outside the frustum (Stride 4.2 tests boxes only, so the box around the tile's bounding sphere)
   or entirely behind the horizon (a 5 × 5 sample of the tile, `dot(p, C) > 1`); it is refined while
   one of its 256 texels would cover more than `Screen Space Error` pixels — texel ground size /
   distance × screen height / (2 tan(fov/2)), Cesium's screen-space error applied to imagery. Bounded
   by `Max Zoom` and 400 tiles.
3. **Request** (`DrawSet.Requests`, pure): every wanted tile's zoom-2 ancestor first, then the tile,
   skipping loaded/pending/failed; at most `Max Requests In Flight` at once, each on the thread
   pool via `HttpTileSource.GetTileAsync` (which reads and writes the disk cache itself).
4. **Build** what arrived, at most 8 per frame: `Texture.Load` (sRGB), a CPU mesh
   (`TileGeometry.Vertices`: rows and columns linear in Mercator — MapLibre's layout — on one global
   grid so neighbours share edge vertices bit for bit; `SegmentsFor(zoom) = max(8, 64 >> zoom)` so
   coarse facets sag ≤ 0.0024 radii), an emissive-only material (the map is the light), an entity.
5. **Draw** (`DrawSet.Resolve`, pure): each wanted tile, or its nearest arrived ancestor; never a
   tile together with one of its ancestors (coplanar → z-fight). Entities are added/removed as
   children of the one output entity.
6. **Evict** least-recently-used tiles beyond 600, disposing texture and buffers.
7. **Report** `Tiles Visible`, `Tiles Loading`, `Requests Sent`, `Bytes Received`, `Attribution`,
   `Status` — and log `Status` when its head changes (`ILogger`, lands in vvvv's `--log` file).

`Enabled` off draws and requests nothing. A source change resets everything.

## The camera (`GlobeView`, `GlobeCameraNodes.cs`)

- **State is geographic**: `Longitude`, `Latitude` of the point straight below, `Height` in metres,
  `Heading`. Never a free 3D pose, so the camera cannot reach the ground: every change goes through
  `SetHeight`, clamped to Min/Max Height. Tilt is always 0 (looking straight down) until a Tilt node
  exists.
- **All camera maths is double** (`D3`, an internal record struct with a hand-written `ToString` —
  the generated one recursed and crashed vvvv). Floats appear only in the view matrix handed to
  Stride.
- **One primitive does both gestures**: `BringUnderCursor(wanted, camDir)` rotates the whole rig
  about the globe's centre by the rotation taking the point now along `camDir` to `wanted`
  (axis = now × wanted, Rodrigues). Drag calls it with the point grabbed at press; ZoomByWheel calls
  it after changing the height, with the point that was under the cursor before. Cesium's `pan3D`
  and MapLibre 6's `versorSetLocationAtPoint` use the same rotation.
- **The cursor arrives in projection space** (vvvv Mouse's `Position in Projection`, −1..1, y
  assumed up) and becomes a camera-space direction in doubles via the projection scale (M11, M22)
  GlobeCamera reads from its own CameraComponent each frame. The Mouse's world `Ray` is NOT used: it
  is unprojected in float next to an eye of magnitude ~1 and jittered by metres near the ground.
- **Wheel**: height × 0.5^(`Levels Per Notch` × delta / `Notch Size`), default 0.5 levels a notch
  (MapLibre 0.151, Cesium ~0.1 + inertia, VL.Mapsui 1).
- **Clip planes follow the height**: near = 0.1 × height, far = 1.1 × horizon distance + 0.01 R.
  This is what removed the depth fight between tiles and a background sphere.
- **Off the globe** (the cursor in space) the camera does not move — a known gap.

## Threading and lifetime

Process nodes only (a static node runs 60×/s). Network work runs on the thread pool and only
enqueues bytes; every GPU resource is created and every entity added or removed in `Update`, on the
main thread. A tile from a source that has since been replaced is dropped on arrival.

## Tests (`test\VL.Globe.Tests`, 53, no network, no GPU)

Coordinates (axes, round trips, Coordinate's Z), tile geometry (exact seams across tiles, levels and
the date line, sag bounds, Mercator round trip), selection (far side not requested, sharper when
closer, the tile under the camera is the sharpest, bounded count, no overlaps), draw set (stand-ins,
no tile over its ancestor, coarse-first requests), camera invariants (grabbed point stays under the
cursor, zoom keeps the cursor's point, height clamped, off-globe does not move, heading survives,
clip planes follow height, projection input agrees with pixels, a 100 m drag steady to 1 cm), and
`D3.ToString` terminating on a small stack. The test project references VL.Stride.Runtime **without**
`ExcludeAssets`, or Stride's module initializer kills every test.
