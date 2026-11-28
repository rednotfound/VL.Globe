using System.Reflection;
using VL.Globe.Engine.VectorTiles;

namespace VL.Globe.Tests.Nodes;

// TileCache [Globe.Layers] (2026-11-28): the folders tile layers keep their tiles in, a size limit, Clear.
// It deletes files, so the first facts are about what it may never touch.
public class TileCacheFacts : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vl-globe-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string Write(string relative, int bytes, DateTime? written = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (written is { } t) File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    [Fact]
    public void A_folder_holds_raster_and_vector_tiles_side_by_side()
    {
        var cache = GlobeTileCache.In(_root, true);
        Assert.Equal(Path.Combine(_root, "tiles"), cache.RasterFolder);
        Assert.Equal(Path.Combine(_root, "vector-tiles"), cache.VectorFolder);
    }

    [Fact]
    public void No_folder_is_the_default_folders_the_raster_one_shared_with_VL_Mapsui()
    {
        var cache = GlobeTileCache.In("", true);
        Assert.EndsWith(Path.Combine("VL.Mapsui", "tiles"), cache.RasterFolder);
        Assert.EndsWith(Path.Combine("VL.Globe", "vector-tiles"), cache.VectorFolder);
        Assert.Equal(GlobeTileCache.Default.Key, cache.Key);
    }

    [Fact]
    public void Only_the_caches_own_files_are_counted()
    {
        Write(@"tiles\osm\3\1\2.png", 100);
        Write(@"vector-tiles\host\abc.pbf", 50);
        Write(@"tiles\notes.txt", 999);            // not a tile
        Write(@"vector-tiles\photo.png", 999);     // a png, but in the vector folder
        Write(@"holiday.png", 999);                // beside the cache, not in it
        var files = TileCacheFiles.Files(GlobeTileCache.In(_root, true));
        Assert.Equal((2, 150L), TileCacheFiles.Stats(files));
    }

    [Fact]
    public void Clear_deletes_the_tiles_and_nothing_else()
    {
        var tile = Write(@"tiles\osm\3\1\2.png", 100);
        var vector = Write(@"vector-tiles\host\abc.pbf", 50);
        var notes = Write(@"tiles\notes.txt", 10);
        var beside = Write(@"holiday.png", 10);
        var cache = GlobeTileCache.In(_root, true);
        Assert.Equal((0, 0L), TileCacheFiles.Clear(TileCacheFiles.Files(cache)));
        Assert.False(File.Exists(tile));
        Assert.False(File.Exists(vector));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(beside));
    }

    [Fact]
    public void Over_the_limit_the_tiles_written_longest_ago_go_first()
    {
        var now = DateTime.UtcNow;
        var oldest = Write(@"tiles\osm\1\0\0.png", 400, now.AddDays(-5));
        var old = Write(@"vector-tiles\host\a.pbf", 400, now.AddDays(-3));
        var recent = Write(@"tiles\osm\1\0\1.png", 400, now.AddDays(-1));
        var newest = Write(@"vector-tiles\host\b.pbf", 400, now);
        var cache = GlobeTileCache.In(_root, true);

        // 1600 bytes over a limit of 1000: down to nine tenths, 900 - two files stay
        var (tiles, bytes) = TileCacheFiles.Prune(TileCacheFiles.Files(cache), 1000);
        Assert.Equal((2, 800L), (tiles, bytes));
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Under_the_limit_and_without_one_nothing_is_deleted()
    {
        Write(@"tiles\osm\1\0\0.png", 400);
        Write(@"vector-tiles\host\a.pbf", 400);
        var cache = GlobeTileCache.In(_root, true);
        Assert.Equal((2, 800L), TileCacheFiles.Prune(TileCacheFiles.Files(cache), 800));
        Assert.Equal((2, 800L), TileCacheFiles.Prune(TileCacheFiles.Files(cache), 0));
        Assert.Equal(2, TileCacheFiles.Files(cache).Count);
    }

    [Fact]
    public void The_node_hands_out_the_same_cache_while_nothing_changes()
    {
        var node = new GlobeTileCacheNode();
        var folder = new VL.Lib.IO.Path(_root);
        var first = node.Update(out _, out _, out _, folder, 0);
        var second = node.Update(out _, out _, out _, folder, 0);
        Assert.Same(first, second);
        Assert.Equal(1, node.CachesBuilt);
        Assert.True(first.IsOn);
        Assert.True(Directory.Exists(first.RasterFolder) && Directory.Exists(first.VectorFolder));

        var off = node.Update(out _, out _, out var status, folder, 0, enabled: false);
        Assert.NotSame(first, off);
        Assert.False(off.IsOn);
        Assert.StartsWith("off", status);
        Assert.Equal("off", off.Key);
    }

    [Fact]
    public void A_folder_that_cannot_be_used_is_off_and_says_why_never_the_default()
    {
        var file = Write("occupied", 1);   // a file where the folder should be
        var node = new GlobeTileCacheNode();
        var cache = node.Update(out _, out _, out var status, new VL.Lib.IO.Path(file), 0);
        Assert.False(cache.IsOn);
        Assert.NotNull(cache.Problem);
        Assert.StartsWith("off", status);
        Assert.NotEqual(GlobeTileCache.Default.RasterFolder, cache.RasterFolder);
    }

    [Fact]
    public async Task The_node_counts_and_prunes_off_the_main_thread()
    {
        var now = DateTime.UtcNow;
        Write(@"tiles\osm\1\0\0.png", 700_000, now.AddDays(-2));
        Write(@"vector-tiles\host\a.pbf", 700_000, now);
        var node = new GlobeTileCacheNode();
        var folder = new VL.Lib.IO.Path(_root);
        int tiles = 0; float mb = 0; string status = "";
        for (int i = 0; i < 200 && tiles != 1; i++) { node.Update(out tiles, out mb, out status, folder, 1); await Task.Delay(10); }
        // 1.4 MB over a limit of 1 MB: the older file is gone
        Assert.Equal(1, tiles);
        Assert.InRange(mb, 0.6f, 0.7f);
        Assert.Contains("of 1 MB", status);
        Assert.False(File.Exists(Path.Combine(_root, @"tiles\osm\1\0\0.png")));
    }

    [Fact]
    public void A_vector_service_keeps_its_files_in_the_caches_folder_or_nowhere_when_off()
    {
        var file = typeof(VectorTileService).GetMethod("CacheFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string url = "https://tiles.example.org/planet/3/1/2.pbf";

        using var on = new VectorTileService("https://tiles.example.org/style", GlobeTileCache.In(_root, true));
        var path = (string?)file.Invoke(on, new object[] { url });
        Assert.StartsWith(Path.Combine(_root, "vector-tiles"), path);
        Assert.EndsWith(".pbf", path);

        using var off = new VectorTileService("https://tiles.example.org/style", GlobeTileCache.In(_root, false));
        Assert.Null(file.Invoke(off, new object[] { url }));

        using var unconnected = new VectorTileService("https://tiles.example.org/style", (GlobeTileCache?)null);
        Assert.StartsWith(VectorTileService.DefaultCacheRoot, (string?)file.Invoke(unconnected, new object[] { url }));
    }

    [Fact]
    public void A_raster_source_keeps_its_tiles_in_the_caches_folder_or_nowhere_when_off()
    {
        const string url = "https://tiles.example.org/{z}/{x}/{y}.png";
        var on = TileSourceFactory.Create(url, "test", url, GlobeTileCache.In(_root, true));
        Assert.StartsWith(Path.Combine(_root, "tiles"), on.CacheFolder);
        Assert.IsType<BruTile.Cache.FileCache>(on.Http.PersistentCache);
        var off = TileSourceFactory.Create(url, "test", url, GlobeTileCache.In(_root, false));
        // BruTile puts its NullCache where none is given: nothing on disk
        Assert.Equal("NullCache", off.Http.PersistentCache.GetType().Name);
    }

    [Fact]
    public void The_layers_take_a_cache_before_Enabled_and_TileCaches_pins_are_in_vvvvs_order()
    {
        static string[] Pins(Type node) => node.GetMethod("Update")!.GetParameters().Select(p => p.Name!).ToArray();
        Assert.Equal(new[] { "opacity", "cache", "enabled" }, Pins(typeof(OpenStreetMapNode)));
        Assert.Equal(new[] { "url", "attribution", "opacity", "cache", "enabled" }, Pins(typeof(XyzTilesNode)));
        Assert.Equal(new[] { "attribution", "status", "style", "name", "cache", "enabled" }, Pins(typeof(GlobeVectorTilesNode)));
        Assert.Equal(new[] { "tiles", "sizeMB", "status", "folder", "maxSize", "clear", "enabled" }, Pins(typeof(GlobeTileCacheNode)));
    }
}
