using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VL.Globe.Engine;

// A MapLibre style fetched for the globe (ROADMAP 4c): the style JSON by URL or file, every vector
// source's TileJSON, then the tiles on demand. The painter asks for a tile synchronously from a
// worker thread (Load): it is answered from memory or the disk cache, or a fetch starts and the
// answer is "not yet" (Pending). Every arrival bumps Arrivals, which is how the globe knows to
// redraw the overlays that were drawn without it.
//
// Nothing is fetched until Start is called - the node calls it only while Enabled is on.
internal sealed class VectorTileService : IDisposable
{
    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(TileSourceFactory.UserAgent);
        return client;
    }

    // the disk cache: %LOCALAPPDATA%\VL.Globe\vector-tiles\<host>-<hash of the template>\<hash of the url>.pbf
    internal static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VL.Globe", "vector-tiles");
    static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    const int MaxInFlight = 6;

    readonly CancellationTokenSource _cancel = new();
    readonly ConcurrentDictionary<string, byte[]> _arrived = new();
    readonly ConcurrentDictionary<string, bool> _inFlight = new();
    readonly ConcurrentDictionary<string, bool> _failed = new();
    readonly ConcurrentQueue<string> _queue = new();
    readonly ConcurrentDictionary<string, bool> _queued = new();
    int _arrivals, _fetched, _fromDisk, _version, _started;
    volatile MapStyleDocument? _document;
    volatile string _styleStatus = "off - switch Enabled on to fetch";
    volatile string _lastError = "";

    public VectorTileService(string styleUrl) => StyleUrl = styleUrl;

    public string StyleUrl { get; }

    /// <summary>The parsed style, once it has come; null before.</summary>
    public MapStyleDocument? Document => _document;

    /// <summary>Bumped when the style is ready.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Bumped on every tile or resource that arrives.</summary>
    public int Arrivals => Volatile.Read(ref _arrivals);

    /// <summary>The credits the style's sources require, as plain text.</summary>
    public string Attribution { get; private set; } = "";

    public string Status
    {
        get
        {
            var doc = _document;
            if (doc is null) return _styleStatus;
            var skipped = doc.Skipped;
            var byZoom = string.Join(" ", doc.DecodedByZoom.Select(kv => $"z{kv.Key}:{kv.Value}"));
            return $"{doc.Layers.Count} layers drawn, {skipped.Count} parts skipped; tiles: {_inFlight.Count + _queue.Count} loading, {_fetched} fetched, {_fromDisk} from disk, {_failed.Count} failed; decoded {byZoom}"
                + (_lastError.Length > 0 ? " - last error: " + _lastError : "");
        }
    }

    /// <summary>Starts fetching the style; once only.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _styleStatus = "fetching the style";
        Task.Run(LoadStyleAsync);
    }

    async Task LoadStyleAsync()
    {
        try
        {
            var bytes = await GetAsync(StyleUrl).ConfigureAwait(false) ?? throw new IOException($"no style at {StyleUrl}");
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            // every vector source's TileJSON, before the parse that needs them
            var tileJsons = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("sources", out var sources))
                foreach (var s in sources.EnumerateObject())
                    if (s.Value.TryGetProperty("type", out var t) && t.GetString() == "vector" && s.Value.TryGetProperty("url", out var u) && u.GetString() is { } url)
                    {
                        _styleStatus = $"fetching TileJSON {url}";
                        var tj = await GetAsync(Resolve(url)).ConfigureAwait(false);
                        if (tj != null) tileJsons[url] = JsonDocument.Parse(tj).RootElement.Clone();
                    }
            var doc = MapStyleDocument.Parse(root, lenient: true, url => tileJsons.TryGetValue(url, out var e) ? e : null);
            doc.Load = Load;
            doc.Pending = Pending;
            doc.Peek = Peek;
            Attribution = string.Join(" ", doc.Sources.Values.OfType<VectorSource>().Select(v => PlainText(v.Attribution)).Where(a => a.Length > 0).Distinct());
            _document = doc;
            Interlocked.Increment(ref _version);
        }
        catch (Exception e) when (e is not OperationCanceledException || !_cancel.IsCancellationRequested)
        {
            _styleStatus = $"the style failed: {e.Message}";
        }
    }

    /// <summary>A resource's bytes when they are here; otherwise starts fetching it and returns null.</summary>
    public byte[]? Load(string url)
    {
        url = Resolve(url);
        if (_arrived.TryRemove(url, out var bytes)) return bytes;
        if (_failed.ContainsKey(url)) return null;
        if (!IsWeb(url)) return File.Exists(url) ? File.ReadAllBytes(url) : null;
        var file = CacheFile(url);
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CacheAge)
        {
            try { var cached = File.ReadAllBytes(file); Interlocked.Increment(ref _fromDisk); return cached; } catch (IOException) { }
        }
        if (_queued.TryAdd(url, true)) _queue.Enqueue(url);
        Pump();
        return null;
    }

    /// <summary>A resource's bytes if they are already in memory or on disk; never fetches.</summary>
    public byte[]? Peek(string url)
    {
        url = Resolve(url);
        if (_arrived.TryRemove(url, out var bytes)) return bytes;
        if (!IsWeb(url)) return File.Exists(url) ? File.ReadAllBytes(url) : null;
        var file = CacheFile(url);
        if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= CacheAge) return null;
        try { var cached = File.ReadAllBytes(file); Interlocked.Increment(ref _fromDisk); return cached; } catch (IOException) { return null; }
    }

    /// <summary>True while a resource that Load returned null for may still come.</summary>
    public bool Pending(string url)
    {
        url = Resolve(url);
        return IsWeb(url) && !_failed.ContainsKey(url);
    }

    void Pump()
    {
        while (_inFlight.Count < MaxInFlight && _queue.TryDequeue(out var url))
        {
            if (!_inFlight.TryAdd(url, true)) continue;
            Task.Run(async () =>
            {
                try
                {
                    var bytes = await GetAsync(url).ConfigureAwait(false);
                    if (bytes is null) _failed[url] = true;   // 404: an empty tile, not an error
                    else
                    {
                        // the disk first: a second Load that misses _arrived then finds the file
                        try
                        {
                            var file = CacheFile(url);
                            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
                        }
                        catch (IOException) { }
                        _arrived[url] = bytes;
                        Interlocked.Increment(ref _fetched);
                    }
                }
                catch (Exception e) when (!_cancel.IsCancellationRequested)
                {
                    _failed[url] = true;
                    _lastError = e.Message;
                }
                finally
                {
                    _inFlight.TryRemove(url, out _);
                    _queued.TryRemove(url, out _);
                    Interlocked.Increment(ref _arrivals);
                    if (!_cancel.IsCancellationRequested) Pump();
                }
            });
        }
    }

    // bytes, or null for a 404/204; throws for anything else
    async Task<byte[]?> GetAsync(string url)
    {
        if (!IsWeb(url)) return File.Exists(url) ? await File.ReadAllBytesAsync(url, _cancel.Token).ConfigureAwait(false) : null;
        using var response = await Http.GetAsync(url, _cancel.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(_cancel.Token).ConfigureAwait(false);
    }

    // a relative URL in the style is relative to the style's own URL
    string Resolve(string url)
    {
        if (IsWeb(url) || Path.IsPathRooted(url)) return url;
        if (Uri.TryCreate(StyleUrl, UriKind.Absolute, out var b) && b.Scheme is "http" or "https") return new Uri(b, url).ToString();
        var dir = Path.GetDirectoryName(Path.GetFullPath(StyleUrl));
        return dir is null ? url : Path.Combine(dir, url);
    }

    static bool IsWeb(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static string CacheFile(string url)
    {
        var uri = new Uri(url);
        var template = uri.GetLeftPart(UriPartial.Authority);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        return Path.Combine(CacheRoot, TileSourceFactory.Slug(template), hash + ".pbf");
    }

    // "<a href=...>&copy; OpenMapTiles</a>" -> "© OpenMapTiles"
    internal static string PlainText(string html)
        => WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]*>", "")).Trim();

    public void Dispose() => _cancel.Cancel();
}
