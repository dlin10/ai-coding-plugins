<#
.SYNOPSIS
The gate checks of concurrency-hunter phase 5d run B3b, one task at a time.

.DESCRIPTION
Each task of the run's plan calls this script with its number. Every dotnet command writes its output to a log under
scratch/b3b-gate/ and is judged by its exit code; a failure prints the log's tail and exits 1. A task that adds tests first
checks that each of them is in the test list, so a gate cannot pass on a filter that matches nothing.

.PARAMETER Task
The task of the plan whose checks to run, 1 to 12.

.PARAMETER Fix
Runs the short fix gate instead: the fast test classes the run adds or changes. The slow ones it leaves out - the deep stage
cancellation runs and the compilation of two installed framework libraries - run in the full suite of G1.

.PARAMETER Protected
Compares what the run must not change with HEAD: the demo snapshot's findings, groups and fingerprints, the demo's expected
findings and the eShop snapshot. Runs no test.
#>
param([ValidateRange(1, 12)][int]$Task, [switch]$Fix, [switch]$Protected)

if (@(@(($Task -ne 0), [bool]$Fix, [bool]$Protected) | Where-Object { $_ }).Count -ne 1)
{
    Write-Host 'FAIL: name exactly one of -Task <1..12>, -Fix and -Protected'
    exit 1
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# git show prints the committed JSON as UTF-8.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'
$logs = Join-Path $root 'scratch/b3b-gate'
New-Item -ItemType Directory -Force $logs | Out-Null

function Fail([string]$message)
{
    Write-Host "FAIL: $message"
    exit 1
}

# Runs dotnet with its output in a log, and fails the gate with the log's tail when dotnet exits non-zero.
function Invoke-Dotnet([string]$logName, [string[]]$arguments)
{
    $log = Join-Path $logs "$logName.log"
    & dotnet @arguments *> $log
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0)
    {
        Get-Content $log -Tail 60 | ForEach-Object { Write-Host $_ }
        Fail "dotnet $($arguments[0]) ($logName) exited $code; log: $log"
    }
    return $log
}

# Runs the tests a filter selects.
function Invoke-Tests([string]$logName, [string]$filter)
{
    Invoke-Dotnet "test-$logName" @('test', $solution, '-c', 'Release', '--filter', $filter) | Out-Null
    Write-Host "tests pass: $logName"
}

$script:listed = $null

# Checks that each named test, Class.Method, is in the test list, which is listed once per call of the script.
function Assert-Listed([string[]]$names)
{
    if ($null -eq $script:listed)
    {
        $log = Invoke-Dotnet 'list-tests' @('test', $solution, '-c', 'Release', '--list-tests')
        $script:listed = @(Get-Content $log | ForEach-Object { $_.Trim() })
    }
    foreach ($name in $names)
    {
        $found = @($script:listed | Where-Object { $_ -like "*.$name" -or $_ -like "*.$name(*" })
        if ($found.Count -eq 0)
        {
            Fail "test $name is not in the test list"
        }
    }
    Write-Host "listed: $($names.Count) tests"
}

# The fingerprint a build wrote into the intermediate folder of one configuration.
function Built-Fingerprint([string]$configuration)
{
    $files = @(Get-ChildItem (Join-Path $root "src/ConcurrencyHunter.Core/obj/$configuration") -Recurse -File -Filter 'GeneratorVersion.g.cs')
    if ($files.Count -ne 1)
    {
        Fail "expected one GeneratorVersion.g.cs under obj/$configuration, found $($files.Count)"
    }
    $text = Get-Content $files[0].FullName -Raw
    $match = [regex]::Match($text, '"([0-9a-f]{64})"')
    if (-not $match.Success)
    {
        Fail "$($files[0].FullName) holds no 64-hex fingerprint"
    }
    return $match.Groups[1].Value
}

# A file of the plugin as HEAD has it.
function Committed-Text([string]$relativePath)
{
    $text = & git -C $root show "HEAD:./$relativePath"
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0)
    {
        Fail "git show HEAD:./$relativePath exited $code"
    }
    return ($text -join "`n")
}

