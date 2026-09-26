<#
.SYNOPSIS
Guards demo/expected-findings.json: that what is written is consistent with the cases on disk, and that the entries of the
finished phases are untouched.

.DESCRIPTION
Without arguments two checks run.

Consistency: for every entry with phase 5b the case name is the part of `id` before `/`, and that name must have a file
<Pascal>.cs somewhere under demo/**/Cases/ whose namespace ends with the same <Pascal>, the kebab name joined in PascalCase.

The past: the committed file, read with `git show HEAD:plugins/concurrency-hunter/demo/expected-findings.json`, must still
hold the same entries for the finished phases 0, 1a, 1b, 2, 2b, 3, 4, 4b and 5a, entry by entry, by id, rule, resource, accesses,
confidence and phase. Formatting and property order are not compared; the two accesses of an entry are compared as the
unordered pair they are (SPEC 12.2).

With -Complete a third check runs, completeness: the case names of the phase 5b table of demo/SCENARIOS.md must each have a
file and an entry. Completeness is only reachable at the end of the phase, which is why it is a switch and not the default.

Registration in Program.cs is not checked: cases that live on a controller have none, MVC finds them, and checking would
fail on them.

.PARAMETER Complete
Also require every case of the phase 5b catalog to have a file and an entry.
#>
param(
    [switch]$Complete
)

$ErrorActionPreference = 'Stop'

# This script reads a Cyrillic heading out of the catalog, so it is decoded correctly or it is not run at all. Windows
# PowerShell 5.1 reads a file with no byte-order mark in the system's ANSI code page, which turns every non-ASCII literal here
# into mojibake and the phase table into nothing at all; pwsh 7 reads it as UTF-8 and the same file works. The mark is what
# makes both shells agree, and this check is on the literal itself, so a mark some tool strips is reported as the encoding
# fault it is rather than as an empty table.
$PHASE_WORD = 'Фаза'
if ([int][char]$PHASE_WORD[0] -ne 0x424) {
    Write-Output "build/check-demo-expectations.ps1 was decoded as $([Text.Encoding]::Default.WebName), not UTF-8, so its headings are mojibake."
    Write-Output "  Restore the UTF-8 byte-order mark on this file, or run it under pwsh 7."
    exit 1
}

$PHASE = '5b'
$FINISHED_PHASES = @('0', '1a', '1b', '2', '2b', '3', '4', '4b', '5a')
$COMMITTED = 'HEAD:plugins/concurrency-hunter/demo/expected-findings.json'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$expectationsPath = Join-Path $root 'demo/expected-findings.json'
$scenariosPath = Join-Path $root 'demo/SCENARIOS.md'
$problems = [Collections.Generic.List[string]]::new()

function Get-PascalName([string]$Kebab) {
    ($Kebab.Split('-') | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) -join ''
}

function Get-Entries($File) {
    @(@($File.findings) + @($File.notDefects) | Where-Object { $null -ne $_ })
}

# Everything the immutability check compares, as one string: the property order of the JSON does not reach it.
function Get-Signature($Entry) {
    $accesses = if ($Entry.PSObject.Properties['accesses'] -and $null -ne $Entry.accesses) {
        (@($Entry.accesses) | ForEach-Object { "$($_.symbol)#$($_.operation)" } | Sort-Object) -join ';'
    }
    else { '<none>' }
    $properties = @(
        "id=$($Entry.id)"
        "phase=$($Entry.phase)"
        "rule=$($Entry.rule)"
        "confidence=$($Entry.confidence)"
        "region=$($Entry.resource.region)"
        "accessPath=$(@($Entry.resource.accessPath) -join '|')"
        "accesses=$accesses"
    )
    $properties -join ' '
}

$expectations = Get-Content $expectationsPath -Raw | ConvertFrom-Json
$entries = Get-Entries $expectations

# The case files, by their PascalCase name.
$caseFiles = @{}
foreach ($file in Get-ChildItem (Join-Path $root 'demo') -Recurse -Filter '*.cs' -File) {
    if ($file.FullName -notmatch '[\\/]Cases[\\/]') { continue }
    $caseFiles[[IO.Path]::GetFileNameWithoutExtension($file.Name)] = $file.FullName
}

