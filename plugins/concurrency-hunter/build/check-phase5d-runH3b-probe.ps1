<#
  Gate of the 5d run H3b: a handle names the execution the waiting execution started itself (CONTEXT "Join", ADR 0008),
  measured on the demo and eShop with Polly 7 bodies admitted at the call site and the records withheld. Normal runs are
  compared with the triples recorded at 1e594c8a in scratch/h3b/baseline: a finding may disappear, none may appear or
  change its rule or confidence. Without -Final it is the gate of the probe (task 1, nothing but the probe changed);
  with -Final it is the run's gate after every task. Run from the plugin root with CH_ESHOP_ROOT set.
#>
param([string] $Probe = 'scratch/h3b/probe', [switch] $SkipTests, [switch] $Final)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
$core = 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj'
$JOINED = 'beab0b89c04944ad'
$REAL_RACE = '0d8df6303781e5e8'
# docs/runs/5d-H3a.md, «Проба»: the admitted demo findings beyond the records that H3a left; none may be added.
$H3A_RESIDUALS = @(
    '0524864340b4353b', '0d8df6303781e5e8', '2379952072974ddb', '2cf8baf23ae18f63', '311c9df2db3d44da', '3a949021a7a660a5',
    '494eb6ff8187f085', '79f689686e9e9b59', '807b9682d3bff99c', '807deb9f1da2bbdf', '84fdefd055027cc4', 'be087dc34329ce4b',
    'beab0b89c04944ad', 'c203ff7acbe7a484', 'e38e848da2220a30', 'ec7a1c9eb38b5705')
$PROBE_TESTS = 'src/ConcurrencyHunter.Core.Tests/Engine/CallSiteProbeTests.cs'
$SNAPSHOTS = @('src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json', 'src/ConcurrencyHunter.Cli.Tests/Snapshots/eshop.json')
$SKIP_BEFORE = '[Fact(Skip = "Waits for #400: one task region stands for several executions in one tree.")]'
$SKIP_AFTER = '[Fact(Skip = "Waits for #402, #403 and #404: a pass in a library loop''s continuation, library objects apart per call, later runs of a sequential-awaited record.")]'
$PROBE_CLASSES = [ordered]@{ 'HandleExecutionRuleTests' = 5; 'HandleExecutionProbeTests' = 2 }
if ($Final) { $PROBE_CLASSES['HandleExecutionMatrixTests'] = 3 }
$CHECK_CLASSES = @(
    'Engine.OwnPassJoin', 'Engine.AwaitedRuns', 'Engine.PerPass', 'Engine.HappensBeforeTests', 'Engine.ResultJoinMatrixTests',
    'Engine.BlockingWaitAssumptionMatrixTests', 'Engine.PollyRetryAttemptMatrixTests', 'Engine.CompletionValueHeapTests',
    'Engine.TaskForwarder', 'Engine.TimerTests', 'Engine.DecidingGapTests', 'Engine.CallSiteContextMatrixTests',
    'Engine.CallSiteAdmissionTests', 'Engine.FindingIdentityTests', 'Engine.CancellationCoverageTests',
    'Core.Tests.AnalysisBoundaryTests', 'Core.Tests.DemoGeneratedModelsTests')
$UNCHANGED = @(
    'build/check-phase5d-runH3-probe.ps1', 'build/check-phase5d-runH3-gate.ps1', 'build/check-phase5d-runH3a.ps1',
    'demo/expected-findings.json', 'demo/SCENARIOS.md', 'demo/Demo.Web', 'src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn', 'skills')
if (-not $Final) { $UNCHANGED += @($PROBE_TESTS, 'build/test-baseline.txt', 'src/ConcurrencyHunter.Cli.Tests/Snapshots', 'docs/SPEC.md') }
$MODELS = 'demo/.concurrency-hunter/models/generated'
$FINDING_FIELDS = @('fingerprint', 'rule', 'confidence', 'resource', 'occurrenceCount', 'pairCount', 'occurrences')
$PAIR_FIELDS = @('roots', 'first', 'second', 'proof')
$ACCESS_FIELDS = @('symbol', 'operation', 'instance', 'execution')
$PROOF_FIELDS = @('timing', 'branch', 'creator', 'facts', 'frames', 'steps')
$FRAME_FIELDS = @('execution', 'parent', 'anchor', 'spawn', 'caller', 'operation', 'joins', 'rejected')
$JOIN_FIELDS = @('instance', 'operation', 'completedFault', 'reason')
$STEP_FIELDS = @('step', 'execution', 'other', 'outcomes', 'result')

