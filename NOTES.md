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
- **Vector tiles needed the raster** ("这个事情非常奇怪！", the user): a globe tile existed only once its
  raster image had arrived, and the overlay lies on the tile. No Tile Source, no tile, no vector.
  Plain tiles in the background colour are built when there are layers and no source.
- **631 ms an overlay.** A z14 vector tile covers 16 z16 globe tiles; every overlay filtered and
  triangulated all of it before the canvas clip threw 15/16 away. Measured by layer and by stage
  (expressions 13, triangulation 9, band cutting 26, Skia 13 ms after the fixes): four minor-road
  layers were two thirds; round caps had 16 slices regardless of radius.
- **SkSL runtime shaders crash SkiaSharp 2.88 on the CPU raster backend** (SEHException in
  sk_canvas_draw_rect, the process gone): the GL fragment shader's edge ramp cannot move into a
  Skia shader in vvvv 7.4. The band cutting on the CPU stays.

## 2026-11-21 — 4d: the GPU path's first faults, seen by the user

- **Discs of background in the sea, then a chequerboard before that.** A vertex lifted towards the
  eye by 0.0015 of its distance stays over the globe's tiles only if every chord between vertices
  sags less than that *at the distance that zoom is seen from*. Cutting edges by an absolute 2
  degrees fixed the chequerboard (zoom 1, 8 background cells of 22 degrees) and left zoom 8's ocean
  squares as two flat triangles 1 km deep, seen from 300 km: discs. Edges are now an eighth of a
  tile up to zoom 10, as the globe's own tiles are cut. **A sag bound must be relative to the view
  distance of the zoom, not absolute.**
- **Dotted cracks along tile borders:** two tiles clipped to one edge in the pixel shader each
  discarded some of its pixels (their uv interpolated in float). Clip rectangles overlap by 2e-4,
  and the background grid reaches past its square.
- **The globe shook when the pole was dragged into view** (camera, older than the GPU path): the
  rigid rotation, used as the first guess of every drag frame, crossed the pole once the camera's
  latitude was at its 89.9 limit, and the refinement found the point under the cursor again 180
  degrees round. Frames alternated between the two sides. Not damped: the solve now starts from
  the camera's own pose, keeps the answer on this side of the pole, and at the limit fits only the
  longitude, so the camera stops. `DraggingThePoleIntoViewStopsAtItWithoutShaking`. The user,
  mid-fix: "请用合理的方式来解决，而不是一味设延时" — fix the solve, never damp the symptom.
