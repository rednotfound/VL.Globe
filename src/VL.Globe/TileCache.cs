using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VL.Core.Import;

using VLPath = VL.Lib.IO.Path;

namespace VL.Globe;

// The /// text is the tooltip; the reasoning is here.
//
// Asked by the user on 2026-11-28 ("我们有类似于tilesCache的节点来设置vectortiles缓存的目录什么的么"): the
// folders were constants and the vector cache only grew - 1.1 GB on their machine. Shaped as VL.Mapsui's
// TileCache: one node owns the cache, a layer's Cache pin consumes it, "off" and "this folder did not
// work" are values and never null (an unconnected pin is null and means the default).
//
// One Folder holds both kinds: <Folder>\tiles (raster, BruTile's layout, as VL.Mapsui's) and
// <Folder>\vector-tiles. Unconnected: the two default folders, the raster one shared with VL.Mapsui.
//
// Max Size, Max Age and Clear delete files, so they only ever touch *.png under the raster folder and
// *.pbf under the vector folder - a Folder pointed at Documents loses nothing of the user's. The scan runs
// on the thread pool every 30 s (a cache of 1 GB is tens of thousands of files), never on vvvv's main thread.
//
// The same day, the user's two decisions: layers with NO TileCache connected are limited too (the default
// folders: 4096 MB, 7 days - TileCacheJanitor, every 5 minutes while such a layer runs; the raster folder
// is shared with VL.Mapsui and its tiles go with ours), and Max Age is a pin ("输入不超过四个的规则也不用
// 那么死板"): five inputs here.

/// <summary>Where tile layers keep their tiles on disk: from TileCache, for a tile layer's Cache pin.</summary>
public sealed class GlobeTileCache
{
    internal const int DefaultMaxSizeMB = 4096;
    internal const int DefaultMaxAgeDays = 7;

    internal static readonly GlobeTileCache Default = new(TileSourceFactory.CacheRoot, Engine.VectorTiles.VectorTileService.DefaultCacheRoot, true, null, DefaultMaxAgeDays);

    internal GlobeTileCache(string rasterFolder, string vectorFolder, bool on, string? problem, int maxAgeDays)
    {
        RasterFolder = rasterFolder;
        VectorFolder = vectorFolder;
        IsOn = on;
        Problem = problem;
        MaxAgeDays = Math.Max(0, maxAgeDays);
    }

    internal static GlobeTileCache In(string? folder, bool on, string? problem = null, int maxAgeDays = DefaultMaxAgeDays)
        => string.IsNullOrWhiteSpace(folder)
            ? new GlobeTileCache(Default.RasterFolder, Default.VectorFolder, on, problem, maxAgeDays)
            : new GlobeTileCache(Path.Combine(folder, "tiles"), Path.Combine(folder, "vector-tiles"), on, problem, maxAgeDays);

    /// <summary>The folder raster tiles are kept in.</summary>
    public string RasterFolder { get; }

    /// <summary>The folder vector tiles, styles, fonts and icons are kept in.</summary>
    public string VectorFolder { get; }

    /// <summary>Whether tiles are kept on disk.</summary>
    public bool IsOn { get; }

    /// <summary>Why the cache is off, when its folder could not be used.</summary>
    public string? Problem { get; }

    /// <summary>Days a tile is used before it is fetched again; 0: for ever.</summary>
    public int MaxAgeDays { get; }

    // 0 is "never": a century, so nobody has to know what a library does with TimeSpan.Zero
    internal TimeSpan MaxAge => MaxAgeDays > 0 ? TimeSpan.FromDays(MaxAgeDays) : TimeSpan.FromDays(36500);

    // what a layer rebuilds its source for
    internal string Key => IsOn ? $"{RasterFolder}|{VectorFolder}|{MaxAgeDays}" : "off";

    internal bool IsDefaultFolders => RasterFolder == Default.RasterFolder && VectorFolder == Default.VectorFolder;

    // short enough for a Status IOBox: the folder the two are in, or that they are the default ones
    public override string ToString() => !IsOn ? Problem ?? "off"
        : IsDefaultFolders ? "the default folders"
        : Path.GetDirectoryName(RasterFolder) ?? RasterFolder;
}