# Fails unless what the run must not change equals HEAD: the demo snapshot's findings, groups and fingerprints, the demo's
# expected findings and the eShop snapshot, which generation never touches.
function Assert-Protected
{
    foreach ($path in @('demo/expected-findings.json', 'src/ConcurrencyHunter.Cli.Tests/Snapshots/eshop.json'))
    {
        & git -C $root diff --quiet HEAD -- $path
        $code = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        if ($code -ne 0)
        {
            Fail "$path differs from HEAD"
        }
    }

    $snapshot = 'src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json'
    $before = (Committed-Text $snapshot | ConvertFrom-Json).counts
    $after = (Get-Content (Join-Path $root $snapshot) -Raw | ConvertFrom-Json).counts
    foreach ($count in @('findings', 'groups', 'fingerprints'))
    {
        # Compared as JSON: a count may be a list or an object, which -ne would compare element-wise or by reference.
        $old = ConvertTo-Json -InputObject $before.$count -Depth 16 -Compress
        $new = ConvertTo-Json -InputObject $after.$count -Depth 16 -Compress
        if ($null -eq $before.$count -or $new -ne $old)
        {
            Fail "demo.json counts.$count differs from HEAD"
        }
    }
    Write-Host 'demo findings, groups, fingerprints and expectations, and the eShop snapshot, equal HEAD'
}