- **White dashed outlines, then jagged ones.** Hardware line antialiasing (D3D's
  AntialiasedLineEnable, Stride's `MultisampleAntiAliasLine`) multiplies coverage into alpha only.
  With premultiplied blending (Stride's `BlendStates.AlphaBlend`) the colour is not weighed by it and
  the edge comes out lighter than both sides. Off, a 1-px line is a staircase. Both seen by the user.
  Now the line list blends `NonPremultiplied` and the shader writes straight colour for it.
- **"偶尔在运动加载的时候会卡一下" — measured before fixing.** The node logs a `Globe perf` line a
  second. After MapLibre's worker count (half the cores, ≤ 6, threads below normal priority),
  8 MB uploads a frame, pooled bucket arrays and line-list outlines: frames ≤ 27 ms while 80 tiles
  and 170 MB arrived in one second. A 580 ms Update with a gen-2 collection in the same second was
  *not* the GC: two later ones (200, 330 ms) had 2-3 ms of GC pause, all inside GpuVectorTiles.Update
  — its stages are logged now (`gpu vectors: finish+upload …`). Measure the pause
  (`GC.GetTotalPauseDuration`), do not infer it from a collection count.
- **Found by the stages: "draw list 214 ms", 2 ms of GC pause, a gen-2 collection in that second —
  as in every long Update.** The draw list was a new `List<Item>` each frame: thousands of ~140-byte
  items, a large-object-heap array dropped 60 times a second. That both fed gen-2 collections and,
  allocating while a background one ran, waited for it — a wait `GetTotalPauseDuration` does not
  count. A probe first ruled out the guess (a layer's expression throwing for the first time: none
  of Liberty's 82 layers throws at any zoom, all evaluate in 0.3 ms). Two frames are reused in turn
  now. **Nothing above 85 KB may be allocated per frame.**
- **3D buildings showed their insides** ("所有的建筑都看到了其内部，是透明的", the user). Not the
  normals: the depth pass wrote nothing. vvvv's `VLForwardRenderer.BindDepthAsResourceDuringTransparentRendering`
  (true by default) binds the scene's depth READ-ONLY in the transparent stage, where the
  EntityRendererComponent draws; writes to it are dropped without a word. An extrusion layer now
  draws on a depth buffer of our own, a copy of the scene's (so the globe still hides what is
  behind it), with the scene's colour targets, and the scene's depth is bound back after.

## 2026-11-22 — the GPU path under MapLibre's render tests

- **A test can compile and run GlobeVector.sdsl with no vvvv**: `GraphicsDevice.New` headless, an
  `EffectCompiler` over a folder holding GlobeVector + ShaderBase + ShaderBaseStream (from
  `~\.nuget\packages\stride.graphics\4.2.1.2487\stride\Assets`), `UseFileSystem = true` with the
  folder's ABSOLUTE path in `SourceDirectories` (a relative one: "not in the module"), and the mixin
  given a `Name` (null: a NullReferenceException building the log file name).
- First run 84 / 76; then 94 (GL's fill outline), 96 (clip by the pixel), 97 (the tie rule).
- **Two guesses the fixtures refuted, each reverted**: 16-bit extrusion precision (the fix changed
  nothing; GL's bytes were kept as the right format anyway), and a 1/1024-px nudge (below the float
  error of the transform chain). The pixel dump (`results-gpu`, a 40-line PNG reader) settled both:
  a whole row on exactly horizontal edges only, top one way and bottom the other - a tie rule.
- **Stride vertex elements must be 4-byte multiples** ("VertexElementOffsetNotMultipleFour" from the
  VectorVertex type initializer).

## 2026-11-22 — labels

- **A Stride stage VSMain must not `return;` early**: it becomes an HLSL function with a return value
  (X3080). Hide a vertex with a flag and one assignment of ShadingPosition at the end. Found by the
  test host's compile before vvvv ever saw the shader - compile shaders in the tests.
- **Liberty writes every name with `concat`**: without it every label layer was dropped (lenient) in
  silence on the globe while the fixtures passed. Real styles need their own check beside the fixtures.
- **Strictly parsing a new layer type changes both harnesses**: symbol layers read made the drape's and
  the GPU harness's text fixtures "failed" instead of "unsupported" until each renderer said what it
  cannot draw. A subagent saw the two VectorTilesFacts it broke and reported them instead of
  "fixing" them.
- **Placement skipped every tile without point labels** - so line labels were never placed: each
  drew as a heap of glyphs on its anchor (the fixtures' actual.png showed it at once).
- **geojson-vt cuts GeoJSON before GL JS places labels**: a line-center label sits at the middle of
  the tile's piece (128 px of buffer), not of the whole line - two labels, not one, in
  text-max-width/force-newline-line-center. Found by comparing dark-pixel columns, not by the diff.
- **An unsupported feature found while drawing stops everything**: the renderer treats any exception
  as a broken shader. Refuse while building, where the lenient parse drops one layer.
- **A stand-in tile drawn once per square draws its labels once per square**: the symbol shader does
  not clip, and the copies collided with themselves. Labels come only from tiles drawn whole.

## 2026-11-22 — icons (milestone 4)

- **`SKBitmap.Decode` premultiplies, and `CopyTo` an unpremultiplied bitmap keeps the premultiplied
  bytes**: premultiplied again on the way to the atlas, every soft icon edge came out dark (0.7 % of
  the pixels in a dozen fixtures). Decode with `SKCodec` straight into `Rgba8888` / `Unpremul`;
  `IconFacts` compares against Skia's own premultiplied decode.
- **Symbols that may overlap are drawn by their anchor's y** (GL JS `sortFeatures`: symbol-z-order
  auto, any `*-allow-overlap` or `*-ignore-placement`; ties by feature index, higher first). In
  source order, a cluster of overlapping POI icons showed every circle's rim.
- **A legacy function's default type is `exponential` for arrays too** (`icon-offset` stops of
  `[x, y]`): evaluated as `Kind.Any` it fell to `interval` and the offset jumped. `Kind.NumberArray`.
- **A lenient style must not lose a layer for an icon it cannot draw yet**: refusing an icon along a
  line drops the layer whole, and Liberty's road-name layers (shields along the line) would have lost
  their text - caught reading the code before vvvv, not seen. Strict throws; lenient leaves out the
  icons and names it in Skipped.
- **Most icon-text-fit fixtures need a circle layer** (it marks the anchor): the stretch-zone code
  passed nothing until circles were drawn. Read why a group is unsupported before calling it tested.
- **GL JS lays labels out in CSS pixels**: offsets and the collision grid were in device pixels, so
  the 2x-screen fixtures drew labels at half size. `Frame.PixelRatio` (1 in vvvv).
- **The test host copies the shaders it compiles by name**: a new `.sdsl` must be added to the list
  in `GpuFixtureRenderer.Create`, or every fixture fails with "E1202 ... not in the module".
- **Ported code drifts from GL JS's newer versions**: our pole of inaccessibility was the old one; v6.19
  prefers the centroid within the precision ("Trap" 6 px off). When a port fails by a few pixels,
  diff the current upstream file before suspecting the inputs.
- **Line-placed icons sample linear in GL JS**: pitch-alignment auto follows the map there, which
  counts as "transformed" - NEAREST drew the rotated arrows jagged.
- **Symbols past the source's maxzoom are laid out at the overscaled zoom in GL JS** (one bucket for
  the maxzoom tile, spacing in the shown pixels). Built as a separate symbol entry per overscale now.
  Two hypotheses for the remaining half-spacing offset on some lines were refuted by the fixtures
  (integer line coordinates; and overscaling 1 in getAnchors, which came closer but contradicts GL
  JS's own unit tests) - not kept.
- **"现在的帧率就只有30多" (the user, tilted over Kyoto)**: Update was 3 ms; the time was in the render
  thread's Draw, which no perf line measured - the perf line now carries the Draw's timings. Label
  placement every frame cost 2-7 ms (25 max) and a scan of every label per symbol layer. Placed now as
  GL JS: on a change of view at most every 300 ms, once more at rest, at once for new tiles; between,
  only the shown glyphs along lines follow the camera. Measured again with the user tilting: placement
  0.2-2.9 ms mean, 60 fps while no tiles arrive. What is left while moving is tile loading (50-150 MB/s
  uploaded, Update 15-30 ms) and 1500-2400 draws (3-4 ms). Open: a one-off 170-190 ms "draw list" in
  the Update the first time a tilt brings ~400 label tiles - not yet timed finer.
- **The one-off hitch was the atlases, not the draw list** (timed finer, the user tilting at Kyoto):
  "atlases 150", "atlases 153" in two Updates 0.7 s apart, GC pause 5 ms counted. My guess - the
  item list growing on the large-object heap - was refuted by the same lines (it grew 0 -> 4). Every
  glyph added bumped the atlas's Version, and each new version cloned the whole image (megabytes, a
  large-object-heap array) and made a new texture from it. Now a texture is made again only when the
  atlas grows taller, from the atlas's own array under its lock; the rows placed since go into the
  same texture in the Draw (`AtlasTexture`, `SetData` with a region). Test: `AtlasTextureFacts`, red
  with the upload skipped. Not yet told apart: the clone or the texture creation - both are gone.
  Seen again in vvvv the same day, the user tilting at Kyoto: no long Update after the first two
  seconds of loading (atlases 0 and 2 ms there). What is left while tiles load: frames 40-80 ms with
  Update 20-30 ms (single tiles of 20-30 MB uploaded in one frame), label placement up to 38 ms, submit
  up to 22 ms.
- **What a street-level tile is made of** (`TileSizeProbe`, by hand: `VLGLOBE_PROBE=1`): Liberty's
  z14 tile at Kyoto Station was 9.1 MB on the GPU - 3D buildings 5.1, the flat `building` fill and
  outline 2.3, every road twice (casing and line, the same geometry) ~0.8. The flat building has
  maxzoom 14: never drawn from a z14 tile, built and uploaded anyway. GL JS's worker skips a layer at
  `zoom >= maxzoom`; so now do we: 6.7 MB, built in 194 ms instead of 251. Its `zoom < floor(minzoom)`
  half is deliberately not followed - the far z13 tiles of a tilted view keep their 3D buildings (4f).
- **"道路名字，或者是铁路电车名字一顿一顿" while dragging very close (the user)**: the renderer took
  "the camera moved" from `view * projection` alone. Camera-relative, the view is a rotation only and
  the pan lives in the world matrices: centimetres a frame at street level often left the rotation bit
  for bit the same, so `UpdateLines` was skipped and the glyphs along lines stood still while the roads
  under them moved, catching up when the rotation's float next changed. The frame carries the eye now
  and it counts as a move (`VectorRenderer.NextLabelStep`, `LabelStepFacts` - red without the eye).
  Anything else that keys on the view alone has the same flaw.
  Seen by the user in vvvv the same day: "顺多了".
- Labels above the 3D buildings, seen through them: Liberty's order (building-3d is layer 84, opacity
  0.8; road names from 95) - as MapLibre draws it, not a fault.
