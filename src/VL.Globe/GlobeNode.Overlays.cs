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

// The Globe node's draped layers: rendered into each tile's overlay on worker threads, uploaded a few a frame.
public sealed partial class GlobeNode
{
    // Layers draped into the tiles: raster layers above the bottom one, and shapes before any
    // VectorTiles layer (ROADMAP 0.1 items 3-4, 6). Overlays are rendered on worker threads and
    // uploaded here, at most OverlaysPerFrame a frame; a stand-in's overlay, drawn from what was
    // cached, is drawn again once the tile is wanted and may fetch.
    long _signature;
    List<OverlayLayer> _layers = new();
    int _overlayVersion;
    const int OverlaysPerFrame = 6;
    static readonly int OverlayJobs = Math.Max(2, Environment.ProcessorCount - 2);
    double _overlayMs; int _overlaysDrawn;   // the mean time an overlay takes, for Status
    // A replaced overlay texture may still be sampled by a fade or a stand-in's material for a while;
    // it is disposed this many frames later instead of at once.
    readonly List<(Texture texture, int frame)> _retired = new();

    static SKColor ToSk(Color4 c) => new((byte)Math.Clamp(c.R * 255, 0, 255), (byte)Math.Clamp(c.G * 255, 0, 255), (byte)Math.Clamp(c.B * 255, 0, 255), (byte)Math.Clamp(c.A * 255, 0, 255));

    void UpdateOverlays(IEnumerable<GlobeLayer>? layers)
    {
        foreach (var r in _retired.Where(r => _frame - r.frame > 60).ToList()) { r.texture.Dispose(); _retired.Remove(r); }
        // VectorTiles are drawn by the GPU, never draped
        var list = layers?.Where(l => l is { Enabled: true } && l.HasContent && l.Vector is null).Select(l => l!).ToList() ?? new List<GlobeLayer>();
        var h = new HashCode();
        h.Add(list.Count);
        foreach (var l in list) h.Add(l.Signature());
        long signature = h.ToHashCode();
        if (signature != _signature)
        {
            _signature = signature;
            _layers = list.Select(l => l.Raster is { } raster ? (OverlayLayer)new RasterOverlay(raster, l.Opacity)
                : new ShapesOverlay(l.Geometries, new TileOverlay.Style(ToSk(l.Style.LineColor), l.Style.LineWidth, ToSk(l.Style.FillColor)))).ToList();
            _overlayVersion++;
        }
        if (_device is null) return;

        // finished jobs: upload, nearest first, a few a frame
        int budget = OverlaysPerFrame;
        int running = 0;
        // the wanted tiles first, then the stand-ins shown while they load; each nearest first
        var nearest = DrawSet.NearestFirst(_shown.ToList(), _lastEye, _radius);
        var order = nearest.Where(_wantedSet.Contains).Concat(nearest.Where(t => !_wantedSet.Contains(t))).ToList();
        foreach (var t in order)
        {
            var s = _loaded[t];
            if (s.OverlayJob is not { } job) continue;
            if (!job.IsCompleted) { running++; continue; }
            if (budget == 0) continue;
            s.OverlayJob = null;
            if (job.IsFaulted)
            {
                _logger.LogWarning(job.Exception, "Globe: the overlay of {Tile} failed", t);
                s.OverlayVersion = s.OverlayJobVersion; s.OverlayIncomplete = false;   // not retried in a loop
                continue;
            }
            if (s.OverlayJobVersion != _overlayVersion) continue;   // the layers changed meanwhile
            budget--;
            var (bytes, complete, ms) = job.Result;
            SetOverlay(s, bytes);
            _overlaysDrawn++;
            _overlayMs += (ms - _overlayMs) / Math.Min(_overlaysDrawn, 50);   // a running mean of the last ~50
            s.OverlayVersion = s.OverlayJobVersion;
            s.OverlayIncomplete = !complete;
            s.OverlayFetched = s.OverlayJobFetch;
        }

        // new jobs, nearest first
        foreach (var t in order)
        {
            if (running >= OverlayJobs) break;
            var s = _loaded[t];
            if (s.OverlayJob != null) continue;
            bool fetch = _wantedSet.Contains(t);
            // drawn again only when it can come out different: the layers changed, or a stand-in
            // became wanted (now it may fetch)
            bool stale = s.OverlayVersion != _overlayVersion
                || (s.OverlayIncomplete && fetch && !s.OverlayFetched);
            if (!stale) continue;
            if (_layers.Count == 0)
            {
                if (s.Overlay != null) SetOverlay(s, null);   // nothing to drape: undo
                s.OverlayVersion = _overlayVersion; s.OverlayIncomplete = false;
                continue;
            }
            var snapshot = _layers;
            var key = t;
            s.OverlayJobVersion = _overlayVersion;
            s.OverlayJobFetch = fetch;
            s.OverlayJob = Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var bytes = OverlayRenderer.Render(key, snapshot, 256, out bool complete, fetch);
                return (bytes, complete, clock.Elapsed.TotalMilliseconds);
            });
            running++;
        }
    }

    void SetOverlay(Slot s, byte[]? bytes)
    {
        if (s.Overlay is { } old) _retired.Add((old, _frame));
        s.Overlay = bytes is null ? null : FromLevels(_device!, bytes, 256, 256, PixelFormat.R8G8B8A8_UNorm_SRgb, srgb: true, straightAlpha: true);
        s.OverlayStamp++;
        s.Plain = Material.New(_device!, new MaterialDescriptor
        {
            Attributes = { Emissive = new MaterialEmissiveMapFeature(Look(s, Vector2.One, Vector2.Zero)), CullMode = CullMode.None },
        });
        if (s.Fading is null) s.Model.Materials[0] = s.Plain;
    }
}
