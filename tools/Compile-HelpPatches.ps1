<#
.SYNOPSIS
    Compiles every help patch headlessly with vvvvc and checks that this package's nodes RESOLVED.

.DESCRIPTION
    This is the only automated check that says anything about whether a node actually works.
    Test-VLPatch.ps1 proves the XML is well formed; Test-VLPackage.ps1 proves the package could
    contribute nodes. Neither notices that a node failed to resolve.

    AND THE EXIT CODE OF vvvvc DOES NOT NOTICE EITHER. An unresolved node is built with no working
    pins, every link to it is dropped, and vvvvc exits 0 with nothing red. So this script does not
    trust the exit code: it READS the generated C# and asserts that every node the patch takes from
    VL.Globe appears in it - and that every [ProcessNode] is constructed in __Create__, never in
    Update, because built per frame the map node once opened 17,000 connections and took a network
    down. A patch in which nothing resolved looks exactly like a patch in which everything did,
    until the C# is read.

    Carried from vl-nettopologysuite\tools\Compile-HelpPatches.ps1 on 2026-09-24; the previous
    script here (tools\legacy\Compile-HelpPatches.no-csharp-read.ps1) trusted the exit code.

    Carried from vl-geojson\tools\Compile-HelpPatches.ps1, where two things were learned that both
    have to line up:

      1. `--package-repositories` - how vvvv finds a *package folder*, i.e. dist\VL.Globe\.
         Point it at dist\, not dist\feed\, or vvvvc says "Missing package".

      2. a NuGet source carrying the *nupkg* - because vvvvc then generates a .csproj with a
         PackageReference and runs a normal restore. Point that at dist\feed\, or restore says
         "NU1101: package VL.Globe not found". The sibling's CLAUDE.md used to call that NU1101
         "expected, because it is the export stage after codegen". The C# was indeed already
         written, but the export half never ran, so half of what a compile proves was never proved.
         A NuGet.config dropped at the output root fixes it; restore finds it by walking up.

    Requires .\pack.ps1 to have run: dist\feed\*.nupkg is what (2) reads, and pack.ps1 evicts the
    global NuGet cache entry, which is what makes the NU1101 check able to fail at all. Running this
    twice without repacking proves less the second time, because restore is then satisfied from
    %USERPROFILE%\.nuget\packages and never consults a source.

    Pass ABSOLUTE paths for both the document and the output directory: with a relative document
    path vvvvc dies inside PathUtils.GetRelativePath with "UriFormatException: Invalid URI".

    What it still cannot tell you: which CATEGORY a node lands in. LastCategoryFullName in a .vl
    is a hint - negative-tested here by setting it to NTS.Wrong (in the sibling), after which every node still
    resolved. Only the NodeBrowser proves that, and only running the patch proves it computes the
    right value.

.PARAMETER Patch
    Compile only patches whose name matches this wildcard. Default: all of them, and that is the
    gate - "compile every help patch, not the one you edited". Deleting a node once produced
    "Not found" in a file nobody had touched, in a sibling repository.

.PARAMETER KeepOutput
    Leave the generated projects in place so the *.vl.1.cs can be read by hand. The path is
    printed at the end.

.EXAMPLE
    .\pack.ps1 ; .\tools\Compile-HelpPatches.ps1
.EXAMPLE
    .\tools\Compile-HelpPatches.ps1 -Patch "*Buffer*" -KeepOutput
#>
param(
    [string]$OutputDirectory = '',
    [string]$Patch = '*',
    [switch]$KeepOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Dist     = Join-Path $RepoRoot 'dist'
$Deps     = Join-Path $RepoRoot 'deps'
$Feed     = Join-Path $RepoRoot 'dist\feed'

if (-not (Test-Path $Dist)) { throw "dist\ not found. Run .\build.ps1 first." }
if (-not (Get-ChildItem $Feed -Filter '*.nupkg' -ErrorAction SilentlyContinue)) {
    throw "no .nupkg in dist\feed - run .\pack.ps1 first, or restore will fail with NU1101."
}

$vvvvc = & (Join-Path $PSScriptRoot 'Find-Vvvv.ps1') -Compiler
if (-not (Test-Path $vvvvc)) { throw "vvvvc.exe not found at '$vvvvc'" }

# Detect and refuse - never kill. A running vvvv holds the staged assemblies open, and killing it
# corrupts its session state. Same guard as build.ps1.
$running = @(Get-Process 'vvvv' -ErrorAction SilentlyContinue)
if ($running) { throw "vvvv is running (PID $($running.Id -join ', ')). Close it by hand, then run this again." }

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) "vl-mapsui-compile-$PID"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory $OutputDirectory -Force | Out-Null

