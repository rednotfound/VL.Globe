# Picking vector tile features (ROADMAP 6d)

Asked by the user 2026-11-25: "vector tiles中的对象是能够选择的么？对象是带有信息的么？" They carry their
properties (OpenMapTiles' `name`, `class`, `render_height`, ...; the style reads them), but `Pick [Globe]`
looks only in FeatureLayer and Geometry. This makes `Pick` find what a VectorTiles layer draws, as MapLibre
GL JS's `queryRenderedFeatures` does - one Pick for every kind of layer, as one draw path for every kind.

## What the references do (read from source 2026-11-25; the survey's copies in the session scratchpad)

| | MapLibre GL JS (`query_features.ts`, `feature_index.ts`) | CesiumJS | deck.gl MVTLayer |
|---|---|---|---|
| how | CPU: per tile a 16×16 grid of feature bboxes (EXTENT 8192); the query point in tile units; the style layer's own test | GPU: a pick pass draws ids into a 3×3 px framebuffer | GPU: picking colours into an offscreen buffer |
| data kept | the tile's raw bytes, decoded again on a query | the batch table (3D Tiles) | binary features, made GeoJSON on a pick |
| filter / zoom | the layer's filter and zoom range applied when the bucket was built (no index entry otherwise) | - | - |
| fill | polygon contains the point (fill-translate applied) | | |
| line | buffered by half the width + offset, pixels → tile units per tile | | |
| circle | radius + stroke, in pixels or in map units by pitch-alignment | | |
| fill-extrusion | the prism's roof and walls projected; returns the distance; the query region runs from the point towards the camera's nadir (8 points along the sphere on the globe) so a tall building in front is found | the nearest drawn | the nearest drawn |
| symbols | placed labels and icons via the collision index in screen pixels; collided labels never returned | | |
| order | style layer order top-first; every fill-extrusion hit sorted by distance, nearest first | nearest | nearest |
| result | GeoJSON feature: properties, id (or promoteId), layer, source, sourceLayer, state; geometry in lng/lat, the tile's clipped copy | `Cesium3DTileFeature.getProperty` | `info.object` as GeoJSON |
| duplicates | per tile by feature index; none across tiles | | |

## The design

- **`Pick [Globe]` unchanged in its pins** (View, Input, Cursor, Tolerance → Output, Hit, Layer, Longitude,
  Latitude). In the Input, a VectorTiles layer is now looked in too, in its place: layers top-first, within a
  style its layers top-first (GL JS's order), fill-extrusion hits by distance.
- **Output**: an NTS `Feature` - the tile's geometry in lon/lat (clipped to the tile, as GL JS), its properties
  as the attributes, plus `$id` (the feature id or promoteId), `$layer` (the style layer's id) and
  `$sourceLayer`. `Layer` (the output pin) is the style layer's id for a vector tile hit, the layer's Name otherwise.
- **The tile**: the source's tile at the floored camera zoom (`GpuVectorTiles.VectorKey`'s rule), the finest one
  already on disk if that is not (`VectorTileService.Peek`), never fetched by a pick. Decoded with
  `VectorTile.Decode` and kept in a small LRU in the Pick node (8 tiles): the cursor moves within a tile
  most frames. Decoding on the main thread is ms for a typical tile; a worker if measured slow.
- **The tests** (local metres round the hit point, as Pick does for shapes; pixels → metres at that point):
  - the layer visible at the camera zoom (`minzoom`/`maxzoom`, `visibility`), its `filter` (`Expression.FilterFeature`);
  - fill: the polygon contains the point; line: within half its width (evaluated per feature) + Tolerance;
    circle: within radius + stroke + Tolerance; the layer's translate applied where it has one;
  - **fill-extrusion**: the cursor's ray, not the ground point. In the tangent plane, the ray between the
    height h and the base b of a building projects to a ground segment; the building is hit where that segment
    meets its footprint, the distance where it enters. Candidates: features along the segment from the
    highest building's height down to the ground point (GL JS's camera query region, in metres).
- **Not in this item**: labels and icons (they need the placement's screen boxes, which live in the renderer;
  a second step through the Globe node if asked), feature-state, a query by box, raster layers.

## Tests (offscreen, no network: styles with inline GeoJSON sources cut into tiles as VectorTiles cuts them)

- a fill under the cursor, its properties out; outside the polygon, nothing
- a line hit within its width at the camera zoom, missed past it
- a layer's filter and zoom range respected; a hidden layer never hit
- two layers over one point: the upper one first
- tilted at street level, a tall building in front of the cursor's ground point is hit by its wall, the
  nearer of two buildings first
- the help patch `HowTo Pick a feature` gains a VectorTiles cell; rung 4 in vvvv over Tokyo