internal static class TileCacheFiles
{
    /// <summary>The cache's own files: *.png under the raster folder, *.pbf under the vector folder. Nothing else is ever counted or deleted.</summary>
    internal static List<FileInfo> Files(GlobeTileCache cache)
    {
        var files = new List<FileInfo>();
        Add(cache.RasterFolder, "*.png");
        Add(cache.VectorFolder, "*.pbf");
        return files;

        void Add(string folder, string pattern)
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) return;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var f in dir.EnumerateFiles(pattern, options)) files.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static (int Tiles, long Bytes) Stats(List<FileInfo> files)
    {
        long bytes = 0;
        foreach (var f in files) { try { bytes += f.Length; } catch (IOException) { } }
        return (files.Count, bytes);
    }

    /// <summary>
    /// Files older than <paramref name="maxAge"/> are deleted (they would be fetched again anyway); then, over
    /// <paramref name="maxBytes"/>, the files written longest ago until nine tenths of it are left. Returns what stays.
    /// </summary>
    internal static (int Tiles, long Bytes, int Deleted) Prune(List<FileInfo> files, long maxBytes, TimeSpan maxAge = default)
    {
        int count = 0, deleted = 0; long bytes = 0;
        var kept = new List<(FileInfo File, DateTime Written, long Length)>(files.Count);
        var expired = maxAge > TimeSpan.Zero ? DateTime.UtcNow - maxAge : DateTime.MinValue;
        foreach (var f in files)
        {
            DateTime written; long length;
            try { written = f.LastWriteTimeUtc; length = f.Length; } catch (IOException) { continue; }
            if (written < expired && Delete(f)) { deleted++; continue; }
            kept.Add((f, written, length)); count++; bytes += length;
        }
        if (maxBytes > 0 && bytes > maxBytes)
        {
            long target = maxBytes / 10 * 9;
            foreach (var (f, _, length) in kept.OrderBy(k => k.Written))
            {
                if (bytes <= target) break;
                if (Delete(f)) { bytes -= length; count--; deleted++; }
            }
        }
        return (count, bytes, deleted);
    }

    internal static (int Tiles, long Bytes, int Deleted) Clear(List<FileInfo> files)
    {
        int left = 0, deleted = 0; long bytes = 0;
        foreach (var f in files)
        {
            if (Delete(f)) deleted++;
            else { try { bytes += f.Length; left++; } catch (IOException) { } }
        }
        return (left, bytes, deleted);
    }

    static bool Delete(FileInfo f)
    {
        try { f.Delete(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Keeps the DEFAULT folders within their limits for layers with no TileCache connected, and carries
/// what any cache scan deleted to the log (the Globe node writes it).
/// </summary>
internal static class TileCacheJanitor
{
    const long EveryMs = 5 * 60_000;
    static long _next;
    static int _running;

    /// <summary>Off in the unit tests: they run layer nodes, and must not delete from the machine's real cache.</summary>
    internal static bool Off;

    /// <summary>What scans deleted, for the log: taken by the Globe node.</summary>
    internal static readonly ConcurrentQueue<string> Reports = new();

    /// <summary>Called by a tile layer every frame: prunes the default folders now and then, on the thread pool.</summary>
    internal static void Tend(GlobeTileCache cache)
    {
        if (Off || !ReferenceEquals(cache, GlobeTileCache.Default)) return;   // a TileCache node looks after its own
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _next) || Interlocked.Exchange(ref _running, 1) == 1) return;
        Volatile.Write(ref _next, now + EveryMs);
        Task.Run(() =>
        {
            try { Report(cache, PruneDefault(cache), "no TileCache connected"); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    internal static (int Tiles, long Bytes, int Deleted) PruneDefault(GlobeTileCache cache)
        => TileCacheFiles.Prune(TileCacheFiles.Files(cache), GlobeTileCache.DefaultMaxSizeMB * 1048576L, cache.MaxAge);

    internal static void Report(GlobeTileCache cache, (int Tiles, long Bytes, int Deleted) result, string why)
    {
        if (result.Deleted > 0)
            Reports.Enqueue($"{cache}: {result.Deleted} tiles deleted ({why}), {result.Tiles} left, {result.Bytes / 1048576.0:F0} MB");
    }
}

/// <summary>Chooses where tile layers keep their tiles on disk, how much and for how long.</summary>
/// <remarks>Hand the output to the Cache pin of OpenStreetMap, XyzTiles and VectorTiles. Reads and deletes on disk only, never fetches.</remarks>
[ProcessNode(Name = "TileCache", Category = "Globe.Layers")]
public sealed class GlobeTileCacheNode
{
    const long ScanEveryMs = 30_000;

    GlobeTileCache? _cache;
    string _folder = "";
    int _maxAge;
    bool _enabled, _lastClear;
    int _clearAsked;
    Task? _scan;
    long _nextScan;
    volatile int _tiles;
    long _bytes;
    volatile bool _scanned;

    /// <summary>Caches built by this node: 1 while nothing changes.</summary>
    internal int CachesBuilt { get; private set; }

    /// <param name="tiles">How many files are in the cache.</param>
    /// <param name="sizeMB">How much disk they take, in megabytes.</param>
    /// <param name="status">The folder in use, or why the cache is off.</param>
    /// <param name="folder">Where tiles go. Unconnected: the default folders under %LOCALAPPDATA%. An empty Path IOBox is the patch's own folder.</param>
    /// <param name="maxSize">Megabytes to keep; over it the oldest tiles are deleted. 0: no limit.</param>
    /// <param name="maxAge">Days a tile is used before it is fetched again, and deleted. 0: for ever.</param>
    /// <param name="clear">Deletes every tile in the cache.</param>
    /// <param name="enabled">Keeps fetched tiles on disk. Off: every start fetches again.</param>
    [return: Pin(Name = "Output")]
    public GlobeTileCache Update(out int tiles, out float sizeMB, out string status,
        VLPath? folder = null, int maxSize = GlobeTileCache.DefaultMaxSizeMB, int maxAge = GlobeTileCache.DefaultMaxAgeDays,
        bool clear = false, bool enabled = true)
    {
        var wanted = folder?.Value ?? "";
        maxAge = Math.Max(0, maxAge);
        if (_cache is null || enabled != _enabled || maxAge != _maxAge || !string.Equals(wanted, _folder, StringComparison.OrdinalIgnoreCase))
        {
            _cache = Build(wanted, enabled, maxAge);
            _folder = wanted;
            _enabled = enabled;
            _maxAge = maxAge;
            _scanned = false;
            _nextScan = 0;
            CachesBuilt++;
        }
        if (clear && !_lastClear) { Interlocked.Exchange(ref _clearAsked, 1); _nextScan = 0; }
        _lastClear = clear;

        long now = Environment.TickCount64;
        if (now >= _nextScan && (_scan is null || _scan.IsCompleted))
        {
            _nextScan = now + ScanEveryMs;
            var cache = _cache;
            long maxBytes = Math.Max(0, maxSize) * 1048576L;
            _scan = Task.Run(() =>
            {
                var files = TileCacheFiles.Files(cache);
                (int Tiles, long Bytes, int Deleted) result;
                if (Interlocked.Exchange(ref _clearAsked, 0) == 1) { result = TileCacheFiles.Clear(files); TileCacheJanitor.Report(cache, result, "Clear"); }
                else if (cache.IsOn) { result = TileCacheFiles.Prune(files, maxBytes, cache.MaxAgeDays > 0 ? cache.MaxAge : default); TileCacheJanitor.Report(cache, result, "over Max Size or Max Age"); }
                else { var (count, bytes) = TileCacheFiles.Stats(files); result = (count, bytes, 0); }
                if (!ReferenceEquals(cache, _cache)) return;
                Interlocked.Exchange(ref _bytes, result.Bytes);
                _tiles = result.Tiles;
                _scanned = true;
            });
        }

        tiles = _tiles;
        sizeMB = (float)(Interlocked.Read(ref _bytes) / 1048576.0);
        status = _cache.Problem is { } problem ? problem
            : !_cache.IsOn ? "off - tiles are not kept, every start fetches again"
            : !_scanned ? $"{_cache}: counting..."
            : maxSize > 0 ? $"{_cache}: {tiles} tiles, {sizeMB:F0} of {maxSize} MB"
            : $"{_cache}: {tiles} tiles, {sizeMB:F0} MB, no limit";
        return _cache;
    }

    static GlobeTileCache Build(string folder, bool enabled, int maxAge)
    {
        if (!enabled) return GlobeTileCache.In(folder, false, null, maxAge);
        var cache = GlobeTileCache.In(folder, true, null, maxAge);
        try
        {
            Directory.CreateDirectory(cache.RasterFolder);
            Directory.CreateDirectory(cache.VectorFolder);
            return cache;
        }
        // a folder that cannot be used is off and says so; falling back to the default would put files
        // where nobody asked for them
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return GlobeTileCache.In(folder, false, $"off - {folder} cannot be used: {e.Message}", maxAge);
        }
    }
}
