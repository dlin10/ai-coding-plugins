[CmdletBinding()]
param(
    [ValidateSet('probe', 'expectations', 'matrix', 'records', 'docs', 'fix')]
    [string] $Stage = 'probe',
    [switch] $Final
)

$effectiveStage = if ($Final) { 'final' } else { $Stage }
$completed = $effectiveStage -in @('docs', 'fix', 'final')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'scratch/h3e/gates'
$core = 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj'
$generated = 'demo/.concurrency-hunter/models/generated'
$script:check = 'env'
$script:log = Join-Path $logs 'env.log'
$residual = "0d8df6303781e5e8`tDCA1001`tHigh"
$baselineFolders = @{
    callsite = 'scratch/h3e/baseline-145b385f-callsite'
    handles = 'scratch/h3b/h3e-baseline-145b385f-ddbee9c3257a4911a30492fe42db2404'
}

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

# Probe assertions and the inventory may fail; a written, nonempty trx is required even then.
function Run-TestResults([string] $Name, [string] $Filter, [string] $Project = $core) {
    Start-Check $Name
    $directory = Join-Path $logs "$Name-$([Guid]::NewGuid().ToString('N'))"
    & dotnet test $Project -c Release --nologo --no-build --filter $Filter --logger 'trx;LogFileName=results.trx' --results-directory $directory *> $script:log
    $script:testExit = $LASTEXITCODE
    $file = Join-Path $directory 'results.trx'
    Require (Test-Path -LiteralPath $file) "$Name wrote no trx (exit $script:testExit)"
    [xml] $trx = Get-Content -LiteralPath $file -Raw
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    Require ($results.Count -gt 0) "$Name wrote no test result (exit $script:testExit)"
    return ,$results
}

function Run-Tests([string] $Name, [string] $Class, [string] $Project = $core) {
    $results = Run-TestResults $Name "FullyQualifiedName~$Class" $Project
    Require ($script:testExit -eq 0) "$Name exited $script:testExit"
    Require (@($results | Where-Object outcome -ne 'Passed').Count -eq 0) "$Class needs every fact Passed"
    Write-Host "$Name`: $($results.Count) Passed"
    return ,$results
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
    Require ($missing.Count -eq 0 -and $added.Count -eq 0 -and $Expected.Count -eq $Actual.Count) "$Name has an unexpected triple shift"
    Write-Host "$Name`: $($Actual.Count) triples as expected"
}

function Check-Environment {
    Start-Check 'env'
    $restore = 'dotnet restore $env:CH_ESHOP_ROOT/src/eShopOnContainers-ServicesAndWebApps.sln --disable-build-servers -nodeReuse:false'
    Require (-not [string]::IsNullOrWhiteSpace($env:CH_ESHOP_ROOT)) "Set CH_ESHOP_ROOT; restore with: $restore"
    Require (Test-Path -LiteralPath $env:CH_ESHOP_ROOT) "eShop is absent; restore with: $restore"
    $projects = @(Get-ChildItem -LiteralPath $env:CH_ESHOP_ROOT -Recurse -Filter '*.csproj')
    $missing = @($projects | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'obj/project.assets.json')) })
    $missing.FullName | Set-Content -LiteralPath $script:log
    Require ($projects.Count -gt 0 -and $missing.Count -eq 0) "Missing eShop assets; the orchestrator must run: $restore"
    Write-Host "env: $($projects.Count) restored eShop projects"
    $demo = @(Get-ChildItem -LiteralPath 'demo' -Recurse -Filter '*.csproj')
    $missing = @($demo | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'obj/project.assets.json')) })
    $missing.FullName | Add-Content -LiteralPath $script:log
    Require ($demo.Count -eq 5 -and $missing.Count -eq 0) 'The five demo projects need obj/project.assets.json'
    Write-Host 'env: five restored demo projects'
}

