[CmdletBinding()]
param(
    [ValidateSet('rule', 'twins', 'matrix', 'records', 'docs', 'fix')]
    [string] $Stage = 'rule',
    [switch] $Final
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'scratch/h3c/gates'
$core = 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj'
$cli = 'src/ConcurrencyHunter.Cli.Tests/ConcurrencyHunter.Cli.Tests.csproj'
$script:check = 'env'
$script:log = Join-Path $logs 'env.log'
$waited = 'Parameter_bound_to_a_fresh_object_by_a_waited_call_is_fresh'
$unwaited = 'Parameter_bound_by_a_call_not_awaited_is_not_fresh'
$oldFact = 'Parameter_bound_to_a_fresh_object_is_not_fresh'
$removed = @(
    '0524864340b4353b', '2379952072974ddb', '2cf8baf23ae18f63', '311c9df2db3d44da', '3a949021a7a660a5', '494eb6ff8187f085',
    '79f689686e9e9b59', '807b9682d3bff99c', '807deb9f1da2bbdf', '84fdefd055027cc4', 'be087dc34329ce4b', 'c203ff7acbe7a484',
    'e38e848da2220a30', 'ec7a1c9eb38b5705')
$remaining = @('beab0b89c04944ad', '0d8df6303781e5e8')
$skip = '[Fact(Skip = "Waits for #402 and #404: a pass in a library loop''s continuation, later runs of a sequential-awaited record.")]'

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Start-Check([string] $Name) {
    $script:check = $Name
    $script:log = Join-Path $logs "$Name.log"
    [IO.File]::WriteAllText($script:log, '')
}

function Run-Command([string] $Name, [string] $Command, [string[]] $Arguments) {
    Start-Check $Name
    & $Command @Arguments *> $script:log
    Require ($LASTEXITCODE -eq 0) "$Name exited $LASTEXITCODE"
    Write-Host "$Name passed"
}

function Run-Tests([string] $Name, [string] $Project, [string[]] $Classes) {
    $directory = Join-Path $logs "$Name-$([Guid]::NewGuid().ToString('N'))"
    $filter = ($Classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    Run-Command $Name dotnet @('test', $Project, '-c', 'Release', '--nologo', '--no-build', '--filter', $filter,
        '--logger', 'trx;LogFileName=results.trx', '--results-directory', $directory)
    $file = Join-Path $directory 'results.trx'
    Require (Test-Path -LiteralPath $file) "$Name wrote no trx"
    [xml] $trx = Get-Content -LiteralPath $file -Raw
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    Require ($results.Count -gt 0) "$Name ran no tests"
    Require (@($results | Where-Object outcome -eq 'Failed').Count -eq 0) "$Name has failed results"
    foreach ($class in $Classes) {
        Require (@($results | Where-Object { $_.testName.Contains($class) }).Count -gt 0) "$Name did not run $class"
    }
    return ,$results
}

function Passed([object[]] $Results, [string] $Stem, [int] $Count) {
    $found = @($Results | Where-Object { $_.testName.Contains($Stem) })
    Require ($found.Count -eq $Count -and @($found | Where-Object outcome -ne 'Passed').Count -eq 0) "$Stem needs $Count Passed results; found $($found.Count)"
}

function Read-Triples([string] $Path) {
    return ,@((Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json).findings |
        ForEach-Object { "$($_.fingerprint)`t$($_.rule)`t$($_.confidence)" } | Sort-Object)
}

function Compare-Triples([string] $Name, [string[]] $Expected, [string[]] $Actual) {
    $missing = @($Expected | Where-Object { $_ -cnotin $Actual })
    $added = @($Actual | Where-Object { $_ -cnotin $Expected })
    foreach ($triple in $missing) { "${Name} missing: $triple" | Tee-Object -FilePath $script:log -Append | Write-Host }
    foreach ($triple in $added) { "${Name} added: $triple" | Tee-Object -FilePath $script:log -Append | Write-Host }
    foreach ($before in $missing) {
        $fingerprint = ($before -split "`t")[0]
        foreach ($after in $added | Where-Object { ($_ -split "`t")[0] -eq $fingerprint }) {
            "${Name} changed: $before -> $after" | Tee-Object -FilePath $script:log -Append | Write-Host
        }
    }
    Require ($missing.Count -eq 0 -and $added.Count -eq 0 -and $Expected.Count -eq $Actual.Count) "$Name has a finding shift outside the fourteen allowed removals"
    Write-Host "$Name`: $($Actual.Count) triples unchanged"
}

function Check-Environment {
    Start-Check 'env'
    $restore = 'dotnet restore $env:CH_ESHOP_ROOT/src/eShopOnContainers-ServicesAndWebApps.sln --disable-build-servers -nodeReuse:false'
    Require (-not [string]::IsNullOrWhiteSpace($env:CH_ESHOP_ROOT)) "Set CH_ESHOP_ROOT; restore with: $restore"
    Require (Test-Path -LiteralPath $env:CH_ESHOP_ROOT) "eShop is absent; restore with: $restore"
    $projects = @(Get-ChildItem -LiteralPath $env:CH_ESHOP_ROOT -Recurse -Filter '*.csproj')
    $missing = @($projects | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'obj/project.assets.json')) })
    $missing.FullName | Set-Content -LiteralPath $script:log
    Require ($projects.Count -gt 0 -and $missing.Count -eq 0) "Missing eShop assets; run: $restore"
    Write-Host "env: $($projects.Count) restored eShop projects"
}

