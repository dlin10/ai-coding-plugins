<#
.SYNOPSIS
Writes the demo's committed generated models, demo/.concurrency-hunter/models/generated/.

.DESCRIPTION
Builds the executable in Release, then runs `metrics --target demo/Demo.slnx --generation on --recording`, which answers the targets of the
demo's scope calls from every generation round, plus the dependency members generation reaches, and writes final answers below the repository
root. `--recording` gives the recording its own limit, so that one run generates everything.
The members of the shared frameworks Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0 that the demo reaches are the frameworks'
census, which ships in the built-in models (ADR 0012, amendment of 2026-11-07), so the committed files hold package members only. The
framework folders below the generated folder are removed before the run, and the run is refused when it writes one again: then the census
does not answer a framework member the demo reaches, and build/generate-frameworks.ps1 regenerates it.

The run is refused when coverage left a target of a scope call at `budget`: such a member was cut by the recording's limit, its answer
depends on the machine and would not be written. Dependency members do not appear in scope coverage. A second run, `--generation read`, then
checks that the committed files are enough: it is refused when it lists a target of a scope call with the cause `not-generated`, including
a target whose nonfinal answer was not saved by the generating run.

While build/generator-version.pin exists the build takes it as the generator version (ADR 0023, amendment of 2026-11-19), and an engine
change leaves the committed files current. -Check then shows whether the change moved an answer: it copies the committed files aside,
generates into an empty folder, compares the members of each file with the committed ones (the `generator` field left out), and puts the
committed files back byte for byte; it writes nothing. -Repin sets the pin to the generator version the sources give and records, so
every file is generated again under the new version.

A difference where either answer is a `timeout` refusal is reported apart, as unsettled: the member limit counts CPU time, and on a loaded
machine it can cut a member that a quiet machine answers. Such a difference is no reason to repin until a repeat on a quiet machine gives it
again.

Exit code: 0 when the files are written and a run that only reads them would generate nothing, 1 otherwise. With -Check: 0 when a fresh
generation answers every committed member exactly as the committed files do, 1 when an answer moved, 2 when the only differences are
unsettled; each member that differs is named.
#>
[CmdletBinding()]
param(
    # Compares a fresh generation with the committed files and writes nothing.
    [switch] $Check,
    # Sets build/generator-version.pin to the tree's computed generator version, then records.
    [switch] $Repin
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
# MSBuild nodes that outlive a redirected dotnet command hold its pipe open for their idle timeout.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$CLI_PROJECT = 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj'
$CORE_PROJECT = 'src/ConcurrencyHunter.Core/ConcurrencyHunter.Core.csproj'
$PIN = 'build/generator-version.pin'
$TARGET = 'demo/Demo.slnx'
$GENERATED_DIR = 'demo/.concurrency-hunter/models/generated'
$BUDGET = 'budget'
$NOT_GENERATED = 'not-generated'
$TIMEOUT = 'timeout'
$FRAMEWORKS = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path $root 'scratch'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$generatedRoot = Join-Path $root $GENERATED_DIR

function Show-Tail([string]$Log) { Get-Content -LiteralPath $Log -Tail 40 -Encoding utf8 | ForEach-Object { Write-Host $_ } }

# Runs dotnet with its whole output in a log and stops on a non-zero exit, printing the log's tail.
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $scratch "record-demo-models-$Name.log"
    & dotnet @Arguments *> $log
    if ($LASTEXITCODE -ne 0) {
        Show-Tail $log
        throw "dotnet $($Arguments[0]) ($Name) exited $LASTEXITCODE; log: $log"
    }
}

# One measurement of the demo; returns the parsed JSON. $Extra holds further metrics arguments.
function Invoke-Metrics([string]$Name, [string]$Generation, [string[]]$Extra = @()) {
    $out = Join-Path $scratch "demo-$Name.json"
    Remove-Item -LiteralPath $out -ErrorAction Ignore
    Invoke-Dotnet $Name (@('run', '--project', $CLI_PROJECT, '-c', 'Release', '--no-build', '--',
                           'metrics', '--target', $TARGET, '--generation', $Generation, '--out', $out) + $Extra)
    if (-not (Test-Path -LiteralPath $out)) { throw "metrics wrote no $out." }
    return Get-Content -LiteralPath $out -Raw -Encoding utf8 | ConvertFrom-Json
}