- **Placement's 20-38 ms while tiles came, measured offline** (`PlacementProbe`, VLGLOBE_PROBE=1:
  Liberty, 25 z14 tiles round Kyoto Station at overscale 4, z16 pitched 60 degrees, 500 label tiles):
  a whole placement 11.5 ms, `UpdateLines` 0.4. Lines cost 4 us each (points 0.3): every glyph of
  every candidate was walked along its line, hidden or not. GL JS's collision walks only the first and
  last glyph (placeFirstAndLastGlyph) - the others lie on the segments those two walked - and every
  glyph only for what is shown. So now: 6.7 ms. GL JS's other remedy, a placement paused after 2 ms a
  frame (PauseablePlacement), it uses only with fades: at fadeDuration 0 it forces a full placement,
  so newly loaded tiles show their labels in their first frame. It comes with the fades.
  Seen in vvvv the same day (the user tilting at Kyoto, ~900 label tiles): at rest the slowest
  placement a second is 6-9 ms; while tiles load still 17-50 ms, as before. Offline is warm, 500
  label tiles, no occlusion test, no six workers allocating beside it - which of these it is was not
  measured. The 2-ms-a-frame placement bounds it whatever it is.
- Open: one frame of 550 ms at rest (13:20:20) with Update 10 ms and Draw ~1.5 ms - outside both.

- **4f step 1, the tilt limited by the zoom** (`GlobeView.TiltLimit`, Google's 30 to z10, 45 at z14,
  60 at z15, 67.5 from z15.5; MaxTilt the ceiling; zooming out brings the tilt down). Tests that
  study the geometry of any tilt switch it off (`TiltByZoomLimit`). Found by it, not caused: at
  -70/-45, 300 km, tilt 30 (reachable before and after) two corner pixels get a z8 tile at 1.76-1.85
  px per texel, over the 1.6 bound - the tile selector's corner shortfall (TileCoverageFacts' 1.6
  comment) is larger than measured there. Open.

- **The fog did nothing for the cut the user saw** (2026-11-22 evening, Tokyo tilted 57 degrees,
  2.3 km up, by the view line): the 3D buildings stopped in a straight line 6 km off. Not missing
  data in the far tiles, not a draw bug: the z13 tiles there were built and drawn with their
  building-3d - but OpenMapTiles merges z13's buildings into one feature without `render_height`, so
  they were 0 m tall. The fog started at 3 eye heights, whole at 16; the line was at 2.5. Found by
  drawing the user's view offscreen (`ViewProbe`, the whole GPU path, VLGLOBE_PROBE=1) and listing
  the draws by pass and zoom; a reading of the code had found nothing. Fixed by 4f step 3 (ROADMAP).

- **A yardstick that did not move (2026-11-22 night, ROADMAP 4g).** GL JS's bench page read 60 fps,
  0 frames over 20 ms, through two 10-s pans - and the map had stayed where it began: `map.panBy([dx,
  0], {animate: false})` called every frame did not move it in 6.19.0 (why not found). The page's own
  "moving" flag said moving (`move` events fired). Caught only by setting its last screenshot beside
  vvvv's after the same bench: 13 km apart. Now the page makes the point right of the centre the
  centre (`jumpTo`), as `CameraBench` drags, and both log the centre at each pan's edges and the
  distance (6.58 vs 6.59 km). A benchmark proves its motion before its numbers.

- **Halving the bytes did not halve the misses (2026-11-23, ROADMAP 4g step 2).** The misses sat in
  the seconds that uploaded tiles, so the bytes were taken for the cause; tiles went to half their size
  (16-byte vertex, shared casings, 16-bit indices) and the bench missed more frames (5-7% against
  3.5-4.2%), because each draw now set two more parameters. A correlation named the second, not the
  cost. Measure the frame that misses, by its parts, before choosing what to make smaller.
- **A parameter set by `"prefix." + name`** allocated and hashed a string per call: 12 a draw, 3000
  draws a frame. Resolve a key once.

- **The missed frames were the workers' garbage (2026-11-23, ROADMAP 4g step 4).** Taken apart frame
  by frame (`Globe frame:`), the misses were gen0 collections - 3.6 ms each, several a frame while
  tiles came - and the workers building tiles allocated 90-955 MB a second: 200 MB for one Tokyo z14
  tile, most of it the style expressions interpreted from JSON per feature. In .NET every thread's
  garbage stops every thread, the renderer's too; GL JS's workers have heaps of their own. A larger
  gen0 does not help: 1/8 of the collections, each 4-8 times as long - the pause follows what
  survives. Two earlier readings were wrong: "the bytes uploaded" (4g step 2) and "Update and draw"
  (they were only where the pause happened to land).
- **Fewer collections did not shorten the pauses; fewer survivors did (2026-11-23, 4g step 4).**
  Compiling the style expressions cut a tile's garbage by half and the misses not at all: the
  collections came less often and each took longer. Logging what each collection promoted showed
  why - the pause is ~1.5 ms per promoted MB - and promoted bytes followed the tiles decoded (one
  decoded tile, 8.6 MB of small objects, per tile; twice that with the decoded cache off, the cache
  having been the first suspect). Decoded tiles made of a few arrays halved what was promoted and
  the misses fell to 0.8-1.6%. In .NET, an object that outlives one gen0 interval (~50 ms here) is
  what costs; what dies young is nearly free.
