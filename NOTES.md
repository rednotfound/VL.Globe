# NOTES — the failure log

Read before composing; append when something dies. Newest last.

## 2026-11-09 — the repository starts

- First help patch opened with the point invisible: the default Stride camera looks at longitude 0,
  and Tokyo (139.7°E) is on the far side. Not a defect, a help-patch design fault: a help patch must
  show its result on open. Fixed with an `OrbitCamera` aimed at the point (yaw −lon/360, pitch
  −lat/360, distance negative — the sign every shipped use has).

## 2026-11-09 — the Globe engine's first rung 4, three failures in order

1. **Every test died in `<Module>.cctor` once the library referenced VL.Stride.Runtime.** Stride's
   build step writes a module initializer into the *test* assembly that loads `Stride.Engine`; the
   library references Stride with `ExcludeAssets="runtime"` (vvvv supplies it), so the test host had
   none. The test project references VL.Stride.Runtime without exclusion, as it does VL.Core.
2. **"camera sees no tile (camera at 0.000 radii)" — an empty globe, every compile green.**
   vvvv's `OrbitCamera` leaves its entity transform at identity and drives the camera through
   `CameraComponent.ViewMatrix` (`Use Custom View Matrix` defaults to true). Reading
   `Transform.WorldMatrix` put the camera at the centre. Fixed by taking the eye from the inverse view
   matrix when the camera uses one. Found in one launch because the node had a `Status` pin that is
   also logged — added for exactly this, after a first launch showed a dark sphere and nothing else.
3. **Dark blotches all over the far view.** Two causes stacked, separated by fixing one at a time:
   (a) coarse facets sagged below the placeholder sphere — 8 segments per tile at every zoom gave a
   zoom-1 facet a sag of 0.019 radii; now `SegmentsFor(zoom) = max(8, 64 >> zoom)`, one 64-cell grid
   for zooms 0..3, sag ≤ 0.0024, a test pins it; (b) depth precision — the help patch's near clip is
   0.0001 so the camera can come down to street level, and at distance 2 a 24-bit depth buffer then
   resolves only ~0.0025, the same order as the 0.005 gap to a sphere at 0.995. The placeholder
   sphere went to 0.97. **Anything a user draws close to the globe's surface will fight the tiles
   for depth when the near plane is that small** — the research doc's precision section, now seen;
   logarithmic depth or a near plane that follows the height is the library's eventual answer.

Measured with the fix: close-up over Gabon (60 tiles, zoom 9..10) 0.27 cores, GPU 14 %, 747 MB;
far view 0.28 cores, 684 MB.

## 2026-11-09 — the geographic camera, and an untested gesture

- **Driven input can land in someone else's window.** `drive-globe.ps1` (scratchpad) sends real
  mouse events to screen coordinates; its "is the renderer on top at this point" check refused once a
  full-screen game (not ours) covered the screen. That check is the only thing standing between a
  test and the user's own applications - keep it in any input-driving tool, and do not drive input
  while the user is using the machine.
