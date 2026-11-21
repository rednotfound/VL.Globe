<#
.SYNOPSIS
    Fetches a pinned subset of MapLibre GL JS's render tests into test\fixtures\maplibre (git-ignored).

.DESCRIPTION
    ROADMAP 0.1 item 4b. MapLibre's test/integration/render/tests holds one folder per fixture with a
    style.json and one or more expected*.png. This script downloads the folders named in -Groups at
    the tag in -Tag, so every run of the harness compares against the same images. The fixtures are
    BSD-3-Clause (MapLibre); the licence is fetched next to them.

    Two calls go to api.github.com (unauthenticated: 60 an hour), the files come from
    raw.githubusercontent.com. Files already present are not fetched again; -Force re-fetches.

.EXAMPLE
    .\tools\Fetch-MapLibreFixtures.ps1
#>
[CmdletBinding()]
param(
    # The MapLibre GL JS tag the fixtures and the harness's thresholds are taken from.
    [string]$Tag = 'v6.19.0',
    # Fixture groups (top-level folders under test/integration/render/tests).
    [string[]]$Groups = @(
        'background-color', 'background-opacity',
        'fill-antialias', 'fill-color', 'fill-opacity', 'fill-outline-color',
        'line-cap', 'line-color', 'line-join', 'line-opacity', 'line-width',
        # 4c: filters, geometry types, zoom ranges, tile schemes, overscaling
        'extent', 'fill-outside-tile', 'filter', 'geojson', 'geometry-type', 'line-miter-limit',
        'line-overlap', 'line-round-limit', 'line-triangulation', 'overscale', 'tms', 'visibility',
        'zoom-visibility', 'zoomed-fill',
        # 4e, labels, milestone 1: point labels - glyphs, shaping, size, colour, halo, anchor, offset
        'text-field', 'text-font', 'text-size', 'text-color', 'text-opacity', 'text-halo-color',
        'text-halo-width', 'text-halo-blur', 'text-anchor', 'text-offset', 'text-justify',
        'text-max-width', 'text-letter-spacing', 'text-transform', 'text-rotate'),
    # Files under test/integration/assets that the fixtures load as local://<path> (the Berlin
    # tiles of the line-* vector fixtures). Saved under <tag>\_assets.
    [string[]]$Assets = @(
        'tiles/14-8802-5374.mvt', 'tiles/14-8802-5375.mvt', 'tiles/14-8803-5374.mvt', 'tiles/14-8803-5375.mvt',
        'tiles/extent1024-14-8802-5374.mvt', 'tiles/0-0-0.mvt', 'tiles/extent65536-0-0-0.mvt',
        'tiles/outside_ring.mvt', 'tiles/2-1-1.tms.mvt', 'tiles/2-1-2.tms.mvt', 'tiles/2-2-1.tms.mvt', 'tiles/2-2-2.tms.mvt',
        'data/feature.geojson', 'data/invalid.geojson', 'data/malformed.geojson', 'data/places.geojson',
        'data/winding.geojson',
        # the labels' SDF glyphs (local://glyphs/{fontstack}/{range}.pbf)
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/0-255.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/256-511.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/1536-1791.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/5888-6143.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/8192-8447.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/12288-12543.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/64256-64511.pbf',
        'glyphs/Open Sans Semibold,Arial Unicode MS Bold/65024-65279.pbf',
        # the glyph metrics MapLibre's symbol-shaping tests shape with (test/integration/symbol-shaping)
        'glyphs/fontstack-glyphs.json'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\test\fixtures\maplibre'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repo = 'maplibre/maplibre-gl-js'
$root = 'test/integration/render/tests'
$headers = @{ 'User-Agent' = 'VL.Globe-fixture-fetch'; 'Accept' = 'application/vnd.github+json' }

$dest = Join-Path ([IO.Path]::GetFullPath($Destination)) $Tag
New-Item -ItemType Directory -Force $dest | Out-Null

# the tree SHA of the tests folder, then that subtree recursively (the whole repository's tree is
# too large for one response and comes back truncated)
$parent = Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$repo/contents/test/integration/render?ref=$Tag"
$testsSha = ($parent | Where-Object { $_.name -eq 'tests' }).sha
if (-not $testsSha) { throw "No $root at $Tag" }
$tree = Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$repo/git/trees/$testsSha`?recursive=1"
if ($tree.truncated) { throw "The tree of $root came back truncated; fetch fewer groups per run" }

$files = $tree.tree | Where-Object {
    $_.type -eq 'blob' -and ($Groups -contains ($_.path -split '/')[0]) -and
    ($_.path -match '/style\.json$' -or $_.path -match '/expected[^/]*\.png$')
}
$missing = $Groups | Where-Object { $g = $_; -not ($files | Where-Object { $_.path -like "$g/*" }) }
if ($missing) { throw "No fixtures under: $($missing -join ', ')" }

$fetched = 0
foreach ($f in $files) {
    $target = Join-Path $dest ($f.path -replace '/', '\')
    if ((Test-Path $target) -and -not $Force) { continue }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = $headers['User-Agent'] } `
        "https://raw.githubusercontent.com/$repo/$Tag/$root/$($f.path)" -OutFile $target
    $fetched++
}
foreach ($a in $Assets) {
    $target = Join-Path $dest ('_assets\' + ($a -replace '/', '\'))
    if ((Test-Path $target) -and -not $Force) { continue }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    # each segment escaped: font stacks are named with spaces and commas
    $escaped = (($a -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = $headers['User-Agent'] } `
        "https://raw.githubusercontent.com/$repo/$Tag/test/integration/assets/$escaped" -OutFile $target
    $fetched++
}
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/$repo/$Tag/LICENSE.txt" `
    -OutFile (Join-Path $dest 'LICENSE.txt')

$count = ($files | Where-Object { $_.path -match '/style\.json$' }).Count
Write-Host "$count fixtures in $($Groups.Count) groups and $($Assets.Count) assets at $Tag -> $dest ($fetched files fetched)"
