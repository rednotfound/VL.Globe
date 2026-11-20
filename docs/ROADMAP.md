# VL.Globe — roadmap

Written 2026-11-09 at the end of the session that built the package; **revised 2026-11-21** after the
session that made it camera-relative and judged it at street level with the user. Read
[ARCHITECTURE.md](ARCHITECTURE.md) first for what exists, and [../NOTES.md](../NOTES.md) for what
already failed. Items are in the order they should be done; each says how it is verified. Nothing
is published. Branch `feature/camera-relative` is pushed to `github.com/rednotfound/VL.Globe`; the
user merges and releases.

**The goal the user set (2026-11-09): street level.** "我们肯定还是要做到很细才行" — the globe has to
work down to streets, not only at country scale, where errors hide. Every item is judged at the
lowest height, not the comfortable one. **vvvv 7.4 (LTS)** is the verification host (user,
2026-11-20).

## Done 2026-11-20 (branch `feature/camera-relative`)

Each was found or confirmed on the user's screen; details and numbers in NOTES.md.

| what | why it was needed |
|---|---|
| **Camera-relative rendering**, steps 1–4 of item 1 | float at radius 1 snapped everything to a 0.4 m lattice; now 0.008 mm at 20 m (`CameraRelativeFacts`) |
| Background sphere drawn by Globe (`Background` pin, drawn while off) | a patch Sphere sat on the camera once the camera was at the origin |
| **Drag and zoom keep north up** (Gauss-Newton on lon/lat) | the rig rotation drifted the heading: 31° per dragged circle — "拖动一下就是会拖歪掉" |
| **`GlobeWindow`** + help patch `HowTo Open a globe window` | Globe needs the window's real height; a 1080 default with an 808 px window made text small |
| Selection by distance to the tile's surface, texel at its equator-most row | bounding-sphere distance refined a level early; mid-latitude texel left a row stretched to 2.55× |
| **Levels rounded (SSE 1.41), mipmaps in linear light** | MapLibre and Cesium both round; the one-level seam read as 2× against 1× text |
| Post effects off, VSync on in every SceneWindow | default post effects darkened and blurred the map; Present Interval ran Immediate and tore |
| **Fades both ways**, 0.3 s (`Fade Duration`) | MapLibre's raster fade; first version dipped dark (Stride's Add is an alpha-weighted 3ds blend) |
| Wheel: 0.25 level a notch, eased 0.15 s (`Smoothing`) | "滚一个就拉近了很多" — one notch was half a level in one frame; one notch = Wheel Delta 1 |

## 1. Camera-relative rendering — steps 5 and 6 left

Steps 1–4 are built and verified (see ARCHITECTURE "Coordinates"). What remains:

5. **Placing your own things.** `ToGlobe` stays absolute (it is maths, and it is tested). Add a node
   that gives a **transformation relative to the camera's frame** for a longitude/latitude/height —
   working name `PlaceOnGlobe [Globe]` (in: `Camera` or `View`, `Longitude`, `Latitude`, `Height`;
   out: `Transformation`, oriented east-north-up so a model stands on the surface). Measure first how
   VL.Stride patches usually pass a transformation before fixing the name and pins (family rule:
   follow the usual pattern). `HowTo Place a point on the globe` moves to it. Until then
   **GlobeWindow's `Input` pin is not useful**: anything a patch places is drawn relative to an eye
   at the origin.
