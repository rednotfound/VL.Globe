# Toward 0.1 — research, 2026-11-21

The user's goal for 0.1: "at least the basic functions Mapsui covers", while thinking ahead to terrain,
with GeoLibre as a project to imitate and learn from. This document collects the research done on
2026-11-21 from primary sources: GitHub code of CesiumJS `main`, MapLibre GL JS v5 and `main`, and
loaders.gl/deck.gl, plus vendor docs and the GeoLibre fork. It ends with the proposal that the
ROADMAP revision is decided from.

## 1. What VL.Mapsui covers, and its globe equivalent

VL.Mapsui has 33 nodes (`vl-mapsui/docs/GUIDE.md`, "Not one map node").

| Mapsui | On the globe |
|---|---|
| MapWindow, Map | `GlobeWindow`, `Globe` — exist |
| OpenStreetMap, XYZ, TileCache | `OpenStreetMap`, `XyzTiles` — exist; the cache is shared by folder convention |
| ViewportInfo | **rethink**: centre lon/lat, height, zoom level, heading. A bounding box fits a sphere poorly |
| LayerInfo | applies to whatever layer types 0.1 has |
| Pick | ray against the sphere → lon/lat, then an NTS query within a tolerance **in metres** |
| ScreenToWorld, WorldToScreen | **rethink**: `ScreenToGlobe` can miss the globe (needs `Success`); `GlobeToScreen` needs `Visible` (far side) |
| Geometry, FeatureLayer | **the hard one**: NTS geometry on a curved surface (section 4) |
| VectorStyle, SymbolStyle, LabelStyle | keep their shape; labels become screen-facing text |
| StyleByGeometry, StyleByValue | port once FeatureLayer is stable |
| Graticule | lines on the sphere. Cheap, and it exercises FeatureLayer's line path |
| VisibleRange | by height instead of resolution |
| CenterOn, ZoomToLevel, ZoomIn, ZoomOut | direct. Level ↔ height goes through the selector's maths, or GeoLibre's ground resolution (`cesium-camera.ts` `groundResolution`) |
| DragBetween | `FlyTo`: great-circle path with a height arc (Cesium, MapLibre flyTo) |
| ZoomToLayer(s) | `ZoomToBounds`. Bounding cap centre and angular radius θ; eye distance ≈ R·cos θ + R·sin θ / tan(fov/2) |
| Drag, ZoomByWheel | exist |
| ScaleBar | ground distance of one pixel at a probe point, largest round length under 100 px (TerriaJS DistanceLegend). Hidden when the probe misses the globe |
| ZoomButtons, Click | direct. A Compass only once Rotate exists |

## 2. What the mature globes have (CesiumJS, MapLibre v5 globe, deck.gl GlobeView, globe.gl, Mapbox v3, ArcGIS SceneView)

- **Common to all six:**
  - several imagery layers with opacity and order;
  - points, lines, polygons and labels;
  - picking;
  - fly-to / fit-bounds;
  - an attribution display;
  - an atmosphere or sky.
- **Not common:**
  - a scale bar: Cesium has none;
  - a graticule: only globe.gl has one;
  - measurement: only as plugins.
- **Lines on a globe.** Cesium densifies along a geodesic or rhumb line (`granularity`). MapLibre subdivides every line and fill on the globe (granularity 512 / 128), and its lines stay straight in Mercator. deck.gl has a separate GreatCircleLayer.
- **Draped vectors** (Cesium GroundPrimitive, MapLibre render-to-texture) exist because terrain exists.
- **Trap: densify by metres, not by degrees.** Lines densified by degrees look right at country scale and facet at 20 m.

## 3. GeoLibre (`rednotfound/GeoLibre` = a fork of opengeos/GeoLibre, MIT, v3.2.0)

GeoLibre is a full GIS **application** written in React/TypeScript and packaged with Tauri. It **renders nothing itself**. Four engines sit behind one `MapEngine` interface: MapLibre (the default), Mapbox, CesiumJS for the real 3D globe, and ArcGIS. deck.gl draws the overlays. The store holds engine-neutral layer records, and each engine reconciles them.

