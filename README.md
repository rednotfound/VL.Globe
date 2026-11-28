# VL.Globe

A globe of map tiles for vvvv gamma, drawn with Stride, from the whole Earth down to street level:

* Raster tile layers: OpenStreetMap or any XYZ tile service, stacked with an opacity, cached on disk
* Vector tiles drawn from a MapLibre style: fills, lines, labels, icons and 3D buildings
* Your own geometry and features on the globe, with a lat/lon graticule
* A camera that moves like a map: drag, wheel, turn and tilt, fly to a place, frame your data
* Picking what is under the mouse, converting between screen pixels, degrees and globe positions
* Your own Stride entities standing on the globe, and a sun that lights the buildings
* A globe in one node, `GlobeWindow`, that answers the mouse; or the `Globe` entity, to put in a scene of your own

**Early: not published yet, and the nodes may still change.**

For use with vvvv, the visual live-programming environment for .NET: http://vvvv.org

## Getting started
- Requires vvvv gamma 7.4 (.NET 8, Stride 4.2).
- Not on nuget.org yet. To try it, clone this repository, run `.\build.ps1` and `.\pack.ps1`, and add its `dist` folder to vvvv's [package repositories](https://thegraybook.vvvv.org/reference/hde/commandline-arguments.html).
- Usage examples are included in the pack and can be found via the [Help Browser](https://thegraybook.vvvv.org/reference/hde/findinghelp.html). Start with *HowTo Open a globe window*; every node opens its own example with **F1**.

## Map tiles
- Tiles are fetched from the internet as soon as a layer is drawn; **Enabled** switches a layer off. The help patches ship with it off: switch it on.
- Raster tiles are cached under `%LOCALAPPDATA%\VL.Mapsui\tiles` (shared with VL.Mapsui), vector tiles under `%LOCALAPPDATA%\VL.Globe\vector-tiles`, for 7 days. `TileCache` sets another folder, a size limit, and clears it.
- Each tile service sets its own terms. The credit prints in the window by itself; for an `XyzTiles` layer or your own data, fill in its `Attribution` pin.
- To edit a vector tile style, save it as a `.json` file, open it in [Maputnik](https://maplibre.org/maputnik/), read it with `MapLibreStyle (File)`, and press its `Read` after each save.

## Related packages
- [VL.NetTopologySuite](https://github.com/rednotfound/VL.NetTopologySuite): make the geometry drawn on the globe
- [VL.NetTopologySuite.GeoJSON](https://github.com/rednotfound/VL.NetTopologySuite.GeoJSON): read and write GeoJSON
- [VL.Mapsui](https://github.com/rednotfound/VL.Mapsui): the same layers on a flat map, as a VL.Skia layer
- [VL.PROJ](https://github.com/rednotfound/VL.PROJ): move coordinates from one coordinate system to another

## Contributing
- Report issues on [GitHub](https://github.com/rednotfound/VL.Globe/issues)
- What exists and why: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); what is planned: [docs/ROADMAP.md](docs/ROADMAP.md)
- When making a pull-request, please make sure to read the general [guidelines on contributing to vvvv libraries](https://thegraybook.vvvv.org/reference/extending/contributing.html)

## Credits
Vector tiles are drawn after [MapLibre GL JS](https://github.com/maplibre/maplibre-gl-js) (BSD-3-Clause) and checked against its render tests. Uses [BruTile](https://github.com/BruTile/BruTile) (Apache-2.0) for tile schemas and [NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite) (BSD-3-Clause) for geometry. Map data from OpenStreetMap is © OpenStreetMap contributors, available under the [ODbL](https://www.openstreetmap.org/copyright).

## License
[MIT](LICENSE)
