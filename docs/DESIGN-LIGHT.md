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

## The design (proposed; the user's choices 2026-11-24 below)

**Decided 2026-11-24:** `DirectionalLight [Globe]` keeps both `Camera` and `Anchor` (MapLibre's shape,
seven inputs); ambient occlusion on by default; first `SunLight` and reading the scene's lights (prove
the path in vvvv), then `DirectionalLight`, then AO. Intensity is Stride's (1 = vvvv's DirectionalLight
default, so the user's models look as with vvvv's own light); the buildings read Stride 1 as GL JS's 0.5.

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

## Built (2026-11-24)

- **A.** `SunLight [Globe.Lights]` (Date Time, Color, Intensity, Cast Shadows, Enabled -> Entity): a Stride
  `LightDirectional` entity. `VectorRenderer.ReadLight` takes the scene's first directional light from the
  visibility group; `GlobeVector.sdsl` lights fill-extrusion with it in GL JS's formula. No light in the
  scene: GL JS's default, unchanged. Seen in vvvv against a baseline of the same build.
- **B.** Ambient occlusion: a wall darkened by 0.35 at its foot, gone at 3 m, per pixel. Barely visible
  from 1 km up.
- **B, the ground halo** (2026-11-28): the ground darkened by the same 0.35 at a wall's foot, gone 3 m out
  (squared falloff), for buildings standing on the ground (base 0). No geometry of its own: the layer's
  extrusion range is drawn once more with `Misc.w` = the radius, and `GlobeVector.sdsl` lays each wall down -
  its foot stays, its top goes out along the wall's normal - and drops the roofs; the wedges at outward
  corners are one triangle each over the walls' vertices (`RangeKind.Halo`, `Builder.HaloCorners`). Drawn
  after the layer's colour on the buildings' own depth, before the shadows are received. Follows `Ao`
  (off in the render tests) and the buildings' fade. Seen in vvvv against `VLGLOBE_HALO=0` of the same build
  (Yanaka from 120 m and 40 m): 5 % of the pixels darker, none lighter; the tiles' memory the same (112 MB
  for 36 tiles), +91 draws of 1289; the pan bench 34.1 % GPU against 32.9 % (one run each, inside the spread).
- **C, first cut.** `ShadowPass`: the buildings' depth items drawn again into a 2048² map along the sun
  (orthographic, fitted round the view out to 4 x the eye's height + 1 km, 1-6 km, texels snapped to the
  ground, 1.5 km of margin towards the sun); received by a full-screen pass (`GlobeShadow`/`GlobeShadowMS`
  for a multisampled depth) on the depth the buildings were drawn on, before the labels: 3 x 3 PCF,
  normal offset 1.5 texels, faces turned from the sun skipped (their light already darkens them; they drew
  acne stripes), strength 0.45, faded over the last fifth of the distance. On when the light's own Stride
  `Shadow.Enabled` is (SunLight's Cast Shadows, on by default; vvvv's DirectionalLight's shadow too).
  Not yet: buildings outside the camera's tiles casting, the user's models casting onto the ground and
  buildings.
- **C, two cascades** (the same day, asked by the user): a near map out to a split (1.5 x the eye's height
  + 200 m, 300 m up to 40 % of the distance: ~350 m at street level, ~2 km from 1.2 km up) and a far one
  to the whole distance, blended over the split's last tenth. Seen in vvvv: from 200 m the high-rises'
  shadows across Tokyo Station's platforms with smooth edges; from 1.2 km the near edges fine, the far
  ones (2-6 km) still a little stepped.

## Baseline, measured 2026-11-28 (before the rest of C)

The pan bench (`VLGLOBE_BENCH=pan`, 2 x 10 s at 300 px/s) on scratch copies of `HowTo Light the buildings
with the sun` with Enabled on: Tokyo Station from 500 m, tilted 60, Liberty with all its labels, a 1250 x 800
window, RTX 3060 Ti, vsync on. GPU = the process's 3D engine use (`Get-Counter "\GPU Engine(pid_N_*engtype_3D)"`,
every 2-3 s after loading), since at 60 fps the frame time shows nothing.

| run | frames while panning | submit (mean) | draws | GPU 3D, mean (min-max) |
|---|---|---|---|---|
| SunLight, shadows on | 60 fps, 16.7 ms | 1.5 ms | ~2950 | 35.6 % (32.5-38.1) |
| SunLight, Cast Shadows off | 60 fps, 16.7 ms | 1.4-1.5 ms | ~2950 | 33.9 % (32.3-35.8) |
| no light in the scene | 60 fps, 16.7 ms | 1.4-1.5 ms | ~2950 | 34.2 % (32.7-36.0) |

- The two cascades and the full-screen receive cost about 1.7 points of GPU (~0.3 ms of a 16.7 ms frame) and
  ~0.1 ms of submit on this card - one run each, the difference is the size of the spread between samples.
- The perf line's `draws` does not count the shadow casters' draws: it reads the same with and without.
- Not the shadows': one frame of 163-168 ms in pan 2 with shadows on AND with no light, all of it label
  placement (`place 162.2` / `155.7`). Gone in 3 runs after `LabelFades.UpdateOpacities` stopped allocating its
  collections at every call (NOTES.md, 2026-11-28).
- Instrument notes: the perf line says `still` during the bench's pans, so `Summarize-Perf.py --moving` drops
  them; GlobeWindow's unused inner camera runs the bench too (it spins, drawn by nothing).