6. **Then lower the floor.** Default `Min Height` from 100 m to ~20 m, `Max Zoom` 19 (OSM's maximum),
   and look at the text at that height with the user.

**Verify:** a test that a point placed by `PlaceOnGlobe` lands within 1 mm at 20 m; in vvvv, a model
on Tokyo stays put while dragging at Min Height; `HowTo Place a point on the globe` still shows Tokyo
at the centre.

**Open inside this item:** depth precision once tilt exists (far plane grows toward the horizon) —
Stride's depth buffer, reversed-Z, or logarithmic depth; measure before choosing.

## 2. Confirm the gestures — partly done

- ~~wheel units~~: one notch is `Mouse Wheel Delta` 1 (the user's mouse, 2026-11-20).
- drag and wheel: the user judged them "问题不大" / "好多了" after the fixes above — at the 100 m
  floor; at 20 m only after step 6 of item 1.
- **vertical direction**: `Position in Projection` is assumed y-up; nobody has said dragging up moves
  the map the wrong way, but it was never asked. Ask; if inverted, flip the sign in
  `GlobeView.CameraDirectionFromProjection` and add the fact to NOTES.
- Drag and ZoomByWheel log at Information on every event — move to Debug now that item 2 is settled.

## 3. Drag that leaves the globe

Today the camera stops when the cursor is off the sphere. Cesium switches to a trackball above
7,500 km (rate = clamp((|eye| − R)/R, 1/5000, 1.77)); MapLibre 6 bends the ray past the horizon into a
saturating angle from 90 % of the horizon angle. Choose one; test "a drag that crosses the limb
continues smoothly". Note: BringUnderCursor now solves lon/lat by Newton with north up — a ray that
misses the globe must not reach it.

## 4. Tilt and Rotate (proposal §9, phase 2)

`Tilt [Globe]` and `Rotate [Globe]` as separate gestures (Cesium: middle/ctrl-drag). Rotate is now
the only way to change the heading. Tilt needs the pose to look at a point ahead, a tilt limit
(MapLibre 60°), and tilt reduced at altitude. Far plane and depth precision from item 1 meet here;
`TileCoverageFacts` must gain tilted poses (tilt is where far tiles minify and anisotropic filtering
starts to matter).

## 5. The engine's own camera assumptions — partly done

- ~~Screen Height from the window~~: GlobeWindow wires it; a patch using the separate nodes wires
  `Client Bounds → Height → FrameDelay` itself (the help patch shows how).
- `Globe` still assumes a 16:9 aspect for its frustum unless the camera sets one; read the projection
  Stride actually used, as GlobeCamera already does for the gestures.
- Polar caps: the sphere is open above 85.05°. Fill with a coloured cap (MapLibre's fan), in the
  Background colour.

## 6. Tidy what was built under time pressure

- **The compile check cannot see inside a patched definition** (GlobeWindow): teach
  `Compile-HelpPatches.ps1` to decompile `VL.Globe.vl.dll` and run its dropped-link check on
  `GlobeWindow_P` — the first GlobeWindow had its height link silently dropped and only a manual
  decompile found it.
- `tools\Test-VLPatch.ps1` and `HelpPatchGen.ps1` still carry VL.Mapsui's node lists; run
  `tools\Test-VLPackage.ps1` (never run in this repo yet) and fix what it reports.
- `XyzTiles` has no help patch; GlobeWindow has no F1 HelpFocus flag yet.
- Restyle the help patches to the family's help rules (memory: "help patch cells, not hubs",
  "help patch community style", "help patch layout, measured").
- `README.md` and `docs\` screenshots — the `rung4-*` pictures predate camera-relative rendering.

## 7. The course

vl-overworld's `lab\Experiment globe view.vl` (ACT-IV-LAB experiment 7) is still the hand-wired
prototype. Rebuild it on GlobeWindow, with the same two camera poses, and compare with its pictures.
VL.Overworld's nuspec gains VL.Globe only when a chapter uses it.

## Later — phase 2 of the proposal, each waiting for a consumer

`Pick [Globe]` (cursor → longitude/latitude), vector data on the globe (NTS lines on the sphere, or
VL.Mapsui rendering vector tiles into textures), terrain (Terrarium heights as vertex displacement —
vl-overworld's `Prompt How high is here` already decodes them), a graticule, inertia. Not before
items 1–6. Stage 3 of the research (3D Tiles via cesium-native on .NET 10) is a different package.

## Not before

Publishing: the family's rule is the node surface going a week without moving. This session added
GlobeWindow and pins (`Background`, `Fade Duration`, `Smoothing`), and item 1 step 5 adds
`PlaceOnGlobe`, so the clock starts after it.
