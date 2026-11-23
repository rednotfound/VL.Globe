# What Stride 4.2.1 and VL.Stride offer the GPU path - a survey, 2026-11-23

Asked by the user after ROADMAP 4g ("在 stride 下可能有更加先进的技术，能够帮助我们更简单地处理某些问题").
Read: Stride `releases/4.2.1.2487` (commit 649612840, what vvvv 7.4 ships), VL.StandardLibs `main`
(no 7.4 tag; details may differ), vvvv 7.4's `packs\VL.Stride\help` and `vvvv.runtimeconfig.json`.
"Verified" = read in source; "inferred" = D3D11 / .NET documented behaviour, not measured here.

| # | What | Verdict |
|---|---|---|
| 1 | GC: keep long-lived data out of gen0's copying | the biggest lever, not Stride's |
| 2 | Per-draw constants in one per-frame buffer, a draw id per draw | submit ~2 -> ~0.5-1 ms (inferred) |
| 3 | Becoming a Stride RenderFeature | not worth it on D3D11 |
| 4 | Drawing in the Opaque stage (depth writable) | cheap to try; drops the depth copy |
| 5 | Compute shaders | available; only the line-label walk fits, and it is 0.2 ms now |
| 6 | Deferred contexts / command lists on other threads | not on D3D11 in Stride |

1. **GC.** Verified: vvvv's runtimeconfig sets no GC option and vvvv's assemblies never touch
   `GCSettings` / `TryStartNoGCRegion` - workstation concurrent GC, `DOTNET_*` variables apply (the
   64-MB gen0 was tried: fewer, longer pauses). Inferred: long-lived tile data can avoid gen0 copying
   altogether - `GC.AllocateArray<T>(n, pinned: true)` (pinned object heap, collected with gen2, never
   copied), arrays >= 85 KB (large object heap), or native memory. `SustainedLowLatency` limits only
   blocking gen2; a no-GC region has a budget. `DOTNET_gcServer=1` is a cheap experiment (shorter,
   parallel pauses, still stop-the-world, more memory). Real isolation = building tiles in another
   process (GL JS's workers), the last resort.
2. **One buffer of per-draw data.** Verified: on D3D11 every `EffectInstance.Apply` maps each constant
   buffer (WriteDiscard), copies, makes descriptor sets, binds (`BufferPool.UseBufferOffsets = false`,
   `EffectInstance.cs:98`, `ResourceGroupBufferUploader.Apply`); `SetShaderResourceView` is not
   deduplicated and unsets UAV slots each call (`CommandList.Direct3D.cs:261, 328`). Available:
   `CommandList.DrawIndexedInstanced(indexCount, 1, start, baseVertex, startInstance)` and instance
   input elements (`InputClassification.Instance`). The way: the frame's per-draw values (world-view,
   colour, clip, tile constants, flags) written once into a per-instance vertex buffer (or a
   `StructuredBuffer` indexed by a draw id from an instance stream - `SV_InstanceID` does not include
   `StartInstanceLocation`), the frame's constants applied once, each draw only DrawIndexedInstanced.
   Stride's own pattern: `TransformationInstancing.sdsl` + `InstancingRenderFeature.cs`; VL.Stride's
   instancing nodes and help ("Different Color or Material Properties per Instance") index a buffer
   by instance. D3D11 has no multi-draw indirect; GL JS draws as many (layer x tile).
3. **RenderFeature.** Verified: Stride's cheapness is preparing per-draw data in parallel
   (`RootEffectRenderFeature.cs:714`); drawing still maps, sets descriptor sets and draws per node
   (`MeshRenderFeature.cs:177-243`). Same per-draw cost for much machinery.
4. **Opaque stage.** Verified: `EntityRendererComponent`'s stages include Opaque (its default); the
   read-only depth is `VLForwardRenderer.BindDepthAsResourceDuringTransparentRendering` (transparent
   stage only); in Opaque, entity renderers (sort key 190) draw after meshes (128) - the raster tiles
   first, the map after, the skybox (192) last. Moving the whole VectorRenderer keeps its order and
   lets 3D buildings write depth without `UseOwnDepth`'s copy; moving buildings alone would put their
   depth in front of later roads. Risks (inferred): MSAA / several targets bound there; the user's
   transparent objects then draw after the map.
5. **Compute.** Verified: `ComputeEffectShader` (Dispatch, indirect dispatch), VL.Stride's
   `VLComputeEffectShader`, a buffer both UnorderedAccess and VertexBuffer (help: "Write into a
   VertexBuffer in a Compute Shader"). Collision placement is GL JS's serial greedy - on the GPU it
   would be another algorithm. The glyph walk along lines is parallel but now ~0.2 ms.
6. **Threads.** Verified: `CommandList.New` throws "Can't create multiple command lists with D3D11";
   buffers created with data on workers (done, 4g) is D3D11's best upload.

No vvvv/Stride vector-tile or globe renderer was found to learn from (one web search; the forum not read).

**Order suggested:** keep cutting long-lived small objects (1); try the Opaque stage (4); per-draw
data in one buffer (2) when submit matters again; not 3, 5, 6.
