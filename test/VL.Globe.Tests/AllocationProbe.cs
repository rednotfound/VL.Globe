using System.Runtime;
using VL.Globe.Engine;
using Xunit.Abstractions;

namespace VL.Globe.Tests;

// Not a fact: a measurement by hand (VLGLOBE_PROBE=1). ROADMAP 4g step 4: the frames that missed
// their vsync were paused by gen0 collections, whose cost grew with what survived them, not with how
// many there were (2026-11-23: a 64-MB gen0 made 1/8 of the collections, each 4-8 times as long).
// What building one street-level tile allocates, stage by stage, and how much of it is still alive
// when it is done - what a collection in the middle must mark and copy.
public class AllocationProbe(ITestOutputHelper output)
{
    [Fact]
    public void Tokyo_z14_on_liberty()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1") return;
        using var service = new VectorTileService("https://tiles.openfreemap.org/styles/liberty");
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (service.Document is null && DateTime.UtcNow < until) Thread.Sleep(100);
        var doc = service.Document ?? throw new InvalidOperationException(service.Status);
        var (sourceId, source) = doc.Sources.First(s => s.Value is VectorSource);
        var all = Enumerable.Range(0, doc.Layers.Count).Select(i => GpuStyleLayer.Of(doc, i)).Where(l => l != null && l.Layer.Source == sourceId).Select(l => l!).ToList();
        var shapes = all.Where(l => !l.IsSymbol).ToList();
        var symbols = all.Where(l => l.IsSymbol).ToList();