function Check-Static {
    Start-Check 'static'
    foreach ($path in @('src/ConcurrencyHunter.Core.Tests/Engine/FreshRouteProbeTests.cs', 'build/check-phase5d-runH3c-probe.ps1')) {
        Require (-not (Test-Path -LiteralPath $path)) "The retired probe remains: $path"
    }
    $source = Get-Content -LiteralPath 'src/ConcurrencyHunter.Core.Tests/Engine/CallSiteProbeTests.cs' -Raw
    Require ($source.Contains($skip)) 'The H3 demo skip attribute differs'
    Require ($source -notmatch 'Skip\s*=\s*"[^"\r\n]*#403') 'A skip still names #403'
    & git diff --exit-code HEAD -- demo/expected-findings.json *> $script:log
    Require ($LASTEXITCODE -eq 0) 'demo/expected-findings.json differs from HEAD'
    Write-Host 'static passed'
}

function Check-Measurements {
    $folder = Join-Path $root "scratch/h3b/h3c-$Stage-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))-$([Guid]::NewGuid().ToString('N'))"
    Require (-not (Test-Path -LiteralPath $folder)) "Measurement folder already exists: $folder"
    $env:CH_H3B_PROBE_OUT = $folder
    try {
        $results = Run-Tests 'measure' $core @('Engine.HandleExecutionProbeTests')
        Require ($results.Count -eq 2) 'The measurement must run both facts'
        Passed $results 'Demo_with_polly_bodies_admitted_loses_the_joined_retry_finding_and_keeps_the_records' 1
        Passed $results 'EShop_with_polly_bodies_admitted_has_exactly_the_findings_of_the_records' 1
    }
    finally { Remove-Item Env:CH_H3B_PROBE_OUT -ErrorAction SilentlyContinue }
    $baseline = Join-Path $root 'scratch/h3b/probe'
    $records = Read-Triples (Join-Path $baseline 'demo-records.json')
    $admitted = Read-Triples (Join-Path $baseline 'demo-admitted.json')
    Require ($records.Count -eq 277 -and $admitted.Count -eq 293) 'The H3b baseline has unexpected demo counts'
    $expected = @($records) + @($admitted | Where-Object { ($_ -split "`t")[0] -in $remaining })
    Require ($expected.Count -eq 279) 'The baseline does not contain the two remaining admitted triples'
    Compare-Triples 'baseline admitted removals' @($admitted | Where-Object { ($_ -split "`t")[0] -notin $removed }) $expected
    $shifts = [Collections.Generic.List[string]]::new()
    foreach ($name in @('demo-records', 'demo-admitted', 'eshop-records', 'eshop-admitted')) {
        $wanted = if ($name -eq 'demo-admitted') { $expected } else { Read-Triples (Join-Path $baseline "$name.json") }
        if ($name.StartsWith('eshop')) { Require ($wanted.Count -eq 2) "$name baseline must contain two findings" }
        try { Compare-Triples $name $wanted (Read-Triples (Join-Path $folder "$name.json")) }
        catch { $shifts.Add($_.Exception.Message) }
    }
    Require ($shifts.Count -eq 0) ($shifts -join '; ')
    Write-Host "measurements: $folder"
}

