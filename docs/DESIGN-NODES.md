# The node surface — content, camera and window, 2026-11-23

The user, 2026-11-23: "现在既有layer source又有layers，怎么样做才合理？... init lat 等等的，这些有可能放给另外的
节点来做设置么？就像stride做的那样！我们需要做得专业一点，像专业的vvvv gamma package一样呀！！请广泛地搜索一下！"

Today `GlobeWindow` has three content inputs of three kinds — `Tile Source` (one raster basemap),
`Layers` (FeatureLayer, Geometry, VectorTiles) and `Input` (Stride entities) — and the camera's start
(`Initial Longitude/Latitude/Height`, `Min Height`) as its own pins. This is the survey and a proposal.

## 1. How vvvv gamma's own packs do it (read in vvvv 7.4's source patches and compiled packs)

| Node | Content | Camera / view | Pins in order (Update) |
|---|---|---|---|
| SceneWindow (Stride) | `Input`: one scene from **RootScene** | `Camera`, `Enable Default Camera` | Input, Camera, Enable Default Camera, Title, …, Clear Color, Clear, Post Effects, …, Enabled |
| SceneTexture (Stride) | `Input` | `Camera` | Input, Size, Camera, Clear Color, …, Enabled |
| Renderer (Skia) | `Input`: one layer | none (`Space`); the camera is a layer wrapper | Input, Title, …, Color, Clear, Space, Show Cursor, VSync, …, Enabled |
| SceneWidget (ImGui.Stride) | `Input` | `Camera` | as SceneWindow |
| AudioOut | `Input`: one signal | – | Input, Channel Offset |

