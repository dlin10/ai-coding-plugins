<#
.SYNOPSIS
Checks whether the generator's answers moved: the model evals without recording, and with -Demo build/record-demo-models.ps1 -Check.

.DESCRIPTION
Run after each engine run's commit, before the next run starts, and with -Demo after a regeneration of the frameworks' census (ADR 0023,
amendment of 2026-11-19). While build/generator-version.pin exists an engine change does not change the generator version, so this check
is what shows that a change moved an answer: a run is one commit, and a shift found here belongs to the commit it names. An engine run's
final gate already ran -Check on the tree it commits, so after a run the demo is not checked again; a census regeneration has no such
gate.

The model evals of SPEC 12.1 item 8a run with CH_MODEL_EVALS=1 and without CH_MODEL_EVALS_RECORD, their verdict reports go to the output
folder, and every eval test must pass: one that did not run fails the check, since without the variable the evals skip and a test run
stays green. skills/hunt/evals/models/generator-snapshot.json must be unchanged afterwards. With -Demo the demo check then generates the
members of the demo's committed files afresh and compares; it writes nothing.

Logs, the trx and the reports go to scratch/model-drift/<commit>/, with `-dirty` added to the commit when the tree has uncommitted changes:
then the result names no commit.

Exit code: 0 when the evals pass and, with -Demo, the demo's answers are unchanged, 1 otherwise.
#>
[CmdletBinding()]
param(
    # Also compares a fresh generation of the demo's members with its committed files (build/record-demo-models.ps1 -Check).
    [switch] $Demo
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$SOLUTION = 'src/ConcurrencyHunter.slnx'
$TESTS = 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj'
$NAMESPACE = 'ConcurrencyHunter.Core.Tests.Generation.'
$EVAL_CLASSES = @('ModelEvalTests', 'EffectsEvalTests', 'LinqOracleTests', 'AccessorEvalTests')
$SNAPSHOT = 'skills/hunt/evals/models/generator-snapshot.json'
$VARIABLES = @('CH_MODEL_EVALS', 'CH_MODEL_EVALS_RECORD', 'CH_MODEL_EVALS_REPORT')

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

Push-Location $root
$saved = @{}
foreach ($name in $VARIABLES) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $commit = (& git rev-parse --short HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'git rev-parse failed.' }
    $dirty = @(& git status --porcelain -- .)
    if ($dirty.Count -gt 0) {
        $commit += '-dirty'
        Write-Host 'The tree has uncommitted changes, so a shift found here names no commit.'
    }
    $out = Join-Path $root "scratch/model-drift/$commit"
    Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction Ignore
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $failures = [Collections.Generic.List[string]]::new()

    # 1. Build.
    $log = Join-Path $out 'build.log'
    & dotnet build $SOLUTION -c Release --nologo --disable-build-servers -nodeReuse:false *> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet build exited $LASTEXITCODE; log: $log" }

    # 2. The model evals, against the committed snapshot.
    $env:CH_MODEL_EVALS = '1'
    $env:CH_MODEL_EVALS_RECORD = $null
    $env:CH_MODEL_EVALS_REPORT = Join-Path $out 'reports'
    $filter = ($EVAL_CLASSES | ForEach-Object { "FullyQualifiedName~$NAMESPACE$_." }) -join '|'
    $log = Join-Path $out 'evals.log'
    & dotnet test $TESTS -c Release --nologo --no-build --filter $filter --logger 'trx;LogFileName=evals.trx' --results-directory $out *> $log
    $code = $LASTEXITCODE
    foreach ($name in $VARIABLES) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    $trx = Join-Path $out 'evals.trx'
    if (-not (Test-Path -LiteralPath $trx)) { $failures.Add("the evals left no trx (exit $code); log: $log") }
    else {
        [xml] $document = [IO.File]::ReadAllText($trx)
        $results = @($document.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
                [pscustomobject] @{ Name = [string] $_.testName; Outcome = [string] $_.outcome } })
        foreach ($class in $EVAL_CLASSES) {
            if (@($results | Where-Object { $_.Name.StartsWith("$NAMESPACE$class.", [StringComparison]::Ordinal) }).Count -eq 0) {
                $failures.Add("no eval of $class ran")
            }
        }
        foreach ($result in @($results | Where-Object Outcome -cne 'Passed')) { $failures.Add("$($result.Name): $($result.Outcome)") }
        if ($code -ne 0 -and $failures.Count -eq 0) { $failures.Add("the evals exited $code; log: $log") }
        Write-Host "Model evals: $(@($results | Where-Object Outcome -ceq 'Passed').Count) of $($results.Count) passed; log: $log"
    }
    & git diff --quiet -- $SNAPSHOT
    if ($LASTEXITCODE -ne 0) { $failures.Add("the evals changed $SNAPSHOT") }

    # 3. With -Demo, the demo's committed answers against a fresh generation.
    if ($Demo) {
        $log = Join-Path $out 'demo-check.log'
        & pwsh -NoProfile -File (Join-Path $root 'build/record-demo-models.ps1') -Check *> $log
        $code = $LASTEXITCODE
        Get-Content -LiteralPath $log -Encoding utf8 | Where-Object { $_ -notmatch '^\s*$' } | Select-Object -Last 1 | ForEach-Object { Write-Host "Demo: $_" }
        if ($code -eq 2) { $failures.Add("record-demo-models.ps1 -Check found only differences by a timeout; repeat on a quiet machine; log: $log") }
        elseif ($code -ne 0) { $failures.Add("record-demo-models.ps1 -Check exited $code; log: $log") }
    }

    if ($failures.Count -gt 0) {
        Write-Host "The generator's answers moved at $commit, or the check could not tell:"
        $failures | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    Write-Host "No answer moved at $commit; logs: $out"
    exit 0
}
finally {
    foreach ($name in $VARIABLES) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    Pop-Location
}
