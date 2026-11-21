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
        'line-cap', 'line-color', 'line-join', 'line-opacity', 'line-width'),
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
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/$repo/$Tag/LICENSE.txt" `
    -OutFile (Join-Path $dest 'LICENSE.txt')

$count = ($files | Where-Object { $_.path -match '/style\.json$' }).Count
Write-Host "$count fixtures in $($Groups.Count) groups at $Tag -> $dest ($fetched files fetched)"