if ($Fix)
{
    $classes = @('GeneratorVersionTests', 'GeneratedModelFilesTests', 'GeneratedFileVersionMatrixTests', 'GeneratedEntryAcceptanceMatrixTests',
                 'RunGenerationTests', 'ModelGenerationModeTests', 'WorkCancellationTests', 'CancellationCoverageTests',
                 'EngineCancellationTests', 'ModelGeneratorCancellationTests', 'LibraryCompilationTests', 'DemoGeneratedModelsTests')
    $filter = (($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|')
    Invoke-Tests 'fix' "($filter)&FullyQualifiedName!~Installed_System_Text_Json_and_System_Net_Security_compile"
    Write-Output 'check-phase5d-runB3b fix gate ok'
    exit 0
}

if ($Protected)
{
    Assert-Protected
    Write-Output 'check-phase5d-runB3b protected ok'
    exit 0
}

switch ($Task)
{
    1
    {
        Assert-Listed @('GeneratorVersionTests.Built_fingerprint_matches_the_sources',
                        'GeneratorVersionTests.Fingerprint_is_64_lowercase_hex',
                        'GeneratorVersionTests.Fingerprint_moves_with_its_inputs_and_only_with_them')
        Invoke-Tests 'generator-version' 'FullyQualifiedName~GeneratorVersionTests'
        Invoke-Dotnet 'build-core-debug' @('build', (Join-Path $root 'src/ConcurrencyHunter.Core/ConcurrencyHunter.Core.csproj'), '-c', 'Debug',
                                           '--disable-build-servers', '-nodeReuse:false') | Out-Null
        $debug = Built-Fingerprint 'Debug'
        $release = Built-Fingerprint 'Release'
        if ($debug -ne $release)
        {
            Fail "the Debug build's fingerprint $debug differs from the Release build's $release"
        }
        Write-Host "Debug and Release fingerprints agree: $release"
    }
    2
    {
        Assert-Listed @('ModelGenerationModeTests.ReadOnly_names_a_file_of_another_version_and_lists_its_target',
                        'ModelGenerationModeTests.Generate_replaces_a_file_of_another_version_whole')
        Invoke-Tests 'version-rule' ('FullyQualifiedName~GeneratedModelFilesTests|FullyQualifiedName~GeneratedEntryAcceptanceMatrixTests|' +
                                     'FullyQualifiedName~RunGenerationTests|FullyQualifiedName~ModelGenerationModeTests|' +
                                     'FullyQualifiedName~GeneratedCoverageTests|FullyQualifiedName~RunGenerationPassTests|' +
                                     'FullyQualifiedName~GeneratedEntryApplicabilityTests')
    }
    3
    {
        Assert-Listed @('GeneratedFileVersionMatrixTests.Every_cell_gets_its_expected_answer',
                        'GeneratedFileVersionMatrixTests.No_cell_applies_an_answer_of_another_version',
                        'GeneratedFileVersionMatrixTests.Every_axis_value_occurs')
        Invoke-Tests 'version-matrix' 'FullyQualifiedName~GeneratedFileVersionMatrixTests'
    }
    4
    {
        Assert-Listed @('WorkCancellationTests.Check_outside_any_work_does_nothing',
                        'WorkCancellationTests.Nested_work_restores_the_outer_check',
                        'WorkCancellationTests.Nested_work_without_an_observer_reports_to_the_outer_one',
                        'WorkCancellationTests.Nested_work_observes_an_enclosing_cancellation',
                        'WorkCancellationTests.Outside_suspends_an_enclosing_cancellation',
                        'WorkCancellationTests.Analysis_stages_outside_the_pipeline_observe_the_work',
                        'WorkCancellationTests.Generation_observes_an_enclosing_cancellation_in_every_cell',
                        'WorkCancellationTests.Stage_checks_observe_the_work_in_every_cell',
                        'WorkCancellationTests.Solved_heap_queries_observe_the_work_in_every_cell',
                        'WorkCancellationTests.Happens_before_queries_observe_the_work_in_every_cell',
                        'WorkCancellationTests.Shared_initialization_ignores_a_cancelled_work',
                        'WorkCancellationTests.Direct_checks_report_before_any_token',
                        'WorkCancellationTests.Interrupted_happens_before_query_leaves_the_graph_unchanged',
                        'RunGenerationTests.Enclosing_cancellation_during_a_member_is_cancelled_and_not_kept')
        Invoke-Tests 'work-cancellation' ('FullyQualifiedName~WorkCancellationTests|FullyQualifiedName~EngineCancellationTests|' +
                                          'FullyQualifiedName~ModelGeneratorCancellationTests|FullyQualifiedName~RunGenerationTests|' +
                                          'FullyQualifiedName~LibraryCompilationTests|FullyQualifiedName~GeneratedModelFilesTests')
    }
    5
    {
        Assert-Listed @('CancellationCoverageTests.Analysis_code_checks_cancellation',
                        'CancellationCoverageTests.Frontend_code_checks_cancellation',
                        'CancellationCoverageTests.Core_code_checks_cancellation',
                        'CancellationCoverageTests.Generation_code_checks_cancellation',
                        'CancellationCoverageTests.Bounded_marker_names_a_reason',
                        'CancellationCoverageTests.Check_helpers_reach_the_work_cancellation',
                        'CancellationCoverageTests.Checks_receivers_are_stage_checks',
                        'CancellationCoverageTests.Recognizer_classifies_each_form',
                        'CancellationCoverageTests.Excluded_code_exists')
        Invoke-Tests 'coverage-analysis' ('FullyQualifiedName~CancellationCoverageTests&FullyQualifiedName!~Frontend_code_checks_cancellation&' +
                                          'FullyQualifiedName!~Core_code_checks_cancellation&FullyQualifiedName!~Generation_code_checks_cancellation')
        Invoke-Tests 'engine-cancellation' 'FullyQualifiedName~EngineCancellationTests|FullyQualifiedName~WorkCancellationTests'
    }
    6
    {
        Invoke-Tests 'coverage-frontend-core' 'FullyQualifiedName~CancellationCoverageTests&FullyQualifiedName!~Generation_code_checks_cancellation'
        Invoke-Tests 'engine-cancellation' 'FullyQualifiedName~EngineCancellationTests|FullyQualifiedName~WorkCancellationTests|FullyQualifiedName~ModelGenerationModeTests'
    }
    7
    {
        Invoke-Tests 'coverage-all' 'FullyQualifiedName~CancellationCoverageTests'
        Invoke-Tests 'generation-cancellation' ('FullyQualifiedName~ModelGeneratorCancellationTests|FullyQualifiedName~LibraryCompilationTests|' +
                                                'FullyQualifiedName~WorkCancellationTests|FullyQualifiedName~ModelGenerationModeTests')
    }
    8
    {
        Assert-Listed @('StageCancellationTests.Cancellation_deep_inside_each_pipeline_step_stops_it',
                        'StageCancellationTests.Cancellation_deep_inside_each_generation_stage_stops_it',
                        'StageCancellationTests.Every_scope_step_is_a_row_or_nested_in_one',
                        'StageCancellationTests.Decompilation_stops_every_worker_after_the_cancellation')
        Invoke-Tests 'stage-cancellation' 'FullyQualifiedName~StageCancellationTests'
    }
    9
    {
        Assert-Listed @('LibraryCompilationTests.A_trimmed_auto_property_without_a_getter_gets_one',
                        'LibraryCompilationTests.An_explicit_interface_event_in_error_calls_a_bodiless_member',
                        'LibraryCompilationTests.A_static_explicit_interface_event_in_error_calls_a_static_bodiless_member',
                        'LibraryCompilationTests.Subscribing_through_the_interface_to_an_explicit_event_in_error_hands_the_delegate_to_an_opaque_call',
                        'LibraryCompilationTests.Installed_System_Text_Json_and_System_Net_Security_compile',
                        'LibraryCompilationTests.An_accessor_modifier_without_a_trimmed_getter_is_not_rewritten',
                        'LibraryCompilationTests.Explicit_interface_events_in_error_get_distinct_bodiless_members')
        Invoke-Tests 'library-compilation' 'FullyQualifiedName~LibraryCompilationTests'
    }
    10
    {
        Assert-Listed @('DemoGeneratedModelsTests.Committed_files_name_the_current_generator')
        Invoke-Tests 'demo' 'FullyQualifiedName~DemoGeneratedModelsTests|FullyQualifiedName~DemoExpectationTests|FullyQualifiedName~MetricsCommandTests'

        $untracked = @(& git -C $root status --porcelain --untracked-files=all -- 'demo/.concurrency-hunter/models/generated' | Where-Object { $_ -like '`?`? *' })
        $global:LASTEXITCODE = 0
        if ($untracked.Count -ne 0)
        {
            Fail "new generated files in the demo: $($untracked -join '; ')"
        }

        Assert-Protected
    }
    11
    {
        $env:CH_MODEL_EVALS = '1'
        Remove-Item Env:CH_MODEL_EVALS_RECORD -ErrorAction SilentlyContinue
        Invoke-Tests 'model-evals' ('FullyQualifiedName~ModelEvalTests|FullyQualifiedName~EffectsEvalTests|' +
                                    'FullyQualifiedName~AccessorEvalTests|FullyQualifiedName~LinqOracleTests')

        $path = 'skills/hunt/evals/models/generator-snapshot.json'
        $before = Committed-Text $path | ConvertFrom-Json
        $after = Get-Content (Join-Path $root $path) -Raw | ConvertFrom-Json
        $allowed = @('M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)',
                     'M:System.Net.Security.SslClientAuthenticationOptions.set_RemoteCertificateValidationCallback(System.Net.Security.RemoteCertificateValidationCallback)')
        foreach ($section in @('members', 'effects', 'linq', 'accessors'))
        {
            $old = @{}
            foreach ($entry in @($before.$section))
            {
                $old[$entry.member] = $entry | ConvertTo-Json -Depth 64 -Compress
            }
            $new = @{}
            foreach ($entry in @($after.$section))
            {
                $new[$entry.member] = $entry | ConvertTo-Json -Depth 64 -Compress
            }
            foreach ($member in @(@($old.Keys) + @($new.Keys) | Sort-Object -Unique))
            {
                if ($old[$member] -ne $new[$member] -and $allowed -notcontains $member)
                {
                    Fail "the recorded answer of $member in $section changed"
                }
            }
            foreach ($member in $allowed)
            {
                $entry = @($after.$section | Where-Object { $_.member -eq $member })
                if ($entry.Count -eq 1 -and $entry[0].reason -eq 'library-does-not-compile')
                {
                    Fail "$member still answers library-does-not-compile"
                }
            }
        }

        # The packages whose compilation the run's compile fixes change may move their body counts, nothing else; every other
        # package entry, and every other field, equals HEAD.
        $moving = @('System.Text.Json|10.0.13', 'System.Net.Security|10.0.13')
        $counts = @('bodies', 'externBodies', 'driversSynthesized')
        $oldPackages = @{}
        foreach ($entry in @($before.packages))
        {
            $oldPackages["$($entry.assembly)|$($entry.implementationVersion)"] = $entry
        }
        $newPackages = @{}
        foreach ($entry in @($after.packages))
        {
            $newPackages["$($entry.assembly)|$($entry.implementationVersion)"] = $entry
        }
        foreach ($key in @(@($oldPackages.Keys) + @($newPackages.Keys) | Sort-Object -Unique))
        {
            $old = $oldPackages[$key]
            $new = $newPackages[$key]
            if ($null -eq $old -or $null -eq $new)
            {
                Fail "the packages entry $key is only on one side"
            }
            if ($moving -contains $key)
            {
                $old = $old | Select-Object -Property * -ExcludeProperty $counts
                $new = $new | Select-Object -Property * -ExcludeProperty $counts
            }
            $oldKept = ConvertTo-Json -InputObject $old -Depth 16 -Compress
            $newKept = ConvertTo-Json -InputObject $new -Depth 16 -Compress
            if ($oldKept -ne $newKept)
            {
                Fail "the packages entry $key changed beyond what the compile fixes may move"
            }
        }

        foreach ($threshold in @('exact', 'fateGoldEntries', 'effectsExact', 'accessorsExact'))
        {
            if ([int]$after.$threshold -lt [int]$before.$threshold)
            {
                Fail "$threshold fell from $($before.$threshold) to $($after.$threshold)"
            }
        }
        foreach ($group in @($before.linqExact.PSObject.Properties.Name))
        {
            if ([int]$after.linqExact.$group -lt [int]$before.linqExact.$group)
            {
                Fail "linqExact.$group fell from $($before.linqExact.$group) to $($after.linqExact.$group)"
            }
        }
        Write-Host 'evals pass; only the two members moved; no threshold fell'
    }
    12
    {
        & (Join-Path $PSScriptRoot 'check-docs.ps1')
        $code = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        if ($code -ne 0)
        {
            Fail "check-docs.ps1 exited $code"
        }

        # Each document holds the statements the plan's last task names.
        $expected = @(
            @{ File = 'docs/SPEC.md'; Pattern = 'ADR 0023'; Min = 1 },
            @{ File = 'docs/SPEC.md'; Pattern = 'верси[а-я]+ генератора'; Min = 3 },
            @{ File = 'docs/SPEC.md'; Pattern = '// bounded:'; Min = 1 },
            @{ File = 'docs/SPEC.md'; Pattern = 'auto-свойств'; Min = 2 },
            @{ File = 'docs/PLAN.md'; Pattern = '^\| \*\*5d run B3b\*\* \| Построено:'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 18[2-9] \|.*CS0051.*\| 5d run B4 \|$'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 18[2-9] \|.*CS0281.*\| 5d run C \|$'; Min = 1 },
            @{ File = 'docs/adr/0023-*.md'; Pattern = 'key.{0,80}before.{0,40}version'; Min = 1 },
            @{ File = 'docs/runs/5d-B3b.md'; Pattern = 'System\.Text\.Json'; Min = 1 },
            @{ File = 'docs/runs/5d-B3b.md'; Pattern = 'System\.Net\.Security'; Min = 1 },
            @{ File = 'docs/runs/5d-B3b.md'; Pattern = 'record-demo-models'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 106 \| Закрыт →'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 115 \| Закрыт →'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 130 \| Закрыт →'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 181 \| Закрыт →'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 18[2-9] \|.*CS0104.*\| 5d run B4 \|$'; Min = 1 },
            @{ File = 'docs/QUESTIONS.md'; Pattern = '^\| 18[2-9] \|.*ASP0029.*\| 5d run C \|$'; Min = 1 },
            @{ File = 'docs/adr/0012-*.md'; Pattern = 'ADR 0023|0023-'; Min = 1 },
            @{ File = 'docs/adr/0023-*.md'; Pattern = 'unreadable'; Min = 1 },
            @{ File = 'docs/adr/0023-*.md'; Pattern = 'does not know the generator version reads the new file as unreadable and leaves it untouched'; Min = 1 },
            @{ File = 'docs/adr/0023-*.md'; Pattern = "Two builds with different fingerprints replace each other's files"; Min = 1 },
            @{ File = 'docs/SPEC.md'; Pattern = 'первая запись рана генерации заменяет его целиком'; Min = 1 },
            @{ File = 'docs/SPEC.md'; Pattern = 'смена версии генератора перезаписывает файлы demo'; Min = 1 },
            @{ File = 'AGENTS.md'; Pattern = 'record-demo-models\.ps1.*[Gg]enerator version|[Gg]enerator version.*record-demo-models\.ps1'; Min = 1 },
            @{ File = 'docs/runs/5d-B3b.md'; Pattern = 'generator-snapshot\.json'; Min = 1 },
            @{ File = 'docs/runs/5d-B3b.md'; Pattern = 'test-baseline\.txt'; Min = 1 }
        )
        foreach ($item in $expected)
        {
            $files = @(Get-ChildItem (Join-Path $root $item.File) -File -ErrorAction SilentlyContinue)
            if ($files.Count -ne 1)
            {
                Fail "$($item.File) names $($files.Count) files, not one"
            }
            $hits = @(Select-String -Path $files[0].FullName -Pattern $item.Pattern).Count
            if ($hits -lt $item.Min)
            {
                Fail "$($item.File) has $hits lines matching '$($item.Pattern)', fewer than $($item.Min)"
            }
        }

        $stale = @(Select-String -Path (Join-Path $root 'docs/PLAN.md') -Pattern 'Сгенерированная модель не привязана к версии генератора|Отмена генерации видна в начале каждой стадии')
        if ($stale.Count -ne 0)
        {
            Fail "docs/PLAN.md still states a temporary bound this run removed (line $($stale[0].LineNumber))"
        }
        Write-Host 'docs hold the run'
    }
}

Write-Output "check-phase5d-runB3b task $Task ok"
exit 0
