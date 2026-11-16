[CmdletBinding()]
param(
    [ValidateSet('quick','selection','coverage','generation','options','docs','close','scope')]
    [string] $Stage = 'quick',
    [switch] $Final
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gateDirectory = Join-Path $root 'scratch/h3-279/gates'
New-Item -ItemType Directory -Force -Path $gateDirectory | Out-Null
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'

function Invoke-Dotnet([string] $Name, [string[]] $Arguments) {
    $log = Join-Path $gateDirectory "$Name.log"
    & dotnet @Arguments *> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 40
        throw "$Name exited $code; log: $log"
    }
    return $log
}

function Invoke-Tests([string] $Name, [string[]] $Classes, [string] $Exclude = '') {
    $results = Join-Path $gateDirectory "$Name-results"
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    Get-ChildItem -LiteralPath $results -Filter '*.trx' -File | Remove-Item -Force
    $filter = ($Classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    if ($Exclude) { $filter = "($filter)&($Exclude)" }
    $log = Invoke-Dotnet $Name @('test', $solution, '-c', 'Release', '--no-build', '--nologo', '--filter', $filter,
        '--logger', 'trx', '--results-directory', $results)
    $files = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -File)
    if ($files.Count -eq 0) { throw "$Name wrote no test results; log: $log" }
    $total = 0
    foreach ($file in $files) {
        [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($test in @($trx.TestRun.Results.UnitTestResult)) {
            if ($null -eq $test) { continue }
            $total++
            if ($test.outcome -ne 'Passed') { throw "$($test.testName): $($test.outcome); log: $log" }
        }
    }
    if ($total -eq 0) { throw "$Name ran no tests; log: $log" }
    Write-Host "$Name passed $total tests; log: $log"
}

function Invoke-Quick {
    $null = Invoke-Dotnet 'quick-build' @('build', $solution, '-c', 'Release', '--nologo', '--disable-build-servers', '-nodeReuse:false')
    $list = Invoke-Dotnet 'quick-list' @('test', $solution, '-c', 'Release', '--no-build', '--list-tests', '--nologo')
    $text = [IO.File]::ReadAllText($list)
    $required = @{ DecidingCallTests = 12; ProducerCallTests = 11; AdmissionRoundTests = 8; CallSiteBoundTests = 4; CallSiteAdmissionTests = 13 }
    foreach ($name in $required.Keys) {
        $count = [regex]::Matches($text, "(?m)^\s+ConcurrencyHunter\.Core\.Tests\.Engine\.$name\.").Count
        if ($count -lt $required[$name]) { throw "$name lists $count tests, fewer than $($required[$name]); log: $list" }
    }
    Invoke-Tests 'quick-tests' @(
        'Engine.DecidingCallTests', 'Engine.ProducerCallTests', 'Engine.AdmissionRoundTests', 'Engine.CallSiteBoundTests', 'Engine.CallSiteCoverageTests',
        'Engine.CallSiteAdmissionTests', 'Engine.CallSiteContextMatrixTests', 'Engine.TaskForwarderJoinMatrixTests',
        'Engine.LibraryInternalObjectTests', 'Engine.LibraryInternalObjectMatrixTests', 'Engine.DecidingGapTests',
        'Engine.GapConfidenceTests', 'Engine.FindingIdentityTests', 'Engine.ScopePipelineTests', 'Engine.ReachableSetTests',
        'Engine.CancellationCoverageTests', 'AnalysisBoundaryTests', 'Providers.RunGenerationPassTests',
        'Providers.GeneratedFateTests', 'Providers.PollyRetryRecordTests', 'Generation.PackageReachabilityTests',
        'ConcurrencyHunter.Cli.Tests.MetricsGenerationOptionMatrixTests'
    )
}

function Invoke-Coverage {
    Invoke-Quick
    $list = Invoke-Dotnet 'coverage-list' @('test', $solution, '-c', 'Release', '--no-build', '--list-tests', '--nologo')
    $text = [IO.File]::ReadAllText($list)
    $count = [regex]::Matches($text, '(?m)^\s+ConcurrencyHunter\.Core\.Tests\.Engine\.CallSiteCoverageTests\.').Count
    if ($count -ne 13) { throw "CallSiteCoverageTests lists $count tests, expected 13; log: $list" }
    Invoke-Tests 'coverage-tests' @('Engine.CallSiteCoverageTests', 'Engine.DecidingGapTests', 'ReportSkeletonTests',
        'SemanticGapTests', 'MetricsCommandTests') 'FullyQualifiedName!~Demo_measurement_matches_the_snapshot&FullyQualifiedName!~EShop_measurement_matches_the_snapshot'
}

function Read-Run([string] $Directory, [string] $Name) {
    $path = Join-Path $Directory "$Name.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing measurement $path" }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Invoke-Generation {
    Invoke-Quick
    Invoke-Tests 'generation-tests' @('ConcurrencyHunter.Cli.Tests.ModelGenerationModeTests', 'MetricsGenerationOptionMatrixTests',
        'OfflineOptionTests', 'MetricsCommandTests',
        'Providers.ModelGenerationModeTests', 'Providers.RunGenerationTests', 'Providers.RunGenerationPassTests',
        'Providers.GenerationRoundMatrixTests', 'Engine.WorkCancellationTests', 'Providers.GeneratedFileVersionMatrixTests',
        'Providers.RunOnlyAnswerTests', 'Providers.SourceCodeStatisticsTests', 'Providers.SourcePreparationTests', 'Providers.SourceRequestFailureTests') `
        'FullyQualifiedName!~Demo_measurement_matches_the_snapshot&FullyQualifiedName!~EShop_measurement_matches_the_snapshot'
    $obsolete = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Select-String -CaseSensitive -Pattern 'RUN_LIMIT|GenerationBudget\.Run\b')
    if ($obsolete.Count -gt 0) { throw "Removed run generation budget remains: $($obsolete -join ', ')" }
    $agents = [IO.File]::ReadAllText((Join-Path $root 'AGENTS.md'))
    if ($agents -match '(?is)`on`\s*(?:\(the default\)|is (?:the )?default)|default\s+(?:is\s+)?`on`|generat(?:e|es|ion|ing)\s+by default') {
        throw 'AGENTS.md still claims that normal metrics runs default to generation.'
    }
}

function Invoke-Docs {
    $log = Join-Path $gateDirectory 'docs.log'
    & pwsh -NoProfile -File (Join-Path $root 'build/check-docs.ps1') *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log -Tail 40
        throw "check-docs exited $LASTEXITCODE; log: $log"
    }
    $spec = [IO.File]::ReadAllText((Join-Path $root 'docs/SPEC.md'))
    $prd = [IO.File]::ReadAllText((Join-Path $root 'docs/PRD.md'))
    foreach ($document in @(@{ Name = 'SPEC'; Text = $spec }, @{ Name = 'PRD'; Text = $prd })) {
        foreach ($pattern in @('Generate`?\s*\(MCP-ран и', '900 с времени внутри генераций за ран',
            'ран генерирует генератором', 'schemaVersion`?\s*`?\s*1\.6', 'сгенерированные плагин выводит сам',
            'сгенерированные в (?:run|ране) модели')) {
            if ($document.Text -match $pattern) { throw "$($document.Name) retains obsolete generation or schema text: $pattern" }
        }
    }
    foreach ($required in @('callSiteAnalysis', '1.7', 'last-round', 'no-answer', 'timings.analyses[].seconds')) {
        if (-not $spec.Contains($required)) { throw "SPEC lacks $required." }
    }
    $source = [IO.File]::ReadAllText((Join-Path $root 'src/ConcurrencyHunter.Core/Analysis/CallSiteRounds.cs'))
    $constant = [regex]::Match($source, 'private const int CALL_SITE_BODY_BOUND = (\d+);')
    if (-not $constant.Success) { throw 'CALL_SITE_BODY_BOUND was not found.' }
    $bound = $constant.Groups[1].Value
    $callSite = [regex]::Match($spec, '(?ms)^\*\*TD-034d\.\*\*.*?(?=^\*\*TD-035\.)').Value
    if ($callSite -notmatch "CALL_SITE_BODY_BOUND``?, $bound библиотечных тел") {
        throw "SPEC call-site bound must equal CALL_SITE_BODY_BOUND ($bound)."
    }
    $adr = [IO.File]::ReadAllText((Join-Path $root 'docs/adr/0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md'))
    $amendment = [regex]::Match($adr, '(?ms)^## Amendment[^\r\n]*the call is admitted, the bound counts the call graph, and coverage names each call.s answer.*').Value
    if ($amendment -notmatch "The bound is $bound bodies\.") {
        throw "ADR 0029's last amendment must record the measured bound ($bound bodies)."
    }

    # Follow the normal job's directed edges: generation has only recording/census as its entry.
    $architecture = [regex]::Match($spec, '(?s)```mermaid\s+flowchart LR\s+(.*?)```').Groups[1].Value
    if (-not $architecture) { throw 'SPEC lacks the job architecture diagram.' }
    $edges = @{}
    foreach ($edge in [regex]::Matches($architecture, '(?m)^\s*(\w+)(?:\[[^\r\n]*?\]|\([^\r\n]*?\))?\s*-->\s*(?:\|[^|\r\n]*\|)?\s*(\w+)')) {
        $from = $edge.Groups[1].Value
        $edges[$from] = @($edges[$from]) + $edge.Groups[2].Value
    }
    $pending = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending.Enqueue('A')
    while ($pending.Count -gt 0) {
        $node = $pending.Dequeue()
        if (-not $seen.Add($node)) { continue }
        if ($node -eq 'MG') { throw 'The normal job architecture still invokes the model generator.' }
        foreach ($next in $edges[$node]) { if ($next) { $pending.Enqueue($next) } }
    }
    if (-not $seen.Contains('CA') -or $architecture -notmatch 'REC\[Recording / census\] --> MG\[') {
        throw 'The architecture must show call-site analysis in the job and generation under recording/census.'
    }

    $flow = [regex]::Match($spec, '(?ms)^## 6\..*?(?=^## 7\.)').Value
    $previous = -1
    foreach ($step in @('Стабилизация сгенерированного слоя', 'Index и кандидаты', 'Круги допуска',
        'Оставшиеся решающие gaps и resolver', 'Candidate-local refinement')) {
        $position = $flow.IndexOf("**$step.", [StringComparison]::Ordinal)
        if ($position -le $previous) { throw "SPEC section 6 lacks the ordered analysis step: $step." }
        $previous = $position
    }
    if ($flow -notmatch 'три круга допуска' -or $flow -notmatch "граница $bound библиотечных тел" -or
        $flow -notmatch 'Z3 один раз на scope на последнем анализе' -or
        $flow -notmatch 'Только после записей и кругов допуска оставшиеся решающие gaps становятся пакетами') {
        throw 'SPEC section 6 must select deciding checks, bound admission rounds, then packetize remaining gaps and solve the last analysis once.'
    }
    $risk = [regex]::Match($spec, '(?m)^\| Генерация моделей долгая \|[^\r\n]*').Value
    if ($risk -notmatch 'запись' -or $risk -notmatch 'перепись' -or $risk -notmatch 'run только читает' -or
        $risk -match 'первый run') {
        throw 'The generation risk must belong to recording/census; a normal run only reads.'
    }
    $diagnostics = [regex]::Match($prd, '(?ms)^### 6\.4\..*?(?=^## 7\.)').Value
    foreach ($required in @('анализ библиотеки в месте вызова', 'число анализов', 'места производителей',
        'число тел', 'снятых и добавленных пар', 'только в диагностике записи моделей репозитория и переписи фреймворков')) {
        if (-not $diagnostics.Contains($required)) { throw "PRD diagnostics lacks: $required." }
    }
    Write-Host "docs passed; call-site bound: $bound bodies; log: $log"
}