# The generation targets of every scope that carry the cause, as "scope member file".
function Get-Refusals($Measurement, [string]$Cause) {
    @($Measurement.coverage | ForEach-Object {
            $scope = $_.scopeId
            @($_.generationRefusals) | Where-Object { $null -ne $_ -and $_.cause -eq $Cause } | ForEach-Object { "$scope $($_.member) ($($_.file))" }
        })
}

# Each entry of every file below $Folder, as "relative-path member" -> the entry's JSON in one line and whether it is a `timeout` refusal.
function Get-Entries([string]$Folder) {
    $entries = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $Folder -Recurse -Filter '*.json' -File -ErrorAction Ignore)) {
        $relative = [IO.Path]::GetRelativePath($Folder, $file.FullName).Replace('\', '/')
        $document = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8 | ConvertFrom-Json
        foreach ($entry in @($document.members)) {
            $cut = $null -ne $entry.refusal -and $entry.refusal.reason -ceq $TIMEOUT
            $entries["$relative $($entry.member)"] = @{ Json = ($entry | ConvertTo-Json -Depth 64 -Compress); Timeout = $cut }
        }
    }
    return $entries
}

if ($Check -and $Repin) { throw '-Check and -Repin do not go together.' }

Push-Location $root
try {
    # 0. Repin: the version the sources give becomes the pin, so the build below takes it and every file is generated again.
    if ($Repin) {
        $pin = Join-Path $root $PIN
        if (-not (Test-Path -LiteralPath $pin)) { throw "$PIN does not exist, so the version is not pinned; record without -Repin." }
        $log = Join-Path $scratch 'record-demo-models-source-fingerprint.log'
        & dotnet msbuild (Join-Path $root $CORE_PROJECT) -restore -t:ComputeSourceFingerprint -getProperty:SourceFingerprint -nologo `
            --disable-build-servers -nodeReuse:false *> $log
        if ($LASTEXITCODE -ne 0) { Show-Tail $log; throw "ComputeSourceFingerprint exited $LASTEXITCODE; log: $log" }
        $fingerprint = @(Get-Content -LiteralPath $log -Encoding utf8 | Where-Object { $_ -match '^[0-9a-f]{64}$' }) | Select-Object -Last 1
        if (-not $fingerprint) { throw "ComputeSourceFingerprint printed no fingerprint; log: $log" }
        [IO.File]::WriteAllText($pin, "$fingerprint`n")
        Write-Host "The pin is now $fingerprint."
    }

    # 1. Build.
    Invoke-Dotnet 'build' @('build', $CLI_PROJECT, '-c', 'Release', '--disable-build-servers', '-nodeReuse:false')

    if ($Check) {
        # The committed files go aside and come back byte for byte whatever happens; the fresh generation starts from nothing.
        $committed = Join-Path $scratch 'record-demo-models-check/committed'
        Remove-Item -LiteralPath (Split-Path $committed) -Recurse -Force -ErrorAction Ignore
        New-Item -ItemType Directory -Force -Path $committed | Out-Null
        Copy-Item -Path (Join-Path $generatedRoot '*') -Destination $committed -Recurse
        try {
            Remove-Item -LiteralPath $generatedRoot -Recurse -Force
            $generating = Invoke-Metrics 'check' 'on' @('--recording')
            $cut = @(Get-Refusals $generating $BUDGET)
            $before = Get-Entries $committed
            $after = Get-Entries $generatedRoot
        }
        finally {
            Remove-Item -LiteralPath $generatedRoot -Recurse -Force -ErrorAction Ignore
            New-Item -ItemType Directory -Force -Path $generatedRoot | Out-Null
            Copy-Item -Path (Join-Path $committed '*') -Destination $generatedRoot -Recurse
        }
        if ($cut.Count -gt 0) {
            Write-Host "The fresh generation left $($cut.Count) member(s) at ${BUDGET}; their answers depend on the machine and are not compared:"
            $cut | ForEach-Object { Write-Host "  $_" }
            exit 1
        }
        # A difference where either side is a `timeout` refusal can be the member limit cutting the member on a loaded machine, so it is
        # unsettled rather than moved: repeat on a quiet machine, and only a repeat that gives the same difference is a move.
        $moved = [Collections.Generic.List[string]]::new()
        $unsettled = [Collections.Generic.List[string]]::new()
        foreach ($key in @(@($before.Keys) + @($after.Keys) | Sort-Object -Unique)) {
            if (-not $after.ContainsKey($key)) { $moved.Add("  $key (committed only)") }
            elseif (-not $before.ContainsKey($key)) { $moved.Add("  $key (fresh only)") }
            elseif ($before[$key].Json -cne $after[$key].Json) {
                if ($before[$key].Timeout -or $after[$key].Timeout) {
                    $unsettled.Add("  $key ($(if ($after[$key].Timeout) { 'fresh' } else { 'committed' }) answer is $TIMEOUT)")
                }
                else { $moved.Add("  $key (answer differs)") }
            }
        }
        if ($moved.Count -gt 0) {
            Write-Host "A fresh generation answers $($moved.Count) member(s) differently from the committed files; record them with -Repin:"
            $moved | ForEach-Object { Write-Host $_ }
        }
        if ($unsettled.Count -gt 0) {
            Write-Host "$($unsettled.Count) member(s) differ by a $TIMEOUT, which the machine's load can cause; repeat -Check on a quiet machine, and repin only if it gives the same:"
            $unsettled | ForEach-Object { Write-Host $_ }
        }
        if ($moved.Count -gt 0) { exit 1 }
        if ($unsettled.Count -gt 0) { exit 2 }
        Write-Host "A fresh generation answers all $($before.Count) committed member(s) as the committed files do."
        exit 0
    }

    # 2. Generate, without the framework folders, which the census answers.
    foreach ($framework in $FRAMEWORKS) {
        Remove-Item -LiteralPath (Join-Path $generatedRoot $framework) -Recurse -Force -ErrorAction Ignore
    }
    $generating = Invoke-Metrics 'models' 'on' @('--recording')
    $frameworkFiles = @($FRAMEWORKS | ForEach-Object { Join-Path $generatedRoot $_ } | Where-Object { Test-Path -LiteralPath $_ } |
                        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Filter '*.json' -File })
    if ($frameworkFiles.Count -gt 0) {
        Write-Host "The run generated members of the shared frameworks, which the census should answer; regenerate it with build/generate-frameworks.ps1:"
        $frameworkFiles | ForEach-Object { Write-Host "  $($_.FullName)" }
        exit 1
    }

    # 3. Nothing the recording's limit cut.
    $cut = @(Get-Refusals $generating $BUDGET)
    if ($cut.Count -gt 0) {
        Write-Host "The generating run left $($cut.Count) member(s) at $BUDGET; their answers depend on the machine and are not written:"
        $cut | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    $files = @(Get-ChildItem -LiteralPath $generatedRoot -Recurse -Filter '*.json' -File -ErrorAction Ignore)
    if ($files.Count -eq 0) {
        Write-Host "The generating run wrote no file below $GENERATED_DIR."
        exit 1
    }

    # 4. The committed files are enough: a run that only reads them would generate nothing.
    $reading = Invoke-Metrics 'read' 'read'
    $missing = @(Get-Refusals $reading $NOT_GENERATED)
    if ($missing.Count -gt 0) {
        Write-Host "A run that only reads $GENERATED_DIR would generate $($missing.Count) member(s):"
        $missing | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host "$($files.Count) generated file(s) below $GENERATED_DIR; a run that only reads them generates nothing."
    exit 0
}
finally {
    Pop-Location
}
