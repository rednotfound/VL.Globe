using System;
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
// Max Size and Clear delete files, so they only ever touch *.png under the raster folder and *.pbf under
// the vector folder - a Folder pointed at Documents loses nothing of the user's. The scan runs on the
// thread pool every 30 s (a cache of 1 GB is tens of thousands of files), never on vvvv's main thread.
// Layers with no TileCache connected use the default folders and are not pruned.

/// <summary>Where tile layers keep their tiles on disk: from TileCache, for a tile layer's Cache pin.</summary>
public sealed class GlobeTileCache
{
    internal static readonly GlobeTileCache Default = new(TileSourceFactory.CacheRoot, Engine.VectorTiles.VectorTileService.DefaultCacheRoot, true, null);

    internal GlobeTileCache(string rasterFolder, string vectorFolder, bool on, string? problem)
    {
        RasterFolder = rasterFolder;
        VectorFolder = vectorFolder;
        IsOn = on;
        Problem = problem;
    }

    internal static GlobeTileCache In(string? folder, bool on, string? problem = null)
        => string.IsNullOrWhiteSpace(folder)
            ? new GlobeTileCache(Default.RasterFolder, Default.VectorFolder, on, problem)
            : new GlobeTileCache(Path.Combine(folder, "tiles"), Path.Combine(folder, "vector-tiles"), on, problem);

    /// <summary>The folder raster tiles are kept in.</summary>
    public string RasterFolder { get; }

    /// <summary>The folder vector tiles, styles, fonts and icons are kept in.</summary>
    public string VectorFolder { get; }

    /// <summary>Whether tiles are kept on disk.</summary>
    public bool IsOn { get; }

    /// <summary>Why the cache is off, when its folder could not be used.</summary>
    public string? Problem { get; }

    // what a layer rebuilds its source for
    internal string Key => IsOn ? RasterFolder + "|" + VectorFolder : "off";

    // short enough for a Status IOBox: the folder the two are in, or that they are the default ones
    public override string ToString() => !IsOn ? Problem ?? "off"
        : RasterFolder == Default.RasterFolder && VectorFolder == Default.VectorFolder ? "the default folders"
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

    /// <summary>Over <paramref name="maxBytes"/>: the files written longest ago are deleted until nine tenths of it are left. Returns what stays.</summary>
    internal static (int Tiles, long Bytes) Prune(List<FileInfo> files, long maxBytes)
    {
        var (count, bytes) = Stats(files);
        if (maxBytes <= 0 || bytes <= maxBytes) return (count, bytes);
        long target = maxBytes / 10 * 9;
        foreach (var f in files.OrderBy(f => SafeTime(f)))
        {
            if (bytes <= target) break;
            try { long length = f.Length; f.Delete(); bytes -= length; count--; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (count, bytes);

        static DateTime SafeTime(FileInfo f) { try { return f.LastWriteTimeUtc; } catch (IOException) { return DateTime.MaxValue; } }
    }

    internal static (int Tiles, long Bytes) Clear(List<FileInfo> files)
    {
        int left = 0; long bytes = 0;
        foreach (var f in files)
        {
            try { f.Delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { try { bytes += f.Length; left++; } catch (IOException) { } }
        }
        return (left, bytes);
    }
}

/// <summary>Chooses where tile layers keep their tiles on disk, and how much.</summary>
/// <remarks>Hand the output to the Cache pin of OpenStreetMap, XyzTiles and VectorTiles. Reads and deletes on disk only, never fetches.</remarks>
[ProcessNode(Name = "TileCache", Category = "Globe.Layers")]
public sealed class GlobeTileCacheNode
{
    const long ScanEveryMs = 30_000;

    GlobeTileCache? _cache;
    string _folder = "";
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
    /// <param name="status">The folders in use, or why the cache is off.</param>
    /// <param name="folder">Where tiles go. Unconnected: the default folders under %LOCALAPPDATA%. An empty Path IOBox is the patch's own folder.</param>
    /// <param name="maxSize">Megabytes to keep; over it the oldest tiles are deleted. 0: no limit.</param>
    /// <param name="clear">Deletes every tile in the cache.</param>
    /// <param name="enabled">Keeps fetched tiles on disk. Off: every start fetches again.</param>
    [return: Pin(Name = "Output")]
    public GlobeTileCache Update(out int tiles, out float sizeMB, out string status,
        VLPath? folder = null, int maxSize = 4096, bool clear = false, bool enabled = true)
    {
        var wanted = folder?.Value ?? "";
        if (_cache is null || enabled != _enabled || !string.Equals(wanted, _folder, StringComparison.OrdinalIgnoreCase))
        {
            _cache = Build(wanted, enabled);
            _folder = wanted;
            _enabled = enabled;
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
                var (count, bytes) = Interlocked.Exchange(ref _clearAsked, 0) == 1 ? TileCacheFiles.Clear(files)
                    : cache.IsOn ? TileCacheFiles.Prune(files, maxBytes)
                    : TileCacheFiles.Stats(files);
                if (!ReferenceEquals(cache, _cache)) return;
                Interlocked.Exchange(ref _bytes, bytes);
                _tiles = count;
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

    static GlobeTileCache Build(string folder, bool enabled)
    {
        if (!enabled) return GlobeTileCache.In(folder, false);
        var cache = GlobeTileCache.In(folder, true);
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
            return GlobeTileCache.In(folder, false, $"off - {folder} cannot be used: {e.Message}");
        }
    }
}
