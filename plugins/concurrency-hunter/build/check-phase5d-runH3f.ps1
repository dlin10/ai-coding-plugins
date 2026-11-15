[CmdletBinding()]
param(
    [ValidateSet('rule', 'matrix', 'close', 'scope')]
    [string] $Stage = 'rule',
    [switch] $Final
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$scratch = Join-Path $root 'scratch'
$logs = Join-Path $scratch 'h3/gates'
$core = 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj'
$cli = 'src/ConcurrencyHunter.Cli.Tests/ConcurrencyHunter.Cli.Tests.csproj'
$filter = 'FullyQualifiedName~Engine.CallSitePackageProbeTests'
$factNames = @(
    'Demo_without_a_call_site_input_is_the_committed_normal_run',
    'Demo_with_polly_core_bodies_admitted_reaches_library_bodies',
    'Pipeline_case_finding_is_unchanged_with_polly_core_bodies_admitted',
    'Demo_with_polly_core_bodies_admitted_has_no_finding_in_polly_core_code'
)
$ruleFactNames = @(
    'Two_library_writes_to_an_object_a_library_static_keeps_make_no_pair',
    'Two_library_writes_to_the_inside_of_a_library_object_the_program_holds_make_no_pair',
    'Two_library_writes_to_a_library_object_the_program_holds_keep_their_pair',
    'A_library_write_and_a_program_write_keep_their_pair',
    'Two_library_writes_to_an_object_the_program_created_keep_their_pair',
    'A_library_object_handed_to_a_program_delegate_keeps_its_pair',
    'A_library_static_field_keeps_its_pair',
    'A_merged_library_object_keeps_its_pair',
    'A_library_list_inside_a_library_object_the_program_holds_makes_no_pair',
    'A_program_list_inside_a_library_internal_object_keeps_its_pair',
    'A_protected_pair_on_a_library_internal_object_is_still_suppressed',
    'A_pair_an_earlier_reason_skips_keeps_its_reason',
    'A_library_struct_that_carries_the_object_is_no_program_holder',
    'A_program_struct_that_carries_the_object_is_a_program_holder'
)
$matrixFactNames = @(
    'Every_cell_is_exact',
    'No_cell_is_skipped_where_its_expectation_pairs',
    'Existing_counts_are_those_of_the_pairing_before_the_rule',
    'Every_value_of_every_axis_occurs_and_the_cells_are_counted',
    'The_inside_of_a_held_library_object_is_the_accepted_narrowing',
    'Skips_and_pairs_do_not_depend_on_the_candidate_index'
)
$specItem = 'оба accesses сделаны телами библиотеки, проанализированной в месте вызова, и трогают library-internal object (`CONTEXT.md`, **Library-internal object**, ADR 0037): это состояние библиотеки, и отвечает за него библиотека'
$specParagraph = '**Library-internal object.** Объект, который при анализе в месте вызова выделило тело библиотеки и на который напрямую не ссылается ни одно место кода программы — ни значение тела программы, ни поле, ячейка или захваченная переменная объекта, созданного программой, либо её статического или внедрённого хранилища, — это library-internal object (`CONTEXT.md`, ADR 0037). Ссылка из объекта, созданного библиотекой, и из статического хранилища библиотеки ссылкой программы не считается. Два accesses, оба сделанные телами библиотеки, к такому объекту не образуют candidate; объект доступа — сама коллекция, когда access трогает коллекцию или её ячейку, иначе объект, которому принадлежит поле. Access кода программы, объект, созданный программой, объект библиотеки, который программа держит сама, статическое хранилище библиотеки и слитый или открытый регион пары сохраняют. Пропущенная пара считается под причиной `library-internal` (`SKIP_LIBRARY_INTERNAL`); эта причина проверяется последней — после остальных причин пропуска и после подавления достаточной защитой.'

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Get-UnexpectedPaths([string[]] $Lines, [string[]] $ModelDiff = @()) {
    $allowed = @(
        'plugins/concurrency-hunter/CONTEXT.md',
        'plugins/concurrency-hunter/docs/adr/0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md',
        'plugins/concurrency-hunter/docs/adr/0037-two-library-accesses-to-an-object-only-the-library-refers-to-do-not-pair.md',
        'plugins/concurrency-hunter/docs/SPEC.md',
        'plugins/concurrency-hunter/build/check-phase5d-runH3f.ps1',
        'plugins/concurrency-hunter/build/test-baseline.txt',
        'plugins/concurrency-hunter/docs/PLAN.md',
        'plugins/concurrency-hunter/docs/runs/5d-H3f.md'
    )
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        foreach ($path in ($line.Substring(3) -split ' -> ')) {
            $path = $path.Trim('"')
            if ($path -cin $allowed) { continue }
            $relative = $path -creplace '^plugins/concurrency-hunter/', ''
            if ($relative -clike 'demo/.concurrency-hunter/models/generated/*') {
                if ($line.Substring(0, 2) -cnotin @(' M', 'M ', 'MM')) { $path }
            }
            elseif ($relative -clike 'src/*' -and
                    $relative -cnotlike 'src/ConcurrencyHunter.Cli.Tests/Snapshots/*' -and
                    $relative -cnotlike 'src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn/*') { continue }
            else { $path }
        }
    }
    $model = ''
    foreach ($line in $ModelDiff) {
        if ($line -cmatch '^diff --git a/(.+) b/(.+)$') { $model = $Matches[2] }
        elseif ($line -cmatch '^[+-]' -and $line -cnotmatch '^(---|\+\+\+) ' -and
                $line -cnotmatch '^[+-]\s*"generator": "[0-9a-f]+",$') { $model }
    }
}

function Get-WorkingTree {
    $lines = @(& git -C $root status --porcelain --untracked-files=all -- .)
    Require ($LASTEXITCODE -eq 0) 'git status failed'
    $diff = @(& git -C $root diff HEAD --unified=0 -- demo/.concurrency-hunter/models/generated)
    Require ($LASTEXITCODE -eq 0) 'git diff of generated models failed'
    Get-UnexpectedPaths $lines $diff | Sort-Object -Unique
}

