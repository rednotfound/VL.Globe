# CLAUDE.md

Guidance for Claude Code working in this repository.

## Start here

1. Read **[docs/ROADMAP.md](docs/ROADMAP.md)** — planned by release: **0.1 = VL.Mapsui's basics on a smooth
   globe plus draped vector tiles, 0.2 = terrain** (the user's decisions); where each item stands.
   How it got here, session by session: [docs/HISTORY.md](docs/HISTORY.md).
2. Read **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — what exists and why.
3. Read **[NOTES.md](NOTES.md)** — every failure so far, before touching the same code.
4. The family's rules are the siblings': `D:\2026_Projects\vl-mapsui\docs\RULES.md` and that repo's
   CLAUDE.md (the dev loop, the four verification rungs, never leave vvvv running, close only your
   own pid, help patches ship an `Enabled` toggle OFF (nodes default ON since 2026-11-15/23), edit a .vl in place, NOTES.md is the
   failure log). They are not repeated here.

The shared memory (`~\.claude\projects\D--2026-Projects-vl-globe\memory`) is a junction onto the
family's one store, as for the other four repositories — what was learned anywhere is loaded here.

## What this is

**VL.Globe** — Web Mercator raster tiles on a sphere in vvvv gamma's Stride, fetching only the tiles
the camera can see, a camera that moves like a map, and longitude/latitude ↔ globe-position
conversion. The fifth member of the VL.NetTopologySuite family (siblings: `vl-nettopologysuite`,
`vl-mapsui`, `vl-geojson`, `vl-overworld`, all under `D:\2026_Projects`). **It does not depend on
VL.Mapsui.** Scope: `vl-overworld\docs\GLOBE-SCOPE-PROPOSAL.md` (§3–§5, §9 the camera revision);
how it came to exist: `vl-overworld\docs\GLOBE-RESEARCH.md` and ACT-IV-LAB experiments 5–7.

The user's standard for this package (2026-11-09): **street level, not country level.** Errors hide
at country scale; judge every change at the lowest height.

**Now (2026-11-24).** An engineering pass: the engine in eight folders (ARCHITECTURE.md, "The
engine"), the tests mirroring them, probes skipped unless `VLGLOBE_PROBE=1`, the tools reading
`tools\Nodes.psd1`. Step 3, the user's choices: VectorTiles' drape retired (the `Rendering` pin gone), the hand-written
IDs replaced; the family's `tools\` stay per repository, no CI. `GlobeNode.cs` split into partial
files by concern (Tiles, Show, Overlays, Background, Perf; 2026-11-24). Still open: where terrain goes.
**ROADMAP 6c done (2026-11-24)**: every raster layer is a GPU layer (`Engine/Gpu/RasterTiles.cs`, the `Raster` pass), the globe is a sphere in its Background colour - shape and depth only (the user: "地球globe就是蓝色的球就结束"); judged in vvvv. Offered, not asked yet: a Group (Spectral) cell in `Stack tile layers`. Shapes are GPU layers since ca1f0bf. Light/shadows: ROADMAP 6b, docs/DESIGN-LIGHT.md. Open: the 45 N parallel dotted west of France; my vvvv hung on exit twice (NOTES.md); overlapping buildings doubling in colour (stencil, offered); SunLight's output `Entity` vs DirectionalLight's `Output` (asked).

**Waiting on the user:** their judgement of the gestures and the help patches' layout
(docs/DESIGN-NODES.md); the drag near the poles and the attribution, not yet seen by them in vvvv.
**When asked:** ScaleBar, ZoomButtons, Click (ROADMAP 6); VectorTiles' Enabled default is still to
decide. Node design rules: the `vvvv-node-design` skill (~/.claude/skills).
A reported view is drawn offscreen by `Probes\ViewProbe` (VLGLOBE_PROBE=1 VLGLOBE_VIEW=#...).

## Where it stands — the nodes (table from 2026-11-20, rows updated since)

| node | state |
|---|---|
| `GlobeWindow [Globe]` — patched in `VL.Globe.vl` | the front door; since 2026-11-23 Input (layers) + Camera + Title + Background + Scene + Enabled, pins in vvvv's order (`ManuallySortedPins`); docs/DESIGN-NODES.md |
| `Globe [Globe]` — the engine | camera-relative, rounded levels; the sphere in its Background colour, every Input layer on the GPU in order (raster: mipmaps, fades both ways); user-judged |
| `OpenStreetMap`, `XyzTiles`, `Group`, `Group (Spectral)` [Globe.Layers] | layers since 2026-11-23 (one Input, as Skia's Group); every one a GPU layer in the Input's order (6c); disk cache shared with VL.Mapsui |
| `GlobeCamera`, `Drag`, `ZoomByWheel`, `DragRotate [Globe]` | GlobeCamera as OrbitCamera since 2026-11-23: reads its window's mouse, Initial pins, Reset, Enabled; north stays up; a drag past the edge holds the horizon |
| `GlobePositionComponent [Globe]` | stands an entity on the globe, placed at draw time; user-judged |
| `ScreenToGlobe`, `GlobeToScreen`, `GlobeViewInfo [Globe]` | built; user-judged |
| `Geometry`, `FeatureLayer [Globe.Layers]`, `VectorStyle [Globe.Styles]` | built; GPU layers of their own since 2026-11-24 (the user: one way, with or without VectorTiles), over every raster layer; seen in vvvv |
| `VectorTiles [Globe.Layers]` | built 2026-11-21: a MapLibre style's fills/lines/background from vector tiles, with or without a Tile Source; drawn by the GPU (judged by the user moving; the drape, "不太行", removed 2026-11-24) |
| `ToGlobe`, `ToGlobe (Coordinate)`, `FromGlobe [Globe]` | built, 4 rungs, **absolute** — right under OrbitCamera; under GlobeCamera use GlobePositionComponent |

351 tests pass and 8 probes are skipped (incl. MapLibre v6.19.0's render tests — 409 fixtures, fetched by script — run offscreen on a D3D11 device of the test's own, on the GPU path: 329 pass). Branch `feature/camera-relative`, pushed to `github.com/rednotfound/VL.Globe`
(public); not merged, not published. Verified on vvvv **7.4 (LTS)**; `Find-Vvvv.ps1` stays below 8.0.

## Facts this repo paid for (details in NOTES.md)

- **Category = namespace + class.** `ImportAsIs(Namespace = "VL")` strips `VL`: static nodes live in
  `namespace VL`, class `[Name("Globe")]` → category `Globe`. Process nodes name their category.
- **Axes are Stride's Sphere's**: y north, lon 0 / lat 0 is +z, 90°E is +x.
- **Never an input and an output of the same name** (family rule): Drag outputs `Result`,
  ZoomByWheel `Output`, as VL.Mapsui's do. Breaking it sent GlobeCamera's View into Drag's output and
  Drag ran on null. `Compile-HelpPatches.ps1` now fails any Globe-typed local that reads `default`.
- **`Mouse [Stride.Input]` must be in the scene**: wire its `Output` into `RootScene`, or none of its
  pins ever change.
- **Use the Mouse's `Position in Projection`, never its world `Ray`** for picking near the ground:
  the Ray is unprojected in float and jittered by metres at 100 m.
- **A record type with a property of its own type needs a hand-written ToString**: D3's generated one
  recursed through `Normalized` and a StackOverflowException closed vvvv. **When vvvv just exits,
  read Windows' Application event log first** (`Get-WinEvent`, provider "Application Error" / WER):
  a stack overflow cannot be logged by the process itself.
- **OrbitCamera's pose is in its CameraComponent's custom view matrix**; its transform is identity.
- **Coarse tiles need more segments** (`SegmentsFor`); **clip planes must follow the height** or
  anything near the tiles z-fights.
- **The test project references VL.Stride.Runtime without `ExcludeAssets`**, or Stride's module
  initializer kills every test before it starts.
- **A C# optional parameter cannot precede an `out` one**: `FromGlobe`'s `Radius` is declared last.
- **Drive input only when the user is not at the machine** (`tools\Drive-Renderer.ps1`). And before
  closing a vvvv this session opened, read its log for presses/wheel: on 2026-11-20 the user was
  using the window I closed.
- **Camera-relative**: GlobeCamera's view is rotation only; the eye (double) rides in an internal
  `GlobeCameraFrame` component. Anything placed absolutely lands on the camera.
- **SceneWindow's defaults are wrong for a map**: `Enable Default Post Effects` (on) darkens and
  blurs; `Present Interval` has no default and runs `Immediate` (tearing). Set False / One.
- **SceneWindow's `Client Bounds` is `2D.Rectangle`** (RectangleF). Stride's `Rectangle` Height was
  dropped by the compiler in silence. **The help-patch compile check cannot see inside a patched
  definition**: decompile `VL.Globe.vl.dll` (`ilspycmd`) and read `GlobeWindow_P.Update`.
- **Stride's `BinaryOperator.Add` is `ComputeColorAdd3ds`**, alpha-weighted; an emissive
  `ComputeColor` drew black. Read the shader a material node maps to (`.sdsl` in the Stride.Rendering
  nuget) before trusting its name; colours go through 1 × 1 textures.
- **Test the whole screen, not the point below**: a stretched row of tiles passed the tile-below
  test; `TileCoverageFacts` samples every pixel.
- **Custom GPU drawing goes through VL.Stride's `EntityRendererComponent`** (an `IGraphicsRendererBase`,
  called in the stage asked for with the scene's RenderView); a package's `shaders\` folder is put on
  Stride's effect path by VLGame, so `DynamicEffectInstance("GlobeVector")` finds `shaders\GlobeVector.sdsl`.
- **A sag bound is relative to the view distance of the zoom**: GPU geometry is lifted 0.0015 of its
  distance towards the eye, so its chords must sag less than that at the distance the zoom is seen
  from — an eighth of a tile up to z10. An absolute 2° bound made discs of background in the sea.
- **D3D line antialiasing puts coverage into alpha only**: blend such lines straight, never premultiplied.
- **The transparent stage's depth is read-only in vvvv** (`BindDepthAsResourceDuringTransparentRendering`):
  depth writes there are dropped in silence; 3D buildings draw on their own copy of the scene's depth.
- **Measure before fixing a hitch**: the `Globe perf` lines in vvvv's log, with `GC.GetTotalPauseDuration`;
  a gen-2 count in the same second was not the cause.
- **No drape, no imagery of the globe's own** (2026-11-24): every raster layer is a `Raster` pass of GlobeVector on
  each wanted globe tile's background grid, an ancestor's image through a rectangle; the tiles are one colour. **Stride's PSMain returns its streams**:
  a `return;` in an SDSL pixel shader is X3080 - branch with if/else. SkSL shaders crash SkiaSharp 2.88's CPU backend.
- **Edit scripts read and write with `newline=''`**: Git Bash `sed -i` turned a CRLF file into LF, and
  Python's text mode turned LF into CRLF — each a "whole file changed" diff.

## Commands

```powershell
dotnet test test\VL.Globe.Tests\VL.Globe.Tests.csproj     # close vvvv first
.\tools\Fetch-MapLibreFixtures.ps1                          # once: MapLibre's render-test fixtures (git-ignored)
.\build.ps1 ; .\pack.ps1                                     # pack.ps1 is not optional: same-version cache
.\tools\Compile-HelpPatches.ps1 -OutputDirectory <abs>       # rung 2, plus the node table and dropped-link checks of rung 3
.\tools\Open-HelpPatch.ps1 -Path <help .vl> -Log             # rung 4; then close YOUR pid with WM_CLOSE
Open-HelpPatch.cmd                                           # the user's GUI launcher; always passes -Log
.\tools\Drive-Renderer.ps1 -Title <window> -Action wheel|drag ...   # real mouse input; user away only
D:\2026_Projects\vl-overworld\tools\Capture-Renderer.ps1 -Title <window> -Out x.png
```

vvvv's log: `%USERPROFILE%\Documents\vvvv\gamma\vvvv_<time>.log`. The Globe node writes its Status
there; Drag and ZoomByWheel write every press, release and wheel event.
**The user names a view by its log line** (2026-11-22, asked for): after every drag, rotate and
wheel zoom the camera nodes log `Globe view: #zoom/lat/lng/bearing/pitch - eye N m up` (MapLibre's
URL hash, its zoom one below ours). Ask for the time; load the hash in a test with
`GlobeView.TryApplyHash`, or paste it into a MapLibre page to see GL JS's picture of the same view.

`tools\` was copied from vl-mapsui on 2026-11-09 with the package name replaced; the node table in
`Compile-HelpPatches.ps1` and `Test-VLPatch.ps1` read VL.Globe's own `tools\Nodes.psd1`.
