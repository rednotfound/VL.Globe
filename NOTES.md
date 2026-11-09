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