function Check-Scope {
    Start-Check 'scope'
    $status = @(& git status --porcelain --untracked-files=all -- .)
    Require ($LASTEXITCODE -eq 0) 'Cannot read git status'
    $status | Set-Content -LiteralPath $script:log
    $allowed = '^(CONTEXT\.md|src/ConcurrencyHunter\.Analysis/.+|src/ConcurrencyHunter\.Core\.Tests/Engine/CallSiteProbeTests\.cs|demo/\.concurrency-hunter/models/generated/.+|build/check-phase5d-runH3e\.ps1)$'
    if ($effectiveStage -in @('expectations', 'matrix', 'records') -or $completed) {
        $allowed = '^(CONTEXT\.md|src/ConcurrencyHunter\.Analysis/Execution/ExecutionModel\.cs|src/ConcurrencyHunter\.Core\.Tests/Engine/(CallSiteProbeTests|AwaitedRunsBoundaryMatrixTests|PerPassAdmissionMatrixTests|PerPassAdmissionTests|PerPassClimbTests|PollyRetryAttemptMatrixTests|ExecutionStartsMatrixTests)\.cs|demo/\.concurrency-hunter/models/generated/.+|build/check-phase5d-runH3e\.ps1|docs/adr/0036-the-later-runs-of-a-sequential-awaited-call-start-in-its-continuation\.md)$'
    }
    if ($effectiveStage -in @('matrix', 'records') -or $completed) {
        $allowed = $allowed.Replace('CallSiteProbeTests|', 'LaterRunsPlacementMatrixTests|CallSiteProbeTests|')
    }
    if ($effectiveStage -eq 'records' -or $completed) {
        $allowed = $allowed.Replace('CallSiteProbeTests|', 'HandleExecutionProbeTests|CallSiteProbeTests|')
        $allowed = $allowed.Replace('CONTEXT\.md|', 'CONTEXT\.md|src/ConcurrencyHunter\.Cli\.Tests/Snapshots/(demo|eshop)\.json|build/test-baseline\.txt|')
    }
    if ($completed) {
        $allowed = $allowed.Replace('CONTEXT\.md|', 'CONTEXT\.md|docs/(SPEC\.md|PLAN\.md|runs/5d-H3e\.md|concurrency-hunter-guide\.ru\.html)|src/ConcurrencyHunter\.Analysis/(Heap/WholeProgram|Execution/HappensBefore)\.cs|')
        foreach ($file in @('src/ConcurrencyHunter.Analysis/Heap/WholeProgram.cs', 'src/ConcurrencyHunter.Analysis/Execution/HappensBefore.cs')) {
            $comments = @(& git diff --no-color -U0 HEAD -- $file)
            Require ($LASTEXITCODE -eq 0) "Cannot read the diff of $file"
            $comments | Add-Content -LiteralPath $script:log
            $lines = @($comments | Where-Object { $_ -match '^[+-]' -and $_ -notmatch '^(\+\+\+|---)' })
            Require (@($lines | Where-Object { $_ -notmatch '^[+-]\s*//' }).Count -eq 0) "$file may change only comment lines"
        }
    }
    foreach ($line in $status) {
        foreach ($path in ($line.Substring(3) -split ' -> ')) {
            $relative = $path.Trim('"').Replace('\', '/') -replace '^plugins/concurrency-hunter/', ''
            Require ($relative -cmatch $allowed) "Out-of-scope change: $line"
        }
    }
    $diff = @(& git diff --no-color -U0 HEAD -- src/ConcurrencyHunter.Core.Tests/Engine/CallSiteProbeTests.cs)
    Require ($LASTEXITCODE -eq 0) 'Cannot read the CallSiteProbeTests.cs diff'
    $diff | Add-Content -LiteralPath $script:log
    $removed = @($diff | Where-Object { $_.StartsWith('-') -and -not $_.StartsWith('---') } | ForEach-Object { $_.Substring(1) } | Sort-Object -CaseSensitive)
    $added = @($diff | Where-Object { $_.StartsWith('+') -and -not $_.StartsWith('+++') } | ForEach-Object { $_.Substring(1) } | Sort-Object -CaseSensitive)
    if ($effectiveStage -eq 'records' -or $completed) {
        $oldLines = [Collections.Generic.List[string]]::new([string[]] $removed)
        $newLines = [Collections.Generic.List[string]]::new([string[]] $added)
        Require ($oldLines.Remove('    [Fact(Skip = "Waits for #404: later runs of a sequential-awaited record.")]') -and
                 $newLines.Remove('    [Fact]')) 'CallSiteProbeTests.cs must replace the #404 Skip line with [Fact]'
        Require ($oldLines.Count -eq $newLines.Count -and ($oldLines -join "`n") -ceq ($newLines -join "`n")) 'CallSiteProbeTests.cs must only move lines and replace the #404 Skip line'
    }
    else {
        Require ($removed.Count -eq $added.Count -and ($removed -join "`n") -ceq ($added -join "`n")) 'CallSiteProbeTests.cs must only move lines'
    }
    Write-Host 'scope passed'
}

# Every field but the generator line is compared, naming changed members on refusal.
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
    $null = Run-Tests 'models' 'Core.Tests.DemoGeneratedModelsTests'
    $files = @(& git ls-files -- $generated)
    Require ($LASTEXITCODE -eq 0 -and $files.Count -gt 0) "Cannot list the committed files under $generated"
    $untracked = @(& git ls-files --others --exclude-standard -- $generated)
    Require ($LASTEXITCODE -eq 0 -and $untracked.Count -eq 0) "The re-record added files: $($untracked -join ', ')"
    foreach ($file in $files) {
        Require (Test-Path -LiteralPath $file) "The re-record removed $file"
        $head = (& git show "HEAD:plugins/concurrency-hunter/$file") -join "`n"
        Require ($LASTEXITCODE -eq 0) "Cannot read the HEAD $file"
        $members = Changed-Members $head (Get-Content -LiteralPath $file -Raw)
        $members | ForEach-Object { "${file}: $_" } | Add-Content -LiteralPath $script:log
        Require ($members.Count -eq 0) "$file changed beyond its generator line: $($members -join '; ')"
        $lines = @(& git diff --no-color -U0 HEAD -- $file 2>$null | Where-Object { $_ -match '^[+-]' -and $_ -notmatch '^(\+\+\+|---)' })
        Require ($LASTEXITCODE -eq 0) "Cannot read the diff of $file"
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
    $stamp = "$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))-$([Guid]::NewGuid().ToString('N'))"
    $folders = @{
        callsite = Join-Path $root "scratch/h3e/probe-$stamp"
        handles = Join-Path $root "scratch/h3b/h3e-probe-$stamp"
    }
    foreach ($folder in $folders.Values) { Require (-not (Test-Path -LiteralPath $folder)) "Measurement folder already exists: $folder" }
    $env:CH_H3_PROBE_OUT = $folders.callsite
    $env:CH_H3B_PROBE_OUT = $folders.handles
    try {
        $results = Run-TestResults 'measure' 'FullyQualifiedName~Engine.CallSiteProbeTests|FullyQualifiedName~Engine.HandleExecutionProbeTests'
    }
    finally {
        Remove-Item Env:CH_H3_PROBE_OUT -ErrorAction SilentlyContinue
        Remove-Item Env:CH_H3B_PROBE_OUT -ErrorAction SilentlyContinue
    }
    $facts = @{
        'CallSiteProbeTests.Demo_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records' = 'Skipped'
        'CallSiteProbeTests.EShop_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records' = 'Passed'
        'CallSiteProbeTests.EShop_with_records_withheld_and_nothing_admitted_differs_from_the_records' = 'Passed'
        'HandleExecutionProbeTests.EShop_with_polly_bodies_admitted_has_exactly_the_findings_of_the_records' = 'Passed'
        'CallSiteProbeTests.Demo_with_records_withheld_and_nothing_admitted_differs_from_the_records' = '*'
        'HandleExecutionProbeTests.Demo_with_polly_bodies_admitted_loses_the_joined_retry_finding_and_keeps_the_records' = '*'
    }
    if ($effectiveStage -eq 'records' -or $completed) {
        foreach ($fact in @($facts.Keys)) { $facts[$fact] = 'Passed' }
        Require ($script:testExit -eq 0) "measure exited $script:testExit"
    }
    Require ($results.Count -eq $facts.Count) "measure needs exactly six facts; found $($results.Count)"
    foreach ($fact in $facts.Keys) {
        $found = @($results | Where-Object { $_.testName -ceq "ConcurrencyHunter.Core.Tests.Engine.$fact" })
        Require ($found.Count -eq 1) "measure needs exactly one result for $fact"
        $outcome = if ($found[0].outcome -eq 'NotExecuted') { 'Skipped' } else { $found[0].outcome }
        Require ($facts[$fact] -eq '*' -or $outcome -ceq $facts[$fact]) "$fact needs $($facts[$fact]); found $outcome"
    }
    $shifts = [Collections.Generic.List[string]]::new()
    foreach ($kind in @('callsite', 'handles')) {
        $baseline = Join-Path $root $baselineFolders[$kind]
        $names = @('demo-records', 'demo-admitted', 'eshop-records', 'eshop-admitted')
        if ($kind -eq 'callsite') { $names += @('demo-control', 'eshop-control') }
        $wanted = @{}
        foreach ($name in $names) { $wanted[$name] = Read-Triples (Join-Path $baseline "$name.json") }
        Require ($wanted['demo-records'].Count -eq 277 -and $wanted['demo-admitted'].Count -eq 278 -and
                 $wanted['eshop-records'].Count -eq 2 -and $wanted['eshop-admitted'].Count -eq 2) "$kind baseline has unexpected counts"
        if ($kind -eq 'callsite') {
            Require ($wanted['demo-control'].Count -eq 281 -and $wanted['eshop-control'].Count -eq 36) 'callsite baseline has unexpected control counts'
        }
        else {
            Require (-not (Test-Path -LiteralPath (Join-Path $baseline 'demo-control.json')) -and
                     -not (Test-Path -LiteralPath (Join-Path $baseline 'eshop-control.json'))) 'handles baseline must have no control run'
        }
        try { Compare-Triples "$kind baseline demo-admitted" @($wanted['demo-records'] + $residual | Sort-Object) $wanted['demo-admitted'] }
        catch { $shifts.Add($_.Exception.Message) }
        foreach ($name in $names) {
            $expected = if ($name -eq 'demo-records') { $wanted['demo-admitted'] } else { $wanted[$name] }
            try { Compare-Triples "$kind $name" $expected (Read-Triples (Join-Path $folders[$kind] "$name.json")) }
            catch { $shifts.Add($_.Exception.Message) }
        }
        Write-Host "$kind measurements: $($folders[$kind])"
    }
    $residuals = @((Get-Content -LiteralPath (Join-Path $folders.handles 'demo-residuals.json') -Raw | ConvertFrom-Json).findings)
    Require ($residuals.Count -eq 0) "demo-residuals.json must list no finding; it lists $(@($residuals.fingerprint) -join ', ')"
    Require ($shifts.Count -eq 0) ($shifts -join '; ')
}

function Write-Inventory {
    $results = Run-TestResults 'inventory' 'FullyQualifiedName!~Engine.CallSiteProbeTests&FullyQualifiedName!~Engine.HandleExecutionProbeTests'
    $passed = @($results | Where-Object outcome -eq 'Passed').Count
    $failed = @($results | Where-Object outcome -eq 'Failed').Count
    $skipped = @($results | Where-Object outcome -eq 'NotExecuted').Count
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# H3e probe inventory')
    $lines.Add('')
    $lines.Add("Passed: $passed; Failed: $failed; Skipped: $skipped")
    $lines.Add('')
    foreach ($result in @($results | Where-Object outcome -eq 'Failed' | Sort-Object testName)) {
        $message = $result.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        $firstLine = if ($null -eq $message) { '' } else { ($message.InnerText -split '\r?\n')[0] }
        $lines.Add("- $($result.testName): $firstLine")
    }
    $file = Join-Path $root 'scratch/h3e/probe-inventory.md'
    [IO.File]::WriteAllLines($file, $lines)
    Write-Host "inventory (reported only): Passed=$passed Failed=$failed Skipped=$skipped; $file"
}

function Check-Expectations {
    $results = Run-TestResults 'expectations' 'FullyQualifiedName!~Engine.CallSiteProbeTests&FullyQualifiedName!~Engine.HandleExecutionProbeTests'
    $failed = @($results | Where-Object outcome -eq 'Failed')
    $failed.testName | Add-Content -LiteralPath $script:log
    Require ($script:testExit -eq 0 -and $failed.Count -eq 0) "Core.Tests has $($failed.Count) Failed results (exit $script:testExit)"
    Write-Host "expectations: $($results.Count) results, no Failed result"
}

function Check-References {
    Start-Check 'references'
    $references = @{
        'AwaitedRunsBoundaryMatrixTests.cs' = '#416'
        'PerPassAdmissionMatrixTests.cs' = '#414'
        'PerPassAdmissionTests.cs' = '#414'
        'PerPassClimbTests.cs' = '#414'
        'PollyRetryAttemptMatrixTests.cs' = '#415'
    }
    foreach ($file in $references.Keys) {
        $path = Join-Path 'src/ConcurrencyHunter.Core.Tests/Engine' $file
        $reference = $references[$file]
        Require ((Get-Content -LiteralPath $path -Raw).Contains($reference)) "$file must name $reference and its cause"
        "$file`: $reference" | Add-Content -LiteralPath $script:log
    }
    Write-Host 'references passed'
}

function Check-Matrix {
    $results = Run-Tests 'placement-matrix' 'Engine.LaterRunsPlacementMatrixTests'
    $script:matrixResults = $results
    $facts = @{
        'There_are_36_cells_covering_every_value_of_every_axis' = 1
        'Every_cell_has_the_first_run_in_the_caller' = 36
        'Every_cell_has_exactly_its_continuation_occurrence' = 36
        'Every_cell_has_exactly_its_pair_after_a_dropped_call' = 36
    }
    Require ($results.Count -eq 109) "Placement matrix needs 109 Passed results; found $($results.Count)"
    foreach ($fact in $facts.Keys) {
        $name = "ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.$fact"
        $found = @($results | Where-Object { $_.testName -ceq $name -or $_.testName.StartsWith("$name(", [StringComparison]::Ordinal) })
        Require ($found.Count -eq $facts[$fact]) "$fact needs $($facts[$fact]) Passed results; found $($found.Count)"
    }
    $pin = Run-Tests 'issue408' 'Engine.ExecutionStartsMatrixTests.Two_instances_of_one_body_at_one_operation_give_one_access'
    Require ($pin.Count -eq 1) "The issue 408 pin needs one Passed result; found $($pin.Count)"
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

function Check-Snapshots {
    $cli = 'src/ConcurrencyHunter.Cli.Tests/ConcurrencyHunter.Cli.Tests.csproj'
    $demo = Run-Tests 'demo-snapshot' 'MetricsCommandTests.Demo_measurement_matches_the_snapshot' $cli
    Require ($demo.Count -eq 1) 'Demo snapshot needs one Passed result'
    $eshop = Run-Tests 'eshop-snapshot' 'EShopSnapshotTests' $cli
    Require ($eshop.Count -eq 1) 'eShop snapshot needs one Passed result'
}

function Check-Records {
    Check-Snapshots
    Run-Command 'baseline-rerecord' pwsh @('-NoProfile', '-File', './build/check-baseline-rerecord.ps1')
    $baseline = @(Get-Content -LiteralPath 'build/test-baseline.txt')
    $facts = @($script:matrixResults | ForEach-Object { $_.testName }) + @(
        'ConcurrencyHunter.Core.Tests.Engine.CallSiteProbeTests.Demo_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records',
        'ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Two_instances_of_one_body_at_one_operation_give_one_access'
    )
    foreach ($fact in $facts) {
        $found = @($baseline | Where-Object { ($_ -split "`t")[0] -ceq $fact })
        Require ($found.Count -eq 1 -and $found[0].EndsWith("`tPassed")) "Baseline needs one Passed row for $fact"
    }
    Start-Check 'snapshot-ledger'
    $ledger = @(Get-Content -LiteralPath 'scratch/h3e/snapshots.md' | ForEach-Object {
        if ($_ -match '^\s*\|?\s*`?([^|`]+?)`?\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*(.+?)\s*\|?\s*$') {
            [pscustomobject]@{ Key = $Matches[1].Trim(); Old = $Matches[2].Trim(); New = $Matches[3].Trim(); Cause = $Matches[4].Trim(' ', '|') }
        }
    })
    foreach ($name in @('demo', 'eshop')) {
        $path = "src/ConcurrencyHunter.Cli.Tests/Snapshots/$name.json"
        $head = (& git show "HEAD:plugins/concurrency-hunter/$path") -join "`n"
        Require ($LASTEXITCODE -eq 0) "Cannot read the HEAD $path"
        $current = Get-Content -LiteralPath $path -Raw
        if ($name -eq 'eshop') {
            $old = $head | ConvertFrom-Json
            $new = $current | ConvertFrom-Json
            Require (($old.counts.fingerprints -join "`n") -ceq ($new.counts.fingerprints -join "`n")) 'eShop fingerprints moved'
        }
        $before = Snapshot-Numbers "$name.json" $head
        $after = Snapshot-Numbers "$name.json" $current
        foreach ($key in @(@($before.Keys) + @($after.Keys) | Sort-Object -Unique)) {
            $was = if ($before.ContainsKey($key)) { $before[$key] } else { 'absent' }
            $now = if ($after.ContainsKey($key)) { $after[$key] } else { 'absent' }
            if ($was -ceq $now) { continue }
            "$key`t$was`t$now" | Add-Content -LiteralPath $script:log
            $row = @($ledger | Where-Object { $_.Key -ceq $key -and $_.Old -ceq $was -and $_.New -ceq $now -and $_.Cause.Length -gt 0 })
            Require ($row.Count -gt 0) "scratch/h3e/snapshots.md lacks the row $key, $was -> $now, with its cause"
        }
    }
    Write-Host 'records passed'
}

function Check-Docs {
    Run-Command 'docs-policy' pwsh @('-NoProfile', '-File', './build/check-docs.ps1')
    Start-Check 'docs'
    $adr = '0036-the-later-runs-of-a-sequential-awaited-call-start-in-its-continuation.md'
    $spec = Get-Content -LiteralPath 'docs/SPEC.md' -Raw
    $header = ($spec -split '(?m)^## 1\.')[0]
    Require ($header.Contains("[ADR 0036](adr/$adr)")) 'SPEC header must absorb ADR 0036'
    $placement = [regex]::Match($spec, '(?s)\*\*TD-034a\.\*\*(.*?)(?=\*\*TD-034b\.\*\*)').Value
    Require ($placement.Contains('продолжении вызова')) 'SPEC TD-034a must state placement in the call continuation'
    $plan = Get-Content -LiteralPath 'docs/PLAN.md' -Raw
    $row = @($plan -split '\r?\n' | Where-Object { $_.StartsWith('| **5d run H3e** |') })
    Require ($row.Count -eq 1 -and $row[0].Contains('(runs/5d-H3e.md)') -and $row[0].Contains("(adr/$adr)")) 'PLAN must link the H3e record and ADR 0036 in its row'
    $limits = [regex]::Match($plan, '(?s)## 3\. .*?(?=\r?\n## 4\.)').Value
    foreach ($reference in @('#413', '#414', '#415', '#416')) {
        $limit = @($limits -split '\r?\n' | Where-Object { $_.StartsWith('- ') -and $_.Contains($reference) })
        Require ($limit.Count -eq 1) "PLAN needs one temporary limit for $reference"
    }
    $record = Get-Content -LiteralPath 'docs/runs/5d-H3e.md' -Raw
    foreach ($word in @('0d8df6303781e5e8', '277', '278', 'LaterRunsPlacementMatrixTests')) {
        Require ($record.Contains($word)) "The run record must name $word"
    }
    foreach ($file in @(& git ls-files -- $generated)) {
        $generator = (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).generator
        Require ($record.Contains($generator)) "The run record must name the current generator of $file"
    }
    Require ($LASTEXITCODE -eq 0) 'Cannot list demo models for the run record'
    $head = @(& git show 'HEAD:plugins/concurrency-hunter/build/test-baseline.txt')
    Require ($LASTEXITCODE -eq 0) 'Cannot read the HEAD test baseline'
    $current = @(Get-Content -LiteralPath 'build/test-baseline.txt')
    $oldNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $newNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $head) { $null = $oldNames.Add(($line -split "`t")[0]) }
    foreach ($line in $current) { $null = $newNames.Add(($line -split "`t")[0]) }
    foreach ($name in $oldNames) {
        if (-not $newNames.Contains($name)) { Require ($record.Contains($name)) "The run record must name the removed baseline case $name" }
    }
    foreach ($name in $newNames) {
        if (-not $oldNames.Contains($name)) { Require ($record.Contains($name)) "The run record must name the added baseline case $name" }
    }
    foreach ($status in @('Passed', 'Skipped')) {
        $was = @($head | Where-Object { $_.EndsWith("`t$status") }).Count
        $now = @($current | Where-Object { $_.EndsWith("`t$status") }).Count
        Require ($record.Contains("$was → $now")) "The run record must name baseline $status totals $was → $now"
    }
    Require ($record.Contains("$($head.Count) → $($current.Count)")) 'The run record must name the baseline total before and after'
    foreach ($line in Get-Content -LiteralPath 'scratch/h3e/snapshots.md') {
        if ($line -match '^\| ((?:demo|eshop)\.json/[^|]+) \| ([^|]+) \| ([^|]+) \|') {
            $key = [regex]::Escape($Matches[1].Trim())
            $was = [regex]::Escape($Matches[2].Trim())
            $now = [regex]::Escape($Matches[3].Trim())
            Require ($record -match "(?m)^\| $key \| $was \| $now \| [^|\r\n]*\S[^|\r\n]* \|\r?`$") "The run record must include the snapshot ledger row with its cause: $line"
        }
    }
    $guide = Get-Content -LiteralPath 'docs/concurrency-hunter-guide.ru.html' -Raw
    $invokeNow = [regex]::Match($guide, '(?s)<tr><td><code>invoke-now</code>.*?</tr>').Value
    Require ($invokeNow.Contains('продолжении вызова') -and $invokeNow.Contains('sequential-awaited')) 'The guide invoke-now row must place later runs in the call continuation'
    $comments = @{
        'src/ConcurrencyHunter.Analysis/Heap/WholeProgram.cs' = 'RunNow'
        'src/ConcurrencyHunter.Analysis/Execution/HappensBefore.cs' = 'AnchorPoint'
        'src/ConcurrencyHunter.Analysis/Execution/ExecutionModel.cs' = 'CreationExecutions'
    }
    foreach ($file in $comments.Keys) {
        $member = $comments[$file]
        $text = Get-Content -LiteralPath $file -Raw
        $pattern = '(?m)(?<doc>(?:^[\t ]*///[^\r\n]*\r?\n)+)[\t ]*private [^\r\n]*\b' + $member + '\('
        $comment = [regex]::Match($text, $pattern).Groups['doc'].Value
        Require ($comment.Contains('continuation') -and $comment.Contains('later run') -and $comment.Contains('sequential-awaited')) "$member must document later runs in the call continuation"
        [xml] $xml = '<doc>' + ($comment -replace '(?m)^[\t ]*/// ?', '') + '</doc>'
        Require (-not [string]::IsNullOrWhiteSpace($xml.SelectSingleNode('/doc/summary').InnerText)) "$member must have a complete summary"
        $parameters = if ($member -eq 'RunNow') { @('caller', 'invocation', 'callee', 'relation') }
                      elseif ($member -eq 'AnchorPoint') { @('anchor') } else { @('region') }
        foreach ($parameter in $parameters) {
            Require (@($xml.doc.param | Where-Object { $_.name -ceq $parameter -and -not [string]::IsNullOrWhiteSpace($_.InnerText) }).Count -eq 1) "$member must document parameter $parameter"
        }
    }
    Write-Host 'docs passed'
}

$variables = @('CH_MODEL_EVALS', 'CH_MODEL_EVALS_RECORD', 'CH_H3B_PROBE_OUT', 'CH_H3_PROBE_OUT')
$saved = @{}
foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $root
try {
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    Check-Environment
    if ($effectiveStage -eq 'docs') {
        Check-Scope
        Check-Docs
    }
    else {
        Run-Command 'build' dotnet @('build', 'src/ConcurrencyHunter.slnx', '-c', 'Release', '--nologo', '--disable-build-servers', '-nodeReuse:false')
        Check-Scope
        Check-Models
        Check-Measurements
        $null = Run-Tests 'cancellation' 'Engine.CancellationCoverageTests'
        switch ($effectiveStage) {
            'probe' { Write-Inventory }
            'expectations' {
                Check-Expectations
                Check-References
            }
            'matrix' {
                Check-Expectations
                Check-References
                Check-Matrix
            }
            'records' {
                Check-Expectations
                Check-References
                Check-Matrix
                Check-Records
            }
            'fix' {
                foreach ($class in @('AwaitedRunsBoundaryMatrixTests', 'PerPassAdmissionMatrixTests', 'PerPassAdmissionTests', 'PerPassClimbTests', 'PollyRetryAttemptMatrixTests', 'ExecutionStartsMatrixTests')) {
                    $null = Run-Tests "fix-$class" "Engine.$class"
                }
                Check-Matrix
                Check-Snapshots
            }
            'final' {
                Check-Expectations
                Check-References
                Check-Matrix
                Check-Records
                Check-Docs
            }
        }
    }
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
Write-Output "h3e $effectiveStage gate ok"
exit 0
