<#
.SYNOPSIS
    Finds links the compiler dropped in silence INSIDE a patched definition (GlobeWindow).

.DESCRIPTION
    The package's own .vl compiles to <Package>.vl.dll with no source kept, so the help-patch check
    cannot read it. Twice a link inside GlobeWindow was cut and every rung stayed green:
      2026-11-20  Client Bounds typed Stride's Rectangle: `int value2 = 0; ... SetValue(value2)`
      2026-11-25  Globe's Background made Color4?:       `Color4? background = null`
    A cut link reads a constant where the link's value should be. So: decompile the assembly, and
    for every definition count
      - the links drawn in the document (source: a pin), and
      - the places in the compiled class that take a value which is not a constant: arguments of
        calls (a pin group's `builder.Add` among them) and assignments to the definition's outputs.
    Every link is one such place. Fewer places than links: a link was dropped.
    More places than links is reported too - the count no longer describes the code and this script
    must be taught the new shape before it can be trusted again.

.PARAMETER Dll
    The compiled document, e.g. <vvvvc output>\<patch>\VL.Globe.vl.dll.
.PARAMETER Document
    The .vl it was compiled from.
.PARAMETER Source
    Already decompiled C# (skips ilspycmd). For testing this script against a mutated decompile.
.OUTPUTS
    One string per problem; nothing when every definition's links are all in the compiled code.
#>
param(
    [string]$Dll = '',
    [Parameter(Mandatory)][string]$Document,
    [string]$Source = ''
)

$ErrorActionPreference = 'Stop'

if ($Source) { $csharp = Get-Content $Source -Raw }
else {
    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        "ilspycmd is not installed (dotnet tool install -g ilspycmd): the patched definitions were NOT checked"
        return
    }
    if (-not (Test-Path $Dll)) { "$Dll not found: the patched definitions were NOT checked"; return }
    $csharp = (& ilspycmd $Dll) -join "`n"
}
$csharp = $csharp -replace "`r", ''

# A constant: what an unlinked pin reads. `__v_<id>` is a pin's stored default value.
$literal = '^(?:-?\d[\w\.]*|true|false|null|"(?:[^"\\]|\\.)*"|default(?:\(.*\))?|__v_\w+)$'
# Made by the compiler, not by a link: node contexts, channels of unlinked pins, default collections.
$generated = 'CreateSubContext|CompilationHelper\.|CreateDefault|GetBuilder\(|\.Commit\(\)'

function Split-Arguments([string]$list) {
    $out = New-Object System.Collections.Generic.List[string]
    $depth = 0; $start = 0; $inString = $false
    for ($i = 0; $i -lt $list.Length; $i++) {
        $c = $list[$i]
        if ($c -eq '"' -and ($i -eq 0 -or $list[$i - 1] -ne '\')) { $inString = -not $inString }
        if ($inString) { continue }
        if ($c -in '(', '<', '[') { $depth++ }
        elseif ($c -in ')', '>', ']') { $depth-- }
        elseif ($c -eq ',' -and $depth -eq 0) { $out.Add($list.Substring($start, $i - $start).Trim()); $start = $i + 1 }
    }
    $last = $list.Substring($start).Trim()
    if ($last) { $out.Add($last) }
    , $out
}

$xml = [xml](Get-Content $Document -Raw)
$definitions = $xml.SelectNodes("//*[local-name()='Node'][@Name][*[local-name()='Patch']//*[local-name()='ProcessDefinition']]")

foreach ($definition in $definitions) {
    $name = $definition.Name
    $pinIds = @{}
    foreach ($pin in $definition.SelectNodes(".//*[local-name()='Pin']")) { $pinIds[$pin.Id] = $pin.Name }
    $links = @($definition.SelectNodes(".//*[local-name()='Link']") | Where-Object { $pinIds.ContainsKey(($_.Ids -split ',')[0]) })
    if ($links.Count -eq 0) { continue }

    $class = [regex]::Match($csharp, "(?ms)^\tpublic sealed class $([regex]::Escape($name))_P\b.*?^\t\}")
    if (-not $class.Success) { "${name}: class ${name}_P is not in the compiled document"; continue }

    $places = New-Object System.Collections.Generic.List[string]
    foreach ($method in [regex]::Matches($class.Value, '(?ms)^\t\tpublic [^\n]*?\b(Update|__Create__)\([^\n]*\)\n\t\t\{\n(.*?)^\t\t\}')) {
        $constants = @{}
        foreach ($line in $method.Groups[2].Value -split "`n") {
            $line = $line.Trim()
            # the tail: the node's own state written back
            if ($line -match "^${name}_P \w+ = this;$" -or $line -eq 'return this;') { break }
            if ($line -notmatch ';$' -or $line -match '^__' -or $line -match '^RuntimeHelpers\.') { continue }

            # `T local = constant;` and `T local = <compiler-made>;`
            $local = [regex]::Match($line, '^[\w\.<>\?\[\], ]+?\s+(\w+)\s*=\s*(.+);$')
            if ($local.Success -and ($local.Groups[2].Value -match $literal -or
                                     ($local.Groups[2].Value -match $generated -and $local.Groups[2].Value -notmatch '\.Update\('))) {
                $constants[$local.Groups[1].Value] = $true
                continue
            }
            # `Name_Out = value;` - a link to one of the definition's outputs
            $output = [regex]::Match($line, '^(\w+_Out)\s*=\s*(.+);$')
            if ($output.Success) {
                $value = $output.Groups[2].Value
                if ($value -notmatch $literal -and -not $constants.ContainsKey($value)) { $places.Add("$($output.Groups[1].Value) = $value") }
                continue
            }
            # the outermost call of the line, its arguments
            $open = $line.IndexOf('(')
            if ($open -lt 0 -or $line -match $generated -and $line -notmatch '\.(Update|Create)\(|^\w[\w\.]* = new ') { continue }
            $callee = [regex]::Match($line.Substring(0, $open), '[\w\.]+$').Value
            $close = $line.LastIndexOf(')')
            foreach ($argument in (Split-Arguments $line.Substring($open + 1, $close - $open - 1))) {
                if ($argument -match '^out\s' -or $argument -match $literal -or $argument -match $generated) { continue }
                if ($constants.ContainsKey($argument)) { continue }
                $places.Add("$callee( $argument )")
            }
        }
    }

    if ($places.Count -eq $links.Count) { continue }
    $what = if ($places.Count -lt $links.Count) { 'a link was dropped' } else { 'the compiled code has a shape this check does not know' }
    "${name}: $($links.Count) links in the document, $($places.Count) values passed in ${name}_P - $what"
    foreach ($place in $places) { "    passes  $place" }
    foreach ($link in $links) {
        $ids = $link.Ids -split ','
        "    link    $($pinIds[$ids[0]]) -> $($pinIds[$ids[1]])"
    }
}
