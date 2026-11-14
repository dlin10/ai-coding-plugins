[CmdletBinding()]
param(
    [ValidateSet('rule', 'matrix', 'records', 'docs', 'fix')]
    [string] $Stage = 'rule',
    [switch] $Final
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'scratch/h3d/gates'
$core = 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj'
$cli = 'src/ConcurrencyHunter.Cli.Tests/ConcurrencyHunter.Cli.Tests.csproj'
$script:check = 'env'
$script:log = Join-Path $logs 'env.log'
$gone = 'beab0b89c04944ad'
$residual = '0d8df6303781e5e8'
$baselineFolder = 'scratch/h3b/h3c-rule-20261113-212101-4532157-c4bb1034505343cbbcb9c2854450252e'
$generated = 'demo/.concurrency-hunter/models/generated'
$skip = '[Fact(Skip = "Waits for #404: later runs of a sequential-awaited record.")]'
$demoFact = 'CallSiteProbeTests.Demo_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records'
$probeFacts = @('HandleExecutionProbeTests.Demo_with_polly_bodies_admitted_loses_the_joined_retry_finding_and_keeps_the_records',
                'HandleExecutionProbeTests.EShop_with_polly_bodies_admitted_has_exactly_the_findings_of_the_records')
$callSiteFacts = @('CallSiteProbeTests.Demo_with_records_withheld_and_nothing_admitted_differs_from_the_records',
                   'CallSiteProbeTests.EShop_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records',
                   'CallSiteProbeTests.EShop_with_records_withheld_and_nothing_admitted_differs_from_the_records')
$matrixClass = 'Engine.ExecutionStartsMatrixTests'
$identity = @('src/ConcurrencyHunter.Analysis/Execution/ExecutionModel.cs', 'src/ConcurrencyHunter.Analysis/Analysis/ConflictFindings.cs')
$splitMatrix = 'src/ConcurrencyHunter.Core.Tests/Engine/ExecutionIdentityMatrixTests.cs'
$limit = 'Доказательство join своего прохода не подтверждает завершение работы попытки'

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

function Fingerprint([string] $Triple) { return ($Triple -split "`t")[0] }

function Compare-Triples([string] $Name, [string[]] $Expected, [string[]] $Actual) {
    $missing = @($Expected | Where-Object { $_ -cnotin $Actual })
    $added = @($Actual | Where-Object { $_ -cnotin $Expected })
    foreach ($triple in $missing) { "${Name} missing: $triple" | Tee-Object -FilePath $script:log -Append | Write-Host }
    foreach ($triple in $added) { "${Name} added: $triple" | Tee-Object -FilePath $script:log -Append | Write-Host }
    foreach ($before in $missing) {
        foreach ($after in $added | Where-Object { (Fingerprint $_) -eq (Fingerprint $before) }) {
            "${Name} changed: $before -> $after" | Tee-Object -FilePath $script:log -Append | Write-Host
        }
    }
    Require ($missing.Count -eq 0 -and $added.Count -eq 0 -and $Expected.Count -eq $Actual.Count) "$Name has a finding shift beyond $gone leaving the admitted demo"
    Write-Host "$Name`: $($Actual.Count) triples as expected"
}

# The facts task 2 names for the matrix, fixed here so that a fact dropped or renamed in the source fails the check.
function Matrix-Facts {
    return ,@('There_are_8_cells_covering_every_value_of_every_axis',
              'Every_cell_has_exactly_the_required_pairs',
              'Every_cell_has_one_execution_with_a_start_per_instance',
              'Pairs_do_not_depend_on_walk_order',
              'The_sequential_shapes_have_no_pair',
              'A_continuation_with_two_antecedents_is_ordered_after_neither',
              'A_continuation_with_one_antecedent_is_ordered_after_it',
              'A_spawn_composite_for_one_start_is_composite',
              'Exclusive_starts_each_joined_in_their_branch_stay_bounded',
              'Exclusive_starts_one_not_joined_keep_their_pair')
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

function Check-Identity {
    Start-Check 'identity'
    & git diff --exit-code HEAD -- @identity *> $script:log
    Require ($LASTEXITCODE -eq 0) 'The execution identity (ExecutionModel.cs, ConflictFindings.cs) differs from HEAD (R1)'
    Require (-not (Test-Path -LiteralPath $splitMatrix)) "The per-instance split's matrix remains: $splitMatrix"
    Write-Host 'identity passed'
}

function Check-Static {
    Start-Check 'static'
    $source = Get-Content -LiteralPath 'src/ConcurrencyHunter.Core.Tests/Engine/CallSiteProbeTests.cs' -Raw
    Require ($source.Contains($skip)) 'The H3 demo skip attribute differs'
    $named = @(Get-ChildItem -LiteralPath 'src/ConcurrencyHunter.Core.Tests' -Recurse -Filter '*.cs' |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Skip\s*=\s*\$?"[^"\r\n]*#402' })
    $named.FullName | Set-Content -LiteralPath $script:log
    Require ($named.Count -eq 0) "A skip still names #402: $($named.Name -join ', ')"
    & git diff --exit-code HEAD -- demo/expected-findings.json *> $script:log
    Require ($LASTEXITCODE -eq 0) 'demo/expected-findings.json differs from HEAD'
    Write-Host 'static passed'
}

# The members of a model file whose content differs between two versions of it, every field but the generator line compared.
function Changed-Members([string] $Before, [string] $After) {
    $old = $Before | ConvertFrom-Json
    $new = $After | ConvertFrom-Json
    $changes = [Collections.Generic.List[string]]::new()
    foreach ($property in @(@($old.PSObject.Properties.Name) + @($new.PSObject.Properties.Name) | Sort-Object -Unique)) {
        if ($property -in @('generator', 'members')) { continue }
        if (($old.$property | ConvertTo-Json -Depth 64 -Compress) -cne ($new.$property | ConvertTo-Json -Depth 64 -Compress)) { $changes.Add("field $property") }
    }
    $was = @{}
    foreach ($member in @($old.members)) { $was[$member.member] = $member | ConvertTo-Json -Depth 64 -Compress }
    $now = @{}
    foreach ($member in @($new.members)) { $now[$member.member] = $member | ConvertTo-Json -Depth 64 -Compress }
    foreach ($name in @(@($was.Keys) + @($now.Keys) | Sort-Object -Unique)) {
        if (-not $now.ContainsKey($name)) { $changes.Add("removed $name") }
        elseif (-not $was.ContainsKey($name)) { $changes.Add("added $name") }
        elseif ($was[$name] -cne $now[$name]) { $changes.Add("changed $name") }
    }
    return ,$changes
}

function Check-Models {
    $recorder = 'run build/record-demo-models.ps1 (online): stale models change the normal demo''s findings, and the measurement would report that as a triple shift'
    try {
        $results = Run-Tests 'models' $core @('Core.Tests.DemoGeneratedModelsTests.Committed_files_name_the_current_generator')
        Passed $results 'DemoGeneratedModelsTests.Committed_files_name_the_current_generator' 1
    }
    catch { throw "The committed demo models do not name the current generator ($($_.Exception.Message)); $recorder" }
    # The re-record changes each committed file's generator line and nothing else.
    $files = @(& git ls-files -- $generated)
    Require ($LASTEXITCODE -eq 0 -and $files.Count -gt 0) "Cannot list the committed files under $generated"
    $untracked = @(& git ls-files --others --exclude-standard -- $generated)
    Require ($untracked.Count -eq 0) "The re-record added files: $($untracked -join ', ')"
    foreach ($file in $files) {
        Require (Test-Path -LiteralPath $file) "The re-record removed $file"
        $head = (& git show "HEAD:plugins/concurrency-hunter/$file") -join "`n"
        Require ($LASTEXITCODE -eq 0) "Cannot read the HEAD $file"
        $members = Changed-Members $head (Get-Content -LiteralPath $file -Raw)
        $members | ForEach-Object { "${file}: $_" } | Add-Content -LiteralPath $script:log
        Require ($members.Count -eq 0) "$file changed beyond its generator line: $($members -join '; ')"
        $lines = @(& git diff --no-color -U0 HEAD -- $file 2>$null | Where-Object { $_ -match '^[+-]' -and $_ -notmatch '^(\+\+\+|---)' })
        $lines | Add-Content -LiteralPath $script:log
        $removed = @($lines | Where-Object { $_.StartsWith('-') })
        $added = @($lines | Where-Object { $_.StartsWith('+') })
        $pattern = '^[+-]\s*"generator"\s*:\s*"[0-9a-f]{64}",?\s*$'
        Require ($removed.Count -eq 1 -and $added.Count -eq 1 -and $removed[0] -match $pattern -and $added[0] -match $pattern) `
            "$file must differ from HEAD in exactly its generator line; it differs in $($lines.Count) lines"
    }
    Write-Host "models: $($files.Count) files differ from HEAD in their generator line only"
}

function Check-Measurements {
    $folder = Join-Path $root "scratch/h3b/h3d-$Stage-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))-$([Guid]::NewGuid().ToString('N'))"
    Require (-not (Test-Path -LiteralPath $folder)) "Measurement folder already exists: $folder"
    $env:CH_H3B_PROBE_OUT = $folder
    try {
        $results = Run-Tests 'measure' $core @('Engine.HandleExecutionProbeTests')
        Require ($results.Count -eq 2) 'The measurement must run both facts'
        foreach ($fact in $probeFacts) { Passed $results $fact 1 }
    }
    finally { Remove-Item Env:CH_H3B_PROBE_OUT -ErrorAction SilentlyContinue }
    $baseline = Join-Path $root $baselineFolder
    Require (Test-Path -LiteralPath $baseline) "The H3c baseline is absent: $baseline"
    $wanted = @{}
    foreach ($name in @('demo-records', 'demo-admitted', 'eshop-records', 'eshop-admitted')) { $wanted[$name] = Read-Triples (Join-Path $baseline "$name.json") }
    Require ($wanted['demo-records'].Count -eq 277 -and $wanted['demo-admitted'].Count -eq 279 -and
             $wanted['eshop-records'].Count -eq 2 -and $wanted['eshop-admitted'].Count -eq 2) 'The H3c baseline has unexpected counts'
    $fingerprints = @($wanted['demo-admitted'] | ForEach-Object { Fingerprint $_ })
    Require ($gone -in $fingerprints -and $residual -in $fingerprints) "The H3c admitted demo baseline lacks $gone or $residual"
    $wanted['demo-admitted'] = @($wanted['demo-admitted'] | Where-Object { (Fingerprint $_) -ne $gone })
    Require ($wanted['demo-admitted'].Count -eq 278) "The admitted demo baseline without $gone must hold 278 triples"
    $shifts = [Collections.Generic.List[string]]::new()
    foreach ($name in @('demo-records', 'demo-admitted', 'eshop-records', 'eshop-admitted')) {
        try { Compare-Triples $name $wanted[$name] (Read-Triples (Join-Path $folder "$name.json")) }
        catch { $shifts.Add($_.Exception.Message) }
    }
    Require ($shifts.Count -eq 0) ($shifts -join '; ')
    $residuals = @((Get-Content -LiteralPath (Join-Path $folder 'demo-residuals.json') -Raw | ConvertFrom-Json).findings)
    Require ($residuals.Count -eq 1 -and $residuals[0].fingerprint -ceq $residual) "demo-residuals.json must list exactly $residual; it lists $(@($residuals.fingerprint) -join ', ')"
    Write-Host "measurements: $folder"
}

function Check-Matrix([object[]] $Results) {
    $facts = Matrix-Facts
    $matrix = @($Results | Where-Object { $_.testName.Contains("$matrixClass.") })
    foreach ($fact in $facts) {
        $found = @($matrix | Where-Object { $_.testName -ceq "ConcurrencyHunter.Core.Tests.$matrixClass.$fact" })
        Require ($found.Count -eq 1 -and $found[0].outcome -ceq 'Passed') "$matrixClass.$fact needs exactly one Passed result; found $($found.Count)"
    }
    Require ($matrix.Count -eq $facts.Count) "$matrixClass must run each of its $($facts.Count) facts exactly once; found $($matrix.Count) results"
}

function Check-Records {
    $null = Run-Tests 'snapshots' $cli @('MetricsCommandTests.Demo_measurement_matches_the_snapshot', 'EShopSnapshotTests')
    Run-Command 'baseline-rerecord' pwsh @('-NoProfile', '-File', './build/check-baseline-rerecord.ps1')
    $baseline = @(Get-Content -LiteralPath 'build/test-baseline.txt')
    $facts = Matrix-Facts
    foreach ($fact in @($facts | ForEach-Object { "ExecutionStartsMatrixTests.$_" }) + $probeFacts + $callSiteFacts) {
        $found = @($baseline | Where-Object { ($_ -split "`t")[0].EndsWith(".$fact") })
        Require ($found.Count -eq 1 -and $found[0].EndsWith("`tPassed")) "Baseline needs one Passed row for $fact"
    }
    $skipped = @($baseline | Where-Object { ($_ -split "`t")[0].EndsWith(".$demoFact") })
    Require ($skipped.Count -eq 1 -and $skipped[0].EndsWith("`tSkipped")) "Baseline needs one Skipped row for $demoFact"
}

function Numbers($Node, [string] $Prefix, $Into) {
    if ($Node -is [Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) { Numbers $property.Value "$Prefix/$($property.Name)" $Into }
    }
    elseif ($Node -is [ValueType] -and $Node -isnot [bool]) { $Into[$Prefix] = [string] $Node }
}

function Snapshot-Numbers([string] $Name, [string] $Json) {
    $numbers = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $snapshot = $Json | ConvertFrom-Json
    Numbers $snapshot.counts "$Name/counts" $numbers
    foreach ($scope in @($snapshot.coverage)) { Numbers $scope "$Name/coverage/$($scope.scopeId)" $numbers }
    return ,$numbers
}

# The blank-line paragraphs of a document, each with its line endings normalized.
function Paragraphs([string] $Text) { return ,@($Text -split '(?:\r?\n){2,}') }

function Check-Docs {
    Run-Command 'docs' pwsh @('-NoProfile', '-File', './build/check-docs.ps1')
    $spec = Get-Content -LiteralPath 'docs/SPEC.md' -Raw
    Require ((($spec -split "`n" | Select-Object -First 30) -join "`n") -match '0035') 'SPEC header does not absorb ADR 0035'
    $paragraphs = Paragraphs $spec
    $ownPass = @($paragraphs | Where-Object { $_.TrimStart().StartsWith('Join своего прохода.') })
    Require ($ownPass.Count -gt 0) 'SPEC lacks the paragraph beginning "Join своего прохода."'
    Require (($ownPass -join "`n").Contains('adr/0035-') -and ($ownPass -join "`n").Contains('каждый старт')) 'The SPEC own-pass paragraph does not link adr/0035- and name каждый старт'
    $runs = @($paragraphs | Where-Object { $_.TrimStart().StartsWith('Движок читает `sequential` и `sequential-awaited`') -and $_.Contains('пару продолжения с собой') })
    Require ($runs.Count -gt 0 -and ($runs -join "`n").Contains('каждого старта')) 'The SPEC TD-034a run-relation paragraph does not contain каждого старта'
    $continuation = @($paragraphs | Where-Object { $_.Contains('`ContinueWith` начинается после завершения antecedent') })
    Require ($continuation.Count -gt 0 -and ($continuation -join "`n").Contains('каждого старта')) 'The SPEC TD-065 ContinueWith paragraph does not contain каждого старта'
    $plan = Get-Content -LiteralPath 'docs/PLAN.md' -Raw
    Require ($plan -match '(?m)^.*5d run H3d.*runs/5d-H3d\.md.*$') 'PLAN lacks the linked H3d row'
    Require (-not $plan.Contains($limit)) 'PLAN still has the temporary limit the run lifts'
    $limits = [regex]::Match($plan, '(?ms)^## 3\. Временные границы\s*$(.*?)(?=^## )').Groups[1].Value
    $spawnEdges = @($limits -split "`n" | Where-Object { $_ -match '^\s*- ' -and $_.Contains('один старт') -and $_.Contains('Parallel') })
    Require ($spawnEdges.Count -gt 0) 'PLAN lacks the temporary limit of the spawn edges that read one start (один старт, Parallel)'
    $record = Get-Content -LiteralPath 'docs/runs/5d-H3d.md' -Raw
    foreach ($text in @($gone, $residual, 'ExecutionStartsMatrixTests')) { Require ($record.Contains($text)) "Run record lacks $text" }
    foreach ($count in @('277', '279', '278')) { Require ($record -match "\b$count\b") "Run record lacks demo count $count" }
    $generators = @(Get-ChildItem $generated -Recurse -Filter '*.json' |
        ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).generator } | Sort-Object -Unique)
    Require ($generators.Count -eq 1 -and $record.Contains($generators[0])) 'Run record lacks the current demo generator'
    $old = @(& git show HEAD:plugins/concurrency-hunter/build/test-baseline.txt)
    Require ($LASTEXITCODE -eq 0) 'Cannot read the HEAD baseline'
    $current = @(Get-Content -LiteralPath 'build/test-baseline.txt')
    $oldNames = [Collections.Generic.HashSet[string]]::new([string[]]@($old | ForEach-Object { ($_ -split "`t")[0] }), [StringComparer]::InvariantCultureIgnoreCase)
    $currentNames = [Collections.Generic.HashSet[string]]::new([string[]]@($current | ForEach-Object { ($_ -split "`t")[0] }), [StringComparer]::InvariantCultureIgnoreCase)
    foreach ($lines in @(@{ Lines = $old; Names = $oldNames }, @{ Lines = $current; Names = $currentNames })) {
        $unique = $lines.Names.Count
        $passed = @($lines.Lines | Where-Object { $_.EndsWith("`tPassed") }).Count
        Require ($record -match "\b$unique\b" -and $record -match "\b$passed\b") "Run record lacks baseline totals $unique and $passed"
    }
    foreach ($name in $oldNames) { if (-not $currentNames.Contains($name)) { Require ($record.Contains($name)) "Run record lacks removed baseline name $name" } }
    foreach ($name in $currentNames) { if (-not $oldNames.Contains($name)) { Require ($record.Contains($name)) "Run record lacks added baseline name $name" } }
    # The snapshot ledger uses Markdown rows: key | old number | new number | cause; a key one side lacks is written as absent.
    $ledger = @(Get-Content -LiteralPath 'scratch/h3d/snapshots.md' | ForEach-Object {
        if ($_ -match '^\s*\|?\s*`?([^|`]+?)`?\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*(.+?)\s*\|?\s*$') {
            [pscustomobject]@{ Key = $Matches[1].Trim(); Old = $Matches[2].Trim(); New = $Matches[3].Trim(); Cause = $Matches[4].Trim(' ', '|') }
        }
    })
    foreach ($name in @('demo', 'eshop')) {
        $path = "src/ConcurrencyHunter.Cli.Tests/Snapshots/$name.json"
        $head = (& git show "HEAD:plugins/concurrency-hunter/$path") -join "`n"
        Require ($LASTEXITCODE -eq 0) "Cannot read the HEAD $path"
        $before = Snapshot-Numbers "$name.json" $head
        $after = Snapshot-Numbers "$name.json" (Get-Content -LiteralPath $path -Raw)
        foreach ($key in @(@($before.Keys) + @($after.Keys) | Sort-Object -Unique)) {
            $was = if ($before.ContainsKey($key)) { $before[$key] } else { 'absent' }
            $now = if ($after.ContainsKey($key)) { $after[$key] } else { 'absent' }
            if ($was -ceq $now) { continue }
            "$key`t$was`t$now" | Add-Content -LiteralPath $script:log
            $row = @($ledger | Where-Object { $_.Key -ceq $key -and $_.Old -ceq $was -and $_.New -ceq $now -and $_.Cause.Length -gt 0 })
            Require ($row.Count -gt 0) "scratch/h3d/snapshots.md lacks the row $key, $was -> $now, with its cause"
            $cause = $row[0].Cause
            $entry = @($record -split "`n" | Where-Object { $_.Contains($key) -and $_ -match "\b$was\b" -and $_ -match "\b$now\b" -and $_.Contains($cause) })
            Require ($entry.Count -gt 0) "Run record lacks snapshot shift $key, $was -> $now, with its cause"
        }
    }
}

