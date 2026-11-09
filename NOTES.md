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