# The sibling package that MAKES geometry. Help patches here may consume it (this package's nuspec
# depends on it); its dist\feed carries the nupkg restore needs and its dist\ the folder vvvv needs.
$NtsRepo = 'D:\2026_Projects\vl-nettopologysuite'

# (2): restore walks up from the generated .csproj and finds this. nuget.org is listed as well
# because the generated project also references Mapsui and NetTopologySuite themselves, which our
# feed does not carry; on a warm cache it is never consulted, on a cold one it is needed.
$sources = @("    <add key=`"mapsui-dist`" value=`"$Feed`" />")
$ntsFeed = Join-Path $NtsRepo 'dist\feed'
if (Test-Path $ntsFeed) { $sources += "    <add key=`"nts-dist`" value=`"$ntsFeed`" />" }
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
$($sources -join "`n")
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $OutputDirectory 'NuGet.config') -Encoding utf8

# (1): package *folders*, not the feed. THREE of them plus the sibling's deps - omitting one gives
# an error naming something else entirely ("The referenced symbol source 'Mapsui.dll' couldn't be
# found", or 25 ambiguous Point candidates). Same list as tools\Open-HelpPatch.ps1.
$repositories = @($Dist, $Deps)
foreach ($sub in @('dist', 'deps')) { $repositories += Join-Path $NtsRepo $sub }
$repositories = ($repositories | Where-Object { Test-Path $_ }) -join ';'

$patches = @(Get-ChildItem (Join-Path $RepoRoot 'help') -Filter '*.vl' -Recurse -File |
             Where-Object { $_.BaseName -like $Patch } | Sort-Object Name)
if ($patches.Count -eq 0) { throw "No help patch matches -Patch '$Patch'" }

# Every node this package contributes, by the name the patch uses, and the C# a resolved call
# must contain: tools\Nodes.psd1, which Test-VLPatch.ps1's F1 audit reads too.
#
# A node the patch names that is NOT in this table is itself a failure, so that adding a node
# to the package and forgetting it there shows up the first time a patch uses it.
$OurNodes = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Nodes.psd1')).Nodes

# Process nodes must be constructed in __Create__ and only updated in Update. Built per frame, the
# map node opened 17,000 TCP connections in 13 minutes and took a home network down (NOTES.md,
# 2026-08-13) - so for this package the Create check is not hygiene, it is the incident's regression
# test at the patch level. Every [ProcessNode] here, node name -> C# class; derived from $OurNodes
# so the two cannot drift.
$OurProcessNodes = @{}
foreach ($entry in $OurNodes.GetEnumerator()) {
    if ($entry.Value -match '^new\\s\+\[\\w\\\.\]\*(\w+)\\\($') { $OurProcessNodes[$entry.Key] = $Matches[1] }
}
# VL.Globe has no process node yet; the Globe engine will be the first.

# Every node the patch takes from THIS package, by name. Anchored on LastDependency so that a
# "Split" from VL.NetTopologySuite or a "Drag" from elsewhere is not counted against us.
function Get-ClaimedNodes([string]$patchXml) {
    $pattern = 'LastDependency="VL\.Globe\.vl">\s*(?:<[^>]+>\s*)*?<Choice Kind="(?:OperationCallFlag|ProcessAppFlag|ProcessNodeFlag|OperationFlag)" Name="([^"]+)"'
    [regex]::Matches($patchXml, $pattern) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
}

$failures = New-Object System.Collections.Generic.List[string]

foreach ($p in $patches) {
    Write-Host "`ncompiling $($p.Name)" -ForegroundColor Cyan

    $outDir = Join-Path $OutputDirectory ($p.BaseName -replace '[^\w\-]', '_')
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force $outDir | Out-Null
    Copy-Item (Join-Path $OutputDirectory 'NuGet.config') $outDir

    $log = & $vvvvc $p.FullName '--package-repositories' $repositories '--output-directory' $outDir 2>&1 | Out-String

    if ($log -match 'NU1101') {
        $failures.Add("$($p.Name): restore failed with NU1101 - dist\feed is not reaching restore")
        Write-Host "  FAIL  NU1101 - dist\feed is not reaching restore" -ForegroundColor Red
    }

    $generated = @(Get-ChildItem $outDir -Recurse -Filter '*.vl.1.cs' -ErrorAction SilentlyContinue)
    if ($generated.Count -eq 0) {
        $failures.Add("$($p.Name): no C# was generated")
        Write-Host "  FAIL  no C# generated" -ForegroundColor Red
        Write-Host (($log -split "`n" | Select-Object -Last 12) -join "`n") -ForegroundColor DarkGray
        continue
    }

    $csharp   = ($generated | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $patchXml = Get-Content $p.FullName -Raw
    $claimed  = @(Get-ClaimedNodes $patchXml)

    # Rung 3, automated for this package's own types: a LOCAL of a VL.Globe type initialised to
    # default(...) is a link the compiler dropped in silence. On 2026-11-09 Drag's View read
    # `GlobeView View_62 = default(GlobeView)` - an input and an output both named View had sent the
    # link to the output - and every rung but the user's hand was green. Fields set to default in
    # Create/Dispose are `this.__p_...` and are not matched.
    # A pin left unconnected on purpose reads default too (2026-11-22: VectorTiles' help patch, no Tile
    # Source - the user took OpenStreetMap out): it is a dropped link only when the patch links a pin of
    # that name (Tile_Source_24 -> "Tile Source"); with no such link it is reported, not failed. Any
    # pin of the name, input or output: on 2026-11-09 the link had gone to the OUTPUT named View.
    $linked = @{}
    foreach ($l in [regex]::Matches($patchXml, '<Link [^>]*Ids="([^"]+)"')) { foreach ($id in $l.Groups[1].Value -split ',') { $linked[$id] = $true } }
    $dropped = [regex]::Matches($csharp, '(?m)^\s*n\d+\.(Globe\w*)\s+(\w+)\s*=\s*default\(n\d+\.Globe\w*\);')
    foreach ($d in $dropped) {
        $pinName = ($d.Groups[2].Value -replace '_\d+$', '') -replace '_', ' '
        $pinIds = @([regex]::Matches($patchXml, "<Pin Id=""([^""]+)"" Name=""$([regex]::Escape($pinName))""") | ForEach-Object { $_.Groups[1].Value })
        if (-not ($pinIds | Where-Object { $linked.ContainsKey($_) })) {
            Write-Host "  ok    $($d.Groups[2].Value) ($($d.Groups[1].Value)) reads default - no pin '$pinName' is linked in the patch: unconnected on purpose" -ForegroundColor DarkGray
            continue
        }
        # Pins of one name on several nodes (2026-11-25, Pick's help: Geometry's Style left open on purpose,
        # VectorTiles' Style linked): every linked one must reach the node with a value - a local of the name
        # that reads something, or the argument of the name passed something else than such a local
        # (`style: Output_11`). As many of those as pins of the name are linked: this default is an open pin.
        $linkedCount = @($pinIds | Where-Object { $linked.ContainsKey($_) }).Count
        $localName = $d.Groups[2].Value -replace '_\d+$', ''
        $parts = $localName -split '_'
        $argName = $parts[0].Substring(0, 1).ToLowerInvariant() + $parts[0].Substring(1) + (($parts | Select-Object -Skip 1) -join '')
        $locals = [regex]::Matches($csharp, "(?m)^\s*[\w\.<>]+\s+$([regex]::Escape($localName))_\d+\s*=\s*(?=\S)(?!default\()").Count
        $args_ = [regex]::Matches($csharp, "\b$([regex]::Escape($argName)):\s*(?!$([regex]::Escape($localName))_\d+\b)(?!default\()\w").Count
        $reading = $locals + $args_
        if ($reading -ge $linkedCount) {
            Write-Host "  ok    $($d.Groups[2].Value) ($($d.Groups[1].Value)) reads default - the $linkedCount linked pin(s) '$pinName' each read a value: this one is open on purpose" -ForegroundColor DarkGray
            continue
        }
        $failures.Add("$($p.Name): $($d.Groups[2].Value) of type $($d.Groups[1].Value) reads default - a link to it was dropped")
        Write-Host "  FAIL  $($d.Groups[2].Value) ($($d.Groups[1].Value)) reads default(...) - a dropped link" -ForegroundColor Red
    }

    if ($claimed.Count -eq 0) {
        Write-Host "  ok    compiled (uses no VL.Globe node)" -ForegroundColor DarkGray
        continue
    }

    foreach ($nodeName in $claimed) {
        if (-not $OurNodes.ContainsKey($nodeName)) {
            $failures.Add("$($p.Name): '$nodeName' is taken from VL.Globe but is not in this script's node table - add it")
            Write-Host "  FAIL  '$nodeName' is not in the node table" -ForegroundColor Red
            continue
        }
        if ($csharp -notmatch $OurNodes[$nodeName]) {
            $failures.Add("$($p.Name): '$nodeName' is in the patch but NOT in the generated C# - it did not resolve")
            Write-Host "  FAIL  '$nodeName' did not resolve" -ForegroundColor Red
        }
        else {
            Write-Host "  ok    '$nodeName' resolved" -ForegroundColor DarkGray
        }

        if ($OurProcessNodes.ContainsKey($nodeName)) {
            $class = $OurProcessNodes[$nodeName]
            # The FIRST "__Create__" in the file is a CALL, not the definition, and the parameter
            # list has its own parentheses - hence the lazy `.*?\)\{`. Carried from vl-geojson.
            $createBody = [regex]::Match(
                $csharp, 'public\s+[\w\.]+\s+__Create__\(.*?\)\{(?<body>.*?)\n        \}', 'Singleline')
            if ($createBody.Success -and $createBody.Groups['body'].Value -match "new\s+[\w\.]*$class\(") {
                Write-Host "  ok    '$nodeName' is constructed in Create, not Update" -ForegroundColor DarkGray
            }
            # Inside a ForEach a process node is built per slice by the region - only when its iterator has
            # no state for the slice yet (`if (!iterator.MoveNext(out state)) { ... new Node() ... }`) - then
            # updated. Read in the generated C# of Stack tile layers' Group (Spectral) cell, 2026-11-24.
            elseif ($csharp -match "if\s*\(!iterator_\d+\.MoveNext\(out\s+state_\d+\)\)\s*\{[^{}]*new\s+[\w\.]*$class\(") {
                Write-Host "  ok    '$nodeName' is constructed per slice of its region, not every frame" -ForegroundColor DarkGray
            }
            else {
                $failures.Add("$($p.Name): $class is not constructed in __Create__ - it would be rebuilt every frame")
                Write-Host "  FAIL  '$nodeName' is not constructed in Create" -ForegroundColor Red
            }
        }
    }
}

# Inside the package's own patched definitions (GlobeWindow): every help patch's output carries the
# compiled VL.Globe.vl.dll, with no source - decompiled and its links counted by Test-PatchedDefinitions.ps1.
# A check that could not run is a failure, not a skip.
$document = Join-Path $RepoRoot 'VL.Globe.vl'
$compiled = Get-ChildItem $OutputDirectory -Recurse -Filter 'VL.Globe.vl.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
Write-Host "`nchecking the patched definitions of VL.Globe.vl" -ForegroundColor Cyan
if (-not $compiled) {
    $failures.Add("VL.Globe.vl: no compiled VL.Globe.vl.dll in the output - its patched definitions were not checked")
    Write-Host "  FAIL  no VL.Globe.vl.dll in the output" -ForegroundColor Red
}
else {
    $problems = @(& (Join-Path $PSScriptRoot 'Test-PatchedDefinitions.ps1') -Dll $compiled.FullName -Document $document)
    if ($problems.Count -eq 0) {
        Write-Host "  ok    every link inside its definitions passes a value in the compiled code" -ForegroundColor DarkGray
    }
    else {
        $failures.Add("VL.Globe.vl: $($problems[0])")
        Write-Host "  FAIL  $($problems[0])" -ForegroundColor Red
        $problems | Select-Object -Skip 1 | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
    }
}

if ($KeepOutput) {
    Write-Host "`ngenerated projects kept at $OutputDirectory" -ForegroundColor DarkGray
}
elseif (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}

Write-Host ""
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    Write-Host "`nFAIL - $($failures.Count) problem(s)." -ForegroundColor Red
    exit 1
}

Write-Host "PASS - $($patches.Count) patch(es) compiled, every VL.Globe node resolved, every process node built in Create, no NU1101." -ForegroundColor Green
Write-Host @"
Note: this proves the nodes RESOLVE. It does not prove which CATEGORY they appear under - only
the NodeBrowser does - nor that the patch computes the right value, which only running it does.
"@ -ForegroundColor Yellow

# Explicitly, because vvvvc's own exit code may still be sitting in $LASTEXITCODE.
exit 0