**Worth taking:**
- **Its Terrarium terrain provider for Cesium** (`packages/map/src/cesium-terrarium.ts`, 155 lines). It is MIT and small, so it can be ported:
  - a 65×65 height grid per tile, sampling every 4th pixel;
  - `h = R·256 + G + B/256 − 32768`, and 0 where alpha is 0;
  - it fetches the right, bottom and corner neighbour tiles, so **adjacent tiles share identical edge heights**;
  - 16 requests in flight, a 128-entry cache.
- **Zoom ↔ camera distance by ground resolution** (`cesium-camera.ts`). It gives a `Zoom` that matches Mapsui's, so a 2D map and the globe can be synced.
- **An ordering lesson.** Terrain is attached before the camera is placed, because ground height is what turns a zoom into a distance. For us this means the floor, the clip planes and picking must follow the ground, not the sphere.
- **The feature set as a menu** for later:
  - elevation profile and terrain-surface measurement (`plugins/terrain-measure.ts`);
  - hiding labels behind the horizon (`globe-popup-occlusion.ts`);
  - COG DEMs re-encoded as Terrarium (`cog-dem-source.ts`);
  - planetary basemaps.
- **Vector tiles on its Cesium globe.** It renders a hidden MapLibre map one tile at a time into Cesium imagery (`cesium-drape.ts`). Its comments list the limits: flat only, labels collide only within a tile, and a second render context.

**Not to take:** the four-engine abstraction, DuckDB/GDAL-wasm, the whitebox toolbox, the app shell, Ion-gated content, 3D Tiles, I3S, point clouds and splats. They are what an application needs, not a package.

## 4. Terrain

### Data

