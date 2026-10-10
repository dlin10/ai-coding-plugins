<#
.SYNOPSIS
Generate every public member of the installed System.Private.CoreLib and compare the snapshot with the committed one.

.DESCRIPTION
Builds tools/ConcurrencyHunter.CoreLibGeneration in Release, runs its `generate` into -Out (by default
scratch/corelib-generation/<time>) and compares <out>/snapshot.tsv with skills/hunt/evals/models/corelib-generation.tsv. A run
over every member takes about two hours. -Members names a file of declaration ids, one per line, to generate and compare only those.
Passing the same -Out again resumes a run that was cut short. <out>/answers.jsonl holds every answer document, with the words of each
cause, and <out>/failures.txt the members whose generation threw.

-Record copies the snapshot of a run over every member over the committed one, and refuses a run over -Members or one with a
`timeout` or `threw` row, whose rows depend on the machine. Account for every moved line before re-recording.

Exit code: 0 when the snapshots are the same or after -Record, 1 when they differ or the run failed.
#>
[CmdletBinding()]
param(
    [string]$Out = '',
    [int]$Parallelism = 0,
    [string]$Members = '',
    [switch]$Record
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path "$PSScriptRoot/..").Path
$committed = Join-Path $root 'skills/hunt/evals/models/corelib-generation.tsv'
$project = Join-Path $root 'tools/ConcurrencyHunter.CoreLibGeneration/ConcurrencyHunter.CoreLibGeneration.csproj'

if ($Record -and $Members) {
    Write-Output 'A run over -Members is no snapshot to record.'
    exit 1
}
if (-not $Out) {
    $Out = Join-Path $root "scratch/corelib-generation/$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

$buildLog = Join-Path $Out 'build.log'
dotnet build $project -c Release --disable-build-servers -nodeReuse:false *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $buildLog -Tail 30
    Write-Output "The tool did not build; see $buildLog."
    exit 1
}

$generate = @('generate', '--out', $Out)
$compare = @()
if ($Parallelism -gt 0) {
    $generate += @('--parallelism', $Parallelism)
}
if ($Members) {
    $list = (Resolve-Path $Members).Path
    $generate += @('--members', $list)
    $compare += @('--members', $list)
}

$log = Join-Path $Out 'generate.log'
Write-Output "Generating into $Out; log: $log"
dotnet run --project $project -c Release --no-build -- @generate *> $log
$code = $LASTEXITCODE
Get-Content -LiteralPath $log -Tail 25
if ($code -ne 0) {
    Write-Output "The generation failed with exit code $code."
    exit 1
}

$fresh = Join-Path $Out 'snapshot.tsv'
if ($Record) {
    $unstable = @(Select-String -LiteralPath $fresh -Pattern "`t(timeout|threw)`t")
    if ($unstable.Count -ne 0) {
        Write-Output "$($unstable.Count) member(s) timed out or threw, so the run is no snapshot to record; see $Out."
        exit 1
    }
    Copy-Item -LiteralPath $fresh -Destination $committed -Force
    Write-Output "Recorded $committed."
    exit 0
}
if (-not (Test-Path -LiteralPath $committed)) {
    Write-Output "No committed snapshot at $committed; record a run over every member with -Record."
    exit 1
}

$compareLog = Join-Path $Out 'compare.log'
dotnet run --project $project -c Release --no-build -- compare $committed $fresh @compare *> $compareLog
$code = $LASTEXITCODE
Get-Content -LiteralPath $compareLog
exit $code
