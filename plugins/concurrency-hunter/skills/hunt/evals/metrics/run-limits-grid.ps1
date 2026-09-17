<#
.SYNOPSIS
Measures the three analysis limits one dimension at a time (R9) and writes the grid with the chosen values.

.DESCRIPTION
After one Release build of the CLI, runs `metrics` on the demo and on eShop for each of the nine rows (depth 4, 8, 12;
contexts 4, 16, 64; scc 16, 64, 256; the other two limits at their defaults 8, 16, 64), measuring the default row once and
repeating it for each dimension. For each row it runs the demo matcher,
DemoExpectationTests.Demo_matches_every_phase_2b_expectation_on_three_runs, with CH_ANALYSIS_LIMITS=depth,contexts,scc set:
PhaseOneAnalyzer reads that variable only when it is set and only when its caller passes no limits, which is how the matcher
runs under the row's limits.

The chosen value of a dimension is the smallest value whose row has the demo matching, that dimension's loss counter
(wildcard-access, merged-context, scc-budget-exceeded) at zero on both corpora, and eShop's fingerprints equal to those at the
largest value; when none qualifies the default stays. Every column but the eShop seconds is deterministic, so a second run
reproduces the file.

Requires CH_ESHOP_ROOT: the eShopOnContainers checkout, whose src/eShopOnContainers-ServicesAndWebApps.sln is measured. The
checkout is only read.

.PARAMETER OutFile
Where the grid is written; limits.md beside this script by default.
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot 'limits.md')
)

$ErrorActionPreference = 'Stop'
if (-not $env:CH_ESHOP_ROOT) {
    Write-Error 'CH_ESHOP_ROOT is not set; point it at the eShopOnContainers checkout.'
    exit 1
}

$OutFile = [IO.Path]::GetFullPath($OutFile)
$pluginsRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../../..')).Path
$demo = 'concurrency-hunter/demo/Demo.slnx'
$eshop = Join-Path $env:CH_ESHOP_ROOT 'src/eShopOnContainers-ServicesAndWebApps.sln'
$defaults = [ordered]@{ depth = 8; contexts = 16; scc = 64 }
$values = [ordered]@{ depth = @(4, 8, 12); contexts = @(4, 16, 64); scc = @(16, 64, 256) }
$lossCounters = @{ depth = 'wildcard-access'; contexts = 'merged-context'; scc = 'scc-budget-exceeded' }
$work = Join-Path ([IO.Path]::GetTempPath()) "ch-limits-grid-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null

function Invoke-Checked([scriptblock]$Command, [string]$What) {
    & $Command | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

# One measurement per target and limits; the default row is measured once and reused by every dimension.
$measurements = @{}
function Get-Measurement([string]$Target, [string]$Limits) {
    $key = "$Target|$Limits"
    if (-not $measurements.ContainsKey($key)) {
        $out = Join-Path $work ("{0}.json" -f $measurements.Count)
        Invoke-Checked { dotnet run --project concurrency-hunter/src/ConcurrencyHunter.Cli -c Release --no-build -- metrics --target $Target --out $out --limits $Limits } "metrics on $Target at $Limits"
        $measurements[$key] = Get-Content $out -Raw | ConvertFrom-Json
    }
    return $measurements[$key]
}

$demoResults = @{}
$testBuilt = $false
function Test-Demo([string]$Limits) {
    if (-not $demoResults.ContainsKey($Limits)) {
        $env:CH_ANALYSIS_LIMITS = $Limits
        try {
            $arguments = @('test', 'concurrency-hunter/src/ConcurrencyHunter.Core.Tests', '-c', 'Release', '--nologo',
                           '--filter', 'FullyQualifiedName~DemoExpectationTests.Demo_matches_every_phase_2b_expectation_on_three_runs')
            if ($script:testBuilt) { $arguments += '--no-build' }
            dotnet @arguments | Out-Host
            $demoResults[$Limits] = $LASTEXITCODE -eq 0
            $script:testBuilt = $true
        }
        finally {
            Remove-Item Env:CH_ANALYSIS_LIMITS -ErrorAction SilentlyContinue
        }
    }
    return $demoResults[$Limits]
}

function Get-Loss($Measurement, [string]$Counter) {
    $sum = 0
    foreach ($scope in $Measurement.coverage) { $sum += [int]$scope.counters.$Counter }
    return $sum
}

function Get-FingerprintHash($Measurement) {
    $sorted = [string[]]@($Measurement.counts.fingerprints)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($sorted -join '|'))
    return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).Substring(0, 16).ToLowerInvariant()
}

Push-Location $pluginsRoot
try {
    Invoke-Checked { dotnet build concurrency-hunter/src/ConcurrencyHunter.Cli -c Release --nologo } 'the Release build of the CLI'

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('| dimension | depth | contexts | scc | demo ok | demo loss | eshop findings | eshop fingerprints | eshop loss | eshop seconds |')
    $lines.Add('|---|---|---|---|---|---|---|---|---|---|')
    $chosen = [ordered]@{}
    foreach ($dimension in $values.Keys) {
        $rows = @()
        foreach ($value in $values[$dimension]) {
            $limits = [ordered]@{ depth = $defaults.depth; contexts = $defaults.contexts; scc = $defaults.scc }
            $limits[$dimension] = $value
            $text = "$($limits.depth),$($limits.contexts),$($limits.scc)"
            $demoMeasurement = Get-Measurement $demo $text
            $eshopMeasurement = Get-Measurement $eshop $text
            $row = [pscustomobject]@{
                Value = $value
                Limits = $limits
                DemoOk = Test-Demo $text
                DemoLoss = Get-Loss $demoMeasurement $lossCounters[$dimension]
                EshopFindings = [int]$eshopMeasurement.counts.findings
                EshopFingerprints = Get-FingerprintHash $eshopMeasurement
                EshopLoss = Get-Loss $eshopMeasurement $lossCounters[$dimension]
                EshopSeconds = [double]$eshopMeasurement.timings.totalSeconds
            }
            $rows += $row
            $lines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |' -f $dimension, $limits.depth, $limits.contexts, $limits.scc,
                        $row.DemoOk.ToString().ToLowerInvariant(), $row.DemoLoss, $row.EshopFindings, $row.EshopFingerprints, $row.EshopLoss,
                        $row.EshopSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)))
        }

        $reference = $rows[-1].EshopFingerprints
        $pick = $defaults[$dimension]
        foreach ($row in $rows) {
            if ($row.DemoOk -and $row.DemoLoss -eq 0 -and $row.EshopLoss -eq 0 -and $row.EshopFingerprints -eq $reference) {
                $pick = $row.Value
                break
            }
        }
        $chosen[$dimension] = $pick
    }

    $lines.Add('')
    $lines.Add("Chosen: depth=$($chosen.depth), contexts=$($chosen.contexts), scc=$($chosen.scc)")
    New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
    [IO.File]::WriteAllText($OutFile, ($lines -join "`n") + "`n")
    Write-Host "Wrote $OutFile"
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

exit 0