| source | encoding | zoom | licence | verdict |
|---|---|---|---|---|
| **AWS Terrain Tiles** (`elevation-tiles-prod`, terrarium PNG) | `R·256 + G + B/256 − 32768` | z0–15, 256 px, Web Mercator | no key, no account; credit USGS SRTM/3DEP/GMTED and others ([attribution](https://github.com/tilezen/joerd/blob/master/docs/attribution.md)) | **start here**. Bare earth, our tiling, and decoded already in vl-overworld |
| Mapbox Terrain-RGB v1 | `−10000 + (R·65536 + G·256 + B)·0.1` | to z15 | token; frozen since 2021-12 | a later keyed source |
| Mapbox Terrain-DEM v1 | same | to z14 | **only inside Mapbox SDKs** | not usable |
| MapTiler terrain-rgb-v2 / quantized-mesh | WebP / qm | z0–14 | key; free plan non-commercial | a later keyed source |
| Cesium World Terrain | quantized-mesh | — | ion token; free tier non-commercial | ruled out |
| Copernicus GLO-30 | Float32 COGs, 1° tiles, EPSG:4326 | — | free, with a notice | a **DSM** (buildings, trees); we would build our own pyramid. Later, labelled as a DSM |

### How the engines do it

- **LOD.**
  - Cesium refines terrain by geometric error, `sse = error · bufferHeight / (distance · 2tan(fov/2))`, with a maximum of 2. The heightmap level-0 error is `R·2π·0.25 / (width · tiles)`, halving per level. The distance is taken to a bounding region that carries min/max height.
  - MapLibre drives selection by its render tiles, with the DEM one level coarser (`terrain_tile_manager.ts`).
- **Meshes.**
  - Cesium decodes on the CPU and stores vertices relative to the tile centre.
  - MapLibre uses one shared 129×129 grid displaced on the GPU from a DEM texture.
  - deck.gl uses Martini/Delatin RTIN meshes.
- **Cracks.** Every engine uses **skirts**. Cesium makes them error × 4 (heightmap, at most 1000 m) or error × 5 (quantized-mesh); MapLibre makes them metresPerTile / 5.
- **Seams at DEM borders.** MapLibre backfills a 2-px border from its neighbours. GeoLibre fetches the neighbours. deck.gl duplicates the last row, which leaves known seams.
- **Draping imagery.**
  - Cesium attaches imagery tiles to each terrain tile and samples Web Mercator imagery through a per-vertex `webMercatorT`.
  - MapLibre renders imagery and vectors to a texture per tile.
- **Camera.**
  - Cesium lifts the eye to the ground height plus `minimumZoomDistance` below 15 km, changing it by at most 10 % per frame unless the user moved, so it does not jump while tiles load.
  - MapLibre's `_elevateCameraIfInsideTerrain` re-solves pitch and zoom.
- **Picking.** Cesium picks the terrain mesh below 150 km and the ellipsoid above.

### Fit with VL.Globe

- **DEM level and mesh.** Ours is a 64-segment tile at level L+2 against a 256-px DEM at level L, sample for sample. So the DEM level is `min(L − 2, 15)`, with bilinear over-zoom beyond that. That is MapLibre's coupling.
- **CPU displacement.** It fits our mesh builder (Cesium's way). The radial offset `h / 6378137` is added in double before the centre is subtracted.
- **Bounds.** Grow each tile's bound by its min/max height. Everest is 0.0014 of the unit radius.
- **Seams.** Sample heights by lon/lat across tile borders, so neighbours compute identical edge heights.
- **Datum.** Terrarium heights are above sea level; `ToGlobe`'s `Height` should say so.

## 5. Proposal for 0.1, to be decided by the user

**Order matters.** Everything a patch places, and picking, depends on `PlaceOnGlobe` and on the ground. Terrain changes the floor, the clip planes, picking and placing. So the coordinate nodes are designed **terrain-ready** from the start: heights mean above sea level, and a `Height Reference` (on the ground / above the ground) is reserved, even if 0.1 only implements sea level.

**Must have (Mapsui's basics):**
- **Window and imagery:** GlobeWindow, Globe, OpenStreetMap, XyzTiles. Add imagery `Opacity` and several imagery layers in order.
- **Coordinates:** `PlaceOnGlobe`, ToGlobe/FromGlobe, `ScreenToGlobe` (`Success`), `GlobeToScreen` (`Visible`).
- **Reading:** `GlobeViewInfo` (centre lon/lat, height, zoom, heading) and `Pick`.
- **Vectors:** `FeatureLayer [Globe]` for NTS points, lines and polygons, densified by metres, camera-relative. Styles `VectorStyle`, `SymbolStyle` and `LabelStyle` in Mapsui's shape. `Graticule`.
- **Navigation:** CenterOn, ZoomToLevel, ZoomIn, ZoomOut, `FlyTo`, `ZoomToBounds`.
- **Widgets:** ScaleBar (Terria's method), ZoomButtons, Click, and the attribution shown.

**Terrain, staged:**
1. **Heights on the mesh.** A Terrarium source, Enabled off by default. Displacement in double, skirts, grown bounds, and a tile rebuilt with a crossfade when its DEM arrives.
2. **The ground governs the camera and picking.** The floor is the ground plus Min Height, with Cesium's 10 % smoothing. `FromGlobe` and `Pick` march against the heights. `PlaceOnGlobe` gains `On the ground`.
3. **Light.** Normals from the DEM, hillshade, and an exaggeration that scales the clamps.
4. **Later.** GPU displacement, keyed sources, a Copernicus DSM, elevation profiles.

**After 0.1:**
- Tilt and Rotate, then a Compass;
- depth precision for tilt;
- great-circle arcs;
- extrusion;
- GPU picking;
- atmosphere and sun;
- polar caps;
- measurement;
- StyleByValue and StyleByGeometry;
- VisibleRange;
- vector tiles.

**Open decisions:**
- Does terrain stage 1–2 belong in 0.1, or does 0.1 ship flat with terrain-ready interfaces and terrain come in 0.2?
- Are lines and polygons drawn as **3D geometry** (crisp at any zoom; must follow the terrain on their own once it exists) or **draped into the tile textures** with VL.Skia (Mapsui's styles nearly for free and terrain-proof; resolution bound to the tile, labels clipped at tile edges, a re-render whenever data changes)?
