using System.Collections.Generic;
using System.Linq;
using Stride.Engine;
using Buffer = Stride.Graphics.Buffer;

namespace VL.Globe;

// The Globe node's drawing: which tiles are in the scene, and the stand-ins for squares not yet built.
// No fades: the tiles are all one colour since ROADMAP 6c step 3; the layers on them fade (RasterTiles).
public sealed partial class GlobeNode
{
    // DrawSet's StandIns: a square not yet built, drawn with its own mesh at the surface - never two
    // things on one spot. Kept by square so a square that stays missing for many frames is built once;
    // dropped when no longer drawn. From: the built ancestor it stands for, kept from eviction.
    sealed class StandIn
    {
        public required Entity Entity;
        public required Buffer Vertices;
        public required Buffer Indices;
        public required D3 Centre;
        public required ModelComponent Model;
        public TileKey From;
        public void Dispose() { Vertices.Dispose(); Indices.Dispose(); }
    }
    readonly Dictionary<TileKey, StandIn> _standIns = new();

    void Show(DrawSet.Layers layers)
    {
        ShowStandIns(layers.StandIns);
        var next = new HashSet<TileKey>(layers.Top);
        foreach (var t in _shown.Where(t => !next.Contains(t)).ToList())
        {
            if (_loaded.TryGetValue(t, out var s)) _root.Transform.Children.Remove(s.Entity.Transform);
            _shown.Remove(t);
        }
        foreach (var t in layers.Top)
        {
            if (_shown.Contains(t)) continue;
            var s = _loaded[t];
            _root.Transform.Children.Add(s.Entity.Transform);
            _shown.Add(t);
            s.LastUsed = _frame;
        }
    }

    void ShowStandIns(List<(TileKey Square, TileKey From)> wanted)
    {
        var keep = new HashSet<TileKey>();
        foreach (var (square, fromKey) in wanted)
        {
            if (_device is null || _plain is null || !_loaded.ContainsKey(fromKey)) continue;
            keep.Add(square);
            if (!_standIns.TryGetValue(square, out var si))
            {
                var (vb, ib, mesh, centre) = BuildMesh(_device, square);
                var (entity, model) = MakeEntity(square, mesh, _plain, centre);
                si = new StandIn { Entity = entity, Vertices = vb, Indices = ib, Centre = centre, Model = model, From = fromKey };
                _standIns[square] = si;
                _root.Transform.Children.Add(entity.Transform);
            }
            else si.From = fromKey;
        }
        foreach (var square in _standIns.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var si = _standIns[square];
            _root.Transform.Children.Remove(si.Entity.Transform);
            si.Dispose();
            _standIns.Remove(square);
        }
    }
}
