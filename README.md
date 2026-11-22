# VL.Globe

**Early — not ready for real work.** Web Mercator raster tiles on a sphere in vvvv gamma's Stride,
requesting only the tiles the camera can see, and conversion between longitude/latitude and
positions on that sphere.

The scope, the name and the first surface were decided in VL.Overworld's
`docs/GLOBE-SCOPE-PROPOSAL.md` (2026-11-09), after three lab experiments proved the idea in
patches (`lab\Experiment globe*.vl` there).

| node | status |
|---|---|
| `GlobeWindow [Globe]` — a globe in one window | built, seen in vvvv |
| `Globe [Globe]` — the tile engine, camera-relative to street level | built, seen in vvvv |
| `OpenStreetMap [Globe]`, `XyzTiles [Globe]` | built |
| `GlobeCamera`, `Drag`, `ZoomByWheel`, `DragRotate [Globe]` | built, seen in vvvv |
| `GlobePositionComponent [Globe]` — your own entities on the globe | built, seen in vvvv |
| `ScreenToGlobe`, `GlobeToScreen`, `GlobeViewInfo [Globe]` | built, tested |
| `Geometry`, `FeatureLayer [Globe.Layers]`, `VectorStyle [Globe.Styles]` | built, seen in vvvv |
| `VectorTiles [Globe.Layers]` — a MapLibre style's vector tiles, labels and icons | built, seen in vvvv |
| `MapLibreStyle (URL)`, `MapLibreStyle (File) [Globe.Styles]` — the style it draws | built |
| `ToGlobe [Globe]`, `ToGlobe (Coordinate) [Globe]`, `FromGlobe [Globe]` | built, tested |

Requires vvvv gamma 7.4 (.NET 8, Stride 4.2). Depends on NetTopologySuite 2.6.0,
NetTopologySuite.Features 2.1.0 and BruTile 5.0.6; VL.Stride and SkiaSharp ship with vvvv.
Start with the help patch `HowTo Open a globe window`.

To edit a vector-tile style, save it as a `.json` file, open it in
[Maputnik](https://maplibre.org/maputnik/) (MapLibre's style editor), read it with
`MapLibreStyle (File)`, and press its `Read` after each save (help patch `HowTo Draw vector tiles`).
