# CLAUDE.md

Guidance for Claude Code working in this repository.

## Start here — the next session begins with ROADMAP item 1

1. Read **[docs/ROADMAP.md](docs/ROADMAP.md)** — what to do next, in order. **Item 1, camera-relative
   rendering, was decided by the user on 2026-11-09 and is the next work**; its design and its
   verification are written there.
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

## Where it stands — end of 2026-11-09

| node | state |
|---|---|
| `Globe [Globe]` — the tile engine | built, 4 rungs: zoom 9–10 OSM over Gabon and the whole face of Africa (`docs/rung4-globe-*.png`) |
| `OpenStreetMap`, `XyzTiles [Globe]` | built; disk cache shared with VL.Mapsui by folder convention; `XyzTiles` has no help patch |
| `GlobeCamera`, `Drag`, `ZoomByWheel [Globe]` | built; the user confirmed in vvvv that drag and wheel work and no longer jitter. Still to confirm: vertical drag direction and wheel units (ROADMAP item 2) |
| `ToGlobe`, `ToGlobe (Coordinate)`, `FromGlobe [Globe]` | built, 4 rungs (`docs/rung4-toglobe-tokyo.png`) |

53 tests green. Not published, not pushed. **The known limit**: positions are absolute floats, so
below ~100 m the picture steps by ~0.4 m — ROADMAP item 1 removes it.

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
- **Drive input only when the user is not at the machine** (`tools\Drive-Renderer.ps1`).

## Commands

```powershell
dotnet test test\VL.Globe.Tests\VL.Globe.Tests.csproj     # close vvvv first
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