        foreach (var key in (Environment.GetEnvironmentVariable("VLGLOBE_PROBE_TILES") == "low"
            ? new[] { new TileKey(10, 909, 403), new TileKey(11, 1819, 806), new TileKey(12, 3638, 1612) }   // Tokyo zoomed out (the zoom bench)
            : new[] { new TileKey(14, 14552, 6451), new TileKey(14, 14553, 6451), new TileKey(13, 7276, 3225) }))
        {
            // the bytes here first (fetched or from the disk cache), and a first build to warm the code
            while (DateTime.UtcNow < until)
            {
                bool complete = true;
                var w = VectorBucketBuilder.Build(doc, source, all, false, key, true, null, out complete);
                if (w != null && complete) { w.Release(); break; }
                Thread.Sleep(200);
            }
            doc.ForgetDecoded();   // decoded again below, as a tile coming for the first time is

            Settle();
            long before = Live();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            bool here = true;
            var data = doc.Tile((VectorSource)source, key, ref here, false, null)!;
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            int features = data.Values.Sum(l => l.Count);
            long afterDecode = Settle() - before;
            var b1 = VectorBucketBuilder.Build(doc, source, shapes, false, key, true, null, out _)!;
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            long afterShapes = Settle() - before;
            var b2 = VectorBucketBuilder.Build(doc, source, symbols, false, key, true, null, out _)!;
            long a3 = GC.GetAllocatedBytesForCurrentThread();
            long afterSymbols = Settle() - before;
            output.WriteLine($"{key}: {features} features in {data.Count} source layers");
            output.WriteLine($"  decode   allocated {(a1 - a0) / 1048576.0,7:F1} MB, alive after it {afterDecode / 1048576.0,6:F1} MB (the decoded tile, cached)");
            output.WriteLine($"  shapes   allocated {(a2 - a1) / 1048576.0,7:F1} MB, alive after it {(afterShapes - afterDecode) / 1048576.0,6:F1} MB more (its bucket {((long)b1.VertexCount * VectorVertex.Size + b1.IndexCount * 2L) / 1048576.0:F1} MB)");
            output.WriteLine($"  symbols  allocated {(a3 - a2) / 1048576.0,7:F1} MB, alive after it {(afterSymbols - afterShapes) / 1048576.0,6:F1} MB more");
            // what the symbols' result is made of - its small objects are what a collection copies
            long lineLabelArrays = b2.LineLabels.Sum(x => (long)x.Line.Length * 12 + x.GlyphOffsets.Length * 4L + 2 * 24);
            output.WriteLine($"  symbols' result: {b2.Labels.Count} point labels (one array), {b2.LineLabels.Count} line labels " +
                $"({3 * b2.LineLabels.Count} objects, {lineLabelArrays / 1048576.0:F1} MB of arrays), symbol vertices {(long)b2.SymbolVertices.Length * SymbolVertex.Size / 1048576.0:F1} MB, " +
                $"indices {b2.SymbolIndices.Length * 4L / 1048576.0:F1} MB, circles {b2.CircleVertices.Length}");
            GC.KeepAlive(data); b1.Release(); b2.Release();

            // layer by layer, the decoded tile cached: the ten that allocate most
            var byLayer = new List<(string id, string type, double mb)>();
            foreach (var layer in all)
            {
                long l0 = GC.GetAllocatedBytesForCurrentThread();
                VectorBucketBuilder.Build(doc, source, new List<GpuStyleLayer> { layer }, false, key, true, null, out _)?.Release();
                byLayer.Add((layer.Layer.Id, layer.Layer.Type, (GC.GetAllocatedBytesForCurrentThread() - l0) / 1048576.0));
            }
            foreach (var (id, type, mb) in byLayer.OrderByDescending(x => x.mb).Take(10))
                output.WriteLine($"    {id,-32} {type,-15} {mb,7:F1} MB");

            // what the worst layer allocates, by type: the runtime's allocation ticks (one per ~100 KB)
            var worst = all.First(l => l.Layer.Id == byLayer.OrderByDescending(x => x.mb).First().id);
            using (var ticks = new AllocationTicks())
            {
                VectorBucketBuilder.Build(doc, source, new List<GpuStyleLayer> { worst }, false, key, true, null, out _)?.Release();
                output.WriteLine($"    {worst.Layer.Id} by type (100-KB ticks):");
                foreach (var (type, n) in ticks.Top(12)) output.WriteLine($"      {n,5}  {type}");
            }
            // ... and by stage of the per-feature loop (AllocMeter, temporary)
            AllocMeter.Reset(); AllocMeter.On = true;
            try { VectorBucketBuilder.Build(doc, source, new List<GpuStyleLayer> { worst }, false, key, true, null, out _)?.Release(); }
            finally { AllocMeter.On = false; }
            output.WriteLine($"    {worst.Layer.Id} by stage:");
            for (int i = 0; i < AllocMeter.Mb.Length; i++)
                if (AllocMeter.Names[i] is { } stage) output.WriteLine($"      {AllocMeter.Mb[i],7:F1} MB  {stage} ({AllocMeter.Calls[i]} calls, {AllocMeter.Mb[i] * 1048576 / Math.Max(1, AllocMeter.Calls[i]):F0} B each)");
            output.WriteLine($"      {AllocMeter.Glyphs} glyphs shaped");
        }
    }

    sealed class AllocationTicks : System.Diagnostics.Tracing.EventListener
    {
        readonly Dictionary<string, int> _types = new();
        bool _on;
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);   // GC
        }
        public AllocationTicks() { _on = true; }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (!_on || e.EventName is null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload is null) return;
            int i = e.PayloadNames!.IndexOf("TypeName");
            var name = i >= 0 ? e.Payload[i] as string ?? "?" : "?";
            lock (_types) _types[name] = _types.GetValueOrDefault(name) + 1;
        }
        public IEnumerable<(string, int)> Top(int n) { lock (_types) return _types.OrderByDescending(x => x.Value).Take(n).Select(x => (x.Key, x.Value)).ToList(); }
        public override void Dispose() { _on = false; base.Dispose(); }
    }

    static long Live() { Settle(); return GC.GetTotalMemory(false); }

    static long Settle()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(false);
    }
}
