using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VL.Globe.Engine;

/// <summary>
/// ROADMAP 4g step 4: a frame that missed its vsync, taken apart - the Globe node's Update by stage,
/// the vector Draw by part, garbage collection, and what is left: time outside the node (vvvv, Stride
/// drawing everything else, the wait for the present).
/// </summary>
/// <remarks>
/// The per-second line could not say where a missing frame went: a second's uploads and a second's
/// misses lined up, the bytes were halved, the misses stayed (2026-11-23). A frame here is the time
/// from one Update's start to the next's. Its Update is known at its end; its Draw comes after it,
/// before the next Update; so a frame is explained at the next Update, from what was kept at the end
/// of its own.
/// </remarks>
internal sealed class FrameAnatomy
{
    public const double Missed = FrameStats.Missed;
    const int MostLinesASecond = 20;
    static readonly string[] GpuStages = { "finish+upload", "tiles+jobs", "resolve", "backgrounds", "atlases", "symbol layers", "other layers", "evict" };

    // the frame under way, kept at the end of its Update
    bool _have;
    double _updateMs;
    readonly List<(string Stage, double Ms)> _stages = new();
    readonly double[] _gpu = new double[GpuStages.Length];
    long _uploadBytes;
    int _uploads;
    TimeSpan _gcPause;
    int _gen0, _gen2;
    long _drawSerial;

    double _secondFrom = double.NegativeInfinity;
    int _lines;

    // Every frame's parts, summed for a second, the frames that made it and those that missed apart: a
    // missed frame's "outside 8 ms" says nothing without what an ordinary frame spends there (2026-11-23).
    static readonly string[] Parts = { "update", "gpu vectors", "place", "opacities", "submit", "upload", "outside", "gc" };
    readonly double[] _okSum = new double[Parts.Length], _missSum = new double[Parts.Length];
    int _okCount, _missCount;

    void Accumulate(double interval, TimeSpan gcPauseNow, VectorRenderer.DrawTiming draw)
    {
        bool one = draw.Serial - _drawSerial == 1;
        double drawMs = one ? draw.Total : 0, gpu = 0;
        foreach (var (stage, ms) in _stages) if (stage == "gpu vectors") gpu = ms;
        var sum = interval > Missed ? _missSum : _okSum;
        if (interval > Missed) _missCount++; else _okCount++;
        sum[0] += _updateMs; sum[1] += gpu;
        if (one) { sum[2] += draw.Place; sum[3] += draw.Labels.Opacities; sum[4] += draw.Submit; sum[5] += draw.Upload; }
        sum[6] += interval - _updateMs - drawMs;
        sum[7] += (gcPauseNow - _gcPause).TotalMilliseconds;
    }

    /// <summary>The second's frames by part (mean ms), made and missed apart; then starts a new second.</summary>
    public string TakeSecond()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Row(string name, double[] sum, int n)
        {
            if (n == 0) return;
            sb.Append(c, $"{(sb.Length > 0 ? "; " : "")}{name} {n}:");
            for (int i = 0; i < Parts.Length; i++) sb.Append(c, $" {Parts[i]} {sum[i] / n:F1}");
        }
        Row("made", _okSum, _okCount);
        Row("missed", _missSum, _missCount);
        Array.Clear(_okSum); Array.Clear(_missSum); _okCount = _missCount = 0;
        return sb.ToString();
    }

    /// <summary>At the end of an Update: what it took, and the counters it began with.</summary>
    public void EndOfUpdate(double updateStart, double now, List<(string stage, double at)> stages, ReadOnlySpan<double> gpu,
        long uploadBytes, int uploads, TimeSpan gcPauseAtStart, int gen0AtStart, int gen2AtStart, long drawSerial)
    {
        _have = true;
        _updateMs = now - updateStart;
        _stages.Clear();
        double prev = updateStart;
        foreach (var (stage, at) in stages) { _stages.Add((stage, at - prev)); prev = at; }
        _stages.Add(("rest", now - prev));
        for (int i = 0; i < _gpu.Length; i++) _gpu[i] = i < gpu.Length ? gpu[i] : 0;
        _uploadBytes = uploadBytes; _uploads = uploads;
        _gcPause = gcPauseAtStart; _gen0 = gen0AtStart; _gen2 = gen2AtStart;
        _drawSerial = drawSerial;
    }

    /// <summary>
    /// At the next Update's start: the frame that ended, if it missed - <paramref name="interval"/> ms
    /// from its Update's start to this one's - as a log line; else null. At most 20 lines a second.
    /// </summary>
    public string? Explain(double interval, double now, TimeSpan gcPauseNow, int gen0Now, int gen2Now, VectorRenderer.DrawTiming draw)
    {
        if (!_have) return null;
        Accumulate(interval, gcPauseNow, draw);
        if (interval <= Missed) return null;
        if (now - _secondFrom >= 1000) { _secondFrom = now; _lines = 0; }
        if (++_lines > MostLinesASecond) return null;
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"{interval:F1} ms = Update {_updateMs:F1}");
        sb.Append(" (");
        bool first = true;
        foreach (var (stage, ms) in _stages)
            if (ms >= 0.5) { sb.Append(c, $"{(first ? "" : ", ")}{stage} {ms:F1}"); first = false; }
        sb.Append(')');
        long draws = draw.Serial - _drawSerial;
        double drawMs = draws == 1 ? draw.Total : 0;
        if (draws == 1)
        {
            var l = draw.Labels;
            sb.Append(c, $" + draw {draw.Total:F1} (place {draw.Place:F1} [index {l.Index:F1}, placement {l.Placement:F1}, commit {l.Commit:F1}, opacities {l.Opacities:F1}, lines {l.Lines:F1}], upload {draw.Upload:F1}, submit {draw.Submit:F1} [vector sets {draw.Parts.VectorSets:F1}, apply {draw.Parts.VectorApply:F1}, draw {draw.Parts.VectorDraw:F1}; text sets {draw.Parts.TextSets:F1}, apply {draw.Parts.TextApply:F1}, draw {draw.Parts.TextDraw:F1}], {draw.Items} items)");
        }
        else sb.Append(c, $" + {draws} draws");
        sb.Append(c, $" + outside {interval - _updateMs - drawMs:F1}");
        sb.Append(c, $"; GC paused {(gcPauseNow - _gcPause).TotalMilliseconds:F1} ms, gen0 +{gen0Now - _gen0}, gen2 +{gen2Now - _gen2}");
        if (_uploads > 0) sb.Append(c, $"; uploaded {_uploads} tiles {_uploadBytes / 1048576.0:F1} MB");
        sb.Append("; gpu vectors:");
        first = true;
        for (int i = 0; i < _gpu.Length; i++)
            if (_gpu[i] >= 0.5) { sb.Append(c, $"{(first ? " " : ", ")}{GpuStages[i]} {_gpu[i]:F1}"); first = false; }
        if (first) sb.Append(" -");
        return sb.ToString();
    }
}
