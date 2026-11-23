# Ground layers sinking under the globe past a source's maxzoom (ROADMAP 4h)

Written 2026-11-23 evening, at the end of a long session, **so that a session after `/clear` starts
from here without the conversation.** The user asked for this explicitly: "请把方向详细写好。因为前几次我
发现我clear之后很多content就已经被忘记了，尤其是很多重要的对话内容". Read all of it before touching code.

## 1. What the user saw, in their words

- In the help patch `HowTo Choose a vector tile style` (14 styles by index), the user tested styles and
  zoomed in: "我在测试style的时候，经常有一些style zoom in之后就会把底下的深蓝色的球也给露出来，比如我现在的画面".
- Which style: "我就在测试13，同时过程中也看见过" - index 13 is MapLibre's demo tiles
  (`https://demotiles.maplibre.org/style.json`, one vector source, **maxzoom 6**); the user has seen
  it with other styles too while testing (which ones was not said - ask, see §6).
- After my first attempt (a diagnostic only): "还是非常明显，我觉得这个问题需要仔细想一下对策".
- Then this document: the user chose to plan the fix in a new session rather than build it in the
  old, very long one.

The pictures, in `docs/overscaled/`:

| file | what |
|---|---|
| `1-user-dark-disc-z10.png` | the user's window, `#9.59/35.15098/139.31365/3.6/19.1` (MapLibre's zoom; ours is one more): a dark-blue **disc round the centre of the view** where the style's light-blue sea should be; the land (fill) still drawn inside it |
| `2-user-dark-sea-z12.png` | the user's window, `#11.58/35.65462/139.83467/-6.3/21.7` (Tokyo Bay, eye 18.6 km): the whole sea dark, the top a dark disc edge; the land still there |
| `3-probe-same-view-fine.png` | `ViewProbe` offscreen, view of picture 1: **all fine** - the probe cannot show this failure (§3) |
| `4-after-background-fix-land-gone.png` | my vvvv run after the background fix (7b278f4), style 13, the help patch's default view (Tokyo Station, eye ~2.5 km): the sea colour everywhere - **the land is gone** (it should be land there); only our own Geometry square and line on top |

## 2. The cause - depth, not data

Measured and reasoned 2026-11-23; numbers at 35°N.

1. **Ground layers are depth-tested against the globe's own tiles.** `VectorRenderer.cs` ~line 477-508:
   fills, lines, background draw with `DepthStencilStates.DepthRead` over the scene's depth, which holds
   the globe's raster tiles (or the plain Background-colour tiles when there is no imagery). To win, GPU
   vector geometry is lifted towards the eye by `VectorRenderer.Lift = 0.0015` of its distance
   (`shaders/GlobeVector.sdsl`).
2. **A bucket is cut by the zoom of the vector tile it was built for**, not by the zoom it is shown at.
   `VectorBucket.Builder` (VectorBucket.cs ~line 314): `_maxSegment = key.Zoom > 10 ? ∞ : min(Extent/8, 2°)`
   - an edge longer than an eighth of the tile is split so its straight chord stays near the sphere.
3. **Past the source's maxzoom the same bucket is shown at every finer zoom** (`GpuVectorTiles.VectorKey`
   clamps to `maxZoom`, as GL JS's tile choice does; `Rect(vk, g)` clips the draw to the shown square).
   For the demo tiles every view below z6 uses z6 buckets.
4. **The arithmetic.** A z6 tile at 35°N is ~513 km wide; an eighth is ~64 km. A chord of length L sags
   L²/(8R) below the sphere: 64 km → **~80 m**. Triangles inside big polygons (earcut) are as big as the
   cut allows, so their interiors sag that much too.
   The lift is 0.0015 × distance: eye 76 km → ~110 m (picture 1: wins at the edges, loses near the
   centre where the distance is least - hence a *disc round the centre*); eye 18.6 km → ~28 m
   (picture 2: loses almost everywhere); eye 2.5 km → ~4 m (picture 4: the land loses everywhere).
   The globe's tiles at those zooms are cut fine (TileGeometry.SegmentsFor) and sit on the sphere,
   so they are in front and hide the sunken vector geometry; what shows is the globe's dark Background.
5. **Not only the demo style.** Sources stopping at z14 (OpenFreeMap, VersaTiles, CARTO): a z14 tile
   is ~2 km wide, `_maxSegment` is ∞ above z10, so a whole-tile chord sags ~8 cm; at the 20 m floor
   the lift is ~3 cm. So the same failure is expected **at street level on every style**, small but
   real - the user's standard is street level (CLAUDE.md). Check this in §5 step 1.

## 3. What was done so far, and why it did not catch it earlier

- `4f30c8b` - a diagnostic: every frame, shown globe tiles that got no square of the style's background
  are logged as `Globe style: background: ...`. In the user's next run it **said nothing** - every tile
  had its background; the draws were made and then hidden. That pointed at depth.
  Also: `ReplayProbe` draws the last of its views to `%TEMP%\vlglobe-replay.bmp`, and
  `VLGLOBE_REPLAY_FRAMES=n` moves on after n updates a view (a hand moving).
- **Why every offscreen probe drew it whole:** `GpuFixtureRenderer.RenderGlobe` (tests) draws only the GPU
  vector frame - **no globe tiles, so nothing in the depth buffer to hide behind**. That probe could not
  fail this way (memory: "verify in the host that can actually fail"). Two replays of the user's 119
  views, settled and quick, all fine - by construction, not by evidence.