function Assert-CloseRecord([string] $Record, [string[]] $PlanLines, $Measurements, [string[]] $ModelDiff,
                            [string[]] $BeforeBaseline, [string[]] $AfterBaseline) {
    foreach ($heading in @('Причина', 'Правило', 'Замеры', 'Матрица', 'Модели demo и снимки',
                            'Базис тестов', 'Остатки для каталога', 'Серверные проверки')) {
        Require ($Record -cmatch ('(?m)^## ' + [regex]::Escape($heading) + '\r?$')) "record needs section $heading"
    }
    foreach ($marker in @('278', '1380', '1100', 'issues/418', 'issues/419', 'issues/420', 'issues/417',
                           'befe953c12bf1af5', 'e8ae8a52f8b90cc9')) {
        Require ($Record.Contains($marker, [StringComparison]::Ordinal)) "record needs $marker"
    }
    foreach ($name in @('records', 'admitted')) {
        $measurement = $Measurements[$name]
        Require ($measurement.Status -ceq 'completed') "record source ${name}: $($measurement.Reason)"
        $label = if ($name -ceq 'records') { 'Без допуска' } else { 'Polly.Core 8' }
        $row = '| После | ' + $label + ' | ' + $measurement.Record.findings.Count + ' | ' + $measurement.Record.libraryBodies + ' |'
        Require ($Record -cmatch ('(?m)^' + [regex]::Escape($row))) "record measurement differs: $row"
    }
    foreach ($sign in @('-', '+')) {
        $values = @($ModelDiff | ForEach-Object {
            if ($_ -cmatch ('^' + [regex]::Escape($sign) + '\s*"generator": "([0-9a-f]+)",$')) { $Matches[1] }
        } | Sort-Object -Unique)
        Require ($values.Count -eq 1) "expected one $sign generator value in the model diff"
        $label = if ($sign -ceq '-') { 'до' } else { 'после' }
        Require ($Record.Contains('- ' + $label + ': `' + $values[0] + '`;', [StringComparison]::Ordinal)) "record generator $label differs from the model diff"
    }
    foreach ($baseline in @(@{ Label = 'HEAD'; Lines = $BeforeBaseline }, @{ Label = 'Рабочее дерево'; Lines = $AfterBaseline })) {
        $lines = @($baseline.Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $counts = @(foreach ($outcome in @('Passed', 'Failed', 'Skipped')) {
            @($lines | Where-Object { ($_ -split "`t", 2)[1] -ceq $outcome }).Count
        })
        $row = '| ' + $baseline.Label + ' | ' + ($counts -join ' | ') + ' | ' + $lines.Count + ' |'
        Require ($Record.Contains($row, [StringComparison]::Ordinal)) "record baseline totals differ: $row"
    }
    $oldNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $BeforeBaseline) { $null = $oldNames.Add(($line -split "`t", 2)[0]) }
    foreach ($line in $AfterBaseline) {
        $name = ($line -split "`t", 2)[0]
        if (-not $oldNames.Contains($name)) {
            Require ($Record.Contains($name, [StringComparison]::Ordinal)) "record needs added baseline name $name"
        }
    }
    $rows = @(for ($i = 0; $i -lt $PlanLines.Count; $i++) {
        if ($PlanLines[$i].StartsWith('| **5d run H3f**', [StringComparison]::Ordinal)) { $i }
    })
    Require ($rows.Count -eq 1) 'PLAN needs exactly one H3f row'
    $index = $rows[0]
    Require ($index -gt 0 -and $PlanLines[$index - 1].StartsWith('| **5d run H3e**', [StringComparison]::Ordinal)) 'PLAN H3f must directly follow H3e'
    Require ($PlanLines[$index].Contains('runs/5d-H3f.md') -and $PlanLines[$index].Contains('ADR 0037') -and
             $PlanLines[$index].Contains('adr/0037-')) 'PLAN H3f needs the run record and ADR 0037 links'
    $server = ($Record -csplit '(?m)^## Серверные проверки\r?$')[1]
    Require ($server -cmatch 'финальные G1–G5 запускает host после задачи' -and
             $server.Contains('не объявляет их пройденными')) 'record must leave final host gates pending'
}

