# VL.Globe — roadmap

Written 2026-11-09 at the end of the session that built the package, so the next session can start
here. Read [ARCHITECTURE.md](ARCHITECTURE.md) first for what exists, and [../NOTES.md](../NOTES.md)
for what already failed. Items are in the order they should be done; each says how it is verified.
Nothing here is published; nothing is pushed — the user pushes.

**The goal the user set (2026-11-09): street level.** "我们肯定还是要做到很细才行" — the globe has to
work down to streets, not only at country scale, where errors hide ("在国家尺寸可能这点抖动就看不出来，
让人以为他是好的"). Every item below is judged at the lowest height, not the comfortable one.

## 1. Camera-relative rendering — DECIDED by the user, 2026-11-09, do first

**The problem, measured.** Everything is placed in absolute coordinates on a globe of radius 1 and
handed to Stride as float. Float spacing near 1.0 is 6e-8 radii ≈ 0.38 m, so the eye and every tile
vertex snap to a ~0.4 m lattice. Dragging at 100 m height moves the picture in ~4 px steps across an
800 px view (under half a pixel at 1 km). The violent jitter the user saw was a different, larger
error (the Mouse's float ray, fixed 2026-11-09); this residual is what remains, and it grows without
bound below 100 m. Street level (OSM zoom 19, ~0.3 m per pixel, a camera ~20–50 m up) is impossible
without fixing it.

**The decision.** The user was offered (a) keep absolute coordinates and raise Min Height to 500 m,
or (b) render relative to the camera, as Cesium and Google Earth do, accepting that how other things
are placed on the globe changes. **The user chose (b).**

**The design to build** (check it against Cesium's RTC / "relative to eye" before writing — the
research notes are in vl-overworld's `docs/GLOBE-RESEARCH.md` §5):

1. **One eye per frame.** `GlobeCamera` snapshots the eye (`D3`, metres or radii in double) when it
   builds the view matrix. Everything drawn that frame uses that snapshot — never the live
   `GlobeView`, which Drag/ZoomByWheel change later in the same frame.
2. **The camera sits at the origin.** GlobeCamera's view matrix becomes rotation only:
   `LookAt(0, forward, up)`.
3. **Tiles are stored relative to their own centre.** `TileGeometry.Vertices` returns vertices minus
   the tile centre (small numbers for fine tiles, so float is precise), plus the centre in double.
   Each tile entity's translation = (centre × R − eye) computed in double, cast to float every
   frame. Error now scales with the distance to the camera, not with the size of the earth.
4. **How Globe finds the eye.** GlobeCamera attaches an internal `EntityComponent` carrying its
   frame snapshot to the camera entity; Globe reads it from the `Camera` it is already given. No new
   pin. A camera without it (vvvv's OrbitCamera) keeps today's absolute behaviour, so nothing breaks.
5. **Placing your own things.** `ToGlobe` stays absolute (it is maths, and it is tested). Add a node
   that gives a **transformation relative to the camera's frame** for a longitude/latitude/height —
   working name `PlaceOnGlobe [Globe]` (in: `Camera` or `View`, `Longitude`, `Latitude`, `Height`;
   out: `Transformation`, oriented east-north-up so a model stands on the surface). Measure first how
   VL.Stride patches usually pass a transformation before fixing the name and pins (family rule:
   follow the usual pattern). `HowTo Place a point on the globe` moves to it.
6. **Then lower the floor.** Default `Min Height` from 100 m to ~20 m, `Max Zoom` 19 (OSM's maximum),
   and look at `Screen Space Error` at that height.

**Verify:**
- a pure test: a tile vertex at 20 m height, positioned through the new path in float, lands within
  1 mm of the double truth; the same vertex through today's absolute path is off by ~0.4 m (keep both
  numbers in the test as the contrast);
- in vvvv, at Min Height over a city, drag slowly: no stepping. The user is the judge — the first
  jitter was found by them, not by a test;
- `HowTo Place a point on the globe` still puts Tokyo at the centre (`docs/rung4-toglobe-tokyo.png`).

**Open inside this item:** depth precision once tilt exists (far plane grows toward the horizon) —
Stride's depth buffer, reversed-Z, or logarithmic depth; measure before choosing.

## 2. Confirm the gestures in vvvv, properly

The user confirmed on 2026-11-09 that the camera no longer jitters. Still unconfirmed:
- **vertical direction**: `Position in Projection` is assumed y-up (Stride's convention; vvvv's Mouse
  definition could not be traced). Dragging up must move the map up. If it is inverted, flip the sign
  in `GlobeView.CameraDirectionFromProjection` and add the fact to NOTES;
- **wheel units**: `Notch Size` defaults to 1; check one physical notch's `Mouse Wheel Delta` in the
  log (ZoomByWheel logs every event);
- drag and wheel at street level after item 1.

Drive input with `tools\Drive-Renderer.ps1` only when the user is not using the machine — it sends
real mouse events to screen coordinates (it refuses when another window covers the point; a
full-screen game did once).

## 3. Drag that leaves the globe

Today the camera simply stops when the cursor is off the sphere. Both references keep going:
Cesium switches to a trackball above 7,500 km (rate = clamp((|eye| − R)/R, 1/5000, 1.77)); MapLibre 6
bends the ray past the horizon into a saturating angle from 90 % of the horizon angle. Choose one,
test "a drag that crosses the limb continues smoothly".

## 4. Tilt and Rotate (proposal §9, phase 2)

`Tilt [Globe]` and `Rotate [Globe]` as separate gestures (Cesium: middle/ctrl-drag). The state
already has `Heading`; tilt needs the pose to look at a point ahead, a tilt limit (MapLibre 60°),
and tilt reduced at altitude. Far plane and depth precision from item 1 meet here.

## 5. Correct the engine's own camera assumptions

- `Globe` assumes a 16:9 aspect unless the camera sets one, and takes `Screen Height` as a pin
  (1080). With GlobeCamera, read the projection Stride actually used (as GlobeCamera already does for
  the gestures) and the real viewport height.
- Polar caps: the sphere is open above 85.05°. Fill with a coloured cap (MapLibre's fan).

## 6. Tidy what was built under time pressure

- Drag/ZoomByWheel log at Information on every event — move to Debug once item 2 is confirmed.
- `tools\Test-VLPatch.ps1` and `HelpPatchGen.ps1` still carry VL.Mapsui's node lists; run
  `tools\Test-VLPackage.ps1` (never run in this repo yet) and fix what it reports.
- `XyzTiles` has no help patch.
- Restyle both help patches to the family's help rules (memory: "help patch cells, not hubs",
  "help patch community style": 20 pt heading, short 9 pt notes, featured nodes spread evenly).
- `README.md` and `docs\` screenshots after item 1.

## 7. The course

vl-overworld's `lab\Experiment globe view.vl` (ACT-IV-LAB experiment 7) is still the hand-wired
prototype. Rebuild it on the nodes, with the same two camera poses, and compare with its pictures.
VL.Overworld's nuspec gains VL.Globe only when a chapter uses it.

## Later — phase 2 of the proposal, each waiting for a consumer

`Pick [Globe]` (cursor → longitude/latitude), vector data on the globe (NTS lines on the sphere, or
VL.Mapsui rendering vector tiles into textures), terrain (Terrarium heights as vertex displacement —
vl-overworld's `Prompt How high is here` already decodes them), a graticule, inertia. Not before
items 1–6. Stage 3 of the research (3D Tiles via cesium-native on .NET 10) is a different package.

## Not before

Publishing: the family's rule is the node surface going a week without moving. Item 1 adds a node
and changes how placement works, so the clock starts after it.
