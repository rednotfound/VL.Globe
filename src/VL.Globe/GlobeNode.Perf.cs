using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BruTile;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Extensions;   // ToMeshDraw
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.Import;
using Buffer = Stride.Graphics.Buffer;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VL.Globe;

// The Globe node's measurements: a long Update taken apart, and once a second a perf line into vvvv's log.
public sealed partial class GlobeNode
{
    // Measured, once a second into the log while vector tiles are drawn by the GPU (the user,
    // 2026-11-21: "偶尔在运动加载的时候会卡一下"; 2026-11-22: "远远不如 GL JS 流畅", ROADMAP 4g): the
    // frames (time between two Updates) as FrameStats sums them up - fps, mean, percentiles, those
    // that missed a 60 Hz vsync - the longest this node's own Update took, bytes uploaded and the
    // most in one frame, and garbage collections by generation - a gen-2 one pauses every thread.
    // Every second the camera moved is logged, smooth or not, so the smooth ones are counted too.
    double _perfFrom = -1, _lastUpdateAt = -1, _updateMax;
    int _perfUploads, _gen0, _gen2;
    long _allocated, _mainAllocated;
    // the ephemeral collections seen (the last one each frame): what they promoted and how long they paused -
    // the pause follows what survives (4g step 4), so the survivors are what to know
    long _lastGcIndex = -1, _promoted;
    int _gcSeen, _gen1Seen;
    double _gcPauseSeen;
    long _perfBytes, _perfBytesMax;
    readonly FrameStats _frames = new();
    Matrix _statsView;
    bool _movedThisFrame;

    // a long Update says where its time went: each stage, and how much of it the collector paused
    // (23:16:05 on 2026-11-21: one Update of 580 ms, a gen-2 collection in the same second)
    readonly List<(string stage, double at)> _stages = new();
    TimeSpan _gcPauseAtStart;
    int _gen0AtStart, _gen2AtStart;
    readonly FrameAnatomy _anatomy = new();
    void Stage(string name) => _stages.Add((name, _clock.Elapsed.TotalMilliseconds));

    void Measure(double updateStart)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _updateMax = Math.Max(_updateMax, now - updateStart);
        if (now - updateStart > 50)
        {
            var parts = new List<string>();
            double prev = updateStart;
            foreach (var (stage, at) in _stages) { parts.Add($"{stage} {at - prev:F0}"); prev = at; }
            parts.Add($"rest {now - prev:F0}");
            _logger.LogWarning("Globe perf: an Update of {Ms:F0} ms - {Stages} ms; GC paused {Pause:F0} ms in it; gpu vectors: {Gpu} ms",
                now - updateStart, string.Join(", ", parts), (GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds, _gpu.Breakdown);
        }
        if (_lastUpdateAt >= 0) _frames.Add(updateStart - _lastUpdateAt, _movedThisFrame);
        var gc = GC.GetGCMemoryInfo(GCKind.Ephemeral);
        if (gc.Index != _lastGcIndex && _lastGcIndex >= 0)
        {
            _gcSeen++; _promoted += gc.PromotedBytes;
            if (gc.Generation == 1) _gen1Seen++;
            if (gc.PauseDurations.Length > 0) _gcPauseSeen += gc.PauseDurations[0].TotalMilliseconds;
        }
        _lastGcIndex = gc.Index;
        _anatomy.EndOfUpdate(updateStart, now, _stages, _gpu.StageMs, _gpu.UploadedBytes, _gpu.Uploads, _gcPauseAtStart, _gen0AtStart, _gen2AtStart, _gpu.LastDraw.Serial);
        _movedThisFrame = false;
        _lastUpdateAt = updateStart;
        _perfBytes += _gpu.UploadedBytes;
        _perfBytesMax = Math.Max(_perfBytesMax, _gpu.UploadedBytes);
        _perfUploads += _gpu.Uploads;
        if (_perfFrom < 0)
        {
            _perfFrom = now; _gen0 = GC.CollectionCount(0); _gen2 = GC.CollectionCount(2);
            _allocated = GC.GetTotalAllocatedBytes(false); _mainAllocated = GC.GetAllocatedBytesForCurrentThread();
            return;
        }
        if (now - _perfFrom < 1000) return;
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        // who allocates (ROADMAP 4g step 4: gen0 collections paused the missed frames): this thread is
        // vvvv's main one, which runs Update and Draw and the patch; the rest is the workers building tiles
        long allocated = GC.GetTotalAllocatedBytes(false), mainAllocated = GC.GetAllocatedBytesForCurrentThread();
        double allocMB = (allocated - _allocated) / 1048576.0, mainMB = (mainAllocated - _mainAllocated) / 1048576.0;
        _allocated = allocated; _mainAllocated = mainAllocated;
        // also every second a frame misses a vsync: a steady 30 fps (vsync halving a 17-33 ms frame)
        // had no line, and the vector draw on the render thread was in none (2026-11-22)
        var draw = _gpu.TakeDrawStats();
        var frames = _frames.Take();
        if (_gpu.TilesBuilt > 0 && (frames.Moved || frames.MissedFrames > 0 || _perfUploads > 0 || gen2 > _gen2))
            _logger.LogInformation("Globe perf: {Frames}; Update max {UpdateMax:F1} ms; uploaded {Uploads} tiles {MB:F1} MB, most {MaxMB:F1} MB in a frame; GC gen0 +{Gen0}, gen2 +{Gen2}, allocated {Alloc:F0} MB ({Main:F0} on the main thread), {Seen} ephemeral seen ({Gen1} gen1) promoted {Promoted:F1} MB, paused {SeenPause:F1} ms; draw: {Draw}; frames by part (mean ms): {Parts}",
                frames, _updateMax, _perfUploads, _perfBytes / 1048576.0, _perfBytesMax / 1048576.0, gen0 - _gen0, gen2 - _gen2, allocMB, mainMB, _gcSeen, _gen1Seen, _promoted / 1048576.0, _gcPauseSeen, draw, _anatomy.TakeSecond());
        else _anatomy.TakeSecond();
        _gcSeen = 0; _gen1Seen = 0; _promoted = 0; _gcPauseSeen = 0;
        _perfFrom = now; _updateMax = 0; _perfUploads = 0; _perfBytes = 0; _perfBytesMax = 0;
        _gen0 = gen0; _gen2 = gen2;
    }
}