function Reset-AttemptDirectory([string] $Directory) {
    $resolved = [IO.Path]::GetFullPath($Directory)
    Require ($resolved.StartsWith($scratch + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "Attempt directory is outside scratch: $resolved"
    # Reject links as well as lexical escapes before recursive removal.
    for ($ancestor = $resolved; $ancestor -and $ancestor -ne $root; $ancestor = Split-Path $ancestor -Parent) {
        if (Test-Path -LiteralPath $ancestor) {
            Require (-not ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) "Attempt directory traverses a link: $ancestor"
        }
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    return $resolved
}

function Get-InvalidPart($Record) {
    if ($Record -isnot [System.Collections.IDictionary]) { return 'record' }
    if ($Record.findings -isnot [array]) { return 'findings' }
    for ($i = 0; $i -lt $Record.findings.Count; $i++) {
        $finding = $Record.findings[$i]
        $prefix = "findings[$i]"
        if ($finding -isnot [System.Collections.IDictionary]) { return $prefix }
        foreach ($key in @('fingerprint', 'rule', 'confidence', 'resource')) {
            if ($finding[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($finding[$key])) { return "$prefix.$key" }
        }
        foreach ($key in @('pipelineCase', 'libraryAccess', 'pipelineFinding')) {
            if ($finding[$key] -isnot [bool]) { return "$prefix.$key" }
        }
        if ($finding.accesses -isnot [array] -or $finding.accesses.Count -ne 2) { return "$prefix.accesses" }
        for ($j = 0; $j -lt 2; $j++) {
            $access = $finding.accesses[$j]
            $part = "$prefix.accesses[$j]"
            if ($access -isnot [System.Collections.IDictionary]) { return $part }
            foreach ($key in @('symbol', 'bodyId', 'file', 'root', 'operation')) {
                if ($access[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($access[$key])) { return "$part.$key" }
            }
            if (($access.line -isnot [long] -and $access.line -isnot [int]) -or $access.line -le 0) { return "$part.line" }
            if ($access.callPath -isnot [array] -or $access.callPath.Count -eq 0) { return "$part.callPath" }
            for ($k = 0; $k -lt $access.callPath.Count; $k++) {
                if ($access.callPath[$k] -isnot [string] -or [string]::IsNullOrWhiteSpace($access.callPath[$k])) { return "$part.callPath[$k]" }
            }
        }
    }
    if (($Record.libraryBodies -isnot [long] -and $Record.libraryBodies -isnot [int]) -or $Record.libraryBodies -lt 0) { return 'libraryBodies' }
    if (($Record.seconds -isnot [double] -and $Record.seconds -isnot [decimal] -and $Record.seconds -isnot [long] -and $Record.seconds -isnot [int]) -or
        $Record.seconds -le 0 -or -not [double]::IsFinite([double] $Record.seconds)) { return 'seconds' }
    return $null
}

function Read-Measurement($Attempt, [string] $Name) {
    $path = Join-Path $Attempt.Directory "demo-$Name.json"
    if (Test-Path -LiteralPath $path) {
        try {
            $record = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path)) -AsHashtable -NoEnumerate
            $invalid = Get-InvalidPart $record
            if ($invalid) { return @{ Status = 'unavailable'; Reason = "invalid record, $invalid"; Record = $null } }
            return @{ Status = 'completed'; Reason = ''; Record = $record }
        }
        catch { return @{ Status = 'unavailable'; Reason = "invalid record, JSON: $($_.Exception.Message)"; Record = $null } }
    }
    $errorPath = Join-Path $Attempt.Directory "demo-$Name.error.txt"
    $reason = if (Test-Path -LiteralPath $errorPath) { [IO.File]::ReadAllText($errorPath).Trim() }
              elseif (-not $Attempt.TestsStarted -and $Attempt.Failure) { $Attempt.Failure }
              else { 'no record written' }
    return @{ Status = 'unavailable'; Reason = $reason; Record = $null }
}

function Read-TestResults($Attempt) {
    $Attempt.TrxFiles = @()
    $Attempt.Results = @()
    $Attempt.ResultsError = ''
    if (-not $Attempt.TestsStarted) { return }
    $Attempt.TrxFiles = @(Get-ChildItem -LiteralPath $Attempt.ResultsDirectory -Filter '*.trx' -Recurse -File)
    try {
        $Attempt.Results = @(foreach ($file in $Attempt.TrxFiles) {
            [xml] $trx = [IO.File]::ReadAllText($file.FullName)
            foreach ($result in $trx.SelectNodes("//*[local-name()='UnitTestResult']")) {
                [pscustomobject] @{ Name = [string] $result.testName; Outcome = [string] $result.outcome }
            }
        })
    }
    catch { $Attempt.ResultsError = "invalid trx: $($_.Exception.Message)" }
}

function Invoke-ProbeTests($Attempt, [scriptblock] $Command) {
    $Attempt.ResultsDirectory = Join-Path $Attempt.LogDirectory 'results'
    New-Item -ItemType Directory -Path $Attempt.ResultsDirectory -Force | Out-Null
    $Attempt.TestLog = Join-Path $Attempt.LogDirectory 'tests.log'
    $env:CH_H3_PACKAGE_PROBE_OUT = $Attempt.Directory
    $Attempt.TestsStarted = $true
    $Attempt.TestExit = -1
    try {
        $global:LASTEXITCODE = 0
        & $Command $Attempt *> $Attempt.TestLog
        $Attempt.TestExit = $LASTEXITCODE
    }
    catch { $Attempt.TestError = $_.Exception.Message }
}

function Assert-ProbeVerdict($Attempt) {
    Read-TestResults $Attempt
    $problems = [Collections.Generic.List[string]]::new()
    if ($Attempt.TrxFiles.Count -eq 0) { $problems.Add("the test process ran and left no results (exit code $($Attempt.TestExit))") }
    if ($Attempt.ResultsError) { $problems.Add($Attempt.ResultsError) }
    if ($Attempt.Results.Count -ne 4) { $problems.Add("expected four fact results, found $($Attempt.Results.Count)") }
    foreach ($name in $factNames) {
        $matches = @($Attempt.Results | Where-Object { ($_.Name -split '\.')[-1] -ceq $name })
        if ($matches.Count -ne 1) { $problems.Add("${name}: expected once, found $($matches.Count)") }
    }
    foreach ($result in $Attempt.Results) {
        if ($result.Outcome -cne 'Passed') { $problems.Add("$($result.Name): $($result.Outcome)") }
    }
    if ($Attempt.TestExit -ne 0) { $problems.Add("test process exit code $($Attempt.TestExit)") }
    if ($Attempt.TestError) { $problems.Add($Attempt.TestError) }
    foreach ($name in @('records', 'admitted')) {
        $measurement = Read-Measurement $Attempt $name
        if ($measurement.Status -ne 'completed') { $problems.Add("${name}: unavailable: $($measurement.Reason)") }
    }
    Require ($problems.Count -eq 0) ($problems -join '; ')
}

function Finding-Row($Finding) {
    return "$($Finding.fingerprint) | $($Finding.rule) | $($Finding.confidence) | $($Finding.resource) | $($Finding.accesses[0].symbol) | $($Finding.accesses[1].symbol)"
}

function Finding-Key($Finding) { return "$($Finding.fingerprint)`t$($Finding.rule)" }

function Write-AttemptSummary($Attempt) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Polly.Core package probe')
    $lines.Add('')
    $lines.Add("started: $($Attempt.Started)")
    $lines.Add("stage: $($Attempt.Stage)")
    $lines.Add("step: $($Attempt.Step)")
    $lines.Add("finished: $([DateTime]::UtcNow.ToString('O'))")
    $lines.Add("outcome: $(if ($Attempt.Failure) { $Attempt.Failure } else { 'passed' })")
    foreach ($step in $Attempt.Steps) { $lines.Add("$($step.Name): $($step.Outcome)") }
    $lines.Add('')
    $lines.Add('## Measurements')
    foreach ($name in @('records', 'admitted')) {
        $measurement = $Attempt.Measurements[$name]
        $lines.Add("${name}: $($measurement.Status)$(if ($measurement.Reason) { ': ' + $measurement.Reason })")
    }
    $lines.Add('')
    $lines.Add('## Tests')
    foreach ($result in $Attempt.RuleResults) { $lines.Add("$($result.Name): $($result.Outcome)") }
    if ($Attempt.RuleFailure) { $lines.Add("rule-tests: $($Attempt.RuleFailure)") }
    if (-not $Attempt.TestsStarted -and $Attempt.RuleResults.Count -eq 0) { $lines.Add('the tests did not start') }
    elseif (-not $Attempt.TestsStarted) { $lines.Add('package-probe tests were not part of this attempt') }
    elseif ($Attempt.TrxFiles.Count -eq 0) { $lines.Add("the test process ran and left no results (exit code $($Attempt.TestExit))") }
    else {
        $lines.Add("the test process left results (exit code $($Attempt.TestExit))")
        foreach ($result in $Attempt.Results) { $lines.Add("$($result.Name): $($result.Outcome)") }
        if ($Attempt.Results.Count -eq 0) { $lines.Add('none') }
        if ($Attempt.ResultsError) { $lines.Add($Attempt.ResultsError) }
    }
    if ($Attempt.TestError) { $lines.Add($Attempt.TestError) }
    $lines.Add('')
    $lines.Add('## Working tree after the attempt')
    if ($Attempt.ScopePaths.Count -eq 0) { $lines.Add('as allowed') }
    else { foreach ($path in $Attempt.ScopePaths) { $lines.Add($path) } }
    $lines.Add('')
    $lines.Add('## Runs')
    $lines.Add('run | findings | library bodies | seconds')
    $lines.Add('--- | ---: | ---: | ---:')
    $completed = 0
    foreach ($name in @('records', 'admitted')) {
        $measurement = $Attempt.Measurements[$name]
        if ($measurement.Status -eq 'completed') {
            $completed++
            $record = $measurement.Record
            $lines.Add("$name | $($record.findings.Count) | $($record.libraryBodies) | $($record.seconds)")
        }
    }
    if ($completed -eq 0) { $lines.Add('none') }
    if ($completed -eq 2) {
        $before = $Attempt.Measurements.records.Record.findings
        $after = $Attempt.Measurements.admitted.Record.findings
        $beforeKeys = @($before | ForEach-Object { Finding-Key $_ })
        $afterKeys = @($after | ForEach-Object { Finding-Key $_ })
        $lines.Add('')
        $lines.Add('## Comparison')
        foreach ($kind in @('Added', 'Lost', 'Changed')) {
            $lines.Add('')
            $lines.Add("### $kind triples")
            $rows = @(switch ($kind) {
                'Added' { $after | Where-Object { (Finding-Key $_) -cnotin $beforeKeys } | ForEach-Object { Finding-Row $_ } }
                'Lost' { $before | Where-Object { (Finding-Key $_) -cnotin $afterKeys } | ForEach-Object { Finding-Row $_ } }
                'Changed' {
                    foreach ($old in $before) {
                        foreach ($new in $after | Where-Object { (Finding-Key $_) -ceq (Finding-Key $old) -and $_.confidence -cne $old.confidence }) {
                            'before: ' + (Finding-Row $old)
                            'after: ' + (Finding-Row $new)
                        }
                    }
                }
            })
            if ($rows.Count -eq 0) { $lines.Add('none') }
            else { foreach ($row in $rows | Sort-Object) { $lines.Add($row) } }
        }
    }
    $lines.Add('')
    $lines.Add('## Pipeline finding')
    foreach ($name in @('records', 'admitted')) {
        $measurement = $Attempt.Measurements[$name]
        if ($measurement.Status -eq 'completed') {
            $pipeline = @($measurement.Record.findings | Where-Object pipelineFinding)
            if ($pipeline.Count -eq 0) { $lines.Add("${name}: none") }
            foreach ($finding in $pipeline) {
                $lines.Add("${name}: $(Finding-Row $finding) | fingerprint still 687f86f10b06fac5: $($finding.fingerprint -ceq '687f86f10b06fac5')")
            }
        }
        else { $lines.Add("${name}: unavailable: $($measurement.Reason)") }
    }
    foreach ($name in @('records', 'admitted')) {
        $lines.Add('')
        $label = if ($name -eq 'admitted') { 'Admitted' } else { 'Records' }
        $lines.Add("## $label findings with a Polly.Core access")
        $measurement = $Attempt.Measurements[$name]
        if ($measurement.Status -eq 'completed') {
            $library = @($measurement.Record.findings | Where-Object libraryAccess | Sort-Object fingerprint)
            if ($library.Count -eq 0) { $lines.Add('none') }
            foreach ($finding in $library) {
                $lines.Add("$(Finding-Row $finding) | pipeline case: $($finding.pipelineCase)")
                foreach ($access in $finding.accesses) {
                    $lines.Add("  $($access.symbol) at $($access.file):$($access.line); root: $($access.root); body: $($access.bodyId)")
                }
            }
        }
        else { $lines.Add("unavailable: $($measurement.Reason)") }
    }
    $lines.Add('')
    $lines.Add('## Files')
    foreach ($file in Get-ChildItem -LiteralPath $Attempt.Directory -File | Sort-Object Name) { $lines.Add($file.Name) }
    if ($Attempt.TestLog) { $lines.Add($Attempt.TestLog) }
    if ($Attempt.ResultsDirectory) { $lines.Add($Attempt.ResultsDirectory) }
    $lines.Add("gate logs: $($Attempt.LogDirectory)")
    [IO.File]::WriteAllLines((Join-Path $Attempt.Directory 'summary.md'), $lines)
}

function Invoke-Attempt([string] $Directory, [object[]] $Steps, [scriptblock] $Scope = { Get-WorkingTree }) {
    $directory = Reset-AttemptDirectory $Directory
    $attempt = @{
        Directory = $directory; Started = [DateTime]::UtcNow.ToString('O'); Step = ''; Failure = ''
        LogDirectory = Join-Path $logs ([Guid]::NewGuid().ToString('N'))
        TestsStarted = $false; TestExit = $null; TestError = ''; TestLog = ''; ResultsDirectory = ''
        Results = @(); TrxFiles = @(); ResultsError = ''; ScopePaths = @(); Measurements = @{}
        RuleResults = @(); RuleFailure = ''; Steps = @(); Stage = $(if ($Final) { 'Final' } else { $Stage })
    }
    $previousOutput = $env:CH_H3_PACKAGE_PROBE_OUT
    try {
        New-Item -ItemType Directory -Path $attempt.LogDirectory -Force | Out-Null
        foreach ($step in $Steps) {
            $attempt.Step = $step.Name
            $result = @{ Name = $step.Name; Outcome = 'in progress' }
            $attempt.Steps += $result
            [IO.File]::WriteAllText((Join-Path $directory 'summary.md'), "started: $($attempt.Started)`nstep: $($step.Name) — in progress`n")
            $null = & $step.Action $attempt $step
            $result.Outcome = 'passed'
        }
    }
    catch {
        if ($attempt.Steps.Count -gt 0) { $attempt.Steps[-1].Outcome = 'failed' }
        $attempt.Failure = "failed at $($attempt.Step): $($_.Exception.Message)"
    }
    finally {
        $env:CH_H3_PACKAGE_PROBE_OUT = $previousOutput
        try {
            $attempt.ScopePaths = @(& $Scope)
            if ($attempt.ScopePaths.Count -gt 0) {
                $scopeFailure = 'failed at final-scope: unexpected paths: ' + ($attempt.ScopePaths -join ', ')
                $attempt.Failure = if ($attempt.Failure) { $attempt.Failure + '; ' + $scopeFailure } else { $scopeFailure }
            }
        }
        catch {
            $attempt.ScopePaths = @("scope unavailable: $($_.Exception.Message)")
            $attempt.Failure += "; failed at final-scope: $($_.Exception.Message)"
        }
        Read-TestResults $attempt
        foreach ($name in @('records', 'admitted')) {
            $attempt.Measurements[$name] = Read-Measurement $attempt $name
            if ($attempt.Measurements[$name].Reason -like 'invalid record,*' -and -not $attempt.Failure) {
                $attempt.Failure = "failed at $($attempt.Step): ${name}: $($attempt.Measurements[$name].Reason)"
            }
        }
        Write-AttemptSummary $attempt
    }
    return $attempt
}

function New-TestStep([scriptblock] $Command) {
    return @{ Name = 'tests'; Command = $Command; Action = { param($a, $s) Invoke-ProbeTests $a $s.Command } }
}

$verdictStep = @{ Name = 'verdict'; Action = {
    param($a)
    $problems = [Collections.Generic.List[string]]::new()
    if ($a.RuleFailure) { $problems.Add($a.RuleFailure) }
    try { Assert-ProbeVerdict $a } catch { $problems.Add($_.Exception.Message) }
    Require ($problems.Count -eq 0) ($problems -join '; ')
} }

function New-SampleFinding([string] $Fingerprint, [string] $Confidence, [bool] $Pipeline = $false, [bool] $Library = $false, [bool] $PipelineFinding = $false) {
    $symbol = if ($PipelineFinding) { 'Demo.Web.Cases.PollyExecuteInvokesDelegate.PipelineController.Post()' } else { "Sample.$Fingerprint.Run()" }
    return @{
        fingerprint = $Fingerprint; rule = 'DCA1002'; confidence = $Confidence; resource = "di:Sample@Singleton / $Fingerprint"
        pipelineCase = $Pipeline; libraryAccess = $Library; pipelineFinding = $PipelineFinding
        accesses = @(@{
            symbol = $symbol; bodyId = $(if ($Library) { 'body:Polly.Core:Sample' } else { 'body:Demo:Sample' })
            file = 'Sample.cs'; line = 15; root = 'Sample.Root()'; operation = 'read-modify-write'; callPath = @($symbol)
        }, @{
            symbol = $symbol; bodyId = 'body:Demo:Sample'; file = 'Sample.cs'; line = 34
            root = 'Sample.Root()'; operation = 'read-modify-write'; callPath = @($symbol)
        })
    }
}

function New-SampleRecord([object[]] $Findings) { return @{ findings = $Findings; libraryBodies = 1; seconds = 1.25 } }

function Test-AttemptExit {
    $directory = Reset-AttemptDirectory (Join-Path $scratch 'h3/package-probe-exit-selfcheck')
    try {
        $source = [IO.File]::ReadAllText((Join-Path $root 'build/check-phase5d-runH3f.ps1'))
        $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref] $null, [ref] $null)
        $dispatch = ($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$previousLocation' }).Extent.StartOffset
        Require ($dispatch -gt 0) 'self-check (k): dispatch missing'
        $action = '{ param($a, $s) [IO.File]::AppendAllText($script:trace, "step: $($s.Name)`n"); if ($script:failure -eq $s.Name) { throw "prepared step failure" } }'
        # Replace only the work inside stage steps. Keep the actual dispatcher, attempt owner and process boundary.
        $edits = @(foreach ($table in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.HashtableAst] -and $node.Extent.StartOffset -gt $dispatch }, $true)) {
            foreach ($pair in $table.KeyValuePairs) {
                if ($pair.Item1.Extent.Text -eq 'Action') {
                    $block = $pair.Item2.Find({ param($node) $node -is [Management.Automation.Language.ScriptBlockExpressionAst] }, $true)
                    Require ($null -ne $block) 'self-check (k): action missing'
                    @{ Start = $block.Extent.StartOffset; Length = $block.Extent.EndOffset - $block.Extent.StartOffset; Text = $action }
                }
            }
        })
        $rootAssignment = $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$root' }
        $edits += @{ Start = $rootAssignment.Extent.StartOffset; Length = $rootAssignment.Extent.EndOffset - $rootAssignment.Extent.StartOffset
                     Text = '$root = ''' + $directory.Replace("'", "''") + "'" }
        $invoke = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Attempt' }, $false)
        $edits += @{ Start = $invoke.Body.EndBlock.Statements[0].Extent.StartOffset; Length = 0
                     Text = '[IO.File]::AppendAllText($script:trace, "attempt: $Directory`n")' + "`n    " }
        foreach ($edit in $edits | Sort-Object Start -Descending) { $source = $source.Remove($edit.Start, $edit.Length).Insert($edit.Start, $edit.Text) }
        $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref] $null, [ref] $null)
        $dispatch = ($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$previousLocation' }).Extent.StartOffset
        $trace = Join-Path $directory 'trace.txt'
        $child = Join-Path $directory 'gate.ps1'
        $expected = @{
            rule = @('self-check', 'scope', 'spec', 'build', 'list', 'rule-tests', 'tests', 'verdict')
            matrix = @('scope', 'build', 'list', 'matrix-tests', 'rule-tests')
            close = @('scope', 'baseline', 'record')
            scope = @('scope')
            Final = @('self-check', 'scope', 'spec', 'build', 'list', 'rule-tests', 'tests', 'verdict', 'list', 'matrix-tests', 'rule-tests', 'baseline', 'record')
        }
        foreach ($entry in @('rule', 'matrix', 'close', 'scope', 'Final')) {
            foreach ($failure in @('', 'initialization', 'scope', 'finalization') + $(if ($entry -eq 'Final') { @('matrix-tests', 'record') })) {
                $overrides = @(
                    ('$script:trace = ''' + $trace.Replace("'", "''") + "'")
                    ('$script:failure = ''' + $failure + "'")
                    'function Get-WorkingTree { @() }'
                    ('function New-TestStep { @{ Name = ''tests''; Action = ' + $action + ' } }')
                    ('$verdictStep = @{ Name = ''verdict''; Action = ' + $action + ' }')
                )
                if ($failure -eq 'initialization') { $overrides += 'function Reset-AttemptDirectory { throw ''prepared initialization failure'' }' }
                if ($failure -eq 'finalization') { $overrides += 'function Write-AttemptSummary { throw ''prepared finalization failure'' }' }
                $text = $source.Insert($dispatch, ($overrides -join "`n") + "`n")
                [IO.File]::WriteAllText($child, $text.Replace("`r`n", "`n").Replace("`n", "`r`n"))
                if (Test-Path -LiteralPath $trace) { Remove-Item -LiteralPath $trace -Force }
                $arguments = @(if ($entry -eq 'Final') { '-Final' } else { '-Stage'; $entry })
                $log = Join-Path $directory 'process.log'
                & pwsh -NoProfile -File $child @arguments *> $log
                $code = $LASTEXITCODE
                Require (($code -eq 0) -eq ($failure -eq '')) "self-check (k), $entry/${failure}: exit $code; $([IO.File]::ReadAllText($log))"
                $rows = [IO.File]::ReadAllLines($trace)
                $attempts = @($rows | Where-Object { $_.StartsWith('attempt: ', [StringComparison]::Ordinal) })
                Require ($attempts.Count -eq 1) "self-check (k), $entry/${failure}: expected one attempt"
                $attemptDirectory = $attempts[0].Substring('attempt: '.Length)
                if ($entry -eq 'Final') {
                    Require ($attemptDirectory -eq (Join-Path $directory 'scratch/h3/package-probe')) 'self-check (k): final artifacts separated'
                }
                $steps = @($rows | Where-Object { $_.StartsWith('step: ', [StringComparison]::Ordinal) } | ForEach-Object { $_.Substring('step: '.Length) })
                $wanted = $expected[$entry]
                if ($failure -eq 'initialization') { $wanted = @() }
                elseif ($failure -and $failure -ne 'finalization') { $wanted = $wanted[0..([array]::IndexOf($wanted, $failure))] }
                Require (($steps -join ',') -ceq ($wanted -join ',')) "self-check (k), $entry/${failure}: wrong ordered steps: $($steps -join ',')"
                if ($failure -notin @('initialization', 'finalization')) {
                    $summary = [IO.File]::ReadAllText((Join-Path $attemptDirectory 'summary.md'))
                    Require ($summary.Contains("stage: $entry")) "self-check (k), $entry/${failure}: wrong stage"
                    foreach ($name in $wanted) {
                        $outcome = if ($name -eq $failure) { 'failed' } else { 'passed' }
                        Require ($summary.Contains("${name}: $outcome")) "self-check (k), $entry/${failure}: missing step $name"
                    }
                    Require ($summary.Contains($(if ($failure) { "outcome: failed at ${failure}: prepared step failure" } else { 'outcome: passed' }))) "self-check (k), $entry/${failure}: wrong overall outcome"
                }
            }
        }
    }
    finally {
        $directory = Reset-AttemptDirectory $directory
        Remove-Item -LiteralPath $directory -Force
    }
}