function Check-Matrix([object[]] $Results) {
    $facts = @(
        'There_are_235_cells_covering_every_value_of_every_axis',
        'Every_cell_is_exact_and_the_wider_list_is_empty',
        'No_cell_is_fresh_where_its_expectation_pairs',
        'Edges_a_model_a_held_delegate_a_consumption_or_a_comparer_adds_are_not_direct',
        'Freshness_and_pairs_do_not_depend_on_walk_order_or_the_candidate_index')
    $matrix = @($Results | Where-Object { $_.testName.Contains('Engine.FreshHandOffMatrixTests.') })
    Require ($matrix.Count -eq 5) 'The hand-off matrix must run exactly its five facts'
    foreach ($fact in $facts) {
        $found = @($matrix | Where-Object { $_.testName -ceq "ConcurrencyHunter.Core.Tests.Engine.FreshHandOffMatrixTests.$fact" })
        Require ($found.Count -eq 1 -and $found[0].outcome -ceq 'Passed') "$fact needs exactly one Passed result"
    }
}

function Check-Records {
    $null = Run-Tests 'snapshots' $cli @('MetricsCommandTests.Demo_measurement_matches_the_snapshot', 'EShopSnapshotTests')
    Run-Command 'baseline-rerecord' pwsh @('-NoProfile', '-File', './build/check-baseline-rerecord.ps1')
    $report = Get-Content -LiteralPath $script:log -Raw
    Require ($report -match ([regex]::Escape($oldFact) + '.*' + [regex]::Escape($waited))) 'The baseline check did not pair the renamed fresh-object fact'
    $baseline = @(Get-Content -LiteralPath 'build/test-baseline.txt')
    foreach ($entry in @(@('FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart', 8),
                          @('HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver', 10),
                          @("FreshObjectTests.$waited", 1), @("FreshObjectTests.$unwaited", 1), @('FreshHandOffMatrixTests.', 5))) {
        $found = @($baseline | Where-Object { $_.Contains($entry[0]) -and $_.EndsWith("`tPassed") })
        Require ($found.Count -eq $entry[1]) "Baseline needs $($entry[1]) Passed rows for $($entry[0])"
    }
    Require (@($baseline | Where-Object { $_.Contains($oldFact) -or $_.Contains('FreshRouteProbeTests.') }).Count -eq 0) 'Baseline still names a retired fact'
}

