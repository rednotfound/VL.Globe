# VL.Globe

**Early — not ready for real work.** Web Mercator raster tiles on a sphere in vvvv gamma's Stride,
requesting only the tiles the camera can see, and conversion between longitude/latitude and
positions on that sphere.

The scope, the name and the first surface were decided in VL.Overworld's
`docs/GLOBE-SCOPE-PROPOSAL.md` (2026-11-09), after three lab experiments proved the idea in
patches (`lab\Experiment globe*.vl` there).

| node | status |
|---|---|
| `ToGlobe [Globe]`, `ToGlobe (Coordinate) [Globe]`, `FromGlobe [Globe]` | built, tested |
| `Globe [Globe]` — the tile engine | built, seen in vvvv |
| `OpenStreetMap [Globe]`, `XyzTiles [Globe]` | built |

Requires vvvv gamma 7.4 (.NET 8, Stride 4.2). Depends on NetTopologySuite 2.6.0; VL.Stride ships
with vvvv.
