using System;
using System.IO;
using VL.Core.Import;
using VLPath = VL.Lib.IO.Path;

namespace VL.Globe;

// A style is a value made by its own node, as a texture is made by FileTexture: VectorTiles draws it,
// and nodes that change a style (one day) take one and give one. Reading again makes a new value, and
// VectorTiles starts over on a new value - VL's change detection is object identity. Nothing here
// fetches: VectorTiles reads the style while its Enabled is on.

/// <summary>A MapLibre style, from MapLibreStyle (URL) or MapLibreStyle (File), for VectorTiles.</summary>
public sealed class MapLibreStyle
{
    internal MapLibreStyle(string source) => Source = source;

    /// <summary>The style's URL, or the full path of its file.</summary>
    internal string Source { get; }

    public override string ToString() => Source;
}

/// <summary>A MapLibre style on the web, for VectorTiles.</summary>
/// <remarks>VectorTiles fetches it while its Enabled is on.</remarks>
[ProcessNode(Name = "MapLibreStyle (URL)", Category = "Globe.Styles")]
public sealed class MapLibreStyleUrlNode
{
    MapLibreStyle? _style;

    /// <param name="url">The style's address; OpenFreeMap's Liberty by default.</param>
    [return: Pin(Name = "Output")]
    public MapLibreStyle? Update([Pin(Name = "URL")] string url = "https://tiles.openfreemap.org/styles/liberty")
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return _style = null;
        if (_style?.Source != url) _style = new MapLibreStyle(url);
        return _style;
    }
}

/// <summary>A MapLibre style in a .json file, for VectorTiles.</summary>
/// <remarks>To edit a style, open the file in Maputnik (maplibre.org/maputnik), save it there, then press Read.</remarks>
[ProcessNode(Name = "MapLibreStyle (File)", Category = "Globe.Styles")]
public sealed class MapLibreStyleFileNode
{
    MapLibreStyle? _style;
    bool _lastRead;

    // Read as vvvv's FileReader and VL.OpenStreetMap's ReadOSM name it, on its rising edge only
    /// <param name="path">A style .json file.</param>
    /// <param name="read">Reads the file again, as after it was edited and saved.</param>
    [return: Pin(Name = "Output")]
    public MapLibreStyle? Update(VLPath? path = null, bool read = false)
    {
        var p = path?.Value ?? "";
        bool pressed = read && !_lastRead;
        _lastRead = read;
        // an empty Path IOBox gives the document's folder: no file, no style
        if (p.Length == 0 || (pressed || _style?.Source != p) && !File.Exists(p)) return _style = null;
        if (pressed || _style?.Source != p) _style = new MapLibreStyle(p);
        return _style;
    }
}
