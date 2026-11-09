# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

**VL.Globe** — Web Mercator raster tiles on a sphere in vvvv gamma's Stride, requesting only the
tiles the camera can see, plus longitude/latitude ↔ globe-position conversion. The fifth member of
the VL.NetTopologySuite family (siblings: `D:\2026_Projects\vl-nettopologysuite`, `vl-mapsui`,
`vl-geojson`, `vl-overworld`). Its scope was decided by the user on 2026-11-09 from
`vl-overworld\docs\GLOBE-SCOPE-PROPOSAL.md`; **that document's §3–§5 and §8 are the spec** —
read it before adding a node. The three lab patches that proved the idea are
`vl-overworld\lab\Experiment globe*.vl` (ACT-IV-LAB experiments 5–7).

The rules the family runs on are the siblings': `vl-mapsui\docs\RULES.md` and its CLAUDE.md
(the dev loop, the four verification rungs, never leave vvvv running, close only your own pid,
`Enabled` off by default on anything that fetches, edit .vl in place, NOTES.md is the failure log).
They are not repeated here.

## Where it stands — 2026-11-09

| node | state |
|---|---|
| `ToGlobe [Globe]`, `ToGlobe (Coordinate) [Globe]`, `FromGlobe [Globe]` | built; 13 tests; `HowTo Place a point on the globe` passes all four rungs (`docs\rung4-toglobe-tokyo.png`: an orbit camera aimed by yaw −lon/360, pitch −lat/360 puts ToGlobe's Tokyo at the exact centre) |
| `Globe [Globe]` — the tile engine | next (proposal §3 phase 1, §8 for the Stride/BruTile API) |
| `OpenStreetMap [Globe]`, `XyzTiles [Globe]` | next |

## Facts this repo paid for

- **Category = namespace + class.** `ImportAsIs(Namespace = "VL")` strips `VL`, so the static
  nodes live in `namespace VL` in a class `[Name("Globe")]` → category `Globe`. A process node
  will name its category explicitly, as VL.Mapsui's do.
- **Axes are Stride's Sphere's**: y north, lon 0 / lat 0 is +z, 90°E is +x. Four tests pin it.
- **A C# optional parameter cannot precede an `out` one**, so `FromGlobe`'s `Radius` pin is
  declared last; without a default it would arrive as 0 from an unconnected pin.

## Commands

```powershell
dotnet test test\VL.Globe.Tests\VL.Globe.Tests.csproj     # close vvvv first
.\build.ps1 ; .\pack.ps1                                     # pack.ps1 is not optional: same-version cache
.\tools\Compile-HelpPatches.ps1 -OutputDirectory <abs>       # rung 2 + the node table's rung 3
.\tools\Open-HelpPatch.ps1 -Path <help .vl>                  # rung 4; then close YOUR pid
```

`tools\` was copied from vl-mapsui on 2026-11-09 with the package name replaced; the node table in
`Compile-HelpPatches.ps1` is VL.Globe's own. Test-VLPatch.ps1 and HelpPatchGen.ps1 still carry
Mapsui's node lists and are not used yet.