function Check-Docs {
    Run-Command 'docs' pwsh @('-NoProfile', '-File', './build/check-docs.ps1')
    $spec = Get-Content -LiteralPath 'docs/SPEC.md' -Raw
    Require (($spec -split "`n" | Select-Object -First 30) -join "`n" -match '0034') 'SPEC header does not absorb ADR 0034'
    $fresh = @($spec -split '(?:\r?\n){2,}' | Where-Object { $_ -match 'fresh-object' }) -join "`n"
    foreach ($word in @('await', 'forwarder', 'RMW', 'UnknownEffect')) { Require ($fresh.Contains($word)) "SPEC fresh-object paragraph lacks $word" }
    Require ($fresh -notmatch '(?:не\s+(?:fresh|свеж[^\s,;]*|fresh-object)[^\r\n.]*параметр|параметр[^\r\n.]*не\s+(?:fresh|свеж))') 'SPEC still excludes parameters from freshness'
    $unresolved = @($spec -split '(?:\r?\n){2,}' | Where-Object { $_ -match 'unresolved-base' }) -join "`n"
    Require ($unresolved.Contains('в том же теле')) 'SPEC unresolved-base coverage does not keep body-local freshness'
    $plan = Get-Content -LiteralPath 'docs/PLAN.md' -Raw
    Require ($plan -match '(?m)^.*5d run H3c.*runs/5d-H3c\.md.*$') 'PLAN lacks the linked H3c row'
    $record = Get-Content -LiteralPath 'docs/runs/5d-H3c.md' -Raw
    foreach ($fingerprint in $removed + $remaining) { Require ($record.Contains($fingerprint)) "Run record lacks $fingerprint" }
    foreach ($count in @('277', '293', '279')) { Require ($record.Contains($count)) "Run record lacks demo count $count" }
    foreach ($run in @('demo records', 'demo admitted', 'eShop records', 'eShop admitted')) {
        $line = @($record -split "`n" | Where-Object { $_.Contains($run) }) -join ' '
        $counts = switch ($run) { 'demo records' { @('277') }; 'demo admitted' { @('293', '279') }; default { @('2') } }
        foreach ($count in $counts) { Require ($line -match "\b$count\b") "Run record lacks $run count $count" }
    }
    $generators = @(Get-ChildItem 'demo/.concurrency-hunter/models/generated' -Recurse -Filter '*.json' |
        ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).generator } | Sort-Object -Unique)
    Require ($generators.Count -eq 1 -and $record.Contains($generators[0])) 'Run record lacks the current demo generator'
    $old = @(& git show HEAD:plugins/concurrency-hunter/build/test-baseline.txt)
    Require ($LASTEXITCODE -eq 0) 'Cannot read the HEAD baseline'
    $current = @(Get-Content -LiteralPath 'build/test-baseline.txt')
    $oldNames = [Collections.Generic.HashSet[string]]::new([string[]]@($old | ForEach-Object { ($_ -split "`t")[0] }), [StringComparer]::InvariantCultureIgnoreCase)
    $currentNames = [Collections.Generic.HashSet[string]]::new([string[]]@($current | ForEach-Object { ($_ -split "`t")[0] }), [StringComparer]::InvariantCultureIgnoreCase)
    $oldRows = [Collections.Generic.HashSet[string]]::new([string[]]$old, [StringComparer]::InvariantCultureIgnoreCase)
    foreach ($lines in @(@{ Lines = $old }, @{ Lines = $current })) {
        $unique = @($lines.Lines | ForEach-Object { ($_ -split "`t")[0] } | Sort-Object -Unique).Count
        $passed = @($lines.Lines | Where-Object { $_.EndsWith("`tPassed") }).Count
        Require ($record -match "\b$unique\b" -and $record -match "\b$passed\b") "Run record lacks baseline totals $unique and $passed"
    }
    foreach ($name in @(@($oldNames) + @($currentNames) | Sort-Object -Unique)) {
        if ($oldNames.Contains($name) -ne $currentNames.Contains($name)) { Require ($record.Contains($name)) "Run record lacks added or removed baseline name $name" }
    }
    # New completion cells are recorded by their full baseline row, including the cell name.
    foreach ($line in $current | Where-Object { $_.Contains('CompletionValueMatrixTests.') -and -not $oldRows.Contains($_) }) {
        $name = ($line -split "`t")[0]
        Require ($record.Contains($name)) "Run record lacks completion cell $name"
    }
    $completion = Get-Content -LiteralPath 'src/ConcurrencyHunter.Core.Tests/Engine/CompletionValueMatrixTests.cs' -Raw
    $oldCompletion = (& git show HEAD:plugins/concurrency-hunter/src/ConcurrencyHunter.Core.Tests/Engine/CompletionValueMatrixTests.cs) -join "`n"
    Require ($LASTEXITCODE -eq 0) 'Cannot read the HEAD completion matrix'
    $cellPattern = '"((?:C_|TW_)[A-Za-z0-9_]+)"'
    $oldCells = @([regex]::Matches($oldCompletion, $cellPattern) | ForEach-Object { $_.Groups[1].Value })
    foreach ($cell in @([regex]::Matches($completion, $cellPattern) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)) {
        if ($cell -cnotin $oldCells) { Require ($record.Contains($cell)) "Run record lacks completion cell $cell" }
    }
    # The snapshot ledger uses Markdown rows: key | old number | new number | cause.
    $snapshots = Get-Content -LiteralPath 'scratch/h3c/snapshots.md'
    foreach ($line in $snapshots) {
        if ($line -notmatch '^\s*\|?\s*`?([^|`]+)`?\s*\|\s*(\d+)\s*\|\s*(\d+)\s*\|\s*(.+?)\s*\|?\s*$') { continue }
        $key = $Matches[1].Trim(); $before = $Matches[2]; $after = $Matches[3]; $cause = $Matches[4].Trim(' ', '|')
        if ($before -eq $after) { continue }
        Require ($cause.Length -gt 0) "Snapshot ledger lacks a cause for $key"
        $entry = @($record -split "`n" | Where-Object { $_.Contains($key) -and $_ -match "\b$before\b" -and $_ -match "\b$after\b" -and $_.Contains($cause) })
        Require ($entry.Count -gt 0) "Run record lacks snapshot shift $key, $before -> $after, with its cause"
    }
}