- **Unexplained: the first driven wheel (8 notches) and drag (25 % of the width) changed nothing**,
  with the cover check passing. No logging existed then. Candidates, untested: vvvv's `Mouse
  [Stride.Input]` reads only the *focused* window ("only Focused Window works" in its help) and the
  injected focus may not have counted; its `Ray` may be zero for a camera whose projection Stride has
  not computed yet; or the `Mouse Wheel Delta` unit. `Drag` and `ZoomByWheel` now log the press and
  release edges with the ray and the hit, and every wheel event with its delta - the next run says
  which.
- Web-globe numbers used (research 2026-11-09, sources in vl-overworld's GLOBE-SCOPE-PROPOSAL §9
  work): Cesium and MapLibre (v6) grab with axis p0 x p1, angle acos(p0.p1) - as here; MapLibre steps
  0.151 zoom levels per wheel notch, Cesium ~6.5 % of the height plus inertia - hence `Levels Per
  Notch` 0.5; both keep dragging when the cursor leaves the globe (Cesium: trackball above 7,500 km;
  MapLibre: the ray bent past the horizon) - here the camera simply stays, a known gap.

## 2026-11-09 — why drag and wheel did nothing: the Mouse was not in the scene

The user opened the help patch and confirmed it: neither gesture worked. The cause was in the help
patch, not the library. vvvv's `Mouse [Stride.Input]` is itself an entity, and it hears the window
only while it is part of the scene graph: vvvv's own `HowTo Get Mouse Keyboard Touch of Window`
wires `Mouse.Output` (and Keyboard's, Touch's) into `RootScene`. Ours used Ray, Left Pressed and
Mouse Wheel Delta and left Output unwired, so every pin stayed at rest - no error, a working-looking
patch. Found by tracing the shipped help's links, not by guessing at the library. **Any patch that
uses Mouse [Stride.Input] must put its Output into the scene.**

## 2026-11-09 — the real reason drag and wheel did nothing: a dropped link, and a rule we had

The Mouse-in-scene fix was right but not enough. The user's run with `--log` (the GUI launcher now
always passes it) showed Globe's status lines and **not one line from Drag** - not its first-ray
line, not its 300-frame line - so Drag's Update returned before logging: its View was null. The
generated C# said so plainly: `GlobeView View_62 = default(GlobeView); Drag.Update(view: View_62, ...)`.
Cause: Drag and ZoomByWheel had an input AND an output named `View`; the patch generator keys pins
by name, the output overwrote the input, and GlobeCamera's View was linked into Drag's OUTPUT.
**This is a recorded family rule** (vl-overworld PATCH-GRAMMAR, Grow a town: never give an input and
an output the same name) and VL.Mapsui's Drag/ZoomByWheel obey it with Result/Output. Fixed: Drag
outputs Result, ZoomByWheel Output; the generator now refuses a name on both sides; and
Compile-HelpPatches.ps1 fails any local of a Globe type that reads default(...) - proved against the
broken output, where it finds both dropped links. Rung 3 had been run on this patch and the View
argument was not read: the check is in the harness now so it cannot be skipped again.

## 2026-11-09 — vvvv closed itself on the first press: a record struct's ToString

With the View link fixed, the user's run logged Drag's first ray and then vvvv vanished - no
"Stopping" lines, the log cut mid-run. The user's "vvvv just exits" sent us to Windows' Application
event log, which had it twice: **`System.StackOverflowException`** in vvvv.exe (WER CLR20r3, 21:51:49
and 21:55:54). Cause: the press-edge log line formatted the grabbed point, a `D3` - a C# `record
struct`, whose compiler-generated `ToString` prints every public property; `Normalized` is itself a
`D3`, so printing it printed its `Normalized`, without end. The diagnostic logging added to find the
drag bug was the crash. Reproduced first by a test that formats a D3 on a 256 KB thread - the old
code takes the test host down with "Stack overflow" - then fixed with a hand-written ToString.
**Never let a record type expose a property of its own type without overriding ToString** (TileKey
has Parent and already did). And when vvvv "just exits", read the Application event log before
anything else: a stack overflow cannot be caught, logged or flushed by the process itself.

## 2026-11-09 — dragging close to the ground jittered: two float limits, one fixed

The user: at the lowest height a drag shook violently; a little higher it calmed; at country scale it
looked fine - "which makes one think it works". Two causes, estimated at 100 m height on a unit globe
(float spacing near 1.0 is 6e-8 radii = 0.38 m):
1. **The cursor ray (dominant, fixed).** Mouse [Stride.Input]'s world Ray is unprojected in float
   from near/far points next to an eye of magnitude ~1; the near plane 10 m away (0.1 x height) means
   the direction carries ~4 % noise - metres on the ground, tens of pixels, different every frame. Drag
   and ZoomByWheel now take Mouse's **Position in Projection** and build the camera-space direction in
   doubles from the projection scale (M11, M22) GlobeCamera reads from its own CameraComponent. A test
   holds the grabbed point to 1 cm through a 20-frame drag at 100 m. Assumed: projection-space y is up
   (Stride's convention; vvvv's definition could not be traced) - the first-cursor log line will show it.
2. **Rendering precision (residual, open).** The eye and the tile vertices are float positions near
   radius 1, so the whole picture moves in ~0.4 m steps relative to the camera: ~4 px across an 800 px
   view at 100 m, under half a pixel at 1 km. The cure is the standard one - render relative to the
   camera (tile vertices stored relative to their tile centre, tile and camera transforms computed in
   doubles), which also changes how anything else in the scene is placed. A design decision, not a
   patch: until then, Min Height 100 m is where the residual shows.

## 2026-11-20 — camera-relative rendering, and a drag that turned the map

- **A Sphere placed by the patch sat on the camera.** Once GlobeCamera's view became rotation only
  (the eye at the origin), anything positioned in absolute coordinates moved to the camera — the
  help patch's 0.995 background sphere first of all. Globe now draws the background itself
  (`Background` pin), placed at (0 − eye) like its tiles. Everything else a patch puts on the globe
  needs the same treatment: ROADMAP item 1 step 5, `PlaceOnGlobe`.
- **"拖动一下就是会拖歪掉，歪了也很难摆正" — found by the user's hand, not by a test.** BringUnderCursor
  rotated the whole rig about grabbed × cursor; each frame exact, but along a curved path those
  rotations compose into a turn of the heading (a sphere's holonomy): 31° for one circle dragged at
  globe scale, 50° for ten wheel notches toward a corner. The existing `HeadingSurvivesADrag` tested
  one short straight step with 0.1° tolerance and could not see it. Now Heading is held and
  longitude/latitude solved by Gauss-Newton (Cesium constrains to the polar axis, MapLibre's globe
  pan never touches the bearing). Test a gesture along a *path*, not a step.
- **Git Bash `sed -i` rewrote a CRLF file as LF**, and Python's text-mode write on Windows turned an
  LF file into CRLF — both showed as "whole file changed". Edit scripts read and write with
  `newline=''`; check `git ls-files --eol` before committing.
- **The background sphere took three rung-4 rounds, each a different fault behind the same black
  window** (the log said "background shown" every time; only pixel sampling told them apart):
  1. the eye was read inside `Select`, which runs only while Enabled — off, the sphere sat on the
     camera and Stride's back-face culling hid it from inside (every pixel the clear colour, 6,6,6);
  2. with culling off, the camera inside it saw its inner wall: every pixel 0,0,0;
  3. placed right, it drew pure black: an emissive `ComputeColor` never reached the shader. A 1×1
     sRGB texture through `ComputeTextureColor`, the tiles' own path, works.
- **Colours come out darker than given** — background 0.04, 0.1, 0.22 (10, 26, 56) shows as 0, 7, 50;
  OSM's land #f2efe9 showed as 220, 219, 217 in the 11-09 captures. Tiles and background share it, so
  they match; the colour-space handling of the whole engine is still to be looked at.
- vvvv sometimes sits in "Shutting Down" for over a minute after WM_CLOSE; wait, never Stop-Process.

## 2026-11-20 — GlobeWindow: a link dropped inside a patched definition, invisible to every check

- `GlobeWindow` (patched in `VL.Globe.vl`, like VL.Mapsui's MapWindow) wires SceneWindow's
  `Client Bounds` → `Height` → `FrameDelay` → Globe's `Screen Height`. The first version used the
  `Height` of **`Stride.Core.Mathematics.Rectangle`** (copied from VL.ImGui.Stride, where it reads a
  RenderWindow); SceneWindow's `Client Bounds` is VL's **`2D.Rectangle`** (RectangleF). The compiler
  dropped the node without a word: `ClientBounds(out var _); int value2 = 0; SetValue(value2)` —
  Globe would have been told the window is 0 px tall. vvvvc PASS, no red node, the window drew.
- **The help-patch compile check cannot see inside a patched definition**: the package's own `.vl`
  compiles to `VL.Globe.vl.dll` with no source kept. Found by decompiling it (`ilspycmd`, installed
  with `--tool-path` into the scratchpad) and reading the Update body. Do this for every patched
  definition until the check does it itself.
- Screen Height became `float` — the window's height is a float in VL.
- **"糊糊的" — the user, 2026-11-20: map and text blurred and dull, "像是有什么叠加模式".** SceneWindow's
  `Enable Default Post Effects` defaults to true: tone mapping and anti-aliasing over the whole
  frame. Measured on the background sphere, no network: 0.04, 0.1, 0.22 (10, 26, 56) showed as
  0, 7, 50 with them, 10, 25, 56 without; the clear colour 6 → 24. This was the "colours come out
  darker" above, not the engine's colour space. Map imagery is finished pixels: both GlobeWindow and
  the help patch's SceneWindow switch the post effects off.
- `Put OpenStreetMap on a globe` now wires `Client Bounds → Height → FrameDelay → Screen Height`
  itself, the way any patch using the separate nodes must; read back from the generated C#.
- **Half the view sharp, half stretched — the user over Central Asia (screenshot), log "25 wanted, zoom
  2..4, 0 loading": not a loading fault, the selector's.** A tile's texel width was taken at the
  tile's middle latitude; tile 3/6/2 spans 66–41 N, judged by cos 56° while its near edge at 41° is
  1.34× wider, so it was drawn at 2.55 px per texel beside zoom-5 neighbours at ~1. Now the widest
  row decides. `TileCoverageFacts` checks every sampled pixel of the screen, not only the one below
  the camera — the test that existed (`TileScaleFacts`) looked straight down and could not see it.
- **The wheel: one notch is `Mouse Wheel Delta` 1** (read from ZoomByWheel's log, the user's mouse,
  2026-11-20 — ROADMAP item 2's open question, settled). The zoom felt like jumps ("滚一个就拉近了很多"):
  0.5 level per notch in one frame. Now 0.25 level, played out over `Smoothing` (0.15 s, exponential,
  frame-rate independent — `WheelFacts`); pending zoom is dropped at Min/Max Height.
- **The seam stayed after the widest-row fix — and it was within the rule.** Over northern China
  (3570 km) zoom 4 read 1.23–1.84 px per texel and zoom 5 0.68–1.09: both inside "refine while
  > 2", 1.7× apart at one latitude line. Researched (MapLibre `covering_tiles.ts`,
  `raster_tile_source.ts` `roundZoom = true`; Cesium `ImageryLayer.getLevelWithMaximumTexelSpacing`
  `Math.round`): both ROUND the imagery level, 0.71–1.41×, and both mipmap. Neither blends a static
  one-level seam; MapLibre crossfades parent↔child over 300 ms while zooming. Now: Screen Space Error
  1.41, CPU mip chain averaged in linear light (`MipChain`), trilinear + clamp. Same view after: zoom
  4 only at the edge, ≤ 1.44.
- **Stripes on a fast drag, "like a frame out of sync" (the user) — tearing.** SceneWindow's
  `Present Interval` is its own operation (`SetPresentInterval`) with no default, so an unwired pin
  ran `Immediate`: no VSync. GlobeWindow and the help patch set `One`; read back from the generated
  and decompiled code (`SetPresentInterval(... "One")`), not yet confirmed on screen.
- Fades both ways (MapLibre's raster fade): in, a finer tile mixes from its ancestor's part; out,
  the drawn descendants mix to the parent's parts before the parent replaces them. Fade weights are
  keyed `ComputeFloat4`s set per frame — the same family of node as the `ComputeColor` that once drew
  black; watch for black flashes until the user has seen it.
- **Every fade dipped dark ("所有的渐变都是建立在深色底上面").** Logged the first fade material's
  parameters: both textures bound, ancestor scale 0.5 / offset right, weights 1 and 0 — all correct.
  The fault was the operator: `BinaryOperator.Add` is **`ComputeColorAdd3ds`**, a 3ds Max blend that
  weighs each side by its alpha (`BlendUtils.BasicBlend`), not `ComputeColorAdd`. Weights
  `(f,f,f,f)` gave a half-way fade half the light. Weights are now `(f,f,f,1)`; with both alphas 1
  the 3ds formula is a plain sum. Read the operator's shader before trusting its name.

## 2026-11-21 — a fast zoom out showed the coarsest tiles, white and blurred

- **"最root的那层tiles会跑出来，然后画面变白，变糊变花" (the user).** The log: `600 loaded` — eviction
  was running. Least-recently-used evicted the coarse levels (zoom 2–6) while the camera sat at
  zoom 8–9; a fast zoom out then wanted zoom 4–6 tiles that were gone, and `DrawSet.Resolve` only
  climbed **up** for a stand-in — to the zoom-2 fallback, re-requested first. The zoom 7–9 tiles just
  drawn were still loaded and unused.
- Now a missing wanted tile is first stood in for by its loaded descendants, up to 3 levels down,
  when they cover it entirely (MapLibre's `_retainLoadedChildren`); only then an ancestor. Zoom ≤ 3
  (≤ 85 tiles) is never evicted. Four `DrawSetFacts` reproduce it; the two that failed before pass.

## 2026-11-21 — placing your own things: a node that needed the camera made a cycle

- `PlaceOnGlobe` was built as a VL-shaped transformation node (Input → Output, plus Camera). With
  the separate nodes it is fine. In GlobeWindow the camera comes out of the node that the placed
  entities must go into — a cycle only FrameDelay breaks, one frame late, i.e. jitter while
  dragging. Replaced before anyone wired it by **`GlobePositionComponent`**: double lon/lat/height on
  the entity; its processor sets a Stride `TransformLink`, asked in `TransformProcessor.Draw` (after
  the patch), which finds GlobeCamera's `GlobeCameraFrame` in the scene. Seen in vvvv: Stride did
  attach our `[DefaultEntityComponentProcessor]` to a component made in a VL patch, and the ball's
  67 px is the size the maths says.
- VL.Stride passes one component into `Components` through **`FromValue (Spread)`** — 18 of 20 shipped
  help patches do.
- An unwired `Matrix` pin of a C# node is all zeros, not identity; the placement treats zero as
  identity.

## 2026-11-21 — still a white flash zooming out: one missing tile hid every loaded one under its ancestor

- The user, after the descendant stand-ins: "缩小过程中的闪现". Not a load wait (the log showed 0 loading
  throughout) but the single-layer rule: a missing tile's stand-in ancestor hid **every loaded tile
  under it** until all arrived. Zooming out, a tile not yet loaded at the edge of the view pulled a
  zoom-3 tile (kept from eviction since the morning) over half the screen. Now two layers
  (`DrawSet.Layers`): Top = wanted tiles that arrived plus any loaded descendants of missing ones;
  Under = the nearest arrived ancestor, drawn **sunk** (0.2 % of the camera height, 0.5 m–2 km,
  coarser deeper) so Top wins the depth test and the ancestor shows only through holes — MapLibre
  draws the parent under its children. A probe at 20,000 km also showed OSM's own low-zoom palette
  (land #f2efe9) is near-white: not this bug.

## 2026-11-21 — tilt and turn

- The camera state became MapLibre's (centre point, distance, heading, tilt) so that turning and
  tilting pivot on the centre. Chosen so that tilt 0 is the old state exactly: all 105 earlier tests
  passed unchanged before any tilt test was written — the check that the refactor changed nothing.
- `RotateRig` (the drag's first guess) rotated `-forward` and `up`, the rig's axes, which are the
  centre point and heading direction only at tilt 0; it now rotates those two directly.
- A help patch's node must name `LastDependency="VL.Globe.vl"`: written as `VL.Globe.dll` (copied from
  inside GlobeWindow) DragRotate compiled but the compile check did not list it, so did not check it.

## 2026-11-21 — loading under tilt, checked before the user saw a problem

- The user: "现在我们的camera允许有斜视了，所以也请确保加载不出现问题". Eight tilted poses joined
  `TileCoverageFacts`: no hole, ≤ 179 tiles. Off-axis pixels read up to 1.6× where the selector aims for
  1.41×: it converts angle to pixels at the view's centre, and a radian covers 1/cos² more toward a
  corner — cancelled by distance when looking down, not when tilted.
- Three faults found by reading the code with tilt in mind, none yet seen on screen: Globe's frustum
  assumed 16:9 (now the aspect from Stride's projection); requests went in the walk's order (now
  nearest first, so the ground ahead sharpens before the horizon); stand-ins sank by the camera's
  height — 0.5 m at 20 m, below the depth buffer's resolution 10 km away (now 0.3 % of each tile's
  distance).

## 2026-11-21 — sunk stand-ins showed as tiles dropping and black cracks; the turn ran backwards

- **"加载的一瞬间…tiles和tiles之间的那个黑框…那个地砖往下下降了一点，然后抬起来" (the user, tilted).** The
  Under layer worked by sinking the stand-in ancestor (0.3 % of its distance): under tilt the sink
  was seen as a drop, and the step between a surface tile and a sunk one had no wall, so the eye saw
  through the crack to the dark background sphere — black edges zooming in; white frames zooming out
  were the same layer. Replaced by **stand-in squares**: each uncovered square drawn with its own mesh
  and the matching part of an arrived ancestor's image (uv × scale + offset, as the fades sample) —
  every drawn thing at the surface, none overlapping (`StandInsNeverOverlapTheTopTilesOrEachOther`).
  When the real tile arrives it fades from exactly the image the square was borrowing.
- **The right-drag turn was backwards.** Researched (MapLibre linear mode, Mapbox, deck.gl, Cesium):
  every one raises the heading on a drag right — the view turns to look right. Ours lowered it, "the
  map turns with the cursor", which is true only above the pivot; a tilted view is mostly what lies
  above the pivot, so the horizon ran against the hand ("体感上面是反的"). Sign flipped, pinned by
  `DraggingRightTurnsTheViewToLookRight`. Speed stays 180° per screen width (deck.gl's constant);
  drag up still tilts toward the horizon (MapLibre/Mapbox/deck.gl; only Google Earth is the reverse).

## 2026-11-21 — vectors: draped, decided; a vector globe is the future, maybe a separate node

- Experiment A (SkiaSharp into per-tile overlays) was seen and judged ("问题不大"); B (geometry) was not
  built — several times A's cost. The user then named the real goal, MapLibre's GPU vector globe ("那是
  未来"), and settled the architecture question in advance: one node if raster and vector can share
  the engine, separate nodes if not. Survey: docs/RESEARCH-VECTOR.md — no .NET GPU vector renderer
  exists; MVT via NTS.IO.VectorTiles; no .NET style-spec port; symbol placement must be ported from GL
  JS; MapLibre Native's FFI is pre-1.0 with no released globe.
- A replaced overlay texture can still be sampled by a fade or a stand-in that borrowed it; disposing
  it at once would leave a material pointing at a freed texture. Retired textures wait 60 frames.
- Mapsui's `VectorStyle` takes `Color4?` — a nullable pin is how a C# node gets a colour default (C#
  cannot write a Color4 default value); used for ours.
- A help patch referencing `Geometry` names its category (`Globe` > `Layers`) in the NodeReference:
  VL.NetTopologySuite also has things called Geometry.

## 2026-11-21 — 4b: MapLibre's render tests

- **pixelmatch 8 is not the pixelmatch everyone quotes.** v8.0.0 (what GL JS v6.19.0 locks) measures
  OKLab HyAB distance, not YIQ; the `threshold` 0.1285 means something else under it. Ported from
  the locked version, its lookup tables included (they change results near black), and checked
  against its own test counts before trusting any render number.
- **render.test.ts un-premultiplies by dividing into a Uint8Array**, which truncates. The PNGs store
  that; RenderStraight does the same so equal renderings give equal bytes.
- **Skia's stroker drew translucent lines 7 % wrong.** It unions a path's outline and blends it once;
  GL JS's triangles blend each on their own, so where a line turns back on itself, at a bevel or
  flipbevel join and between a round join's two half discs the colour doubles (the `*-transparent`
  join fixtures, line-opacity). Ported `line_bucket.ts`'s join logic and draw with `DrawVertices`.
  Hard-edged triangles then failed two 2 px diagonal lines by 0.2 %: GL JS's shader ramps alpha over
  one device pixel at the edge; reproduced exactly by cutting triangles where the ramp bends.
- **A pass can prove nothing.** `fill-outline-color/default` expects an empty image; an empty render
  passes it. The harness compares an empty image too and leaves such passes out of `passedAtLeast`.

## 2026-11-21 — 4c step 1: vector tiles in the painter

- **MapLibre's look includes its storage formats.** Line extrusions are `Math.round(63 x) + 128` in
  a Uint8Array, which wraps: a miter longer than about twice the width folds back, and the
  `line-miter-limit` fixtures expect the fold. GeoJSON goes through geojson-vt, which rounds to 8192
  steps per tile; without that rounding a bevel's edge landed one pixel row off. Both reproduced.
- **`if (this.minzoom && zoom < this.minzoom)`**: an unset minzoom (0) hides nothing, even at zoom
  −1. Testing `zoom < 0` dropped every layer, the background included, at negative zoom.
- **A tile cache keyed by URL** put `outside_ring.mvt` (a template with no `{z}/{x}/{y}`) into the
  first tile's square for every tile. Keyed by URL and tile.
- **GL fills are aliased**; their edge is a separate `gl.LINES` outline, one pixel per step with
  alpha `1 − smoothstep(0, 1, d)`. Skia's antialiased fill made tiny islands and lakes lighter
  (0.19 % at zoom −1); reproducing both brought it to 0.
- **A crash in one fixture ended the run** (an invalid inline GeoJSON). The harness records a
  throw as that fixture's failure; invalid GeoJSON now draws nothing, as in GL JS.
- **NTS has its own `GeometryOverlay`** (NetTopologySuite.Geometries): an overlay record of that name
  in VL.Globe.Engine was ambiguous in GlobeNode. Ours is `ShapesOverlay`.
- **Ignoring a property can draw a different layer.** A lenient parse that only drops `fill-pattern`
  draws the layer in the default black; one that drops `line-gap-width` draws a road casing as a
  solid line. Those properties leave the whole layer out.
- **Stand-ins fetched 31 MB for one view.** Every shown tile rendered its overlay, including the
  ancestors the globe shows while the raster tiles load; each asked for its low-zoom vector tile
  (z5-z10 tiles of the planet are up to 1.3 MB). 118 tiles for a street-level view at Tokyo
  Station. Only this frame's wanted tiles fetch now; the others draw from memory/disk or not at all.
- **"0 fetched" with a full map**: vvvv built the node twice on open; the first instance fetched,
  the second read the disk cache, which Status did not count. It now says `from disk` too, and
  which zoom levels were decoded.