function Test-AttemptRecorder {
    $directory = Join-Path $scratch 'h3/package-probe-selfcheck'
    $directory = Reset-AttemptDirectory $directory
    foreach ($name in @('summary.md', 'demo-records.json', 'old.txt')) { [IO.File]::WriteAllText((Join-Path $directory $name), 'old attempt') }
    $cleanScope = { @() }
    $firstStep = @{ Name = 'first-marker'; Action = {
        param($a)
        $files = @(Get-ChildItem -LiteralPath $a.Directory -Force)
        Require ($files.Count -eq 1 -and $files[0].Name -ceq 'summary.md') 'self-check (a): old files survived'
        $marker = [IO.File]::ReadAllText($files[0].FullName)
        Require ($marker.Contains('step: first-marker — in progress') -and $marker.Contains('started: ')) 'self-check (a): first marker missing'
    } }
    $a = Invoke-Attempt $directory @($firstStep) $cleanScope
    Require (-not $a.Failure) "self-check (a): $($a.Failure)"
    $a = Invoke-Attempt $directory @(@{ Name = 'preflight'; Action = { throw 'prepared failure' } }) $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    foreach ($part in @('failed at preflight: prepared failure', 'records: unavailable: failed at preflight: prepared failure',
                         'admitted: unavailable: failed at preflight: prepared failure', 'the tests did not start')) {
        Require ($text.Contains($part)) "self-check (b): missing $part"
    }
    Require (-not $text.Contains('## Comparison')) 'self-check (b): fabricated comparison'

    # The substituted command writes prepared files and returns a native exit code; tests and verdict stay real.
    $command = {
        param($a)
        foreach ($name in @('records', 'admitted')) {
            if ($script:prepared[$name]) {
                [IO.File]::WriteAllText((Join-Path $a.Directory "demo-$name.json"), (ConvertTo-Json -InputObject $script:prepared[$name] -Depth 20))
            }
            if ($script:prepared.Errors -and $script:prepared.Errors[$name]) {
                [IO.File]::WriteAllText((Join-Path $a.Directory "demo-$name.error.txt"), $script:prepared.Errors[$name])
            }
        }
        if ($script:prepared.Error) { [IO.File]::WriteAllText((Join-Path $a.Directory 'demo-admitted.error.txt'), $script:prepared.Error) }
        if ($script:prepared.Trx) {
            $rows = foreach ($name in $factNames) {
                $outcome = if ($name -ceq $script:prepared.FailedFact) { 'Failed' } else { 'Passed' }
                '<UnitTestResult testName="ConcurrencyHunter.Core.Tests.Engine.CallSitePackageProbeTests.' + $name + '" outcome="' + $outcome + '" />'
            }
            [IO.File]::WriteAllText((Join-Path $a.ResultsDirectory 'results.trx'), '<TestRun><Results>' + ($rows -join '') + '</Results></TestRun>')
        }
        & pwsh -NoProfile -Command "exit $($script:prepared.Exit)"
    }
    $steps = @(@{ Name = 'prepared'; Action = {} }, (New-TestStep $command), $verdictStep)
    $script:prepared = @{
        records = New-SampleRecord @((New-SampleFinding 'normal' 'Medium'))
        Error = "System.InvalidOperationException: prepared analysis failure`nprepared stack"
        Trx = $true; FailedFact = $factNames[2]; Exit = 1
    }
    $a = Invoke-Attempt $directory $steps $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    foreach ($part in @('outcome: failed at verdict:', 'records: completed', 'admitted: unavailable: System.InvalidOperationException: prepared analysis failure',
                         "$($factNames[2]): Failed", 'records | 1 | 1 | 1.25')) {
        Require ($text.Contains($part)) "self-check (c): missing $part"
    }
    Require (-not $text.Contains('## Comparison')) 'self-check (c): fabricated comparison'
    $inside = New-SampleFinding 'library-inside' 'High' $true $true
    $outside = New-SampleFinding 'library-outside' 'High' $false $true
    $script:prepared = @{
        records = New-SampleRecord @((New-SampleFinding 'lost' 'High'), (New-SampleFinding '687f86f10b06fac5' 'Medium' $true $false $true), $inside, $outside)
        admitted = New-SampleRecord @((New-SampleFinding 'added' 'High'), (New-SampleFinding '687f86f10b06fac5' 'High' $true $false $true), $inside, $outside)
        Trx = $true; FailedFact = $factNames[3]; Exit = 1
    }
    $a = Invoke-Attempt $directory $steps $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    foreach ($part in @('outcome: failed at verdict:', 'added | DCA1002 | High', 'lost | DCA1002 | High',
                         'before: 687f86f10b06fac5 | DCA1002 | Medium', 'after: 687f86f10b06fac5 | DCA1002 | High',
                         'records: 687f86f10b06fac5 | DCA1002 | Medium', 'admitted: 687f86f10b06fac5 | DCA1002 | High',
                         "$(Finding-Row $inside) | pipeline case: True", "$(Finding-Row $outside) | pipeline case: False", "$($factNames[3]): Failed")) {
        Require ($text.Contains($part)) "self-check (d): missing $part"
    }
    foreach ($name in @('records', 'admitted')) {
        $script:prepared[$name].findings = @($script:prepared[$name].findings | Where-Object fingerprint -ne 'library-inside')
    }
    $script:prepared.FailedFact = ''
    $script:prepared.Exit = 0
    $a = Invoke-Attempt $directory $steps $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    Require (-not $a.Failure -and $text.Contains('outcome: passed')) "self-check (e): $($a.Failure)"
    Require ($text.Contains("$(Finding-Row $outside) | pipeline case: False") -and -not $text.Contains('library-inside')) 'self-check (e): wrong library findings'
    $ruleFailure = @{ Name = 'rule-tests'; Action = {
        param($a)
        $a.RuleFailure = 'prepared rule failure'
        $a.RuleResults = @([pscustomobject] @{ Name = 'Prepared.RuleFact'; Outcome = 'Failed' })
    } }
    $a = Invoke-Attempt $directory @($ruleFailure, (New-TestStep $command), $verdictStep) $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    foreach ($part in @('outcome: failed at verdict: prepared rule failure', 'Prepared.RuleFact: Failed',
                         'records: completed', 'admitted: completed', "$($factNames[0]): Passed")) {
        Require ($text.Contains($part)) "self-check (i): missing $part"
    }
    $null = $script:prepared.admitted.findings[0].accesses[1].Remove('bodyId')
    $a = Invoke-Attempt $directory $steps $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    foreach ($part in @('outcome: failed at verdict:', 'admitted: unavailable: invalid record, findings[0].accesses[1].bodyId')) {
        Require ($text.Contains($part)) "self-check (f): missing $part"
    }
    Require (-not $text.Contains('## Comparison')) 'self-check (f): compared an invalid record'
    foreach ($name in @('records', 'admitted')) {
        $other = if ($name -eq 'records') { 'admitted' } else { 'records' }
        foreach ($key in @('pipelineCase', 'libraryAccess', 'pipelineFinding')) {
            foreach ($invalid in @('missing', 'wrong-type')) {
                $script:prepared = @{
                    records = New-SampleRecord @((New-SampleFinding '687f86f10b06fac5' 'Medium' $true $true $true))
                    admitted = New-SampleRecord @((New-SampleFinding '687f86f10b06fac5' 'Medium' $true $true $true))
                    Trx = $true; Exit = 0
                }
                $finding = $script:prepared[$name].findings[0]
                if ($invalid -eq 'missing') { $null = $finding.Remove($key) }
                else { $finding[$key] = 'true' }
                $a = Invoke-Attempt $directory $steps $cleanScope
                $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
                $reason = "invalid record, findings[0].$key"
                foreach ($part in @('outcome: failed at verdict:', "${name}: unavailable: $reason",
                                     "${other}: completed", "${other}: 687f86f10b06fac5 | DCA1002 | Medium")) {
                    Require ($text.Contains($part)) "self-check (l), $name/$key/${invalid}: missing $part"
                }
                $pipeline = ($text -split '## Pipeline finding', 2)[1] -split '## (Records|Admitted) findings with a Polly.Core access', 2
                Require ($pipeline[0].Contains("${name}: unavailable: $reason") -and
                         -not $pipeline[0].Contains("${name}: none")) "self-check (l), $name/$key/${invalid}: invalid pipeline observation"
                Require ($a.Failure -and -not $text.Contains('## Comparison')) "self-check (l), $name/$key/${invalid}: compared an invalid record or passed"
            }
        }
    }
    $script:prepared = @{ records = New-SampleRecord @((New-SampleFinding 'normal' 'Medium')); Trx = $false; Exit = 3 }
    $a = Invoke-Attempt $directory $steps $cleanScope
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    foreach ($part in @('outcome: failed at verdict:', 'the test process ran and left no results (exit code 3)',
                         'records: completed', 'admitted: unavailable: no record written')) {
        Require ($text.Contains($part)) "self-check (g): missing $part"
    }
    # Per-run observations survive a missing, failed or invalid record from the other run, in either direction.
    foreach ($completed in @('records', 'admitted')) {
        $other = if ($completed -eq 'records') { 'admitted' } else { 'records' }
        foreach ($unavailable in @('missing', 'failed', 'invalid')) {
            $observed = New-SampleFinding '687f86f10b06fac5' 'Medium' $true $true $true
            $script:prepared = @{ Trx = $true; Exit = 1 }
            $script:prepared[$completed] = New-SampleRecord @($observed)
            if ($unavailable -eq 'failed') { $script:prepared.Errors = @{ $other = 'prepared analysis failure' } }
            if ($unavailable -eq 'invalid') { $script:prepared[$other] = @{ findings = 'invalid' } }
            $a = Invoke-Attempt $directory $steps $cleanScope
            $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
            $label = if ($completed -eq 'records') { 'Records' } else { 'Admitted' }
            foreach ($part in @("${completed}: completed", "${other}: unavailable:",
                                 "${completed}: $(Finding-Row $observed)", "## $label findings with a Polly.Core access",
                                 "$(Finding-Row $observed) | pipeline case: True")) {
                Require ($text.Contains($part)) "self-check (j), $completed/${unavailable}: missing $part"
            }
            foreach ($access in $observed.accesses) {
                $part = "$($access.symbol) at $($access.file):$($access.line); root: $($access.root); body: $($access.bodyId)"
                Require ($text.Contains($part)) "self-check (j), $completed/${unavailable}: missing $part"
            }
            Require ($a.Failure -and -not $text.Contains('## Comparison')) "self-check (j), $completed/${unavailable}: fabricated comparison or success"
        }
    }
    $allowed = @(
        ' M plugins/concurrency-hunter/CONTEXT.md',
        ' M plugins/concurrency-hunter/docs/adr/0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md',
        '?? plugins/concurrency-hunter/docs/adr/0037-two-library-accesses-to-an-object-only-the-library-refers-to-do-not-pair.md',
        ' M plugins/concurrency-hunter/docs/SPEC.md',
        '?? plugins/concurrency-hunter/src/ConcurrencyHunter.Analysis/Accesses/LibraryInternalObjects.cs',
        '?? plugins/concurrency-hunter/src/ConcurrencyHunter.Core.Tests/Engine/LibraryInternalObjectTests.cs',
        '?? plugins/concurrency-hunter/src/ConcurrencyHunter.Core.Tests/Engine/CallSitePackageProbeTests.cs',
        '?? plugins/concurrency-hunter/build/check-phase5d-runH3f.ps1',
        ' M plugins/concurrency-hunter/build/test-baseline.txt',
        ' M plugins/concurrency-hunter/docs/PLAN.md',
        '?? plugins/concurrency-hunter/docs/runs/5d-H3f.md',
        ' M plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Package/model.json'
    )
    $modelDiff = @('diff --git a/plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Package/model.json b/plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Package/model.json',
                   '--- a/plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Package/model.json',
                   '+++ b/plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Package/model.json',
                   '-  "generator": "abc123",', '+  "generator": "def456",')
    Require (@(Get-UnexpectedPaths $allowed $modelDiff).Count -eq 0) 'self-check (h): allowed paths rejected'
    foreach ($path in @('demo/expected-findings.json', 'demo/.concurrency-hunter/models/generated/Package/new.json')) {
        $unexpected = @(Get-UnexpectedPaths (@($allowed) + "?? plugins/concurrency-hunter/$path") $modelDiff)
        Require ($unexpected.Count -eq 1 -and $unexpected[0] -ceq "plugins/concurrency-hunter/$path") "self-check (h): missed $path"
    }
    $badDiff = @(Get-UnexpectedPaths $allowed (@($modelDiff) + '+  "model": "changed",'))
    Require ($badDiff.Count -eq 1 -and $badDiff[0] -ceq 'plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Package/model.json') 'self-check (h): model content change missed'
    $script:scopeSample = @($allowed) + ' M plugins/concurrency-hunter/demo/expected-findings.json'
    $unexpected = @(Get-UnexpectedPaths $script:scopeSample)
    Require ($unexpected.Count -eq 1 -and $unexpected[0] -ceq 'plugins/concurrency-hunter/demo/expected-findings.json') 'self-check (h): unexpected path missed'
    $a = Invoke-Attempt $directory @(@{ Name = 'prepared'; Action = {} }) { Get-UnexpectedPaths $script:scopeSample }
    $text = [IO.File]::ReadAllText((Join-Path $directory 'summary.md'))
    Require ($a.Failure -and $text.Contains('outcome: failed') -and
             $text.Contains("## Working tree after the attempt`nplugins/concurrency-hunter/demo/expected-findings.json".Replace("`n", [Environment]::NewLine))) 'self-check (h): final scope failure missing'
    Test-AttemptExit
    # Recheck containment immediately before removing the completed self-check directory.
    $directory = Reset-AttemptDirectory $directory
    Remove-Item -LiteralPath $directory -Force
}