- **Tile buffers are made on the workers (2026-11-23, 4g step 4).** With GC no longer the cause, a
  tile's upload in Update (5-8 ms) was in most missed frames. D3D11 devices are free-threaded unless
  created `SingleThreaded`; Stride never sets it, the host does - vvvv 7.4's flags read `None` at run
  time (`Globe device:` log line). Stride 4.2.1's `Buffer.New` with data touches only the device; its
  resource set is locked. Not `SetData`: that needs the render thread's command list. A job dropped
  while running must release its buffers (`Drop`'s continuation) - the collector does not.
- **Git Bash `sed -i` turned GlobeNode.cs (CRLF) into LF again** the same day - CLAUDE.md warns of
  it. Edit CRLF files only by byte-preserving scripts.
- **Stride's `ParameterCollection.Set(key, value)` was 4 of a frame's 5.5 ms of submit (2026-11-23).**
  It finds the key in the layout on every call; 3000 draws x a dozen parameters. Set through an
  accessor (`GetAccessor` once, then `Set(accessor, value)`) - fetched again each frame, before the
  first set, since a recompiled effect lays its parameters out anew. Submit ~2 ms. Draws themselves
  (DrawIndexed 0.1 ms, Apply 0.5 ms for 3000) were never the cost; measure before batching.
- **"No city or region names" (the user, 2026-11-23): the place labels were there; the borders were not.**
  Liberty's place layers (label_city, label_town, label_state, ...) were built and drawn - offscreen at
  #8/34.9/135.6 and #11/35.0/135.76 every city, ward and prefecture name shows (`PlaceLabelProbe`,
  `ViewProbe`). What was missing: `boundary_3` (prefecture and state borders), dropped in a lenient
  parse for `typeof` in its filter. `typeof` added (GL JS's typeOf and its array<item, N> names);
  borders drawn, solid - line-dasharray is not done. Where the user looked for names: to be asked.
- **A pan bench does not test zooming (2026-11-23).** At GL JS's level on a pan (0.0-0.1%), the user's
  own session showed label-index spikes while zooming; a zoom bench gave 4% where GL JS gave 0.2-0.4%.
  Zooming builds tens of tiles a second; what survives follows the tiles built. Bench each motion.
- I guessed once more without measuring (the one-way arrows' line arrays: 0.2 MB of 13.8) - the
  AllocMeter lap on the branch then named the real part in one run. Measure the stage first.
- **"The city names went away from some time on" (the user, 2026-11-23): one label dropped the layer.**
  A lenient style drops a layer for good the first time any tile's build throws "unsupported" in it -
  and logged nothing. Replaying the user's 47 views in order (`ReplayProbe`, the hashes from their log)
  found it at the 20th: zoomed out to z5 and tilted, a place name in a script written without spaces
  (Thai, Burmese ...) needed a line break, Shaping refuses those (no dictionary segmenter in .NET), and
  label_city, label_city_capital, label_state, label_country_2 were dropped for the rest of the session.
  The same mechanism had always dropped Liberty's road shields: `["<=", ["get", "ref_length"], 6]` on a
  road without ref_length compared null with a number. Now, lenient only: a label's unbreakable text is
  set on one line (or that label left out), a feature whose filter cannot evaluate is left out (GL JS's
  evaluate returns the default) - never the layer. Shields seen offscreen at Tokyo. Every new skip or
  drop is now a "Globe style:" warning in vvvv's log. Strict (the render tests) unchanged.
- **What survives zooming, by count, not bytes** (2026-11-23, dotnet-gcdump during the zoom bench): the
  heap grew 1.1M objects in 22 s but only ~18 MB, nearly all of it vertex arrays on the large-object
  heap (never copied). The copying is in small objects: value strings, one `StyleFeature` + one
  `TagProperties` + one boxed id per feature, three objects per line label. Merging the feature, its
  property view and its id removed ~360k of them: zoom bench 3.4% -> 2.8%. `dotnet-gcdump collect`
  freezes the process ~0.8 s - those frames are the tool's, not ours.
- **A feature is a row, not an object** (2026-11-23): a decoded MVT layer is a `FeatureTable` - kinds,
  tag ranges, part ranges and ids in arrays - walked through one reused `StyleFeature` cursor. A
  feature seen in a loop is valid until the next: keep its `Index` and `Get(i)` it later (the fill
  outlines did). Zoom bench 2.8% -> 1.2% (two runs), worst 31-35 ms. ViewProbe differs by ~22 pixels
  between two runs of the same code (label timing): compare a change against that, not against zero.
- **Line labels as structs: no measurable gain** (2026-11-23). Three zoom benches 0.6 / 2.2 / 1.1%
  against 1.2 / 1.2 before: the run-to-run spread (tiles arriving at different moments) is now
  wider than the step. Kept - fewer objects, same pixels (24 of 255k, the probe's own spread) - but
  the missed frames are now mixed: GC 4-8 ms a frame, label opacities 5-10 ms in single frames,
  submit 7-9 ms at 6000 draws zoomed out. Run a bench three times before calling a change.
- **A missed frame's parts mean nothing without a made frame's** (2026-11-23): "outside 8 ms" in the
  missed frames looked like a cause; made frames spend 12.9 ms there (Stride's own drawing plus the
  vsync wait), so the missed ones had used their slack up. The perf line now carries every frame's
  parts, made and missed apart. And a GC pause is counted twice - as GC and inside whatever stage it
  stopped - so stage diffs are upper bounds when GC is in the same frame.
- **Most strings were names nobody reads** (2026-11-23): OpenMapTiles carries a place's name in dozens
  of languages, Liberty reads 22 keys. A value table now keeps a string as its place in the tile's
  bytes and makes it at the first read (two threads may both make it: same text). Strings grown while
  zooming 218k -> 88k in 22 s. The tile's bytes stay with the decoded tile (mostly large-object heap).
- **Per-draw values in a structured buffer: two traps** (2026-11-23). (1) A matrix in a StructuredBuffer
  is read column-major (the generated HLSL in the test's `bin\...\log\shader_GlobeVector_*.hlsl` shows
  offset 0 = `_m00 _m10 _m20 _m30`), a cbuffer's row-major: upload `Matrix.Transpose`. (2) A draw's index
  as a per-instance element offset by DrawIndexedInstanced's start instance was read PER VERTEX - found
  by colouring pixels by the index (it varied across a triangle). Bound instead with stride 0 at the
  draw's element (`SetVertexBuffer(1, ids, index * 4, 0)`): every vertex reads it, whatever the
  classification. `return;` in an SDSL PSMain does not compile (X3080). Stride writes each compiled
  effect's HLSL to `log\` beside the binary: read it before guessing.
- **Closing a GlobeWindow with WM_CLOSE sometimes logs ObjectDisposedException** (GlobeWindow_P's
  GetClientBounds on the disposed VLGameForm, then vvvv's MainForm): seen in 4 of 9 bench closes on
  2026-11-23, before and after the day's changes - a shutdown race, not yet looked at.
- **Seen in vvvv, before and after the per-draw buffer** (2026-11-23, the bench patch at
  #14.82/35.68778/139.76758/-78.6/54.5, still, 45 s after opening): 53 of 253k sampled pixels differ
  between the two builds; no renderer error in the log. Every 3D building is drawn at 80%: Liberty's
  building-3d has `fill-extrusion-opacity: 0.8` (GL JS too, depth first, then colour on the nearest
  face). I first called "a few" translucent - those had water or roads behind them; the user saw
  that all are. Read the style's paint before calling a look a bug.
- **Navigation nodes, rung 2-4** (2026-11-23): `Compile-HelpPatches` PASS (8 patches; the six
  Globe.Navigate nodes resolved and built in Create); `HowTo Fly to a place` opened in vvvv with no
  error in the log, the editor captured (PrintWindow - the editor's window has no title; in
  PowerShell 7 make the Bitmap in PowerShell and only P/Invoke in Add-Type, which cannot reference
  System.Drawing there). Not yet: pressing the bangs - the flight's feel is the user's to judge.
- **Pin order is vvvv's, not ours** (2026-11-23, the user: "pin的顺序...做得还很不讲究"; "一般vvvv gamma的节点
  有自己的要求和传统的"). The Gray Book has one rule: "Main input left, .. Reset typically right"
  (reference/extending/design-guidelines.html, Order of Pins). The rest measured in vvvv 7.4's packs
  (compiled *.vl.dll, `_P.Update` parameters, read with System.Reflection.Metadata): Enabled is the LAST
  input in 237 of 289 patched process nodes that have one (first in 5); Skia's Renderer and SceneWindow:
  the window's create pins (Bounds, Bound to Document ...), then Input, then Title, then configuration,
  Enabled at the end. Caveat found doing it: a patched node's COMPILED parameter order is not its
  displayed pin order (GlobeWindow's differs), so the count is close, not exact - the displayed order of
  a patched definition is the order of its `<Pin>` elements in the .vl. Now: GlobeWindow Tile Source,
  Layers, Input, Title, Initial Longitude/Latitude/Height, Min Height, Background, Enabled; outputs
  View, Cursor, Longitude, Latitude, Height, Attribution, Status. Globe, VectorTiles, FeatureLayer,
  Geometry: Enabled last; ZoomIn/ZoomOut: Trigger last. Pins link by name: patches keep their links
  (all 8 help patches compiled; the bench patch drew in vvvv). VL.Mapsui's layer nodes have the same
  Enabled-in-the-middle order (ours copied it) - not changed there.
- **A patched definition's pin order needs `ManuallySortedPins="true"`** on its Create/Update `<Patch>`
  (538 of them in VL.Stride.Engine.vl; none in VL.Globe.vl until 2026-11-23). Without it the `<Pin>`
  elements' order is not what the editor shows - the reorder committed just before (1ca1dba) did not
  say so and may have shown nothing. Added to GlobeWindow; to be confirmed: after vvvv recompiles,
  `GlobeWindow_P.Update`'s parameters should follow the `<Pin>` order (they do for SceneWindow,
  OrbitCamera and Renderer, which are manually sorted).
- **Step 1 of DESIGN-NODES built** (2026-11-23): one Input + Group. OpenStreetMap and XyzTiles are layers
  (Globe.Layers, Output); `Group` (a pin group: `[Pin(PinGroupKind = VL.Model.PinGroupKind.Collection,
  PinGroupDefaultCount = 2)] Spread<T>` - VL.Model, not VL.Core) and `Group (Spectral)`; Globe and
  GlobeWindow take `Input`, GlobeWindow's entity pin is `Scene`. The bottom raster layer gives the tiles;
  a second one is not stacked yet and Status says so. Seen in vvvv: the bench (VectorTiles into Input)
  and the shapes patch (OpenStreetMap + Geometry through Group) draw. **ManuallySortedPins confirmed**:
  with it, GlobeWindow_P.Update's compiled parameters follow the `<Pin>` order exactly (without it they
  did not). Two traps on the way: a test cannot name `Spread.X` (VL.CoreLib has a namespace
  `VL.Lib.Collections.Spread`) - build through `Spread<T>.Empty.ToBuilder()`; and a help patch vvvv had
  saved was LF without BOM while the others are CRLF with BOM - edit scripts must take each file's own.
- **Step 2 of DESIGN-NODES built** (2026-11-23): GlobeCamera as OrbitCamera. It puts VL.Stride's
  `CameraInputSourceComponent` on its entity (the window rendering it fills `InputSource`, as OrbitCamera's
  patch does), reads the first `IMouseDevice` (Position 0..1 -> projection space, IsButtonDown - an
  extension in Stride.Input: `using Stride.Input;` at the top, since inside namespace VL.Globe the name
  `Stride.Input` means VL.Stride.Input), the wheel from `VL.Stride.Resources.GetInputManagerHandle(nodeContext)`'s
  Events filtered by that device (as Stride's SplitMouseDevice), the window's height from SurfaceSize;
  runs Drag, ZoomByWheel and DragRotate inside. Initial Heading/Tilt, Reset, Enabled; output Output.
  GlobeWindow: Camera pin (wired, else its own GlobeCamera, chosen by GlobeWindowCamera), no Initial pins,
  no Longitude/Latitude/Height outputs (GlobeViewInfo reads them from View). Seen in vvvv: wired (the
  help's GlobeCamera, 3000 m) and own camera (the whole globe); the camera's drag logic alive with the
  window's mouse device in both. Traps on the way, each silent:
  - `[Smell(SymbolSmell.Hidden)]` on a C# operation made it UNRESOLVABLE inside VL.Globe.vl itself: the
    node was left out of GlobeWindow_P.Update, its outputs null, "Globe: no Camera", a blank window - and
    every compile check green. `Advanced` resolves (and hides it from the NodeBrowser's default list).
  - vvvvc said "Object is no GlobeView!" for every help patch reading GlobeWindow's View, while the editor
    ran them: the definition's View and Camera pins needed explicit type annotations.
  - Seen only by reading the compiled IL: ilspycmd is not installed here; scratchpad `IlCalls.ps1` lists a
    method's IL with calls resolved (System.Reflection.Metadata + Emit.OpCodes, PowerShell 7 only).
  - `tools`-like generator `make_fly_help.py` (scratchpad) is retired: the help patches are edited in place.
- **The user tried the new GlobeCamera** (2026-11-23 17:06, HowTo Open a globe window): 10 drags, 88
  wheel notches, 3 right-drags, then Reset - the view back at 139.7 / 35.68, 12,000 km, heading and tilt
  0; no error in the log. They rearranged the patch and added a Reset bang. Their save carried Enabled
  True and VL.Globe pinned 0.0.1-alpha: Normalize-HelpPatches put them back (false, 0.0.0).
- **Tile layers stack through the overlay** (2026-11-23, ROADMAP item 6): the layers above the bottom tile
  layer go where the draped vectors already went - each tile's overlay image, rendered on a worker in
  Group order - instead of a second texture and material blend per slot. No new upload path, no new
  fade logic; opacity is the paint's alpha. GPU vector tiles draw above every tile layer. The bottom
  layer was first left opaque ("it is the imagery"); the user: "没有必要，因为用户知道自己再做什么" - below
  Opacity 1 it now goes into the overlay as well, over plain tiles in the Background colour (seen in vvvv). Tested offline (a PNG put into the
  source's FileCache first); seen in vvvv.
- **Shapes over vector tiles** (2026-11-23, the user asked it tested): a Geometry after VectorTiles in
  Group was drawn into the tiles' overlay - under the GPU's vector tiles, so it vanished. Now such
  layers go the GPU's way (`GeometryStyle`: one MapLibre style a layer, an inline GeoJSON source, fill /
  line / circle in its VectorStyle; `VectorTileService` takes the style's JSON). Seen in vvvv over
  Liberty and CARTO Voyager.
- **A long GeoJSON line came out in stray pieces on the GPU** - since the 16-byte vertex (4g) a vertex
  holds its place only from -0.5 to 1.5 tiles, and GeoJSON went to every tile whole: a far end was
  clamped to that edge and its segment into the tile pointed the wrong way. GeoJSON is now cut to each
  tile widened by a quarter (`TileClip`, Liang-Barsky lines, Sutherland-Hodgman rings, new feature
  objects - the workers build tiles in parallel and a shared feature's Index and Bounds would race).
  Fixture reports identical; seen in vvvv as one straight line.
- **Fourteen public styles drawn offscreen** (StyleSurvey.ps1, scratchpad): OpenFreeMap liberty, bright,
  positron, dark, fiord; VersaTiles colorful, graybeard, eclipse, neutrino, shadow; CARTO positron,
  dark-matter, voyager; MapLibre demotiles (zoom 0-6 only). All draw; OpenFreeMap dark comes out nearly
  black with faint labels - to compare with GL JS. Help patch `HowTo Choose a vector tile style` picks
  them by number, a Geometry over them.
- **Pick and Graticule** (2026-11-23): Pick = the cursor's ray to the globe, the tolerance in pixels made
  metres at that point, each geometry moved into local metres (longitude by its cosine), NTS Contains /
  IsWithinDistance, top layer and last feature first; FeatureLayer keeps its features so Pick hands back
  the user's own (with attributes), Geometry gets a new Feature. Graticule = meridians and parallels as
  lines with a point each degree, draped. Seen in vvvv: a parallel near 45 N at the globe's limb looked
  dotted - not looked at yet.
- **Dark holes through a style's background: depth, not data** (2026-11-23, the user: "还是非常明显，我觉得这个
  问题需要仔细想一下对策"). MapLibre's demo tiles stop at z6; zoomed to z10-12 the background grid was the
  z6 vector tile's - cells of a z6 eighth, ~80 m of chord sag below the sphere, more than the lift
  (0.15% of the distance: 27-110 m at an eye 18-76 km up). The globe's own tiles then hid it by depth: a
  dark disc round the centre of the view, where the lift is least. The gap diagnostic (every shown tile
  got its background squares) and offscreen probes (no globe tiles, nothing to hide behind) both said
  "fine" - the probe could not fail this way. Now the background is built per shown square at that
  square's zoom. Overscaled DATA layers have the same sag: confirmed the same evening - after the fix,
  style 13 over Tokyo Station at ~2.5 km drew the sea colour everywhere, the land gone (lift ~4 m).
  The plan: docs/DESIGN-OVERSCALED.md (ROADMAP 4h).
- **Fixed by the sphere's depth, GL JS's way** (2026-11-23 evening, ROADMAP 4h, the user chose B'). GL JS
  subdivides by the SOURCE tile's zoom too (`getGranularityForZoomLevel(canonical.z)`) - its chords sag
  the same; it never compares a 2D layer's own depth (a constant glDepthRange per layer) and cuts the
  far side by the horizon plane. Ours: the ground passes write SV_Depth = the true sphere's depth along
  the pixel's ray (t = c / (b + sqrt(b² - c)), c = |eye|² - R² in double on the CPU), lifted as before;
  entities in the Scene still hide them. **First a probe that could fail**: `RenderGlobe` now draws
  the globe tiles' depth first (`globeTiles`); a z0 water fill at eye 1-12 km went 120000 px alone and
  **0 over the globe's depth** - the old probes' "fine" was the missing depth. **The horizon plane cut
  too much**: tested by the fragment's own position (GL JS's), a z0 chord ~970 m under the sphere at an
  eye 94 m up lay "past the horizon" - all discarded. Cut along the ray instead: past its nearest
  approach to the centre (|p| > b). A far-side box: 284 px without the cut, 0 with it.
- **Skia over Stride from C#** (2026-11-23, the attribution): `new VL.Stride.SkiaRenderer(nodeContext)
  { Layer, Space }` is an `IGraphicsRendererBase` - put it in an `EntityRendererComponent`
  (`DrawerRenderStage.AfterScene`) as the vector renderer is; in the layer, `SetMatrix(Identity)` is
  device pixels (VL.Mapsui's PixelSpace). vvvv's SkiaSharp is 2.88: text through `SKPaint`
  (TextSize, MeasureText, BreakText, FontMetrics) - `SKFont.MeasureText(string)`/`BreakText` do not
  compile against it.
- **The poles, and multisampling** (2026-11-23). Three traps on the way: (1) the selector's horizon
  test sampled only a tile's Mercator square - over a pole no tile passed; (2) the background grid had
  no vertex on its square's edges, so each neighbour cut its clip edge across its own chords; looking
  down on a pole those chords bend in the screen's plane: a pixel of crack on every seam - now a column
  and a row exactly at 0 and 1; (3) **vvvv's window is multisampled, the probes were not**: the clip's
  `discard` runs once a pixel, at its centre, so a pixel straddling a clip edge where the margin is
  under a pixel wide (columns meeting at the pole; the grid held at the polar edge) lost both
  neighbours' samples - dotted seams only in vvvv. `RenderGlobe(..., samples: X4)` and ViewProbe's
  `VLGLOBE_MSAA=4` show it; caps are built to the square's width and not clipped, nor is a square's
  polar edge. Also: `GlobeView.SetHeight` takes metres - a test passing radii stood the camera 1 m up.
- **Dragging near a pole flashed the view to it** (the user, 2026-11-23: "视野在南北极附近拖拽的时候还是会
  抖动，甚至会闪动到北极去"; their log: 89.90 with longitude -1505, then 76.7). The grab with north
  kept up has no answer once the grabbed point would need the camera past the pole; the fallback took
  the far side's: 125-180° in one 5-px frame, tilted 30° at ~10,000 km (`PoleDragFacts`). The earlier
  fix (stop at 89.9) checked longitude only. GL JS does not grab a globe at all ("not a very pleasant
  way to pan a globe"): below MapLibre zoom 12 the pixel move moves the centre by its degrees per
  pixel (computeGlobePanCenter, latitude within ±85.051129, longitude speed capped at 60° when far);
  above it the exact grab. Done so (the user chose it): `GlobeView.DragSession`, chosen at the press.

## 2026-11-24 — engineering: the engine in folders, and two tools that checked nothing

- **The F1 audit audited VL.Mapsui** (`Test-VLPatch.ps1`, copied 2026-11-09): its node list was
  Mapsui's 32 and its pattern matched `LastDependency="VL.Mapsui.vl"`, so here it printed "0 of 32"
  as an `ok` line and could never fail. Now it reads `tools\Nodes.psd1` (shared with
  `Compile-HelpPatches.ps1`): 31 of 31. Its pattern also allowed only PinReferences between the
  Choice and `</p:NodeReference>`; Graticule's CategoryReference stands there, so a flagged node read
  as unflagged.
- **Probes passed**: seven probe classes returned early without `VLGLOBE_PROBE=1` and counted as eight
  passes in every run. `[Probe]` skips them instead (338 passed, 8 skipped).
- **Hand-written IDs**: `Test-VLPatch.ps1` fails every help patch and VL.Globe.vl on "illegal IDs" -
  IDs whose first character is outside A-V (every ID in vvvv 7.4's shipped help starts A-V). They came
  in with the Navigation and GlobeCamera-as-OrbitCamera commits (2026-11-23), written by hand, not by
  `New-VLId.ps1`. vvvv loaded and re-saved them. Replaced the same day: 240 IDs, 373 occurrences,
  one mapping across all twelve documents. The audit had passed ~200 more: PowerShell's `-notmatch`
  ignores case, so a-v counted as A-V (now `-cnotmatch`). GlobeWindow_P read back decompiled: every
  link in place.
- **Partial classes cannot span namespaces**: moving SymbolLayout.cs to `Symbols\` broke the build -
  it holds parts of the GPU bucket's `GpuStyleLayer`/`VectorBucketBuilder`. It lives in `Gpu\` as
  `VectorBucket.Symbols.cs`. And a member named `Fog` in VectorRenderer is why `Engine.Fog` was
  written qualified: now `Gpu.Fog`.
- **The repository mixes LF and CRLF** (CRLF: TileGeometry, TileSelector, GlobeCameraNodes,
  GlobeCoordinates, GlobeView, the csproj, two test files). An edit script that matches `\n` misses the
  CRLF files in silence; split on the file's own newline.
- **The drape of vector tiles retired** (the user, 2026-11-24): `Rendering` pin, `GlobeVectorRendering`,
  `GlobeLayer.Gpu`, `StyleOverlay`, `MapStylePainter`, `MapLibreRenderFacts` and `render-tests.json` gone;
  the two help patches' `<Pin Name="Rendering">` removed in place. What went with it: an overlay no
  longer waits for vector tiles (`OverlayMissing`, `Came`), so a draped overlay is redrawn only when
  the layers change or a stand-in becomes wanted. 334 pass (4 drape facts fewer).

## 2026-11-24 — light (ROADMAP 6b): a parameter added to a shader was never set in vvvv

- **The GlobeVector effect's parameter names were read once per renderer** (`_keys`, on the first
  DrawFrame) and a name not found was remembered as "compiled away" and never set. vvvv may hand out the
  cached effect first and the recompiled one later, whose layout has the new names: `GvLight` would
  have stayed unset forever. The tables are now read again whenever the compiled effect object changes.
  Not proven to have been the cause of anything seen (the log then said "GvLight in the effect"), but
  the assumption "a key found stays right" was wrong for keys not found.
- **A log line computed before the first draw lied**: "GvLight not in the effect" was checked while the
  name table was still empty. Computed after DrawFrame now.
- **The first rung-4 baseline was not one**: the run meant to show GL JS's default light logged
  "default" for its first frame and was in fact lit by the sun - its picture equalled the sun's to within
  8 levels, and "no difference" was read as "the light does nothing". A red sun proved the light reaches
  the shader, `SceneLightFacts` that the direction is used offscreen; then a baseline patch without
  SunLight, same build, same view (10:36): 82,000 samples differ - roofs ~20 % darker under a 31° sun,
  the shaded walls on the north side instead of the east. **A comparison needs both pictures from the
  same build in one sitting, each with its log line saying which light it had.**
- **Stride lights read back**: `context.RenderContext.VisibilityGroup.Tags.Get(ForwardLightingRenderFeature.CurrentLights)`
  holds the scene's lights during EntityRendererComponent's draw in vvvv 7.4 (seen: 1 LightDirectional);
  RenderLight.Direction is the way the light travels, Color linear and times the intensity.
- **The shadow pass took the buildings' antialiasing away** (seen by the user, 2026-11-24): shaded once
  per pixel from the first MSAA sample, a pixel half roof, half shaded ground was darkened whole or not
  at all - a staircase along every outline, plus a seam of unshaded dots where the ddx/ddy normal was
  taken across the edge and the face counted as turned from the sun. `SV_SampleIndex` (per-sample
  shading) does not exist for Stride 4.2's shader parser - it routed the stream through the vertex
  shader (X4502). Now: an outline pixel (its samples' depths differ) is shaded per sample and averaged,
  the rest once; the normal from the neighbour nearer in depth on each axis.
- **SunLight unconnected is now**: after sunset there is no shadow at all, which looks broken. The help
  patch feeds `Create (Year Month Day Hour Minute Second Kind)` [System.DateTime] with Kind Utc (1:00 =
  10:00 in Tokyo); Kind Local is the computer's clock, not the place's.
- **That fix blackened walls in vvvv and drew no shadow offscreen** (the user's view
  #17.41/35.68526/139.80589/87.4/47.9, 18:42): `Texture2DMS.GetDimensions` through Stride 4.2's SDSL gave
  a wrong sample count - samples read past the last. The count now comes from C# (`GhSamples`, the scene
  depth's MultisampleCount). Found by `ViewProbe` with `VLGLOBE_SUN=2026-11-24T01:00:00Z VLGLOBE_MSAA=4`
  (new): old shaders shadowed, new ones not; a constant 4 shadowed. **My rung 4 of the fix had looked at
  outlines only, in a crop - not whether the shadows were still there.** The tests compile the MS
  shader but never draw with it.
- **A grey band over the far map at the lowest eye and steepest tilt** (the user, 2026-11-24; "only at
  the lowest, at the largest tilt"): from ~4.5 eye heights off (eye 20 m: 90 m; eye 354 m: 1.6 km) the
  globe's own tiles won the depth test and their grey lay over the ground, the buildings drawn over it.
  Not light, shadow or fog (the user switched them off; still there). Found in vvvv by switching parts
  off (globe tiles off: gone; buildings off: still there), then offscreen: lift 0 z-fought everywhere,
  0.006 none. Cause: the ground's depth (the sphere's, ROADMAP 4h) is the pixel centre's, written to
  every MSAA sample, the tiles are tested per sample - at a grazing angle a pixel spans more depth than
  the lift (0.0015 of the distance); with X4's sample offsets the break is ~14 degrees, as seen. Fixed
  with a slope-scaled offset (glPolygonOffset's factor): the depth moved towards the eye by 0.75 of
  its ddx + ddy. `OverscaledGroundFacts.AtAGrazingAngle...` draws both views multisampled: 44,000 of
  120,000 ground pixels pure without the offset, all with it.
- **My vvvv hung on exit twice** (20:30, 20:5x; the log stops at the WM_CLOSE, the renderer window
  stays) - **explained by the user 2026-11-25: vvvv asks whether to save the patch on close, and nobody
  answered the dialog.** A changed document (a hot reload, a value moved) makes WM_CLOSE wait on that question.
  Original note: both the first launch after a pack, later launches closed. Stopped with the user's leave
  (my pids only). Not yet looked into.
- **Shapes are GPU layers, always** (the user, 2026-11-24: "有vectorTiles和没有都应该统一的画法 ... 肯定是要作为
  独立的layer存在"): Geometry, FeatureLayer and Graticule were draped into the tiles' images by Skia unless
  a VectorTiles layer came before them. Now every one is a GeoJSON style of its own (GeometryStyle) on the
  GPU; `TileOverlay` removed; the drape holds raster layers above the bottom one only, so shapes lie over
  every raster layer whatever their place in the Group. No test noticed the change of path (all green
  before and after): `ShapeLayerFacts` now draws two shape layers with no VectorTiles and checks the order
  (fails with the order swapped). Seen in vvvv: the shapes help (OSM under) and the graticule help. Open:
  the 45 N parallel drew dotted for a stretch west of France (graticule help, whole-globe view).
- **Raster layers on the GPU, ROADMAP 6c step 1** (2026-11-24): the layers above the first are drawn by
  `VectorRenderer`'s `Raster` pass on each wanted globe tile's background grid (its pole caps included), the
  image its own square's or the finest loaded ancestor's through a rectangle, fades both ways in the shader
  (`RasterTiles`). The Skia overlay is gone. First compile failed in the tests: `return;` in GlobeVector's
  PSMain is X3080 - Stride's generated PSMain returns its streams; if/else instead. Tests offline: the cache
  holds zoom-2 images only, the URL is `127.0.0.1:9` (refused), so every finer square draws an ancestor; a
  gradient image checks the rectangle to 3 levels. Seen by the user in vvvv: "似乎问题不大". Not yet compared
  side by side with the globe's own imagery (step 2); the fades have no test of their own.
- **6c steps 2 and 3** (2026-11-24): compared in vvvv by the bottom layer's Opacity (1: the tiles' material;
  0.999: the GPU layer over plain tiles) - the user saw no difference. Then the tiles lost their imagery: one
  shared Background material, no requests, no fade materials (`GlobeNode.Tiles/Show` rewritten); every raster
  layer a GPU layer. Seen in vvvv. **A transparent Background now builds no tiles** - a bare Globe node (its
  default) then has no depth for the scene's entities; GlobeWindow and every help patch wire the blue.
- **`Color4?` silently cut GlobeWindow's Background link** (2026-11-25): to give a bare Globe node a blue
  default I made its Background pin `Color4?` (VL.Mapsui's pattern). vvvvc, the help compile and the 351 tests
  all passed; the decompiled `GlobeWindow_P.Update` passed `Color4? background = null` - the patched definition's
  link from its own `Color4` Background pin was dropped. The window looked right only because the default was
  the same blue. Reverted to `Color4` (transparent = no tiles, as documented). **Changing a C# pin's type that a
  patched definition links into needs the decompile check** (ilspycmd, `--tool-path` into the scratchpad).
- **Stack tile layers' Group (Spectral) cell** (2026-11-25, edited in place): a spread of spacings, a ForEach with
  one Graticule each, into Group (Spectral) - instead of OSM + Graticule through Cons, which showed nothing a
  fixed Group could not. `Compile-HelpPatches.ps1` failed it ("not constructed in __Create__"): inside a ForEach
  the generated C# builds a process node per slice (`if (!iterator.MoveNext(out state)) { new Node() }`); the
  check now accepts that. Intro text: tile layers, vector tiles and shapes stack alike.
- **No FrameDelay for the view's height** (2026-11-25, the user: vvvv gamma avoids such loops; survey: Stride's
  SceneCameraRenderer, VL.Stride's WithRenderView/WithinCommonSpace/SkiaRenderer read
  `RenderContext.ViewportState.Viewport0` at draw time, Skia hands `CallerInfo.ViewportBounds` upstream, CesiumJS
  reads `drawingBufferHeight` in the frame; no forum thread recommends Client Bounds -> FrameDelay, and Client
  Bounds is wrong on the first frame). `VectorRenderer.Draw` records the viewport height, Globe's selection uses it
  next frame; `Screen Height` is an optional pin (0: that; before the first draw the GlobeCamera's window, else
  1080). Removed from GlobeWindow and `HowTo Build your own globe scene` (decompiled: both cameras and the Globe
  get 0, Background still flows). Seen in vvvv: "screen height 1080 px, from the camera's window", then "800 px,
  from the view it is drawn in". A Globe drawn in two windows of different heights takes the last one drawn.
- **The 45 N parallel dotted at the limb** (2026-11-24, explained 2026-11-25; `GraticuleProbe`, whole globe at
  #1.2/45/-5/0/0, X4): not the far-side discard (switched off: still dotted, and the far side showed through) but
  lines seen edge-on - towards the limb a line's width lies along the view, narrower on the screen than a pixel,
  and the samples broke it into dots. GlobeVector's VS now holds a sphere line at least a pixel wide across its own
  screen direction (from the line's tangent) and fades it by how much narrower it was (`GvThin`); the plane, the
  render tests' path, is unchanged. `EdgeOnLineFacts`: a meridian 3 degrees inside the limb lights every row it
  crosses (142 of 147 rows dark without the change). Faint there is right; also changes far, foreshortened lines
  in a tilted street view (held whole, faded) - to be seen in vvvv.
