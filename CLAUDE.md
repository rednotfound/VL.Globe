# CLAUDE.md

Guidance for Claude Code working in this repository.

## Start here — the next session continues ROADMAP 0.1 item 4d, `VectorTiles` on the GPU

1. Read **[docs/ROADMAP.md](docs/ROADMAP.md)** — planned by release: **0.1 = VL.Mapsui's basics on a smooth
   globe plus draped vector tiles, 0.2 = terrain** (the user's decisions). Items 1, 1b, 2 are done and
   judged; vectors are **draped** (item 3, decided 2026-11-21); Geometry/FeatureLayer/VectorStyle exist
   (item 4); **4b** is done — MapLibre's render tests run in `dotnet test` (48/48 supported pass; fetch
   the fixtures first with `tools\Fetch-MapLibreFixtures.ps1`). **4c** step 1 is done — the painter
   reads MVT (98/164 fixtures pass, 90 counted); step 2, the node `VectorTiles [Globe.Layers]`, is
   built and seen in vvvv; the user found the drape "不太行" and chose the GPU (ROADMAP **4d**,
   [docs/DESIGN-GPU-VECTOR.md](docs/DESIGN-GPU-VECTOR.md), its "As built" and "Next"): built and
   judged by the user the same night ("大方向完全没有问题", "我觉得还行"), 178 tests green.
   Since then: the long Updates found (a per-frame large-object-heap list), 3D buildings, a 28-byte
   vertex, MapLibre's render tests on the GPU path (97 pass offscreen in `dotnet test`).
   Then labels (ROADMAP **4e**): point text and collision ported from GL JS, 154 fixtures pass on the
   GPU path, seen in vvvv on Liberty; then labels along lines (158 fixtures, 150 non-vacuous).
   Then icons (milestone 4), steps 1-4: sprites, point icons, the icon shader, joint placement -
   252 fixtures pass, 243 non-vacuous. Then step 5, icon-text-fit and stretch zones, with circle
   layers (needed by its fixtures) - 304 pass, 295 non-vacuous.
   Then step 6, symbols on lines (shields upright at line anchors, icons along lines) - 311 pass,
   302 non-vacuous; one-way arrows seen in vvvv at Kyoto Station, shields not yet seen.
   Then symbols past the source's maxzoom (laid out at the overscaled zoom, as GL JS) - street
   names along every street at Kyoto Station in vvvv.
   Labels now placed as GL JS (every 300 ms while moving): 60 fps tilted while no tiles arrive.
   2026-11-22 afternoon (NOTES.md, the last entries): the one-off hitch was the atlases (fixed, seen
   gone); layers hidden at a tile's zoom no longer built (Kyoto z14 9.1 -> 6.7 MB); line labels no
   longer step while panning close (the eye counts as a move); lines collide by first/last glyph.
   Placement still spikes 17-50 ms while tiles load in vvvv.
   Then: the style is a node (`MapLibreStyle (URL)` / `(File)`, Maputnik named for editing, the
   user's decision); 4f built - the tilt limited by the zoom; the fog built and then turned off by
   the user ("我宁愿没有"); vector tiles kept at the camera's zoom to 5 eye heights, 3D buildings
   fading out before that (seen tilted over Tokyo: "视野里面相对比较满了"; 853 MB there, over budget).
   A reported view is drawn offscreen by `ViewProbe` (VLGLOBE_PROBE=1 VLGLOBE_VIEW=#...).
   Then label fades (2026-11-22 night): CrossTileSymbolIndex, the pauseable placement (2 ms a frame),
   300 ms fades - built and tested (260 green, fixtures unchanged at 311); the user saw every fade begin
   with a blink (the fade change taken before the step: fixed, not yet seen again); the buildings'
   fade-out turned off ("还是太怪了"); seen again: "没什么问题". The GPU harness now turns by the
   style's bearing: 326 fixtures pass, 317 non-vacuous (viewport-glyph labels were turned to the line).
   Then point symbols turned with the map (rotation-alignment map, not yet pitched with it): 329 pass,
   320 non-vacuous, 262 tests green.
   **Next: ROADMAP 4g, as smooth as GL JS (the user, 2026-11-22: "远远不如人家那么流畅，还是卡卡的") -
   step 2 built (bytes halved, misses not); step 4 found the cause - workers' garbage (gen0 pauses),
   the style expressions interpreted per feature. Built: expressions compiled once, decoded tiles as a
   few arrays, buffers made on the workers, off-screen line labels not walked, parameters set through
   accessors - bench misses 3-5% -> 0.0-0.1%, worst frame 19-20 ms (GL JS 0%, 17). Zoom bench (2026-11-23, dotnet-gcdump):
   features as rows of arrays, line labels as structs - 3.4% -> ~1.1% (GL JS 0.2-0.4%); the user: "好很多". Misses now mixed
   (GC, label opacities, submit at 6000 draws); next: value strings as bytes, or measure those first.**
   Research: [docs/RESEARCH-0.1.md](docs/RESEARCH-0.1.md), [docs/RESEARCH-VECTOR.md](docs/RESEARCH-VECTOR.md).
2. Read **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — what exists and why.
3. Read **[NOTES.md](NOTES.md)** — every failure so far, before touching the same code.
4. The family's rules are the siblings': `D:\2026_Projects\vl-mapsui\docs\RULES.md` and that repo's
   CLAUDE.md (the dev loop, the four verification rungs, never leave vvvv running, close only your
   own pid, `Enabled` off by default on anything that fetches, edit a .vl in place, NOTES.md is the
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

## Where it stands — end of 2026-11-20

| node | state |
|---|---|
| `GlobeWindow [Globe]` — patched in `VL.Globe.vl` | built; the front door (`HowTo Open a globe window`); the user judged it on screen |
| `Globe [Globe]` — the tile engine | camera-relative, rounded levels, mipmaps, fades both ways, background sphere; user-judged |
| `OpenStreetMap`, `XyzTiles [Globe]` | built; disk cache shared with VL.Mapsui by folder convention; `XyzTiles` has no help patch |
| `GlobeCamera`, `Drag`, `ZoomByWheel [Globe]` | north stays up; wheel eased (one notch = Wheel Delta 1); vertical drag direction never asked (ROADMAP item 2) |
| `GlobePositionComponent [Globe]` | stands an entity on the globe, placed at draw time; user-judged |
| `ScreenToGlobe`, `GlobeToScreen`, `GlobeViewInfo [Globe]` | built; user-judged |
| `Geometry`, `FeatureLayer [Globe.Layers]`, `VectorStyle [Globe.Styles]` | built, draped into the tiles; seen in vvvv |
| `VectorTiles [Globe.Layers]` | built 2026-11-21: a MapLibre style's fills/lines/background from vector tiles, with or without a Tile Source; `Rendering` GPU (default, judged by the user moving) or Draped (seen standing still, "不太行") |
| `ToGlobe`, `ToGlobe (Coordinate)`, `FromGlobe [Globe]` | built, 4 rungs, **absolute** — right under OrbitCamera; under GlobeCamera use GlobePositionComponent |

224 tests green (incl. MapLibre v6.19.0's render tests — 409 fixtures, fetched by script — run twice: on the drape and, offscreen on a D3D11 device of the test's own, on the GPU path). Branch `feature/camera-relative`, pushed to `github.com/rednotfound/VL.Globe`
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
- **Draped overlays render on worker threads**; only wanted tiles fetch vector tiles, and an overlay
  is redrawn only when a tile it lacked has come. SkSL shaders crash SkiaSharp 2.88's CPU backend.
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
`Compile-HelpPatches.ps1` is VL.Globe's own. `Test-VLPatch.ps1` and `HelpPatchGen.ps1` still carry
VL.Mapsui's node lists (ROADMAP item 6).