# 1. Consistency: every phase 5b entry has its case file, and that file carries the matching namespace.
$cases = @($entries | Where-Object { $_.phase -eq $PHASE } | ForEach-Object { $_.id.Split('/')[0] } | Sort-Object -Unique)
foreach ($case in $cases) {
    $pascal = Get-PascalName $case
    $path = $caseFiles[$pascal]
    if (-not $path) {
        $problems.Add("$case has entries but no demo/**/Cases/$pascal.cs")
        continue
    }

    $source = Get-Content $path -Raw
    $match = [regex]::Match($source, '(?m)^namespace\s+(?<name>[\w.]+)\s*[;{]')
    if (-not $match.Success) {
        $problems.Add("$pascal.cs declares no namespace")
    }
    elseif (-not $match.Groups['name'].Value.EndsWith(".$pascal", [StringComparison]::Ordinal)) {
        $problems.Add("$pascal.cs is in namespace $($match.Groups['name'].Value), which does not end with $pascal")
    }
}

# 2. The past: the entries of the finished phases are the committed ones. The committed file carries a UTF-8 byte-order mark
# since phase 4. A process's output is decoded in the console's code page, which is IBM437 in one host and UTF-8 in another,
# and in the first the mark arrives as three characters no parser accepts; so the output is read as UTF-8, whichever shell
# runs this, and the mark it then is is dropped before parsing.
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    $committedText = ((& git -C $root show $COMMITTED) -join "`n").TrimStart([char]0xFEFF)
    $gitExit = $LASTEXITCODE
}
finally {
    [Console]::OutputEncoding = $previousEncoding
}

if ($gitExit -ne 0) {
    Write-Error "git show $COMMITTED failed with exit code $gitExit."
    exit 1
}

$frozen = @{}
foreach ($entry in Get-Entries ($committedText | ConvertFrom-Json) | Where-Object { $FINISHED_PHASES -contains $_.phase }) {
    $frozen[$entry.id] = Get-Signature $entry
}

$present = @{}
foreach ($entry in $entries | Where-Object { $FINISHED_PHASES -contains $_.phase }) {
    $present[$entry.id] = Get-Signature $entry
}

foreach ($id in $frozen.Keys | Sort-Object) {
    if (-not $present.ContainsKey($id)) {
        $problems.Add("$id is a committed entry of a finished phase and is gone")
    }
    elseif ($present[$id] -ne $frozen[$id]) {
        $problems.Add("$id changed since HEAD:`n    was: $($frozen[$id])`n    now: $($present[$id])")
    }
}
foreach ($id in $present.Keys | Sort-Object) {
    if (-not $frozen.ContainsKey($id)) {
        $problems.Add("$id is a new entry of a finished phase; a finished phase does not gain entries")
    }
}

# 3. Completeness, on request: every case of the phase 5b catalog has a file and an entry.
$catalog = @()
if ($Complete) {
    $inTable = $false
    foreach ($line in Get-Content $scenariosPath) {
        if ($line -match '^##\s') { $inTable = $line -match "^##\s+$PHASE_WORD $PHASE\b" }
        elseif ($inTable -and $line -match '^\|\s*`(?<case>[a-z0-9-]+)`') { $catalog += $Matches['case'] }
    }

    if ($catalog.Count -eq 0) {
        $problems.Add("no case names were read from the phase $PHASE table of demo/SCENARIOS.md")
    }

    foreach ($case in $catalog) {
        $pascal = Get-PascalName $case
        if (-not $caseFiles.ContainsKey($pascal)) { $problems.Add("$case is in the catalog but has no demo/**/Cases/$pascal.cs") }
        if ($cases -notcontains $case) { $problems.Add("$case is in the catalog but has no entry with phase $PHASE") }
    }
}

if ($problems.Count -gt 0) {
    Write-Output "demo/expected-findings.json does not hold:"
    foreach ($problem in $problems) { Write-Output "  $problem" }
    exit 1
}

Write-Output "$($cases.Count) phase $PHASE cases written, $($frozen.Count) entries of finished phases unchanged."
if ($Complete) { Write-Output "$($catalog.Count) phase $PHASE cases in the catalog, each with a file and an entry." }
exit 0