function Get-Triples($Run) {
    return @($Run.findings | ForEach-Object { "$($_.fingerprint)|$($_.rule)|$($_.confidence)" } | Sort-Object)
}

function Assert-Triples($Expected, $Actual, [string] $Name, [switch] $Different) {
    $equal = [string]::Join("`n", @($Expected)) -ceq [string]::Join("`n", @($Actual))
    if ($equal -eq [bool] $Different) { throw "$Name triple comparison failed (different required: $Different)." }
}

function Invoke-Selection {
    Invoke-Quick
    if (-not $env:CH_ESHOP_ROOT) { throw 'selection requires CH_ESHOP_ROOT.' }
    $projects = @(Get-ChildItem -LiteralPath $env:CH_ESHOP_ROOT -Recurse -Filter '*.csproj' -File)
    if ($projects.Count -eq 0) { throw 'eShop has no project files.' }
    foreach ($project in $projects) {
        $assets = Join-Path $project.DirectoryName 'obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assets)) { throw "Missing eShop restore: $assets" }
    }
    $scratch = [IO.Path]::GetFullPath((Join-Path $root 'scratch/h3-279'))
    $selection = [IO.Path]::GetFullPath((Join-Path $scratch 'selection'))
    if (-not $selection.StartsWith($scratch + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Selection path escapes scratch: $selection"
    }
    if (Test-Path -LiteralPath $selection) { Remove-Item -LiteralPath $selection -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $selection | Out-Null
    $before = $env:CH_H3_CALLSITE_OUT
    try {
        $env:CH_H3_CALLSITE_OUT = $selection
        Invoke-Tests 'selection-tests' @('Engine.CallSiteProbeTests', 'Engine.HandleExecutionProbeTests',
            'Engine.CallSitePackageProbeTests', 'DemoExpectationTests', 'EShopSnapshotTests.EShop_measurement_matches_the_snapshot')
    }
    catch {
        $failure = $_
        $lines = [Collections.Generic.List[string]]::new()
        $lines.Add("# H3 selection failed`n`n$failure`n")
        foreach ($file in Get-ChildItem -LiteralPath $selection -Filter '*.json' -File | Sort-Object Name) {
            $run = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $lines.Add("`n## $($file.BaseName)`n")
            foreach ($scope in $run.scopes) {
                $lines.Add("Scope $($scope.scopeId): $($scope.analyses) analyses.")
                foreach ($call in $scope.calls) {
                    $lines.Add("- $($call.callee) at $($call.place); producers: $($call.producers -join ', '); bodies: $($call.bodyCount); round: $($call.round).")
                }
            }
        }
        [IO.File]::WriteAllText((Join-Path $selection 'summary.md'), ($lines -join "`r`n") + "`r`n")
        throw
    }
    finally { $env:CH_H3_CALLSITE_OUT = $before }

    $demoRecords = Read-Run $selection 'call-site-demo-records'
    $demoAdmitted = Read-Run $selection 'call-site-demo-admitted'
    $demoControl = Read-Run $selection 'call-site-demo-control'
    $eshopRecords = Read-Run $selection 'call-site-eshop-records'
    $eshopAdmitted = Read-Run $selection 'call-site-eshop-admitted'
    $eshopControl = Read-Run $selection 'call-site-eshop-control'
    # Independent normal-run baseline from HEAD, including confidence (F-0013).
    $eshopBaseline = @('7c919c7aedc4b38e|DCA1001|High', 'fa3fbc528c46593e|DCA1001|High')

    $maximum = 0
    foreach ($run in @($demoRecords, $demoAdmitted, $eshopAdmitted)) {
        foreach ($scope in $run.scopes) {
            foreach ($call in $scope.calls) { $maximum = [Math]::Max($maximum, $call.bodyCount) }
        }
    }
    if ($maximum -eq 0) { throw 'No admitted call was measured.' }
    $bound = [int]([Math]::Ceiling(4 * $maximum / 500.0) * 500)
    $source = [IO.File]::ReadAllText((Join-Path $root 'src/ConcurrencyHunter.Core/Analysis/CallSiteRounds.cs'))
    $constant = [regex]::Match($source, 'private const int CALL_SITE_BODY_BOUND = (\d+);')
    $summary = [Collections.Generic.List[string]]::new()
    $summary.Add("# H3 selection`n`nLargest call: $maximum bodies. Bound: $bound bodies.`n")
    foreach ($comparison in @(
        @{ Name = 'Demo admitted vs records'; Expected = (Get-Triples $demoRecords); Actual = (Get-Triples $demoAdmitted) },
        @{ Name = 'Demo control vs records'; Expected = (Get-Triples $demoRecords); Actual = (Get-Triples $demoControl) },
        @{ Name = 'eShop admitted vs records'; Expected = (Get-Triples $eshopRecords); Actual = (Get-Triples $eshopAdmitted) },
        @{ Name = 'eShop control vs records'; Expected = (Get-Triples $eshopRecords); Actual = (Get-Triples $eshopControl) },
        @{ Name = 'Normal eShop vs independent HEAD baseline'; Expected = $eshopBaseline; Actual = (Get-Triples $eshopRecords) }
    )) {
        $equal = [string]::Join("`n", @($comparison.Expected)) -ceq [string]::Join("`n", @($comparison.Actual))
        $summary.Add("$($comparison.Name): equal = $equal.")
    }
    foreach ($file in Get-ChildItem -LiteralPath $selection -Filter '*.json' -File | Sort-Object Name) {
        $run = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $summary.Add("`n## $($file.BaseName)`n")
        foreach ($scope in $run.scopes) {
            $summary.Add("Scope $($scope.scopeId): $($scope.analyses) analyses.")
            foreach ($call in $scope.calls) {
                if ($call.round -lt 1 -or $call.round -gt 3) { throw "$($file.Name) has admission round $($call.round)." }
                if ($call.bodyCount -ge $bound) { throw "$($call.callee) has $($call.bodyCount) bodies, outside measured bound $bound." }
                $summary.Add("- $($call.callee) at $($call.place); producers: $($call.producers -join ', '); bodies: $($call.bodyCount); round: $($call.round).")
            }
        }
    }
    [IO.File]::WriteAllText((Join-Path $selection 'summary.md'), ($summary -join "`r`n") + "`r`n")
    Assert-Triples (Get-Triples $demoRecords) (Get-Triples $demoAdmitted) 'demo admitted'
    Assert-Triples (Get-Triples $demoRecords) (Get-Triples $demoControl) 'demo control' -Different
    Assert-Triples (Get-Triples $eshopRecords) (Get-Triples $eshopAdmitted) 'eShop admitted'
    Assert-Triples (Get-Triples $eshopRecords) (Get-Triples $eshopControl) 'eShop control' -Different
    Assert-Triples $eshopBaseline (Get-Triples $eshopRecords) 'normal eShop baseline'
    if (-not $constant.Success -or [int] $constant.Groups[1].Value -ne $bound) {
        throw "CALL_SITE_BODY_BOUND must equal $bound (four times $maximum, rounded up to 500)."
    }
    Write-Host "selection passed; summary: $(Join-Path $selection 'summary.md')"
}

function Invoke-Close {
    if (-not $env:CH_ESHOP_ROOT) { throw 'close requires CH_ESHOP_ROOT.' }
    $projects = @(Get-ChildItem -LiteralPath $env:CH_ESHOP_ROOT -Recurse -Filter '*.csproj' -File)
    if ($projects.Count -eq 0) { throw 'eShop has no project files.' }
    foreach ($project in $projects) {
        $assets = Join-Path $project.DirectoryName 'obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assets)) { throw "Missing eShop restore: $assets" }
    }
    $null = Invoke-Dotnet 'close-build' @('build', $solution, '-c', 'Release', '--nologo', '--disable-build-servers', '-nodeReuse:false')
    Invoke-Tests 'close-tests' @('MetricsCommandTests.Demo_measurement_matches_the_snapshot',
        'EShopSnapshotTests.EShop_measurement_matches_the_snapshot', 'DemoGeneratedModelsTests',
        'Engine.DecidingGapTests', 'Engine.CallSiteCoverageTests')
    foreach ($name in @('demo', 'eshop')) {
        $snapshot = [IO.File]::ReadAllText((Join-Path $root "src/ConcurrencyHunter.Cli.Tests/Snapshots/$name.json"))
        if ($snapshot -match '(?i)[a-z]:[\\/]|/bin/') { throw "$name snapshot contains a machine path." }
    }
    $diff = @(& git -C $root diff HEAD -- demo/.concurrency-hunter/models/generated)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compare the generated demo models with HEAD.' }
    foreach ($line in $diff) {
        if ($line -match '^(---|\+\+\+)') { continue }
        if ($line -match '^[+-]' -and $line -notmatch '^[-+]\s+"generator"\s*:') {
            throw "Generated demo model changed outside generator: $line"
        }
    }
    $log = Join-Path $gateDirectory 'close-baseline-rerecord.log'
    & pwsh -NoProfile -File (Join-Path $root 'build/check-baseline-rerecord.ps1') *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log -Tail 40
        throw "check-baseline-rerecord exited $LASTEXITCODE; log: $log"
    }
    $record = Join-Path $root 'docs/runs/5d-H3.md'
    if (-not (Test-Path -LiteralPath $record) -or -not [IO.File]::ReadAllText($record).Contains('H3-L1')) {
        throw 'The run record must exist and name H3-L1.'
    }
    $plan = [IO.File]::ReadAllText((Join-Path $root 'docs/PLAN.md'))
    $row = [regex]::Match($plan, '(?m)^\| \*\*5d run H3\*\* \|[^\r\n]*').Value
    if (-not $row -or $row.Contains('Перенесён')) { throw 'PLAN must record what 5d run H3 delivered.' }
    Write-Host "close passed; baseline log: $log"
}

function Invoke-Scope {
    $status = @(& git -C $root -c core.quotepath=false status --porcelain --untracked-files=all -- .)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the working tree status.' }
    $allowed = @('build/check-phase5d-runH3.ps1', 'build/record-demo-models.ps1', 'build/test-baseline.txt',
        'docs/SPEC.md', 'docs/PRD.md', 'docs/PLAN.md', 'docs/runs/5d-H3.md', 'AGENTS.md', 'CONTEXT.md',
        'docs/adr/0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md')
    $prefix = 'plugins/concurrency-hunter/'
    foreach ($entry in $status) {
        foreach ($path in ($entry.Substring(3) -split ' -> ')) {
            if ($path.StartsWith($prefix, [StringComparison]::Ordinal)) { $path = $path.Substring($prefix.Length) }
            if ($path.StartsWith('src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn/', [StringComparison]::Ordinal)) {
                throw "Built-in library models changed: $path"
            }
            if ($path.StartsWith('src/', [StringComparison]::Ordinal) -or $allowed -ccontains $path) { continue }
            if ($entry.Substring(0, 2) -match 'M' -and $entry.Substring(0, 2) -notmatch '[^ M]' -and
                $path.StartsWith('demo/.concurrency-hunter/models/generated/', [StringComparison]::Ordinal)) { continue }
            throw "Path outside the run's scope: $entry"
        }
    }
    $protected = @('skills/', 'demo/expected-findings.json', 'demo/SCENARIOS.md', 'demo/',
        '.claude-plugin/', '.codex-plugin/', '.cursor-plugin/', ':!demo/.concurrency-hunter/models/generated/')
    & git -C $root diff --quiet HEAD -- @protected
    if ($LASTEXITCODE -ne 0) { throw 'Skills, demo inputs or manifests changed from HEAD.' }
    $trackedScratch = @(& git -C $root ls-files -- scratch/)
    if ($LASTEXITCODE -ne 0 -or $trackedScratch.Count -gt 0) { throw 'scratch/ must contain no tracked files.' }
    Write-Host "scope passed; $($status.Count) working tree paths are allowed."
}

try {
    if ($Final) {
        Invoke-Quick
        Invoke-Selection
        Invoke-Coverage
        Invoke-Generation
        Invoke-Docs
        Invoke-Close
        exit 0
    }
    switch ($Stage) {
        'quick' { Invoke-Quick }
        'selection' { Invoke-Selection }
        'coverage' { Invoke-Coverage }
        'generation' { Invoke-Generation }
        'docs' { Invoke-Docs }
        'close' { Invoke-Close }
        'scope' { Invoke-Scope }
        default { throw "Unknown stage $Stage." }
    }
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
