# VL.Globe — how it got here

Moved verbatim from CLAUDE.md's "Start here" on 2026-11-24, where it had grown one session at a
time into the log below. Current state and next steps: CLAUDE.md and docs/ROADMAP.md.

## Up to 2026-11-23

1. Read **[docs/ROADMAP.md](docs/ROADMAP.md)** — planned by release: **0.1 = VL.Mapsui's basics on a smooth
   globe plus draped vector tiles, 0.2 = terrain** (the user's decisions). Items 1, 1b, 2 are done and
   judged; vectors are **draped** (item 3, decided 2026-11-21); Geometry/FeatureLayer/VectorStyle exist
   (item 4); **4b** is done — MapLibre's render tests run in `dotnet test` (48/48 supported pass; fetch
   the fixtures first with `tools\Fetch-MapLibreFixtures.ps1`). **4c** step 1 is done — the painter
   reads MVT (98/164 fixtures pass, 90 counted); step 2, the node `VectorTiles [Globe.Layers]`, is
   built and seen in vvvv; the user found the drape "不太行" and chose the GPU (ROADMAP **4d**,
   [docs/DESIGN-GPU-VECTOR.md](docs/DESIGN-GPU-VECTOR.md), its "As built" and "Next"): built and
   judged by the user the same night ("大方向完全没有问题", "我觉得还行"), 178 tests green.
   Since then: the long Updates found (a per-frame large-object-heap list), 3D buildings, a 28-byte
   vertex, MapLibre's render tests on the GPU path (97 pass offscreen in `dotnet test`).
   Then labels (ROADMAP **4e**): point text and collision ported from GL JS, 154 fixtures pass on the
   GPU path, seen in vvvv on Liberty; then labels along lines (158 fixtures, 150 non-vacuous).
   Then icons (milestone 4), steps 1-4: sprites, point icons, the icon shader, joint placement -
   252 fixtures pass, 243 non-vacuous. Then step 5, icon-text-fit and stretch zones, with circle
   layers (needed by its fixtures) - 304 pass, 295 non-vacuous.
   Then step 6, symbols on lines (shields upright at line anchors, icons along lines) - 311 pass,
   302 non-vacuous; one-way arrows seen in vvvv at Kyoto Station, shields not yet seen.
   Then symbols past the source's maxzoom (laid out at the overscaled zoom, as GL JS) - street
   names along every street at Kyoto Station in vvvv.
   Labels now placed as GL JS (every 300 ms while moving): 60 fps tilted while no tiles arrive.
   2026-11-22 afternoon (NOTES.md, the last entries): the one-off hitch was the atlases (fixed, seen
   gone); layers hidden at a tile's zoom no longer built (Kyoto z14 9.1 -> 6.7 MB); line labels no
   longer step while panning close (the eye counts as a move); lines collide by first/last glyph.
   Placement still spikes 17-50 ms while tiles load in vvvv.
   Then: the style is a node (`MapLibreStyle (URL)` / `(File)`, Maputnik named for editing, the
   user's decision); 4f built - the tilt limited by the zoom; the fog built and then turned off by
   the user ("我宁愿没有"); vector tiles kept at the camera's zoom to 5 eye heights, 3D buildings
   fading out before that (seen tilted over Tokyo: "视野里面相对比较满了"; 853 MB there, over budget).
   A reported view is drawn offscreen by `ViewProbe` (VLGLOBE_PROBE=1 VLGLOBE_VIEW=#...).
   Then label fades (2026-11-22 night): CrossTileSymbolIndex, the pauseable placement (2 ms a frame),
   300 ms fades - built and tested (260 green, fixtures unchanged at 311); the user saw every fade begin
   with a blink (the fade change taken before the step: fixed, not yet seen again); the buildings'
   fade-out turned off ("还是太怪了"); seen again: "没什么问题". The GPU harness now turns by the
   style's bearing: 326 fixtures pass, 317 non-vacuous (viewport-glyph labels were turned to the line).
   Then point symbols turned with the map (rotation-alignment map, not yet pitched with it): 329 pass,
   320 non-vacuous, 262 tests green.
   **Next: ROADMAP 4g, as smooth as GL JS (the user, 2026-11-22: "远远不如人家那么流畅，还是卡卡的") -
   step 2 built (bytes halved, misses not); step 4 found the cause - workers' garbage (gen0 pauses),
   the style expressions interpreted per feature. Built: expressions compiled once, decoded tiles as a
   few arrays, buffers made on the workers, off-screen line labels not walked, parameters set through
   accessors - bench misses 3-5% -> 0.0-0.1%, worst frame 19-20 ms (GL JS 0%, 17). Zoom bench (2026-11-23, dotnet-gcdump):
   features as rows of arrays, line labels as structs, value strings made at first read, each draw's values in one buffer -
   3.4% -> ~0.7% (GL JS 0.2-0.4%); the user: "好很多", then stopped 4g there (ROADMAP 4g, "Stopped here", lists what is left).
   ROADMAP 4h (the dark globe through styles past a source's maxzoom, docs/DESIGN-OVERSCALED.md):
   GL JS read - it is B; the user chose B': ground passes take the true sphere's depth, the far side
   cut along the ray. Built 2026-11-23 night, 315 green, offscreen probes now draw the globe tiles' depth.
   Seen by the user in vvvv: "似乎成功了"; zoom bench 0.1% missed (no cost). Then ROADMAP 6: the
   attribution drawn bottom right by every Globe (built 2026-11-23, seen by me, not yet by the user).
   Then Attribution pins on FeatureLayer/Geometry (the user's choice) and the poles filled GL JS's way
   (edge rows stretched to the pole; caps for a style's background), 334 green, seen by me in vvvv.
   Then the drag near the poles (flashed to the pole): GL JS's globe pan below MapLibre zoom 12, the
   grab above (`GlobeView.DragSession`), centre latitude within ±85.05 - 346 green, not yet seen in vvvv.
   Then the help patches reorganised as VL.Stride/Skia's (topics + subfolders: Overview, Layers, Camera,
   Input, Objects; point + model merged into Objects\HowTo Place objects on the globe), all 11 compile.
   Then F1 flags (tools/Set-HelpFlags.py, all 31 nodes) and cells for FeatureLayer, GlobeToScreen,
   ToGlobe (Coordinate), Group (Spectral). F1 tried by the user: works.
   The user arranged every help patch by hand (committed normalized, 6972235). Globe and GlobeWindow are
   Enabled by default since 2026-11-23 (as SceneWindow/Renderer; help toggles still OFF). Node design
   rules: the `vvvv-node-design` skill (~/.claude/skills). **NEXT: ScaleBar, ZoomButtons, Click
   (ROADMAP 6) when the user asks; VectorTiles' Enabled default is still to decide.**
   Then (2026-11-23) the node surface made vvvv's (docs/DESIGN-NODES.md): navigation nodes [Globe.Navigate], pins in vvvv's
   order, one Input + Group, GlobeCamera as OrbitCamera. Next: the user's judgement of the gestures and the help patches' layout.**
   Research: [docs/RESEARCH-0.1.md](docs/RESEARCH-0.1.md), [docs/RESEARCH-VECTOR.md](docs/RESEARCH-VECTOR.md).
