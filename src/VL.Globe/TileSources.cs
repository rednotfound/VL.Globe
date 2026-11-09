using System;
using System.Collections.Generic;
using System.IO;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;
using VL.Core.Import;

namespace VL.Globe;

// The /// text is the tooltip. Reasoning lives in these comments.
//
// A tile source is a value the Globe node consumes, made by a node whose job it is - the same
// split VL.Mapsui has (OpenStreetMap / XYZ produce, Map consumes), so consent and attribution live
// in a node a reader can see. The pins agree with VL.Mapsui's nodes of the same names, by the
// decision recorded in vl-overworld's GLOBE-SCOPE-PROPOSAL.md (section 6): duplicated, not shared,
// so VL.Globe does not depend on the 2D library.
//
// The disk cache follows VL.Mapsui's folder convention exactly - %LOCALAPPDATA%\VL.Mapsui\tiles\
// <host>-<first 8 hex of sha256(lowercased key)>, BruTile FileCache, png, 7 days - so a tile either
// package fetched serves both without a second request. OSM's key is the one VL.Mapsui uses.

/// <summary>Where a globe's tiles come from: an XYZ server, its credit, and its disk cache.</summary>
public sealed class GlobeTileSource
{
    internal GlobeTileSource(HttpTileSource http, string attribution, string cacheFolder)
    {
        Http = http;
        Attribution = attribution;
        CacheFolder = cacheFolder;
    }

    internal HttpTileSource Http { get; }

    /// <summary>The credit the tile server requires wherever its tiles are shown.</summary>
    public string Attribution { get; }

    /// <summary>The folder tiles are kept in, shared with VL.Mapsui.</summary>
    public string CacheFolder { get; }

    public override string ToString() => Attribution;
}

internal static class TileSourceFactory
{
    // OSM's tile policy requires a User-Agent naming the application.
    internal const string UserAgent = "VL.Globe/0.0.1-alpha (vvvv gamma; VL.NetTopologySuite family)";

    internal static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VL.Mapsui", "tiles");

    internal static string Slug(string sourceKey)
    {
        var key = (sourceKey ?? string.Empty).Trim();
        var host = Uri.TryCreate(key, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : "source";
        foreach (var c in Path.GetInvalidFileNameChars()) host = host.Replace(c, '-');
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return $"{host}-{Convert.ToHexString(digest)[..8].ToLowerInvariant()}";
    }

    internal static GlobeTileSource Create(string url, string attribution, string cacheKey)
    {
        var folder = Path.Combine(CacheRoot, Slug(cacheKey));
        var http = new HttpTileSource(new GlobalSphericalMercator(), url,
            name: attribution, persistentCache: new FileCache(folder, "png", TimeSpan.FromDays(7)),
            attribution: new BruTile.Attribution(attribution), userAgent: UserAgent);
        return new GlobeTileSource(http, attribution, folder);
    }
}

// A process node, not a static method: an HttpTileSource owns an HttpClient and BruTile's is not
// disposable, so one built per frame would leak a connection pool per frame (VL.Mapsui's lesson).

/// <summary>OpenStreetMap's standard tiles, for a Globe.</summary>
/// <remarks>Fetching is the Globe node's, and off until its Enabled is on. Show the credit.</remarks>
[ProcessNode(Name = "OpenStreetMap", Category = "Globe")]
public sealed class OpenStreetMapNode
{
    readonly GlobeTileSource _source = TileSourceFactory.Create(
        "https://tile.openstreetmap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors",
        "https://tile.openstreetmap.org/standard");

    /// <returns>The tile source, for a Globe's Tile Source pin.</returns>
    [return: Pin(Name = "Tile Source")]
    public GlobeTileSource Update() => _source;
}

/// <summary>Any XYZ tile server, for a Globe.</summary>
/// <remarks>Url has {z}, {x} and {y}. Attribution is what the server requires you to show.</remarks>
[ProcessNode(Name = "XyzTiles", Category = "Globe")]
public sealed class XyzTilesNode
{
    readonly Dictionary<string, GlobeTileSource> _sources = new(StringComparer.Ordinal);

    /// <param name="url">A tile URL template with {z}, {x} and {y}.</param>
    /// <param name="attribution">The credit the server requires.</param>
    [return: Pin(Name = "Tile Source")]
    public GlobeTileSource? Update(string url = "", string attribution = "")
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains("{z}") || !url.Contains("{x}") || !url.Contains("{y}")) return null;
        var key = url + "\n" + attribution;
        if (!_sources.TryGetValue(key, out var s))
            _sources[key] = s = TileSourceFactory.Create(url, attribution, url);
        return s;
    }
}
