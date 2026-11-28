<#
.SYNOPSIS
  Draws one view of the globe offscreen - no vvvv - with Liberty's vector tiles, as Probes\ViewProbe
  does, and keeps the picture. With -Baseline it draws the view twice, with two sets of switches, and
  counts the pixels that differ.

.DESCRIPTION
  For iterating on what the GPU path draws: a round is a build and a few seconds, where opening vvvv
  is a build, a pack and a minute. Go to vvvv (..\vvvv-dev\tools\Shoot-Patch.ps1) once it looks right:
  the probe has no scene, no window and no post chain of vvvv's.

  The view is MapLibre's hash, as the camera nodes log it: #zoom/lat/lng/bearing/pitch.
  Switches are environment variables read by the engine (VLGLOBE_HALO, VLGLOBE_MSAA, ...).

.EXAMPLE
  .\tools\Probe-View.ps1 -View '#18.6/35.7275/139.7672/0/50' -Sun 2026-11-24T01:00Z -Out C:\scratch\probe
  .\tools\Probe-View.ps1 -View '#18.6/35.7275/139.7672/0/50' -Set @{ VLGLOBE_HALO = '3' } -Baseline @{ VLGLOBE_HALO = '0' } -Out C:\scratch\probe
#>
param(
    [Parameter(Mandatory)][string]$View,
    [Parameter(Mandatory)][string]$Out,
    # SunLight at this moment (UTC), shadows on; omit for GL JS's default light
    [string]$Sun,
    [string]$Style,
    [hashtable]$Set = @{},
    # a second drawing with these switches instead of -Set's, compared with the first
    [hashtable]$Baseline,
    [string]$Name = 'view',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'test\VL.Globe.Tests\VL.Globe.Tests.csproj'
$compare = Join-Path (Split-Path $repo -Parent) 'vvvv-dev\tools\Compare-Captures.ps1'
if (Get-Process vvvv -ErrorAction SilentlyContinue) { throw 'a vvvv is running: it holds the build''s files - close it first (never one that is not yours)' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

function Invoke-Probe([hashtable]$switches, [string]$name, [bool]$build) {
    $all = @{ VLGLOBE_PROBE = '1'; VLGLOBE_VIEW = $View }
    if ($Sun) { $all.VLGLOBE_SUN = $Sun }
    if ($Style) { $all.VLGLOBE_STYLE = $Style }
    foreach ($k in $switches.Keys) { $all[$k] = [string]$switches[$k] }
    $before = @{}
    foreach ($k in $all.Keys) { $before[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $all[$k]) }
    $bmp = Join-Path ([IO.Path]::GetTempPath()) 'vlglobe-view.bmp'
    $started = Get-Date
    try {
        $arguments = @('test', $project, '--filter', 'FullyQualifiedName~ViewProbe', '--logger', 'console;verbosity=detailed')
        if (-not $build) { $arguments += '--no-build' }
        $text = & dotnet @arguments 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { $text | Select-Object -Last 30 | Out-Host; throw "the probe failed ($LASTEXITCODE)" }
    } finally {
        foreach ($k in $all.Keys) { [Environment]::SetEnvironmentVariable($k, $before[$k]) }
    }
    if (-not (Test-Path $bmp) -or (Get-Item $bmp).LastWriteTime -lt $started) { $text | Select-Object -Last 30 | Out-Host; throw 'the probe wrote no picture (is it still skipped?)' }
    $file = Join-Path $Out "$name.bmp"
    Copy-Item $bmp $file -Force
    $status = $text | Where-Object { $_ -match 'tiles built|eye \d+ m up|light:' } | ForEach-Object { $_.Trim() }
    "$name ($((($switches.Keys | ForEach-Object { "$_=$($switches[$_])" }) -join ' '))): $([int]((Get-Date) - $started).TotalSeconds) s -> $file"
    $status | ForEach-Object { "  $_" }
    $file
}

$first = Invoke-Probe $Set $Name (-not $NoBuild)
$first | Select-Object -SkipLast 1
if ($Baseline) {
    $second = Invoke-Probe $Baseline "$Name-baseline" $false
    $second | Select-Object -SkipLast 1
    & $compare -A ($second | Select-Object -Last 1) -B ($first | Select-Object -Last 1) -Out (Join-Path $Out "$Name-compared.png")
}