function Fail([string] $message)
{
    # Write-Host reaches the gate's output even where a caller discards the success stream.
    Write-Host "GATE: $message"
    exit 1
}

function Read-Json([string] $path)
{
    if (-not (Test-Path -LiteralPath $path)) { Fail "missing $path" }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Triples([string] $path)
{
    @((Read-Json $path).findings | ForEach-Object { "$($_.fingerprint)|$($_.rule)|$($_.confidence)" })
}

function Without-Generator([string] $json)
{
    $value = $json | ConvertFrom-Json
    $value.PSObject.Properties.Remove('generator')
    $value | ConvertTo-Json -Depth 100 -Compress
}

function Has-Fields($value, [string[]] $names)
{
    if ($null -eq $value) { return $false }
    $present = @($value.PSObject.Properties.Name)
    @($names | Where-Object { $present -notcontains $_ }).Count -eq 0
}

function Lf([string] $text) { ($text -replace "`r`n", "`n").TrimEnd("`n") }

if (-not $env:CH_ESHOP_ROOT) { Fail 'CH_ESHOP_ROOT is not set' }
# The probe writes under scratch/h3b only, never into the baseline it is compared with.
$separator = [IO.Path]::DirectorySeparatorChar
$scratch = [IO.Path]::GetFullPath((Join-Path $root 'scratch/h3b')) + $separator
$baseline = [IO.Path]::GetFullPath((Join-Path $root 'scratch/h3b/baseline')) + $separator
$probeDirectory = [IO.Path]::GetFullPath((Join-Path $root $Probe)).TrimEnd($separator) + $separator
if (-not $probeDirectory.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase) -or $probeDirectory -eq $scratch) {
    Fail "the probe folder $probeDirectory is not a folder under $scratch"
}
if ($probeDirectory.StartsWith($baseline, [StringComparison]::OrdinalIgnoreCase) -or $baseline.StartsWith($probeDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    Fail "the probe folder $probeDirectory overlaps the baseline $baseline"
}
Push-Location $root
try {
    # Nothing the run must leave alone changed; new files are allowed. The probe changes no existing test; the finished run
    # changes only the Skip attribute of the H3 probe's demo fact.
    $changed = @(git diff --name-only HEAD -- $UNCHANGED)
    if ($changed.Count -ne 0) { Fail "the run must not change: $($changed -join ', ')" }
    $changedTests = @(git diff --name-only --diff-filter=a HEAD -- src/ConcurrencyHunter.Core.Tests src/ConcurrencyHunter.Cli.Tests | ForEach-Object { $_ -replace '^plugins/concurrency-hunter/', '' })
    $allowedTests = if ($Final) { @($PROBE_TESTS) + $SNAPSHOTS } else { @() }
    $unexpected = @($changedTests | Where-Object { $allowedTests -notcontains $_ })
    if ($unexpected.Count -ne 0) { Fail "existing tests changed: $($unexpected -join ', ')" }
    if ($Final) {
        $before = Lf ((git show "HEAD:plugins/concurrency-hunter/$PROBE_TESTS") -join "`n")
        if (-not $before.Contains($SKIP_BEFORE)) { Fail "HEAD's $PROBE_TESTS has no $SKIP_BEFORE" }
        if ($before.Replace($SKIP_BEFORE, $SKIP_AFTER) -ne (Lf (Get-Content -LiteralPath $PROBE_TESTS -Raw))) { Fail "$PROBE_TESTS changed beyond its Skip attribute" }
        # A re-recorded snapshot may lose fingerprints (R5) but gain none.
        foreach ($snapshot in $SNAPSHOTS) {
            $headPrints = @((((git show "HEAD:plugins/concurrency-hunter/$snapshot") -join "`n") | ConvertFrom-Json).counts.fingerprints)
            $nowPrints = @((Get-Content -LiteralPath $snapshot -Raw | ConvertFrom-Json).counts.fingerprints)
            $gained = @($nowPrints | Where-Object { $headPrints -notcontains $_ })
            if ($headPrints.Count -eq 0 -or $gained.Count -ne 0) { Fail "$snapshot gained fingerprints or has none: $($gained -join ', ')" }
        }
    }

    # The committed demo models are re-recorded for the new generator with their answers unchanged, and no model file is added (R7).
    $tracked = @(git ls-files -- $MODELS | Sort-Object)
    $onDisk = @(Get-ChildItem -LiteralPath $MODELS -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') } | Sort-Object)
    if ($tracked.Count -ne 5) { Fail "expected 5 committed demo model files, found $($tracked.Count)" }
    if (($tracked -join ';') -ne ($onDisk -join ';')) { Fail "demo model files on disk differ from the committed set: $($onDisk -join ', ')" }
    foreach ($file in $tracked) {
        $before = (git show "HEAD:plugins/concurrency-hunter/$file") -join "`n"
        if ((Without-Generator $before) -ne (Without-Generator (Get-Content -LiteralPath $file -Raw))) { Fail "$file changed beyond its generator" }
    }

    if (-not $SkipTests) {
        if (Test-Path -LiteralPath $probeDirectory) { Remove-Item -LiteralPath $probeDirectory -Recurse -Force }
        $env:CH_H3B_PROBE_OUT = $probeDirectory
        $filter = (@($PROBE_CLASSES.Keys) | ForEach-Object { "FullyQualifiedName~Engine.$_" }) -join '|'
        $expect = @($PROBE_CLASSES.Keys | ForEach-Object { "$_=$($PROBE_CLASSES[$_])" })
        & ./build/check-phase5d-runH3-gate.ps1 -Name h3b-probe -Project $core -Filter $filter -Expect $expect
        if ($LASTEXITCODE -ne 0) { Fail 'the rule, matrix or probe facts failed' }
        $filter = ($CHECK_CLASSES | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        & ./build/check-phase5d-runH3-gate.ps1 -Name h3b-checks -Project $core -Filter $filter
        if ($LASTEXITCODE -ne 0) { Fail 'a join, admission, identity, cancellation, boundary or demo-model test failed' }
    }

    # Normal runs (records on, nothing admitted): a finding may disappear, none may appear or change (D-0002).
    foreach ($target in 'demo', 'eshop') {
        $before = Triples (Join-Path $baseline "$target-records.json")
        $now = Triples (Join-Path $probeDirectory "$target-records.json")
        $added = @($now | Where-Object { $before -notcontains $_ })
        if ($added.Count -ne 0) { Fail "$target normal run has new or changed triples: $($added -join ', ')" }
        $gone = @($before | Where-Object { $now -notcontains $_ })
        Write-Host "$target normal run: $($now.Count) triples, $($gone.Count) gone$(if ($gone.Count) { ': ' + ($gone -join ', ') })"
    }

    # Demo admitted: every record triple and the real race of D-0005 stay as they are, and nothing is added to what H3a left
    # (R3, D-0009).
    $records = Triples (Join-Path $probeDirectory 'demo-records.json')
    $admitted = Triples (Join-Path $probeDirectory 'demo-admitted.json')
    $race = @(Triples (Join-Path $baseline 'demo-admitted.json') | Where-Object { $_.StartsWith("$REAL_RACE|") })
    if ($race.Count -ne 1 -or $admitted -notcontains $race[0]) { Fail "demo admitted lost the real race $REAL_RACE or changed it" }
    $lost = @($records | Where-Object { $admitted -notcontains $_ })
    if ($lost.Count -ne 0) { Fail "demo admitted lost or changed record triples: $($lost -join ', ')" }
    $extra = @($admitted | Where-Object { $records -notcontains $_ } | ForEach-Object { $_.Split('|')[0] })
    $new = @($extra | Where-Object { $H3A_RESIDUALS -notcontains $_ })
    if ($new.Count -ne 0) { Fail "demo admitted has findings beyond the records that H3a did not leave: $($new -join ', ')" }
    Write-Host "demo admitted: $($admitted.Count) triples, $($extra.Count) beyond the records; $JOINED $(if ($extra -contains $JOINED) { 'stays' } else { 'is gone' })"

    # eShop admitted gives exactly its records (R4).
    $eshopRecords = (Triples (Join-Path $probeDirectory 'eshop-records.json') | Sort-Object) -join ';'
    $eshopAdmitted = (Triples (Join-Path $probeDirectory 'eshop-admitted.json') | Sort-Object) -join ';'
    if ($eshopRecords -ne $eshopAdmitted) { Fail "eShop admitted differs from its records: [$eshopAdmitted] vs [$eshopRecords]" }

    # Every demo admitted finding beyond the records is described once, with every pair it groups before folding and each pair's
    # whole order proof (R6).
    $residuals = @((Read-Json (Join-Path $probeDirectory 'demo-residuals.json')).findings)
    if ($residuals.Count -ne $extra.Count) { Fail "demo-residuals.json describes $($residuals.Count) findings, expected $($extra.Count)" }
    foreach ($fingerprint in $extra) {
        $entry = @($residuals | Where-Object { $_.fingerprint -eq $fingerprint })
        if ($entry.Count -ne 1) { Fail "residual $fingerprint is described $($entry.Count) times" }
        if (-not (Has-Fields $entry[0] $FINDING_FIELDS)) { Fail "residual $fingerprint misses a field" }
        $pairs = @($entry[0].occurrences)
        if ($pairs.Count -eq 0 -or $pairs.Count -ne [int] $entry[0].pairCount) { Fail "residual $fingerprint lists $($pairs.Count) pairs, pairCount $($entry[0].pairCount)" }
        if ([int] $entry[0].pairCount -lt [int] $entry[0].occurrenceCount) { Fail "residual $fingerprint has fewer pairs than occurrences" }
        $keys = @($pairs | ForEach-Object { "$($_.first.instance)#$($_.first.operation)@$($_.first.execution)|$($_.second.instance)#$($_.second.operation)@$($_.second.execution)" })
        if (@($keys | Sort-Object -Unique).Count -ne $keys.Count) { Fail "residual $fingerprint lists a pair twice" }
        foreach ($pair in $pairs) {
            if (-not (Has-Fields $pair $PAIR_FIELDS) -or -not (Has-Fields $pair.first $ACCESS_FIELDS) -or -not (Has-Fields $pair.second $ACCESS_FIELDS)) {
                Fail "a pair of $fingerprint misses a field"
            }
            if ($pair.proof.error) { Fail "the order proof of a pair of $fingerprint threw: $($pair.proof.error)" }
            if (-not (Has-Fields $pair.proof $PROOF_FIELDS)) { Fail "a pair of $fingerprint has an incomplete order proof" }
            foreach ($frame in @($pair.proof.frames)) {
                if (-not (Has-Fields $frame $FRAME_FIELDS)) { Fail "a frame of $fingerprint misses a field" }
                foreach ($join in @($frame.joins) + @($frame.rejected)) { if (-not (Has-Fields $join $JOIN_FIELDS)) { Fail "a join of $fingerprint misses a field" } }
            }
            foreach ($step in @($pair.proof.steps)) { if (-not (Has-Fields $step $STEP_FIELDS)) { Fail "a step of $fingerprint misses a field" } }
        }
        Write-Host "residual $fingerprint`: $($pairs.Count) pair(s), branches $((@($pairs | ForEach-Object { $_.proof.branch }) | Sort-Object -Unique) -join ', ')"
    }

    # D-0009 rests on the probe: where the _joined retry finding stays, its own await is accepted and the next pass is what
    # fails (#402), not a return of the old source refusal.
    if ($extra -contains $JOINED) {
        $joinedPairs = @(@($residuals | Where-Object { $_.fingerprint -eq $JOINED })[0].occurrences)
        $accepted = @($joinedPairs | ForEach-Object { @($_.proof.frames) } | Where-Object { $_.spawn -eq 'TaskRun' -and @($_.joins).Count -gt 0 })
        $nextPass = @($joinedPairs | ForEach-Object { @($_.proof.steps) } | Where-Object { $_.step -eq 'complete-before-next' -and -not $_.result })
        if ($accepted.Count -eq 0 -or $nextPass.Count -eq 0) { Fail "$JOINED stays without its own await accepted and complete-before-next refused" }
    }
}
finally {
    Pop-Location
}
Write-Output 'h3b gate ok'
exit 0