$previousLocation = Get-Location
$previousOutput = $env:CH_H3_PACKAGE_PROBE_OUT
$exitCode = 1
try {
    Set-Location -LiteralPath $root
    if ($Stage -eq 'scope' -and -not $Final) {
        $stageSteps = @(@{ Name = 'scope'; Action = {
            $unexpected = @(Get-WorkingTree)
            Require ($unexpected.Count -eq 0) ('Unexpected paths: ' + ($unexpected -join ', '))
        } })
    }
    else {
        New-Item -ItemType Directory -Path $logs -Force | Out-Null
        $steps = @(
            @{ Name = 'self-check'; Action = { Test-AttemptRecorder } },
            @{ Name = 'scope'; Action = {
                $unexpected = @(Get-WorkingTree)
                Require ($unexpected.Count -eq 0) ('Unexpected paths: ' + ($unexpected -join ', '))
            } },
            @{ Name = 'spec'; Action = {
                param($a)
                $spec = [IO.File]::ReadAllText((Join-Path $root 'docs/SPEC.md'))
                $candidate = @($spec -split '\r?\n' | Where-Object { $_.StartsWith('Candidate подавляется', [StringComparison]::Ordinal) })
                Require ($candidate.Count -eq 1 -and ($candidate[0] -replace '\s+', ' ').Contains('; ' + $specItem + '. AI inference не доказывает безопасность')) 'SPEC candidate item is missing or changed'
                Require (($spec -replace '\s+', ' ').Contains($specParagraph)) 'SPEC library-internal paragraph is missing or changed'
                $log = Join-Path $a.LogDirectory 'spec.log'
                & pwsh -NoProfile -File (Join-Path $root 'build/check-docs.ps1') *> $log
                Require ($LASTEXITCODE -eq 0) "check-docs exited $LASTEXITCODE; see $log"
            } },
            @{ Name = 'build'; Action = {
                param($a)
                foreach ($project in @($core, $cli)) {
                    $log = Join-Path $a.LogDirectory ((Split-Path $project -Leaf) + '-build.log')
                    & dotnet build $project -c Release --nologo --disable-build-servers -nodeReuse:false *> $log
                    Require ($LASTEXITCODE -eq 0) "build exited $LASTEXITCODE; see $log"
                }
            } },
            @{ Name = 'list'; Action = {
                param($a)
                $log = Join-Path $a.LogDirectory 'list.log'
                & dotnet test $core -c Release --nologo --no-build --list-tests --filter "$filter|FullyQualifiedName~Engine.LibraryInternalObjectTests" *> $log
                Require ($LASTEXITCODE -eq 0) "list exited $LASTEXITCODE; see $log"
                foreach ($class in @('CallSitePackageProbeTests', 'LibraryInternalObjectTests')) {
                    $prefix = "ConcurrencyHunter.Core.Tests.Engine.$class."
                    $listed = @([IO.File]::ReadAllLines($log) | ForEach-Object { $_.Trim() } | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
                    $names = if ($class -ceq 'CallSitePackageProbeTests') { $factNames } else { $ruleFactNames }
                    Require ($listed.Count -eq $names.Count) "expected $($names.Count) listed $class facts, found $($listed.Count); see $log"
                    foreach ($name in $names) {
                        Require (@($listed | Where-Object { $_ -ceq ($prefix + $name) }).Count -eq 1) "list needs $name exactly once"
                    }
                }
            } },
            @{ Name = 'rule-tests'; Action = {
                param($a)
                try {
                    Require (-not [string]::IsNullOrWhiteSpace($env:CH_ESHOP_ROOT)) 'CH_ESHOP_ROOT is required'
                    $classes = @('Engine.LibraryInternalObjectTests', 'Engine.CallSiteContextMatrixTests', 'Engine.CandidateIndexTests',
                                 'Engine.FreshObjectTests', 'Engine.FreshDeduplicationTests', 'Engine.CancellationCoverageTests',
                                 'ConcurrencyHunter.Core.Tests.DemoGeneratedModelsTests')
                    $coreFilter = ($classes | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|'
                    $cliClasses = @('MetricsCommandTests.Demo_measurement_matches_the_snapshot', 'EShopSnapshotTests')
                    $cliFilter = ($cliClasses | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|'
                    $failed = [Collections.Generic.List[string]]::new()
                    foreach ($check in @(@{ Project = $core; Filter = $coreFilter; Classes = $classes; Name = 'core' },
                                          @{ Project = $cli; Filter = $cliFilter; Classes = $cliClasses; Name = 'cli' })) {
                        $directory = Join-Path $a.LogDirectory ($check.Name + '-rule-results')
                        $log = Join-Path $a.LogDirectory ($check.Name + '-rule-tests.log')
                        & dotnet test $check.Project -c Release --nologo --no-build --filter $check.Filter --logger 'trx;LogFileName=results.trx' --results-directory $directory *> $log
                        $code = $LASTEXITCODE
                        if ($code -ne 0) { $failed.Add("$($check.Name) rule-tests exited $code; see $log") }
                        $path = Join-Path $directory 'results.trx'
                        if (-not (Test-Path -LiteralPath $path)) { $failed.Add("$($check.Name) rule-tests left no trx"); continue }
                        [xml] $trx = [IO.File]::ReadAllText($path)
                        $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
                            [pscustomobject] @{ Name = [string] $_.testName; Outcome = [string] $_.outcome }
                        })
                        $a.RuleResults += $results
                        foreach ($class in $check.Classes) {
                            if (@($results | Where-Object { $_.Name.Contains($class, [StringComparison]::Ordinal) }).Count -eq 0) {
                                $failed.Add("no results for $class")
                            }
                        }
                        foreach ($result in $results) {
                            if ($result.Outcome -cne 'Passed') { $failed.Add("$($result.Name): $($result.Outcome)") }
                        }
                    }
                    Require ($failed.Count -eq 0) ($failed -join '; ')
                }
                catch { $a.RuleFailure = $_.Exception.Message }
            } },
            (New-TestStep {
                param($a)
                & dotnet test $core -c Release --nologo --no-build --filter $filter --logger 'trx;LogFileName=results.trx' --results-directory $a.ResultsDirectory
            }),
            $verdictStep
        )
        $ruleSteps = $steps
        $ruleTests = @($ruleSteps | Where-Object Name -eq 'rule-tests')[0]
        $matrixSteps = @(
            @($ruleSteps | Where-Object Name -eq 'scope')[0],
            @($ruleSteps | Where-Object Name -eq 'build')[0],
            @{ Name = 'list'; Action = {
                param($a)
                $log = Join-Path $a.LogDirectory 'matrix-list.log'
                & dotnet test $core -c Release --nologo --no-build --list-tests --filter 'FullyQualifiedName~Engine.LibraryInternalObjectMatrixTests' *> $log
                Require ($LASTEXITCODE -eq 0) "matrix list exited $LASTEXITCODE; see $log"
                $prefix = 'ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.'
                $listed = @([IO.File]::ReadAllLines($log) | ForEach-Object { $_.Trim() } | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
                Require ($listed.Count -eq $matrixFactNames.Count) "expected six matrix facts, found $($listed.Count); see $log"
                foreach ($name in $matrixFactNames) {
                    Require (@($listed | Where-Object { $_ -ceq ($prefix + $name) }).Count -eq 1) "list needs $name exactly once"
                }
            } },
            @{ Name = 'matrix-tests'; Action = {
                param($a)
                Require (-not [string]::IsNullOrWhiteSpace($env:CH_ESHOP_ROOT)) 'CH_ESHOP_ROOT is required'
                $classes = @('Engine.LibraryInternalObjectMatrixTests', 'Engine.CallSiteProbeTests', 'Engine.HandleExecutionProbeTests')
                $matrixFilter = ($classes | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|'
                $directory = Join-Path $a.LogDirectory 'matrix-results'
                $log = Join-Path $a.LogDirectory 'matrix-tests.log'
                & dotnet test $core -c Release --nologo --no-build --filter $matrixFilter --logger 'trx;LogFileName=results.trx' --results-directory $directory *> $log
                $code = $LASTEXITCODE
                $path = Join-Path $directory 'results.trx'
                Require (Test-Path -LiteralPath $path) "matrix-tests left no trx (exit $code); see $log"
                [xml] $trx = [IO.File]::ReadAllText($path)
                $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
                    [pscustomobject] @{ Name = [string] $_.testName; Outcome = [string] $_.outcome }
                })
                $a.RuleResults += $results
                foreach ($class in $classes) {
                    Require (@($results | Where-Object { $_.Name.Contains($class, [StringComparison]::Ordinal) }).Count -gt 0) "no results for $class"
                }
                foreach ($name in $matrixFactNames) {
                    Require (@($results | Where-Object { $_.Name -ceq ('ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.' + $name) }).Count -eq 1) "matrix result needs $name exactly once"
                }
                $failed = @($results | Where-Object Outcome -cne 'Passed')
                Require ($failed.Count -eq 0 -and $code -eq 0) "matrix-tests exited $code; non-passed results: $($failed.Name -join ', '); see $log"
            } },
            @{ Name = 'rule-tests'; Action = {
                param($a)
                & $ruleTests.Action $a
                Require (-not $a.RuleFailure) $a.RuleFailure
            } }
        )
        $closeSteps = @(
            @($ruleSteps | Where-Object Name -eq 'scope')[0],
            @{ Name = 'baseline'; Action = {
                param($a)
                $log = Join-Path $a.LogDirectory 'baseline-rerecord.log'
                & pwsh -NoProfile -File (Join-Path $root 'build/check-baseline-rerecord.ps1') *> $log
                Require ($LASTEXITCODE -eq 0) "baseline-rerecord exited $LASTEXITCODE; see $log"
                $lines = [IO.File]::ReadAllLines((Join-Path $root 'build/test-baseline.txt'))
                foreach ($class in @(@{ Name = 'LibraryInternalObjectTests'; Facts = $ruleFactNames },
                                      @{ Name = 'LibraryInternalObjectMatrixTests'; Facts = $matrixFactNames },
                                      @{ Name = 'CallSitePackageProbeTests'; Facts = $factNames })) {
                    foreach ($name in $class.Facts) {
                        $line = 'ConcurrencyHunter.Core.Tests.Engine.' + $class.Name + '.' + $name + "`tPassed"
                        Require (@($lines | Where-Object { $_ -ceq $line }).Count -eq 1) "baseline needs $line exactly once"
                    }
                }
            } },
            @{ Name = 'record'; Action = {
                param($a)
                $measurements = @{}
                foreach ($name in @('records', 'admitted')) {
                    $measurements[$name] = Read-Measurement @{ Directory = Join-Path $scratch 'h3/package-probe'; TestsStarted = $true } $name
                }
                $diff = @(& git -C $root diff HEAD --unified=0 -- demo/.concurrency-hunter/models/generated)
                Require ($LASTEXITCODE -eq 0) 'git diff of generated models failed'
                $before = @(& git -C $root show HEAD:plugins/concurrency-hunter/build/test-baseline.txt)
                Require ($LASTEXITCODE -eq 0) 'git show of baseline failed'
                Assert-CloseRecord ([IO.File]::ReadAllText((Join-Path $root 'docs/runs/5d-H3f.md'))) `
                    ([IO.File]::ReadAllLines((Join-Path $root 'docs/PLAN.md'))) $measurements $diff $before `
                    ([IO.File]::ReadAllLines((Join-Path $root 'build/test-baseline.txt')))
                $log = Join-Path $a.LogDirectory 'docs.log'
                & pwsh -NoProfile -File (Join-Path $root 'build/check-docs.ps1') *> $log
                Require ($LASTEXITCODE -eq 0) "check-docs exited $LASTEXITCODE; see $log"
            } }
        )
        $stageSteps = if ($Final) {
            @($ruleSteps) + @($matrixSteps | Where-Object Name -notin @('scope', 'build')) + @($closeSteps | Where-Object Name -ne 'scope')
        }
        else {
            switch ($Stage) { 'matrix' { $matrixSteps } 'close' { $closeSteps } default { $ruleSteps } }
        }
    }
    $directory = if ($Final) { 'h3/package-probe' } else {
        switch ($Stage) { 'matrix' { 'h3/library-internal-matrix' } 'close' { 'h3/close' } 'scope' { 'h3/scope' } default { 'h3/package-probe' } }
    }
    $attempt = Invoke-Attempt (Join-Path $scratch $directory) $stageSteps
    Write-Host (Join-Path $attempt.Directory 'summary.md')
    if ($attempt.Failure) { Write-Host $attempt.Failure }
    else { $exitCode = 0 }
}
catch { $exitCode = 1; Write-Host $_.Exception.Message }
finally {
    $env:CH_H3_PACKAGE_PROBE_OUT = $previousOutput
    Set-Location -LiteralPath $previousLocation
}
exit $exitCode
