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