$variables = @('CH_MODEL_EVALS', 'CH_MODEL_EVALS_RECORD', 'CH_H3C_PROBE_OUT', 'CH_H3B_PROBE_OUT')
$saved = @{}
foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $root
try {
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    Check-Environment
    Run-Command 'build' dotnet @('build', 'src/ConcurrencyHunter.slnx', '-c', 'Release', '--nologo', '--disable-build-servers', '-nodeReuse:false')
    if ($Stage -ne 'fix' -or $Final) { Check-Static }
    Check-Measurements
    $classes = @('Engine.FreshObjectTests', 'Engine.FreshDeduplicationTests', 'Engine.HandedSlotTests', 'Engine.CancellationCoverageTests')
    if ($Stage -eq 'fix' -and -not $Final) {
        $classes += @('Engine.FreshHandOffMatrixTests', 'Engine.CompletionValueMatrixTests')
    }
    else {
        $classes += @('Engine.CandidateIndexTests', 'Engine.CanonicalOrderTests', 'Core.Tests.DemoExpectationTests', 'Core.Tests.DemoGeneratedModelsTests',
            'Core.Tests.AnalysisBoundaryTests', 'Engine.CaptureCellTests', 'Engine.CaptureOwnerMatrixTests', 'Engine.TreeConfinedTests',
            'Engine.KeptObjectTests', 'Engine.UnknownEffectTests', 'Engine.DeepReadTests', 'Engine.TaskForwarder',
            'Engine.CallSiteContextMatrixTests', 'Engine.CallSiteAdmissionTests', 'Engine.UnresolvedBaseCoverageTests', 'Engine.WorklistTests')
        if ($Stage -ne 'rule' -or $Final) { $classes += 'Engine.CompletionValueMatrixTests' }
        if ($Stage -in @('matrix', 'records', 'docs') -or $Final) { $classes += 'Engine.FreshHandOffMatrixTests' }
    }
    $results = Run-Tests 'tests' $core $classes
    Passed $results 'FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart' 8
    Passed $results 'HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver' 10
    Passed $results "FreshObjectTests.$waited" 1
    Passed $results "FreshObjectTests.$unwaited" 1
    Require (@($results | Where-Object { $_.testName.Contains($oldFact) }).Count -eq 0) 'The old fresh-object fact still ran'
    if ($Stage -in @('matrix', 'records', 'docs') -or $Final) { Check-Matrix $results }
    if ($Stage -in @('records', 'docs') -or $Final) { Check-Records }
    if ($Stage -eq 'docs' -or $Final) { Check-Docs }
}
catch {
    Write-Host "GATE FAILED [$script:check]: $($_.Exception.Message)"
    if (Test-Path -LiteralPath $script:log) { Get-Content -LiteralPath $script:log -Tail 60 | Write-Host }
    exit 1
}
finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    Pop-Location
}
Write-Output "h3c $Stage gate ok (Final=$Final)"
exit 0