$variables = @('CH_MODEL_EVALS', 'CH_MODEL_EVALS_RECORD', 'CH_H3B_PROBE_OUT')
$saved = @{}
foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $root
try {
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    Check-Environment
    Run-Command 'build' dotnet @('build', 'src/ConcurrencyHunter.slnx', '-c', 'Release', '--nologo', '--disable-build-servers', '-nodeReuse:false')
    Check-Identity
    if ($Stage -ne 'fix' -or $Final) { Check-Static }
    Check-Models
    Check-Measurements
    $classes = @('Engine.TaskForwarderJoinMatrixTests', 'Engine.CancellationCoverageTests', 'Engine.HandleExecutionRuleTests', 'Engine.HandleExecutionMatrixTests',
        'Engine.OwnPassJoinTests', 'Engine.OwnPassJoinMatrixTests', 'Engine.SpawnExecutionTests', 'Engine.TimerTests', 'Engine.OncePerCreatorRunMatrixTests',
        'Engine.HappensBeforeTests', 'Engine.SpawnHeapTests', 'Engine.FindingIdentityTests', 'Engine.CallSiteContextMatrixTests', 'Engine.CallSiteAdmissionTests',
        'Engine.PerPassAdmissionTests', 'Engine.PerPassAdmissionMatrixTests', 'Engine.PerPassClimbTests', 'Engine.AwaitedRunsTests', 'Engine.AwaitedRunsMatrixTests',
        'Engine.AwaitedRunsBoundaryMatrixTests', 'Engine.DecidingGapTests', 'Engine.AwaitExitFlowTests', 'Generation.ConfirmationAccessesTests',
        'Core.Tests.DemoExpectationTests', 'Core.Tests.DemoGeneratedModelsTests', 'Core.Tests.AnalysisBoundaryTests')
    $matrixStage = $Stage -ne 'rule' -or $Final
    $recordsStage = $Stage -in @('records', 'docs') -or $Final
    if ($matrixStage) { $classes += $matrixClass }
    if ($recordsStage) { $classes += @('Engine.CanonicalOrderTests', 'Engine.WalkSchedulingTests') }
    $results = Run-Tests 'tests' $core $classes
    if ($matrixStage) { Check-Matrix $results }
    if ($recordsStage) { Check-Records }
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
Write-Output "h3d $Stage gate ok (Final=$Final)"
exit 0