- **One main `Input` of one type, composed upstream.** Stride: entities → `Group` (a `Child` pin
  group, grown with Ctrl+) → `RootScene` → SceneWindow. Skia: layers → `Group` ("Leftmost is drawn
  first"), a spread → `Group (Spectral)` → Renderer. No window has a pin per kind of content; extra
  kinds are optional advanced pins (Post Effects, Additional Scene Renderers).
- **The camera is a node of its own**, wired to the window's `Camera` pin right after `Input`:
  OrbitCamera — Initial Interest, Initial Yaw, Initial Pitch, Initial Distance, Initial Vertical Field
  Of View, Projection, Near/Far Clip Plane, …, Show Helper, **Reset**, **Enabled**; outputs Output
  (Entity), Position, …. It reads the mouse itself (`Auto Fetch Input Source`, on by default). It
  starts from its `Initial …` values; Reset (or holding R in the window) goes back to them.
  FlatCamera (2D): Initial Interest, Initial Height, Minimum/Maximum Height, …, Reset, Enabled.
- **SceneWindow without a camera** runs its own OrbitCamera (`Enable Default Camera`): a patch can
  start with nothing wired and still look around.
- **Source → layer, as separate nodes, where both exist:** VL.Video — VideoPlayer (a source) →
  VideoSourceToSKImage → DrawImage (a layer) → Group → Renderer. Only the layer reaches the window.
- The Gray Book: "Main input left, .. Reset typically right" (design-guidelines, Order of Pins).
- No third-party map, tile or globe pack for gamma was found (forum "Import map tiles?" 21042).

Evidence: `packs\VL.Stride.Runtime\VL.Stride.Engine.vl` (OrbitCamera, Group, RootScene, SceneWindow),
`packs\VL.Stride\help\Cameras\Explanation Overview Camera Handling.vl`, `packs\VL.Skia\help\Overview\
Explanation Camera.vl`, `…\Explanation Spectral grouping of layers.vl`, `packs\VL.Video\help\HowTo Play
a video file with Skia.vl`.

## 2. How map and globe libraries do it

(From the libraries' documented APIs; written from knowledge, not re-read page by page this time.)

| Library | Basemap | Source vs layer | Order | 3D content | Initial view |
|---|---|---|---|---|---|
| Mapsui (our sibling's engine) | a layer in `Map.Layers` | TileLayer(ITileSource) | list order, bottom first; Opacity | – | Navigator |
| MapLibre GL JS | a raster layer | `sources{}` + `layers[]` | array order; raster-opacity | layers (fill-extrusion); terrain apart | map options; flyTo |
| CesiumJS | `imageryLayers` (own collection) | ImageryLayer(ImageryProvider) | collection; alpha | `entities`, `primitives` apart; terrain apart | `viewer.camera` |
| deck.gl | a layer | data prop of the layer | array order | layers | `views` + `initialViewState` |
| OpenLayers | a layer | every layer has a `source` | collection / zIndex | – | `ol/View` object |
| Leaflet | a layer | in the layer | order added | – | map options |
| ArcGIS | `basemap` (own slot) | in the layer | index; opacity | layers; ground apart | `MapView/SceneView` object |
| iTowns | a ColorLayer | explicit Source objects | ordering API; opacity | layers; elevation a layer kind | GlobeView placement |

- **Everything on the surface is a layer in one ordered list, the basemap the bottom one** (Mapsui,
  MapLibre, deck.gl, OpenLayers, Leaflet, iTowns, WorldWind). A separate basemap slot is the product
  SDKs' (ArcGIS, Google). Raster layers stack with an opacity each, everywhere.
- **What is not draped gets its own place**: terrain (all of them), Cesium's entities and primitives.
- **The camera is its own object** (ol/View, Cesium camera, ArcGIS view, deck.gl views): the start of
  the view belongs to the camera, not to the layers.

## 3. Proposal

1. **One content input, layers composed upstream, as Skia's.** `GlobeWindow`/`Globe` take `Input`:
   one globe layer. `Group [Globe.Layers]` (a `Child` pin group, leftmost at the bottom) and
   `Group (Spectral)` (a spread) compose them. Every surface thing is a layer node:
   `OpenStreetMap` and `XyzTiles` become raster layers (with `Opacity`, `Enabled`), beside
   `VectorTiles`, `FeatureLayer`, `Geometry`. The `Tile Source` pin goes. Raster layers then stack —
   ROADMAP item 6's "several imagery layers with Opacity" becomes the engine's work under this.
2. **Raster layer nodes take their source directly** (as Mapsui's OpenStreetMap layer, and as our
   VectorTiles takes a style): one node for the common case. A separate source node only where a
   source is shared or chosen (a tile cache, an offline folder) — later, if asked.
3. **3D entities stay apart**, Cesium's way and Stride's: an optional `Scene` pin taking an Entity from
   Stride's Group (today's `Input`, renamed — `Input` becomes the layers).
4. **The camera is a node, as OrbitCamera.** `GlobeCamera`: Initial Longitude, Initial Latitude,
   Initial Height, Initial Heading, Initial Tilt, Min/Max Height, Field Of View, Max Tilt, …, **Reset**,
   **Enabled**; it reads the mouse itself (left drag, wheel, right drag), as OrbitCamera does, so Drag,
   ZoomByWheel and DragRotate stay for patches that want their own gestures. `GlobeWindow` gets a
   `Camera` pin right after `Input`; unconnected, it runs its own GlobeCamera (as SceneWindow's
   default camera). The `Initial …` and `Min Height` pins leave GlobeWindow.
5. **GlobeWindow's pins then:** Create — Bounds, Bound to Document; Update — Input, Camera, Title,
   Background, Scene*, Enabled (* optional). Outputs — View, Cursor, Attribution, Status.

Where this differs from the survey, on purpose: the camera node also does the gestures by default
(OrbitCamera does; FlatCamera too) — kept, since a map that does not move under the mouse surprises.

Breaking changes are fine: VL.Globe is 0.0.1-alpha, unpublished. VL.Mapsui's `Map` takes a spread of
layers and the start of the view as its own pins — the family would follow later, if wanted.

## 4. Decided (the user, 2026-11-23)

- **One `Input` + Group** (as Skia): every surface thing a layer, `Group [Globe.Layers]` and
  `Group (Spectral)` compose them; OpenStreetMap and XyzTiles become layers; today's `Input` (Stride
  entities) becomes the optional `Scene`.
- **GlobeCamera as OrbitCamera**: Initial … pins, Reset, Enabled, the mouse read by the camera;
  GlobeWindow's `Camera` pin, its own camera when none is wired; the Initial pins leave GlobeWindow.
- Order of the work: layers first (step 1), the camera second (step 2), each verified and committed.

## 5. As built

- **Step 1, layers — built 2026-11-23**, seen in vvvv (the bench's VectorTiles into Input; the shapes help
  patch's OpenStreetMap + Geometry through Group). GlobeWindow: Bounds, Bound to Document | Input, Title,
  Initial Longitude, Initial Latitude, Initial Height, Min Height*, Background, Scene*, Enabled | View,
  Cursor, Longitude, Latitude, Height, Attribution, Status. Several raster layers: the bottom one is drawn,
  the rest named in Status (stacking: ROADMAP item 6).
- **Step 2, the camera — built 2026-11-23**, seen in vvvv (wired and own camera; the camera's drag logic
  alive on the window's mouse). GlobeCamera: Initial Longitude, Initial Latitude, Initial Height, Initial
  Heading, Initial Tilt, Min Height, Max Height, Field Of View, Max Tilt, Radius, Mouse*, Screen Height*,
  Reset, Enabled | Output, View. GlobeWindow: Bounds, Bound to Document | Input, Camera, Title, Background,
  Scene*, Enabled | View, Cursor, Attribution, Status. The help patches got a GlobeCamera where their
  window had Initial values, and GlobeViewInfo where they read the height. The feel of the gestures:
  the user's to judge.
