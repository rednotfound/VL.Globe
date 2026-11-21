# CLAUDE.md

Guidance for Claude Code working in this repository.

## Start here — the next session begins with ROADMAP 0.1 item 4c, the `VectorTiles` source

1. Read **[docs/ROADMAP.md](docs/ROADMAP.md)** — planned by release: **0.1 = VL.Mapsui's basics on a smooth
   globe plus draped vector tiles, 0.2 = terrain** (the user's decisions). Items 1, 1b, 2 are done and
   judged; vectors are **draped** (item 3, decided 2026-11-21); Geometry/FeatureLayer/VectorStyle exist
   (item 4); **4b** is done — MapLibre's render tests run in `dotnet test` (48/48 supported pass; fetch
   the fixtures first with `tools\Fetch-MapLibreFixtures.ps1`). **4c** step 1 is done — the painter
   reads MVT (98/164 fixtures pass, 90 counted); step 2, the node `VectorTiles [Globe.Layers]`, is
   built and passes rungs 1-3 — **rung 4 in vvvv is next** (`HowTo Draw vector tiles`).
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
| `VectorTiles [Globe.Layers]` | built 2026-11-21: a MapLibre style's fills/lines/background draped from vector tiles; rungs 1-3, **not yet seen in vvvv** |
| `ToGlobe`, `ToGlobe (Coordinate)`, `FromGlobe [Globe]` | built, 4 rungs, **absolute** — right under OrbitCamera; under GlobeCamera use GlobePositionComponent |

160 tests green (incl. MapLibre v6.19.0's render tests — 164 fixtures, fetched by script). Branch `feature/camera-relative`, pushed to `github.com/rednotfound/VL.Globe`
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

`tools\` was copied from vl-mapsui on 2026-11-09 with the package name replaced; the node table in
`Compile-HelpPatches.ps1` is VL.Globe's own. `Test-VLPatch.ps1` and `HelpPatchGen.ps1` still carry
VL.Mapsui's node lists (ROADMAP item 6).