- `7b278f4` - **the background part fixed**: the style's background layer needs no data, so it is now
  built per shown square at that square's own zoom (`GpuVectorTiles.Update`, the "background is drawn
  on every needed tile" block: `Background(device, square)` for each served square). Tests 311 green,
  MapLibre fixture reports unchanged (both paths). Seen by me in vvvv (picture 4: the sea colour fills
  the view, no dark). **Not yet seen by the user.**
- The data layers (fill, line, fill-extrusion) still use the overscaled bucket - picture 4.

## 4. The options

**A. Build overscaled ground geometry per shown tile** (my recommendation, pending the GL JS check below).
Where a shown globe tile g is finer than the source's maxzoom, build a bucket for g: the source tile's
decoded features (one decode, cached as now), cut to g's square widened by a margin
(`TileClip` - today it takes a GeoJSON `FeatureTable` in world coordinates; generalise it to the
decoded vector features), triangulated and cut with g's own zoom so `_maxSegment` fits where it is seen.
- Cache key: today `_entries` is keyed `(service, source, key, scale)`; add the shown key (or make the
  key the shown tile and keep the source tile in the entry). Eviction and the GPU budget (`GpuBudget`
  1.5 GB, `KeepTiles`) must count these.
- **Symbols stay as they are** (laid out once per source tile at the overscale - ROADMAP 4e "symbols
  past the source's maxzoom"); only the ground passes move. Cutting labels per shown tile would
  duplicate them and break CrossTileSymbolIndex.
- Stand-ins: while g's bucket builds, draw the source-tile bucket clipped (today's picture, sunken but
  better than nothing) or nothing - decide by looking.
- Cost: more buckets near the eye, each small (a clipped piece). Watch the 4g numbers
  (`VLGLOBE_BENCH`, missed frames ~0.7% on zoom) - workers' garbage was the stutter before
  (memory: worker garbage stops the renderer): build clipped tables as arrays, not objects per feature.
- The `_maxSegment` rule itself may also need a zoom > 10 cut for street level (§2.5) - an eighth of
  a z14 tile is 250 m, sag 1 mm.

**B. Do not depth-test ground layers against the globe's tiles.** Hide the far side of the globe in the
shader (a horizon test) instead of by depth; ground layers are painted in style order anyway. Removes
the whole class of sag failures, cheap. Risks: entities the user places in the Scene
(GlobePositionComponent) and 3D buildings must still hide and be hidden correctly; ground drawn with no
depth test over an entity standing on it would paint over it. Needs a look at how the stages are
ordered (opaque entities, then our transparent-stage draws - CLAUDE.md: that stage's depth is
read-only in vvvv).

**Rejected:**
- C. Cut overscaled buckets finer (by the shown zoom): a z6 tile cut fine enough for z16 is millions of
  triangles.
- D. A bigger lift: hides the symptom, floats things above the ground, fights the user's entities -
  and the user's rule (memory: fix the cause, not the symptom, "而不是一味设延时").

**First thing in the new session - check how GL JS does it**, before choosing (memory: follow the usual
pattern). In the chat I told the user GL JS builds a bucket per overscaled tile; **that was not
verified**. Read MapLibre GL JS v5's globe code: `src/geo/projection/globe*.ts`,
`src/render/subdivision.ts` (subdivision granularity - by canonical zoom? the canonical tile of an
overscaled tile is the source tile), how globe draws handle the far side (depth? clipping plane?), and
whether GL JS has a separate globe surface for vectors to sink under at all (it may not: its "globe" is
the vector/raster tiles themselves). Put what is found into this file, then decide A or B **with the
user** (they decide direction questions; offer a recommendation).

## 5. Steps once decided

1. **An instrument that can fail.** Give `GpuFixtureRenderer.RenderGlobe` the globe's depth: draw the
   shown globe tiles' meshes (TileGeometry, at their real segments) depth-only before the vector frame.
   Then a test at the user's views must go red today:
   - style 13, `#11.58/35.65462/139.83467/-6.3/21.7` and the help patch's default view (Tokyo Station,
     eye 2.5 km): count pixels of the globe's clear colour - today many, after the fix 0;
   - Liberty (OpenFreeMap) at the 20 m floor somewhere with large fills (a park, the sea at a pier) -
     to settle §2.5.
2. Build A or B.
3. Rungs: `dotnet test` (311 + the new ones), the MapLibre fixture reports diffed against the last
   (`results-gpu\report.md`, `results\report.md` - no fixture may change), the 4g bench numbers, then the
   user in vvvv with style 13 and several others, zooming down to street level.
4. Record in NOTES.md (the entry "Dark holes through a style's background" is the start), ROADMAP 4h,
   CLAUDE.md's Next.

## 6. Open questions for the user

- Which other styles showed it, and at what height? (Probably street level - §2.5.)
- After A or B is chosen: whether a sunken stand-in while a clipped bucket builds is acceptable, or
  the tile should wait.

## 7. State at the time of writing

- Branch `feature/camera-relative`, commits local, **none pushed** (the user pushes).
- 311 tests green; fixture reports identical to the baseline.
- dist\ packed with 7b278f4 (background fix); no vvvv running.
- Waiting elsewhere: ROADMAP 6 widgets (ScaleBar, ZoomButtons, Click, attribution in the window -
  plan: Skia layers over Stride via SkiaRenderer + RenderEntity, Stride help "HowTo Overlay 3D Objects
  with 2D Skia"); the user's hand layout of the new help patches (Normalize + compile after their
  saves); small: OpenFreeMap Dark very dark, a dotted graticule parallel near 45°N at the limb,
  a shutdown ObjectDisposedException race in GlobeWindow.
