# Stride's effects and VL.Globe — a survey (2026-11-28)

Asked by the user: "广泛地研究一下vvvv 也就是stride3d端有的一些效果，看看是否可以应用到我们的项目上".
What vvvv gamma 7.4 ships (VL.Stride on Stride 4.2.1.2487), and what each would take here. Read from the
packs' `.vl` files, the decompiled `VL.Stride.Runtime.dll` and Stride's `.sdsl`; **nothing below was run** -
"inferred" marks what the code suggests and a look in vvvv has to confirm.

## The one fact that decides most of it

VL.Globe draws its layers in the **Transparent** stage, and the buildings on **a depth buffer of its own**
(a copy of the scene's; NOTES, 2026-11-21). The scene's depth - what Stride's post effects read - holds the
globe's sphere and the user's models, **not the buildings**. So every depth-based effect of Stride's sees a
flat globe today. And `GlobeWindow` switches SceneWindow's default post effects off (they are a Hejl2 tone
map: it darkened the map's colours).

## A second fact, found when the experiment was being set up (the same day)

**Stride's effects assume a scene in metres; ours is in earth radii** (`Radius` 1 by default: a building 100 m
away is 1.6e-5 units deep). Read in `AmbientOcclusionRawAOShader.sdsl`, not run: the AO fades out with
`saturate(linearDepth * 2 - 0.5)` - nothing nearer than 0.25 units, 1600 km, is ever occluded - and divides by
`0.01 + distance²`, where our distances² are 1e-12. Its Radius and Bias pins can be scaled; these constants
cannot. So writing the buildings' depth back would not be enough: Stride's AO draws nothing at our scale.
Two ways on, the user's to choose:
- **A. The globe in metres** (`Radius` 6378137): every Stride effect's units are right, and the user's models
  are in metres too. Unknown whether the engine holds at that radius (camera-relative, so it may); a change of
  default for everyone.
- **B. An ambient occlusion pass of our own**, beside `GlobeShadow` and `GlobeFog`: full screen, on the
  buildings' own depth, before the labels - the same sampler with our units. No write-back, the labels are
  never darkened, it works inside `GlobeWindow`. DepthOfField and Outline stay out of reach until A or a
  write-back.

### B tried as a prototype (the same day) - for a picture to judge by, not a feature

The user: "我们就先把测试做了吧，因为我不知道该如何选型，值不值得做" - and: GIS features come before visual polish.
`GlobeShadowBase.sdsl` got an `Occlusion` term behind `VLGLOBE_SSAO=<strength>` (off by default;
`VLGLOBE_SSAO_RADIUS` metres, 8): Stride's spiral sampler, 24 taps on the buildings' depth, in our units.
Drawn offscreen (`tools\Probe-View.ps1`, Yanaka from 100 m, strength 0.6 against 0): 24 % of the pixels
darker, none lighter - the gaps between houses, the alleys, the feet of the walls; the roofs and open ground
untouched. Three rounds of 30 s: black everywhere (Stride's term is 1 / length, of the order of 1 only at
radius 1 - multiplied by the radius), then hatched (the per-pixel rotation needs the blur it comes with -
removed), then stripes on the ground (self-occlusion - bias 0.12 of the radius).
**What a feature would still need**: a pass of its own (the prototype runs only with shadows on), the
bilateral blur and the rotation back, every sample of a multisampled depth at outlines, the radius from the
globe's `Radius`, a pin or a style key for the strength, and its cost measured (24 depth reads a pixel).
Not seen in vvvv. Decision open; the prototype stays in the tree, switched off.

## What there is

| effect (vvvv node, `Stride.Rendering.PostFX` unless said) | reads | here today | to make it work |
|---|---|---|---|
| **AmbientOcclusion** - screen-space (SAO-style spiral sampler + bilateral blur), normals rebuilt from depth, MSAA resolved first | depth | sees no buildings | the buildings' depth in the scene's depth |
| **DepthOfField** - lens blur, auto focus; the "miniature city" look | depth | blurs by the sphere only | the same |
| **Outline** - edges from depth | depth | no building edges | the same |
| **Fog** - exponential distance fog | depth | we have our own (`GlobeFog`, horizon fog) | nothing to gain |
| **LocalReflections** (SSR) | depth, normals, roughness (extra render targets) | no | our shaders would have to write those targets; not a map's look |
| **TemporalAntiAlias** | velocity target | no | the default compositor has no velocity feature (inferred); we use MSAA |
| **Bloom, BrightFilter, LightStreak, LensFlare** | colour | work on the picture as it is, labels included | a PostFX the user wires; the window must let one in |
| **ColorTransforms**: ToneMap (Hejl2, ACES, Reinhard, ...), Vignetting, FilmGrain, Dither | colour | off in GlobeWindow | the same; a tone map changes the map's colours |
| **FXAA** | colour | not needed with MSAA x4 | - |
| **LightShafts** (a ForwardRenderer input, with a bounding volume component) | a light's shadow map | no | our buildings are not in Stride's shadow maps |
| **TextureFX** (`VL.Stride.TextureFX`): Blur, Glow, Edge, Sharpen, Levels, HSCB, MapColor, Posterize, Hatch, Dots, Mosaic, Tilt Shift example, ... | a texture | **works today** on the globe rendered to a texture | a help patch; the labels are filtered with the rest |
| Lights: Directional, Point, Spot, Ambient, Skybox, Projector | - | our buildings read the first directional light only | Ambient would be cheap; Point/Spot need a light loop in `GlobeVector.sdsl` |
| Stride's shadow maps (cascades, PCF 3/5/7) | - | our buildings neither cast into them nor receive | `EntityRendererComponent` has a **ShadowCaster** stage: a drawer of ours could draw the buildings into Stride's maps, so they shade the user's models (inferred) |
| Atmosphere, physical sky, water, level of detail | - | **not in vvvv's Stride**: a cubemap skybox and a Background only | ours to write (ROADMAP "Atmosphere and sun") |

Materials (PBR, clear coat, subsurface, emissive, normal maps) are for meshes of the user's; the map's
layers are drawn by our own shader and take none of it.

## What follows

1. **A help patch with TextureFX on the globe's picture** - no engine work. A tilt shift, a colour grade or
   a hatch over the rendered map shows the package inside vvvv's own toolbox. To check first: the globe
   rendered to a texture (camera, view size) and what the filters do to labels.
2. **The buildings' depth into the scene's depth** - one change that would hand over Stride's AmbientOcclusion,
   DepthOfField and Outline together, and the answer to "why no global SSAO". It touches what was hard won:
   the transparent stage's depth is read-only (why the buildings have their own), the buildings are
   translucent, the labels must stay sharp and undarkened (post effects run after everything). An
   experiment in vvvv before a plan: write the buildings' depth back, wire Stride's AO, look.
3. **The ShadowCaster stage** for the open half of light step C (the user's models and the buildings shading
   each other) - to try before writing a second shadow path of our own.
4. A `Post Effects` way into `GlobeWindow` (colour-only effects work as they are) - a node-design question.

Not worth it for a map: SSR, TAA, lens flare, light streaks.
