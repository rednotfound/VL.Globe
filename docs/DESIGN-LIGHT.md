# Light (ROADMAP 0.1 item 6b)

Asked by the user 2026-11-24: light and shade, above all on VectorTiles' buildings. Decided the same
day: two kinds of light as two nodes (a fixed direction, the sun from a date and time); steps A (a light
that can be set) and B (ambient occlusion) first; **the user's own Stride models lit by the same light**.
Cast shadows (C) after A and B are judged.

## What is there today

`shaders/GlobeVector.sdsl` lights fill-extrusion with GL JS's default light, hard-coded: anchored to the
viewport at [1.15, 210, 30], intensity 0.5, white, 0.03 ambient, the walls' vertical gradient. Nothing
else in the globe is lit: the tiles are emissive ("the map is the light").

## The survey (read 2026-11-24 from each library's source or spec)

| | MapLibre GL JS v5 | Mapbox GL JS v3 (proprietary: ideas only) | CesiumJS | deck.gl | ArcGIS JS 5.2 SceneView |
|---|---|---|---|---|---|
| objects | style root `light` - the only one | style root `lights`: `ambient`, `directional`, `flat` (= the old `light`) | `scene.light` = `SunLight` (default) or `DirectionalLight` | `LightingEffect` of `AmbientLight`, `DirectionalLight`s, `PointLight`s; `SunLight extends DirectionalLight` | `environment.lighting` = `sun` (default) or `virtual` |
| direction | [r, azimuth, polar]; anchor `viewport` (default: azimuth from the screen's top) or `map` (from north) | [azimuth from north, polar 0-90], map-anchored | world (ECEF) vector | Cartesian; SunLight turned into the globe's frame | sun: astronomical; virtual: follows the camera, "slight top-right offset" |
| defaults | [1.15, 210, 30], white, 0.5 | directional [210, 30], 0.5; ambient 0.5 | Sun white, intensity 2.0 | ambient 1.0; two directionals if none given | sun date 2022-03-15 12:00 UTC |
| sun from a date | no | no (Standard's `lightPreset` dawn/day/dusk/night) | yes, from the clock (Simon 1994) | yes, `SunLight({timestamp})` (math.gl `getSunDirection`; older code after suncalc) | yes, `date` |
| ambient | 0.03 in the shader | `ambient` light | (image-based, not studied) | `AmbientLight` | implicit |
| shadows | none | `cast-shadows` (off), `shadow-intensity` 1; 2 cascades, 2048² | `scene.shadows` (off): 4 cascades, 5000 m, darkness 0.3, 2048², fade near the horizon | `_shadow`, experimental | `directShadowsEnabled` (off) |
| AO on extrusions | none | `fill-extrusion-ambient-occlusion-*` (intensity 0, wall radius 3, ground radius 3 m, ground attenuation 0.69): **per vertex from the geometry** (concave corners, height above ground) + a ground-halo pass | screen-space HBAO post effect | none | `globalIllumination` (beta): screen-space |

**vvvv's own lights** (`VL.Stride.Engine.vl`, canvas Lights, category `Stride.Lights`): `DirectionalLight`,
`AmbientLight`, `PointLight`, `SpotLight`, `SkyboxLight`, `ProjectorLight`. Each is a patched node whose
output is an **`Entity`** carrying a `LightComponent`, wired into `RootScene` beside the models
(help: `VL.Stride\help\Lights\Explanation Overview Lights.vl`). DirectionalLight: `Position`, `Target`
(direction = Position -> Target, no Rotation pin), `Color`, `Intensity`, `Shadow`, `Enable Default
Shadow`, ..., `Enabled`, `Show Helper`.

**Stride lets a custom renderer read the scene's lights**: `LightProcessor` rebuilds, every frame before
the render stages, a `RenderLightCollection` kept in `VisibilityGroup.Tags[ForwardLightingRenderFeature.CurrentLights]`;
each `RenderLight` has `Type` (`LightDirectional`, `LightAmbient`, ...), the world `Direction` (the way
the light travels) and `Color` (linear, times the intensity). `VectorRenderer.Draw` has
`context.RenderContext.VisibilityGroup` at hand. (Read from Stride 4.2.1's assemblies; not yet seen at
run time - a null check and a test.)

## The design (proposed)

**A light is a Stride light entity in the scene, as in vvvv.** Our nodes output an `Entity` with a
`LightComponent` (`LightDirectional`); the user wires it into the Scene with their models, exactly as
vvvv's `DirectionalLight`. Stride then lights the user's models by it - item 3 of the user's choices,
for free - and the buildings' shader reads the same light from the scene's light collection. vvvv's own
`DirectionalLight` and `AmbientLight` work too (a fixed direction in the globe's frame, i.e. a sun that
stands still over one place).

Precedence for the buildings: a directional light in the scene; else the style's `light`; else GL JS's
default (today's look - nothing changes for a patch without a light).

Axes are the globe's (y north, lon 0 / lat 0 = +z, 90°E = +x); GlobeCamera's camera-relative frame only
translates, so a direction is the same in both.

### Nodes

| node | inputs (vvvv's order) | output | precedent |
|---|---|---|---|
| `SunLight [Globe]` | `Date Time` (UTC), `Color`, `Intensity`, `Enabled` | `Entity` | Cesium SunLight, deck.gl SunLight, ArcGIS `sun` |
| `DirectionalLight [Globe]` | `Camera`, `Azimuth` (210°), `Polar Angle` (30°), `Anchor` (Viewport / Map), `Color`, `Intensity`, `Enabled` | `Entity` | MapLibre's `light` (names, defaults, anchor), ArcGIS `virtual` |

- `SunLight`: the subsolar point from the date (declination and equation of time, NOAA's / suncalc's
  formulas, tested against published sun positions), the direction from it in the globe's frame. Process
  node (it owns the entity).
- `DirectionalLight [Globe]`: azimuth/polar relative to the view (Viewport) or to north and up at the
  view's centre (Map), so it needs the Camera - seven inputs, two over the family's target. Options:
  drop `Anchor` (Map only, MapLibre's non-default) or drop `Camera` by wiring it the way GlobeWindow
  wires the camera to Globe.
- Ambient: vvvv's `AmbientLight [Stride.Lights]`, read the same way; no node of ours.
- Intensity: GL JS's 0-1 scale (0.5) on our nodes; Stride's own lights' intensity mapped to it
  (to measure: what a vvvv DirectionalLight at its default looks like on our buildings).

### B. Ambient occlusion

Mapbox's way, per vertex from the geometry, no extra screen pass: walls darkened towards their foot
(within a few metres of their base) - in the shader, from the metres the vertex is raised by, already in
the vertex. Then a ground halo around each footprint (a second draw of a soft strip around the walls'
feet), if the wall term alone does not read as grounded. A `fill-extrusion-ambient-occlusion-intensity`
style key (Mapbox's name, default 0 there) or on by default here - to decide on the screen.

### Not in A/B

- The ground is not lit: raster tiles stay emissive, so night does not fall on the map ("Atmosphere
  and sun", ROADMAP After). A sun below the horizon only darkens the buildings.
- Shadows (C): a shadow map from the scene's directional light (Mapbox 2 cascades, Cesium 4 and 5 km),
  the ground receiving it in screen space from the depth, as GlobeFog does. Stride's own shadow maps
  shade the user's models; our buildings would not cast into them.

## Verification

- `SunLight`: unit tests against published subsolar points (equinox, solstices) and a known sun
  azimuth/elevation at a place and time.
- The shader: offscreen probes (VLGLOBE_PROBE=1) of a street-level tilted view with the light from two
  sides; the user's look in vvvv, a model of theirs beside the buildings.
- MapLibre's render tests fetched so far (409) carry no `light`; fetch the fill-extrusion/light ones.
