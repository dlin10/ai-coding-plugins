<#
.SYNOPSIS
The gate checks of concurrency-hunter phase 5d run B4d, one task at a time.

.DESCRIPTION
Each task of the run's plan calls this script with its number. Every dotnet command and every nested script writes its
output to a log under scratch/b4d-gate/ and is judged by its exit code; a failure prints the log's tail and exits 1. A task
that adds tests first checks that each of them is in the test list, so a gate cannot pass on a filter that matches nothing.
Outside -Evals and task 2 the model evals switch is cleared for the tests. Whatever the script puts into the environment it
puts back. The gates of tasks 1 to 4 do not run the tests that read the demo's committed generated files, which the
generator version of these tasks makes stale until task 5 records them again.

.PARAMETER Task
The task of the plan whose checks to run, 1 to 7.

.PARAMETER Fix
The short check of a code-review fix: a Release build and the test classes the plan's Fix gate names.

.PARAMETER Full
Runs the whole suite through check-test-baseline.ps1 (CH_ESHOP_ROOT must be set), then -Protected, then
check-baseline-rerecord.ps1 against HEAD.

.PARAMETER Evals
Runs the model evals against the committed snapshot without recording it, then checks that its answers equal the answers
HEAD gives on this machine's runtime (D-0016) and that the run record names every answer the runtime update itself moved.

.PARAMETER Protected
Compares with HEAD what the run must not change or must account for: the eShop snapshot and the CoreLib table equal HEAD;
the demo snapshot keeps its fingerprints and its number of groups; every moved value of the demo snapshot is in the run
record. Runs no test.

.PARAMETER Measure
Measures eShop with generation on and then read-only into scratch/b4d-gate/measure.json, with the checkout's
.concurrency-hunter/ copied away first and put back byte for byte however the measurement ends. Checks the copy-and-restore
procedure on a temporary folder first.
#>
param([ValidateRange(1, 7)][int]$Task, [switch]$Fix, [switch]$Full, [switch]$Evals, [switch]$Protected, [switch]$Measure)

if (@(@(($Task -ne 0), [bool]$Fix, [bool]$Full, [bool]$Evals, [bool]$Protected, [bool]$Measure) | Where-Object { $_ }).Count -ne 1)
{
    Write-Host 'FAIL: name exactly one of -Task <1..7>, -Fix, -Full, -Evals, -Protected and -Measure'
    exit 1
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'
$logs = Join-Path $root 'scratch/b4d-gate'
New-Item -ItemType Directory -Force $logs | Out-Null

$EVAL_CLASSES = @('ModelEvalTests', 'EffectsEvalTests', 'LinqOracleTests', 'AccessorEvalTests')
$OLD_THROUGH_MODEL_TEST = 'RunGenerationPassTests.Member_reached_only_through_a_model_is_listed_reached_through_model'
$REMOVED_CAUSE = 'reached-through-model'
$DEMO_SNAPSHOT_TEST = 'Demo_measurement_matches_the_snapshot'

function Fail([string]$message)
{
    throw "GATE: $message"
}

# Runs a block with environment variables set (a $null value removes one) and puts every one back however the block ends.
function With-Environment([hashtable]$values, [scriptblock]$body)
{
    $saved = @{}
    foreach ($name in $values.Keys)
    {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    try
    {
        foreach ($name in $values.Keys)
        {
            [Environment]::SetEnvironmentVariable($name, $values[$name])
        }
        & $body
    }
    finally
    {
        foreach ($name in $saved.Keys)
        {
            [Environment]::SetEnvironmentVariable($name, $saved[$name])
        }
    }
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

function Invoke-Build
{
    With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
        Invoke-Dotnet 'build' @('build', $solution, '-c', 'Release', '--nologo') | Out-Null
    }
    Write-Host 'build: Release passes'
}

# Runs the tests a filter selects, with the model evals switch cleared.
function Invoke-Tests([string]$logName, [string]$filter)
{
    With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
        Invoke-Dotnet "test-$logName" @('test', $solution, '-c', 'Release', '--no-build', '--filter', $filter) | Out-Null
    }
    Write-Host "tests pass: $logName"
}

# Runs the eval tests a filter selects under CH_MODEL_EVALS=1, without recording the snapshot.
function Invoke-EvalTests([string]$logName, [string]$filter)
{
    With-Environment @{ CH_MODEL_EVALS = '1'; CH_MODEL_EVALS_RECORD = $null } {
        Invoke-Dotnet "evals-$logName" @('test', $solution, '-c', 'Release', '--no-build', '--filter', $filter) | Out-Null
    }
    Write-Host "eval tests pass: $logName"
}

# A filter selecting every test of the named classes; a class is matched as a whole name segment.
function Class-Filter([string[]]$classes)
{
    return (($classes | ForEach-Object { "(FullyQualifiedName~.$_.)" }) -join '|')
}

$script:listed = $null

function Listed-Tests
{
    if ($null -eq $script:listed)
    {
        $log = With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
            Invoke-Dotnet 'list-tests' @('test', $solution, '-c', 'Release', '--no-build', '--list-tests')
        }
        $script:listed = @(Get-Content $log | ForEach-Object { $_.Trim() })
    }
    return $script:listed
}

function Is-Listed([string]$name)
{
    return @(Listed-Tests | Where-Object { $_ -like "*.$name" -or $_ -like "*.$name(*" }).Count -gt 0
}

# Checks that each named test, Class.Method, is in the test list.
function Assert-Listed([string[]]$names)
{
    foreach ($name in $names)
    {
        if (-not (Is-Listed $name))
        {
            Fail "test $name is not in the test list"
        }
    }
    Write-Host "listed: $($names.Count) tests"
}

function Assert-Unlisted([string[]]$names)
{
    foreach ($name in $names)
    {
        if (Is-Listed $name)
        {
            Fail "test $name is still in the test list"
        }
    }
    Write-Host "gone from the test list: $($names.Count) tests"
}

# Fails when a literal occurs in the C# sources of a folder of src/.
function Assert-NoLiteral([string]$folder, [string]$literal)
{
    $hits = @(Get-ChildItem (Join-Path $root "src/$folder") -Recurse -Filter '*.cs' -File |
              Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
              Select-String -SimpleMatch $literal)
    if ($hits.Count -ne 0)
    {
        Fail "src/$folder still names '$literal', e.g. $($hits[0].Path):$($hits[0].LineNumber)"
    }
    Write-Host "src/$folder names no '$literal'"
}

# A JSON value as compact text with properties sorted by name, so that two values compare equal whatever their formatting
# and the order of their properties.
function Canon($value)
{
    if ($null -eq $value)
    {
        return 'null'
    }
    if ($value -is [string])
    {
        return ConvertTo-Json -InputObject $value -Compress
    }
    if ($value -is [bool])
    {
        return $value.ToString().ToLowerInvariant()
    }
    if ($value -is [ValueType])
    {
        return [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $value)
    }
    if ($value -is [pscustomobject])
    {
        $parts = foreach ($property in ($value.PSObject.Properties | Sort-Object Name))
        {
            (ConvertTo-Json -InputObject $property.Name -Compress) + ':' + (Canon $property.Value)
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($value -is [Collections.IEnumerable])
    {
        return '[' + (@(foreach ($item in $value) { Canon $item }) -join ',') + ']'
    }
    return [string]$value
}

$SNAPSHOT = 'skills/hunt/evals/models/generator-snapshot.json'
$BASELINE_DIR = Join-Path $logs 'head-baseline'
$BASELINE_SNAPSHOT = Join-Path $BASELINE_DIR 'generator-snapshot.json'
$BASELINE_MARK = Join-Path $BASELINE_DIR 'mark.txt'
$HEAD_WORKTREE = Join-Path $logs 'head-worktree'

# What a baseline of HEAD depends on besides HEAD's sources: the commit and the shared frameworks installed on this machine.
function Baseline-Mark
{
    $head = (& git -C $root rev-parse HEAD).Trim()
    $global:LASTEXITCODE = 0
    $dotnet = Split-Path (Get-Command dotnet).Source -Parent
    $frameworks = @(foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'))
                    {
                        $folder = Join-Path $dotnet "shared/$name"
                        if (Test-Path $folder) { Get-ChildItem $folder -Directory | ForEach-Object { "$name/$($_.Name)" } }
                    })
    return "$head`n$(($frameworks | Sort-Object) -join "`n")"
}

# The evals snapshot HEAD's generator records on this machine's runtime (D-0016): built once in a detached worktree of HEAD
# under scratch/, and reused while HEAD and the installed shared frameworks stay what they were.
function Ensure-HeadBaseline
{
    $mark = Baseline-Mark
    if ((Test-Path $BASELINE_SNAPSHOT) -and (Test-Path $BASELINE_MARK) -and ((Get-Content $BASELINE_MARK -Raw) -eq $mark))
    {
        Write-Host 'baseline: HEAD evals snapshot on this runtime reused'
        return
    }
    if (Test-Path $HEAD_WORKTREE)
    {
        & git -C $root worktree remove --force $HEAD_WORKTREE *> (Join-Path $logs 'head-worktree-remove.log')
        $global:LASTEXITCODE = 0
        if (Test-Path $HEAD_WORKTREE)
        {
            Remove-Item -LiteralPath ('\\?\' + (Resolve-Path $HEAD_WORKTREE).Path) -Recurse -Force
        }
        & git -C $root worktree prune *> $null
        $global:LASTEXITCODE = 0
    }
    & git -C $root worktree add --detach $HEAD_WORKTREE HEAD *> (Join-Path $logs 'head-worktree-add.log')
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0)
    {
        Fail "git worktree add of HEAD exited $code; log: $(Join-Path $logs 'head-worktree-add.log')"
    }
    $plugin = Join-Path $HEAD_WORKTREE 'plugins/concurrency-hunter'
    $headSolution = Join-Path $plugin 'src/ConcurrencyHunter.slnx'
    With-Environment @{ CH_MODEL_EVALS = '1'; CH_MODEL_EVALS_RECORD = '1' } {
        Invoke-Dotnet 'head-build' @('build', $headSolution, '-c', 'Release', '--nologo') | Out-Null
        Invoke-Dotnet 'head-evals-record' @('test', $headSolution, '-c', 'Release', '--no-build', '--filter', (Class-Filter $EVAL_CLASSES)) | Out-Null
    }
    New-Item -ItemType Directory -Force $BASELINE_DIR | Out-Null
    Copy-Item (Join-Path $plugin $SNAPSHOT) $BASELINE_SNAPSHOT -Force
    [IO.File]::WriteAllText($BASELINE_MARK, $mark)
    Write-Host 'baseline: HEAD evals snapshot recorded on this runtime'
}

# The snapshot without what a runtime or package update moves: each answer's implementation and each package's
# implementation version.
function Answers-Only([string]$path)
{
    $json = (Get-Content $path -Raw -Encoding utf8).TrimStart([char]0xFEFF) | ConvertFrom-Json
    $strip = {
        param($node)
        if ($node -is [pscustomobject])
        {
            foreach ($name in @('implementation', 'implementationVersion'))
            {
                if ($node.PSObject.Properties.Name -contains $name) { $node.PSObject.Properties.Remove($name) }
            }
            foreach ($property in @($node.PSObject.Properties)) { & $strip $property.Value }
        }
        elseif ($node -is [Collections.IEnumerable] -and $node -isnot [string])
        {
            foreach ($item in $node) { & $strip $item }
        }
    }
    & $strip $json
    return $json
}

# Records the run's evals snapshot under CH_MODEL_EVALS_RECORD=1 (the other eval tests run as usual) into scratch/, puts the
# committed file back byte for byte, and fails unless its answers equal the HEAD baseline's apart from implementation
# identity. With -Keep the recorded file stays as the committed one (task 5).
function Assert-EvalAnswersAsHead([string]$logName, [switch]$Keep)
{
    Ensure-HeadBaseline
    $committed = Join-Path $root $SNAPSHOT
    $saved = [IO.File]::ReadAllBytes($committed)
    $fresh = Join-Path $logs "run-snapshot-$logName.json"
    try
    {
        With-Environment @{ CH_MODEL_EVALS = '1'; CH_MODEL_EVALS_RECORD = '1' } {
            Invoke-Dotnet "evals-$logName" @('test', $solution, '-c', 'Release', '--no-build', '--filter', (Class-Filter $EVAL_CLASSES)) | Out-Null
        }
        Copy-Item $committed $fresh -Force
    }
    finally
    {
        if (-not $Keep)
        {
            [IO.File]::WriteAllBytes($committed, $saved)
        }
    }
    $baseline = Answers-Only $BASELINE_SNAPSHOT
    $run = Answers-Only $fresh
    if ((Canon $baseline) -ne (Canon $run))
    {
        $moved = foreach ($section in @('members', 'effects', 'linq', 'accessors'))
        {
            $left = @{}
            foreach ($item in @($baseline.$section)) { $left[[string]$item.member] = Canon $item }
            foreach ($item in @($run.$section))
            {
                if ($left[[string]$item.member] -ne (Canon $item)) { "$section`:$($item.member)" }
            }
        }
        Fail "the run's eval answers differ from HEAD's on this runtime: $(@($moved | Select-Object -First 10) -join '; ')$(if (@($moved).Count -eq 0) { ' (packages or floors)' })"
    }
    Write-Host "evals: the run's answers equal HEAD's on this runtime"
}

# Runs a script of build/ with its output in a log, and fails the gate when it exits non-zero.
function Invoke-Script([string]$logName, [string]$script, [object[]]$arguments = @())
{
    $log = Join-Path $logs "$logName.log"
    & (Join-Path $PSScriptRoot $script) @arguments *> $log
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0)
    {
        Get-Content $log -Tail 60 | ForEach-Object { Write-Host $_ }
        Fail "$script exited $code; log: $log"
    }
}

# Fails unless CH_ESHOP_ROOT names a checkout whose every project is restored: without it the eShop tests skip, and an
# unrestored checkout measures 2 findings instead of 36 (AGENTS.md, "Environment variables").
function Assert-EShopReady
{
    if (-not $env:CH_ESHOP_ROOT)
    {
        Fail 'CH_ESHOP_ROOT is not set; the eShop tests would skip'
    }
    $projects = @(Get-ChildItem $env:CH_ESHOP_ROOT -Recurse -Filter '*.csproj' -File |
                  Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
    if ($projects.Count -eq 0)
    {
        Fail "CH_ESHOP_ROOT ($env:CH_ESHOP_ROOT) holds no project"
    }
    $unrestored = @($projects | Where-Object { -not (Test-Path (Join-Path $_.DirectoryName 'obj/project.assets.json')) })
    if ($unrestored.Count -ne 0)
    {
        Fail ("$($unrestored.Count) eShop projects have no obj/project.assets.json, e.g. $($unrestored[0].FullName); restore with " +
              "dotnet restore `$env:CH_ESHOP_ROOT/src/eShopOnContainers-ServicesAndWebApps.sln --disable-build-servers -nodeReuse:false")
    }
    Write-Host "eShop: $($projects.Count) projects restored"
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
    return ($text -join "`n").TrimStart([char]0xFEFF)
}

function Working-Text([string]$relativePath)
{
    $path = Join-Path $root $relativePath
    if (-not (Test-Path $path))
    {
        Fail "$relativePath does not exist"
    }
    return (Get-Content $path -Raw -Encoding utf8).TrimStart([char]0xFEFF)
}

$RECORD = 'docs/runs/5d-B4d.md'
$DEMO_SNAPSHOT_FILE = 'src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json'

function Record-Lines
{
    return @((Working-Text $RECORD) -split "`r?`n")
}

# The lines of one '## ' section of the run record, without its heading; the section must exist.
function Section-Lines([string]$heading)
{
    $lines = Record-Lines
    $start = -1
    for ($index = 0; $index -lt $lines.Count; $index++)
    {
        if ($lines[$index] -match "^##\s+$([regex]::Escape($heading))\s*$")
        {
            $start = $index
            break
        }
    }
    if ($start -lt 0)
    {
        Fail "$RECORD has no section '## $heading'"
    }
    $section = [Collections.Generic.List[string]]::new()
    for ($index = $start + 1; $index -lt $lines.Count -and $lines[$index] -notmatch '^##\s'; $index++)
    {
        $section.Add($lines[$index])
    }
    return @($section)
}

# A value as the run record writes it: a string itself, numbers in invariant form, true/false, null for a missing one.
function Shown($value)
{
    if ($null -eq $value)
    {
        return 'null'
    }
    if ($value -is [string])
    {
        return $value
    }
    return Canon $value
}

# The key that identifies the elements of an array of objects, or $null when they have none of the known keys.
function Element-Key([object[]]$items)
{
    foreach ($key in @('scopeId', 'member', 'id'))
    {
        if ($items.Count -gt 0 -and @($items | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties.Name -contains $key }).Count -eq $items.Count)
        {
            return $key
        }
    }
    return $null
}

# Collects every difference between two JSON values: a moved value by its path, an array element that appeared or
# disappeared by the path of the element.
function Diff-Json($old, $new, [string]$path, [Collections.Generic.List[object]]$changes)
{
    if ($old -is [pscustomobject] -and $new -is [pscustomobject])
    {
        $names = @(@($old.PSObject.Properties.Name) + @($new.PSObject.Properties.Name) | Sort-Object -Unique)
        foreach ($name in $names)
        {
            $inner = if ($path) { "$path.$name" } else { $name }
            $left = if ($old.PSObject.Properties.Name -contains $name) { $old.$name } else { $null }
            $right = if ($new.PSObject.Properties.Name -contains $name) { $new.$name } else { $null }
            Diff-Json $left $right $inner $changes
        }
        return
    }
    if ($old -is [array] -and $new -is [array])
    {
        $all = @($old) + @($new)
        if ($all.Count -gt 0 -and @($all | Where-Object { $_ -is [string] }).Count -eq $all.Count)
        {
            foreach ($item in @($old | Where-Object { $new -notcontains $_ } | Sort-Object -Unique))
            {
                $changes.Add([pscustomobject]@{ Path = "$path[$item]"; Kind = 'removed' })
            }
            foreach ($item in @($new | Where-Object { $old -notcontains $_ } | Sort-Object -Unique))
            {
                $changes.Add([pscustomobject]@{ Path = "$path[$item]"; Kind = 'added' })
            }
            return
        }
        $key = Element-Key $all
        if ($null -ne $key)
        {
            $left = @{}
            foreach ($item in $old) { $left[[string]$item.$key] = $item }
            $right = @{}
            foreach ($item in $new) { $right[[string]$item.$key] = $item }
            foreach ($id in @(@($left.Keys) + @($right.Keys) | Sort-Object -Unique))
            {
                if (-not $right.ContainsKey($id))
                {
                    $changes.Add([pscustomobject]@{ Path = "$path[$id]"; Kind = 'removed' })
                }
                elseif (-not $left.ContainsKey($id))
                {
                    $changes.Add([pscustomobject]@{ Path = "$path[$id]"; Kind = 'added' })
                }
                else
                {
                    Diff-Json $left[$id] $right[$id] "$path[$id]" $changes
                }
            }
            return
        }
    }
    if ((Canon $old) -ne (Canon $new))
    {
        $changes.Add([pscustomobject]@{ Path = $path; Kind = 'value'; Old = $old; New = $new })
    }
}

# Fails unless the run record names every change: a moved value in a line with its path and both values, an element that
# appeared or disappeared in a line with its path and the word added or removed.
function Assert-Recorded([Collections.Generic.List[object]]$changes, [string]$what)
{
    if ($changes.Count -eq 0)
    {
        Write-Host "$what`: nothing moved"
        return
    }
    $lines = Record-Lines
    foreach ($change in $changes)
    {
        if ($change.Kind -eq 'value')
        {
            $old = Shown $change.Old
            $new = Shown $change.New
            if (@($lines | Where-Object { $_.Contains($change.Path) -and $_.Contains($old) -and $_.Contains($new) }).Count -eq 0)
            {
                Fail "$what`: the run record has no line with $($change.Path), $old and $new"
            }
        }
        elseif (@($lines | Where-Object { $_.Contains($change.Path) -and $_ -match "\b$($change.Kind)\b" }).Count -eq 0)
        {
            Fail "$what`: the run record has no line with $($change.Path) and the word $($change.Kind)"
        }
    }
    Write-Host "$what`: $($changes.Count) moved values and elements are in the run record"
}

function Assert-Contains([string]$what, [string]$text, [string[]]$needles)
{
    foreach ($needle in $needles)
    {
        if (-not $text.Contains($needle))
        {
            Fail "$what does not say '$needle'"
        }
    }
}

function Assert-Lacks([string]$what, [string]$text, [string[]]$needles)
{
    foreach ($needle in $needles)
    {
        if ($text.Contains($needle))
        {
            Fail "$what still says '$needle'"
        }
    }
}

# The eShop snapshot and the CoreLib table equal HEAD; the demo snapshot keeps its fingerprints and its number of groups,
# and every value of it that moved is in the run record.
function Assert-Protected
{
    foreach ($path in @('src/ConcurrencyHunter.Cli.Tests/Snapshots/eshop.json', 'skills/hunt/evals/models/corelib-generation.tsv'))
    {
        & git -C $root diff --quiet HEAD -- $path
        $code = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        if ($code -ne 0)
        {
            Fail "$path differs from HEAD"
        }
    }
    $before = Committed-Text $DEMO_SNAPSHOT_FILE | ConvertFrom-Json
    $after = Working-Text $DEMO_SNAPSHOT_FILE | ConvertFrom-Json
    $old = @($before.counts.fingerprints)
    $new = @($after.counts.fingerprints)
    $removed = @($old | Where-Object { $new -notcontains $_ })
    $added = @($new | Where-Object { $old -notcontains $_ })
    if ($removed.Count -ne 0 -or $added.Count -ne 0)
    {
        Fail "demo.json fingerprints moved: lost $($removed -join ', '); gained $($added -join ', ')"
    }
    if ([int]$before.counts.groups -ne [int]$after.counts.groups)
    {
        Fail "demo.json counts.groups moved: $($before.counts.groups) -> $($after.counts.groups)"
    }
    $changes = [Collections.Generic.List[object]]::new()
    Diff-Json $before $after '' $changes
    Assert-Recorded $changes 'demo.json'
    Write-Host 'eShop snapshot and CoreLib table equal HEAD; demo.json fingerprints and groups equal HEAD'
}

# The answers HEAD's committed evals snapshot and HEAD's answers on this runtime differ in apart from identity: what the
# runtime update itself moved.
function Runtime-Moved
{
    Ensure-HeadBaseline
    $headFile = Join-Path $logs 'head-committed-snapshot.json'
    [IO.File]::WriteAllText($headFile, (Committed-Text $SNAPSHOT))
    $head = Answers-Only $headFile
    $base = Answers-Only $BASELINE_SNAPSHOT
    $moved = [Collections.Generic.List[string]]::new()
    foreach ($section in @('members', 'effects', 'linq', 'accessors'))
    {
        $left = @{}
        foreach ($item in @($head.$section)) { $left[[string]$item.member] = Canon $item }
        $right = @{}
        foreach ($item in @($base.$section)) { $right[[string]$item.member] = Canon $item }
        foreach ($id in @(@($left.Keys) + @($right.Keys) | Sort-Object -Unique))
        {
            if ($left[$id] -ne $right[$id]) { $moved.Add($id) }
        }
    }
    foreach ($name in @('packages', 'exact', 'fateGoldEntries', 'effectsExact', 'linqExact', 'accessorsExact'))
    {
        if ((Canon $head.$name) -ne (Canon $base.$name)) { $moved.Add($name) }
    }
    return @($moved)
}

# The committed evals snapshot is the one the run recorded on this runtime: the eval tests pass against it unrecorded, its
# answers equal HEAD's on this runtime apart from identity, and the run record names the runtime and each answer the
# runtime update moved.
function Assert-EvalsSnapshot
{
    With-Environment @{ CH_MODEL_EVALS = '1'; CH_MODEL_EVALS_RECORD = $null } {
        Invoke-Dotnet 'evals-committed' @('test', $solution, '-c', 'Release', '--no-build', '--filter', (Class-Filter $EVAL_CLASSES)) | Out-Null
    }
    Write-Host 'eval tests pass against the committed snapshot'
    Ensure-HeadBaseline
    if ((Canon (Answers-Only (Join-Path $root $SNAPSHOT))) -ne (Canon (Answers-Only $BASELINE_SNAPSHOT)))
    {
        Fail "the committed $SNAPSHOT answers differently from HEAD on this runtime"
    }
    $lines = Section-Lines 'Снимок evals'
    if (@($lines | Where-Object { $_.Contains('10.0.14') -and $_.Contains('8.0.33') }).Count -eq 0)
    {
        Fail "$RECORD, 'Снимок evals', has no line naming the runtime 10.0.14 / 8.0.33"
    }
    foreach ($id in Runtime-Moved)
    {
        if (@($lines | Where-Object { $_.Contains($id) -and $_ -match '\bruntime\b' }).Count -eq 0)
        {
            Fail "$RECORD, 'Снимок evals', has no line with $id and the word runtime"
        }
    }
    Write-Host 'evals: the committed snapshot answers as HEAD on this runtime, and the run record names the runtime update'
}

$MEASURE_FILE = Join-Path $logs 'measure.json'

# The fingerprint (ADR 0023) of the generator the current sources build, computed by build/GeneratorFingerprint.cs.
function Source-Fingerprint
{
    if ($null -eq ('GeneratorFingerprint' -as [type]))
    {
        $source = Get-Content (Join-Path $PSScriptRoot 'GeneratorFingerprint.cs') -Raw
        Add-Type -TypeDefinition ("#define GENERATOR_FINGERPRINT_TESTS`n" + $source) -Language CSharp
    }
    return ('GeneratorFingerprint' -as [type])::Compute((Split-Path $root -Parent))
}

# The fingerprint of what a measurement depends on: the generator version, the sources and project files of the CLI,
# and this script, with line endings normalized.
function Measure-Inputs
{
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($file in Get-ChildItem (Join-Path $root 'src/ConcurrencyHunter.Cli') -Recurse -File -Include '*.cs', '*.csproj', '*.json' |
                      Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
    {
        $files.Add($file)
    }
    $files.Add((Get-Item $PSCommandPath))
    $text = [Text.StringBuilder]::new()
    [void]$text.Append('generator ').Append((Source-Fingerprint)).Append("`n")
    foreach ($file in $files | Sort-Object FullName)
    {
        [void]$text.Append([IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')).Append("`n")
        [void]$text.Append(([IO.File]::ReadAllText($file.FullName)).Replace("`r`n", "`n")).Append("`n")
    }
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

# Every file below a folder by its relative path, with its SHA-256; empty when the folder does not exist.
function Folder-Hashes([string]$folder)
{
    $hashes = @{}
    if (Test-Path $folder)
    {
        foreach ($file in Get-ChildItem $folder -Recurse -File -Force)
        {
            $hashes[[IO.Path]::GetRelativePath($folder, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $hashes
}

# Copies a folder whole, with every file's bytes, into a staging folder renamed to the backup only when the copy is
# complete. A copy that fails leaves the original untouched and no backup behind. The hook runs after the first file, for
# the self-test.
function Save-Folder([string]$folder, [string]$backup, [scriptblock]$afterFirstFile = $null)
{
    $staging = "$backup.partial"
    foreach ($path in @($backup, $staging))
    {
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    if (-not (Test-Path $folder))
    {
        return [pscustomobject]@{ Existed = $false; Hashes = @{} }
    }
    New-Item -ItemType Directory $staging | Out-Null
    try
    {
        $first = $true
        foreach ($file in Get-ChildItem $folder -Recurse -File -Force)
        {
            $target = Join-Path $staging ([IO.Path]::GetRelativePath($folder, $file.FullName))
            New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            if ($first -and $null -ne $afterFirstFile)
            {
                $first = $false
                & $afterFirstFile
            }
        }
        Rename-Item -LiteralPath $staging -NewName (Split-Path $backup -Leaf)
    }
    catch
    {
        if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        throw
    }
    return [pscustomobject]@{ Existed = $true; Hashes = (Folder-Hashes $folder) }
}

# Puts a folder back as Save-Folder found it — the copy, or nothing when there was none — and fails unless every file and
# its bytes are as they were.
function Restore-Folder([string]$folder, [string]$backup, $saved)
{
    if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    if ($saved.Existed)
    {
        Copy-Item -LiteralPath $backup -Destination $folder -Recurse -Force
    }
    $now = Folder-Hashes $folder
    $names = @(@($saved.Hashes.Keys) + @($now.Keys) | Sort-Object -Unique)
    foreach ($name in $names)
    {
        if ($saved.Hashes[$name] -ne $now[$name])
        {
            Fail "restore: $folder\$name is not as it was"
        }
    }
    if (-not $saved.Existed -and (Test-Path $folder))
    {
        Fail "restore: $folder did not exist and is still there"
    }
}

# Runs a block with a checkout's .concurrency-hunter/ copied away and its models/generated/ removed, and puts the folder
# back byte for byte however the block ends. Nothing is touched when the copy fails.
function With-ConcurrencyHunterRestored([string]$repositoryRoot, [string]$backup, [scriptblock]$body)
{
    $folder = Join-Path $repositoryRoot '.concurrency-hunter'
    $saved = Save-Folder $folder $backup
    try
    {
        $generated = Join-Path $folder 'models/generated'
        if (Test-Path $generated) { Remove-Item -LiteralPath $generated -Recurse -Force }
        & $body
    }
    finally
    {
        Restore-Folder $folder $backup $saved
    }
}

# The copy-and-restore procedure on a temporary folder: with and without an existing .concurrency-hunter/ holding a lock, a
# project file and a generated file, on success and on a failure after the cold or the warm run; and a copy that fails
# leaves the original untouched and restores nothing.
function Assert-RestoreProcedure
{
    $base = Join-Path $logs 'restore-selftest'
    $backup = Join-Path $base 'backup'
    $cases = 0
    foreach ($existing in @($true, $false))
    {
        foreach ($failure in @('none', 'cold', 'warm'))
        {
            $repository = Join-Path $base 'repository'
            if (Test-Path $base) { Remove-Item -LiteralPath $base -Recurse -Force }
            New-Item -ItemType Directory -Force $repository | Out-Null
            $folder = Join-Path $repository '.concurrency-hunter'
            if ($existing)
            {
                New-Item -ItemType Directory -Force (Join-Path $folder 'models/generated/pkg/1.0') | Out-Null
                Set-Content -LiteralPath (Join-Path $folder 'models/models.lock.json') -Value 'lock before' -NoNewline
                Set-Content -LiteralPath (Join-Path $folder 'models/project.json') -Value 'project' -NoNewline
                Set-Content -LiteralPath (Join-Path $folder 'models/generated/pkg/1.0/a.json') -Value 'generated before' -NoNewline
            }
            $before = Folder-Hashes $folder
            try
            {
                With-ConcurrencyHunterRestored $repository $backup {
                    New-Item -ItemType Directory -Force (Join-Path $folder 'models/generated/pkg/2.0') | Out-Null
                    Set-Content -LiteralPath (Join-Path $folder 'models/generated/pkg/2.0/b.json') -Value 'cold' -NoNewline
                    Set-Content -LiteralPath (Join-Path $folder 'models/models.lock.json') -Value 'lock rewritten' -NoNewline
                    if ($failure -eq 'cold') { throw 'simulated failure after the cold run' }
                    Set-Content -LiteralPath (Join-Path $folder 'models/generated/pkg/2.0/b.json') -Value 'warm' -NoNewline
                    if ($failure -eq 'warm') { throw 'simulated failure after the warm run' }
                }
                if ($failure -ne 'none') { Fail "self-test: the simulated $failure failure did not surface" }
            }
            catch
            {
                if ($failure -eq 'none' -or $_.Exception.Message -notlike '*simulated failure*') { throw }
            }
            $after = Folder-Hashes $folder
            if ((Canon ([pscustomobject]$before)) -ne (Canon ([pscustomobject]$after)) -or ($existing -ne (Test-Path $folder)))
            {
                Fail "self-test: existing=$existing failure=$failure left .concurrency-hunter changed"
            }
            $cases++
        }
    }
    $repository = Join-Path $base 'repository'
    if (Test-Path $base) { Remove-Item -LiteralPath $base -Recurse -Force }
    $folder = Join-Path $repository '.concurrency-hunter'
    New-Item -ItemType Directory -Force (Join-Path $folder 'models/generated/pkg/1.0') | Out-Null
    Set-Content -LiteralPath (Join-Path $folder 'models/models.lock.json') -Value 'lock before' -NoNewline
    Set-Content -LiteralPath (Join-Path $folder 'models/generated/pkg/1.0/a.json') -Value 'generated before' -NoNewline
    $before = Folder-Hashes $folder
    try
    {
        Save-Folder $folder $backup { throw 'simulated copy failure' } | Out-Null
        Fail 'self-test: the simulated copy failure did not surface'
    }
    catch
    {
        if ($_.Exception.Message -notlike '*simulated copy failure*') { throw }
    }
    if ((Canon ([pscustomobject]$before)) -ne (Canon ([pscustomobject](Folder-Hashes $folder))) -or (Test-Path $backup) -or (Test-Path "$backup.partial"))
    {
        Fail 'self-test: a failed copy changed the original or left a backup'
    }
    $cases++
    Remove-Item -LiteralPath $base -Recurse -Force
    Write-Host "restore: the copy-and-restore procedure keeps .concurrency-hunter byte for byte in $cases cases"
}

# The models and refusals in a checkout's generated files.
function Generated-Entries([string]$folder)
{
    $models = 0
    $refusals = 0
    $generated = Join-Path $folder 'models/generated'
    if (Test-Path $generated)
    {
        foreach ($file in Get-ChildItem $generated -Recurse -File -Filter '*.json')
        {
            $json = (Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8).TrimStart([char]0xFEFF) | ConvertFrom-Json
            foreach ($entry in @($json.members))
            {
                if ($null -ne $entry.model) { $models++ } else { $refusals++ }
            }
        }
    }
    return [pscustomobject]@{ Models = $models; Refusals = $refusals }
}

function Seconds-Text($value)
{
    return [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0}', [double]$value)
}

# The run record's lines for one measured run of metrics.
function Measurement-Lines([string]$label, [string]$metricsFile, $entries)
{
    $json = (Get-Content -LiteralPath $metricsFile -Raw -Encoding utf8) | ConvertFrom-Json
    $lines = [Collections.Generic.List[string]]::new()
    $analyses = @($json.timings.analyses)
    if ($analyses.Count -eq 0)
    {
        Fail "$metricsFile has no timings.analyses"
    }
    foreach ($item in $analyses)
    {
        $lines.Add("$label analyses $($item.scope): $($item.count)")
    }
    $generation = @($json.timings.steps | Where-Object { $_.step -eq 'generation' } | ForEach-Object { [double]$_.seconds } | Measure-Object -Sum).Sum
    $lines.Add("$label total: $(Seconds-Text $json.timings.totalSeconds)")
    $lines.Add("$label generation: $(Seconds-Text $generation)")
    $lines.Add("$label entries: $($entries.Models + $entries.Refusals) (models $($entries.Models), refusals $($entries.Refusals))")
    $budget = @($json.coverage | ForEach-Object { @($_.generationRefusals) } | Where-Object { $_.cause -eq 'budget' }).Count
    $lines.Add("$label budget: $budget")
    return @($lines)
}

# Measures eShop with generation on and then read-only, with its .concurrency-hunter/ put back afterwards, into measure.json.
function Measure-EShop
{
    Assert-RestoreProcedure
    Assert-EShopReady
    Invoke-Build
    $repository = (& git -C $env:CH_ESHOP_ROOT rev-parse --show-toplevel).Trim()
    $global:LASTEXITCODE = 0
    $target = Join-Path $env:CH_ESHOP_ROOT 'src/eShopOnContainers-ServicesAndWebApps.sln'
    $folder = Join-Path $repository '.concurrency-hunter'
    $cold = Join-Path $logs 'eshop-generate.json'
    $warm = Join-Path $logs 'eshop-read.json'
    $cli = Join-Path $root 'src/ConcurrencyHunter.Cli'
    $script:measured = $null
    With-ConcurrencyHunterRestored $repository (Join-Path $logs 'eshop-concurrency-hunter-backup') {
        With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
            Invoke-Dotnet 'measure-cold' @('run', '--project', $cli, '-c', 'Release', '--no-build', '--', 'metrics', '--target', $target,
                                           '--out', $cold, '--generation', 'on') | Out-Null
            $coldEntries = Generated-Entries $folder
            Invoke-Dotnet 'measure-warm' @('run', '--project', $cli, '-c', 'Release', '--no-build', '--', 'metrics', '--target', $target,
                                           '--out', $warm, '--generation', 'read') | Out-Null
            $warmEntries = Generated-Entries $folder
            $script:measured = [pscustomobject]@{
                inputs = Measure-Inputs
                lines = @(@(Measurement-Lines 'cold' $cold $coldEntries) + @(Measurement-Lines 'warm' $warm $warmEntries))
            }
        }
    }
    [IO.File]::WriteAllText($MEASURE_FILE, ($script:measured | ConvertTo-Json -Depth 4))
    $script:measured.lines | ForEach-Object { Write-Host $_ }
    Write-Host "measure: written to $MEASURE_FILE; eShop's .concurrency-hunter is as it was"
}

# The run record's eShop lines are the measured ones, and the measurement was taken from the current inputs.
function Assert-Measurements
{
    if (-not (Test-Path $MEASURE_FILE))
    {
        Fail "there is no $MEASURE_FILE; run -Measure"
    }
    $measured = (Get-Content -LiteralPath $MEASURE_FILE -Raw -Encoding utf8) | ConvertFrom-Json
    if ($measured.inputs -ne (Measure-Inputs))
    {
        Fail 'measure.json was taken from other inputs than the current sources and script; run -Measure again'
    }
    $section = Section-Lines 'Замер eShop'
    foreach ($line in @($measured.lines))
    {
        if (@($section | Where-Object { $_.Contains($line) }).Count -eq 0)
        {
            Fail "$RECORD, 'Замер eShop', lacks the line '$line'"
        }
    }
    Write-Host "measure: the run record carries the $(@($measured.lines).Count) measured lines of the current inputs"
}

# The section of PLAN.md whose heading starts with the given text.
function Plan-Section([string]$text, [string]$heading)
{
    $match = [regex]::Match($text, "(?ms)^##\s+$([regex]::Escape($heading)).*?(?=^##\s|\z)")
    if (-not $match.Success)
    {
        Fail "docs/PLAN.md has no section '## $heading'"
    }
    return $match.Value
}

# What task 7 makes the documents say (R9).
function Assert-Documents
{
    Invoke-Script 'docs' 'check-docs.ps1'
    $spec = Working-Text 'docs/SPEC.md'
    Assert-Lacks 'docs/SPEC.md' $spec @($REMOVED_CAUSE)
    Assert-Contains 'docs/SPEC.md' $spec @('adr/0027-the-run-generates-in-rounds-and-a-generation-takes-its-dependencies-models.md', 'analyses')
    $plan = Working-Text 'docs/PLAN.md'
    Assert-Contains 'docs/PLAN.md' $plan @('| **5d run B4e** |')
    $phases = Plan-Section $plan '2.'
    Assert-Contains 'docs/PLAN.md, section 2' $phases @('двадцати трёх', 'B4c, B4d, B4e, B5, C')
    $limits = Plan-Section $plan '3.'
    Assert-Lacks 'docs/PLAN.md, section 3' $limits @('5d run B4d)', $REMOVED_CAUSE)
    if (([regex]::Matches($limits, [regex]::Escape('5d run B4e)'))).Count -lt 2)
    {
        Fail 'docs/PLAN.md, section 3, does not assign the two remaining limits to 5d run B4e'
    }
    # PowerShell names are case-insensitive: a local named like $RECORD would hide the record's path from Section-Lines.
    $recordText = Working-Text $RECORD
    foreach ($number in 1..15)
    {
        Assert-Contains $RECORD $recordText @(('D-{0:D4}' -f $number))
    }
    Section-Lines 'Снимок evals' | Out-Null
    Write-Host 'documents: SPEC, PLAN and the run record say what R9 asks'
}

$FIX_CLASSES = @('GenerationTargetMatrixTests', 'GenerationTargetTests', 'GeneratedDependencyMatrixTests', 'GenerationRoundMatrixTests',
                 'RunGenerationPassTests', 'RunGenerationTests', 'GeneratedCoverageTests', 'ModelGenerationModeTests',
                 'GeneratedFileVersionMatrixTests', 'GeneratedLayerMatrixTests', 'GeneratedEntryApplicabilityTests', 'GeneratedModelFilesTests',
                 'GeneratedDocumentTests', 'GeneratorBoundaryTests', 'CancellationCoverageTests', 'GenerateCommandTests')

try
{
    if ($Protected)
    {
        Assert-Protected
        Write-Host 'PASS: protected'
        exit 0
    }

    if ($Full)
    {
        Assert-EShopReady
        With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
            Invoke-Script 'full-baseline' 'check-test-baseline.ps1'
        }
        Write-Host 'baseline: no recorded Passed case is missing, skipped or failed'
        Assert-Protected
        Invoke-Script 'full-rerecord' 'check-baseline-rerecord.ps1'
        Write-Host 'the rewritten baseline loses no case against HEAD'
        Write-Host 'PASS: full'
        exit 0
    }

    if ($Evals)
    {
        Invoke-Build
        Assert-EvalsSnapshot
        Write-Host 'PASS: evals'
        exit 0
    }

    if ($Measure)
    {
        Measure-EShop
        Write-Host 'PASS: measure'
        exit 0
    }

    if ($Fix)
    {
        Invoke-Build
        Invoke-Tests 'fix' ((Class-Filter $FIX_CLASSES) + "|((FullyQualifiedName~.MetricsCommandTests.)&(FullyQualifiedName!~$DEMO_SNAPSHOT_TEST))")
        Write-Host 'PASS: fix gate'
        exit 0
    }

    switch ($Task)
    {
        1
        {
            Invoke-Build
            Assert-Listed @('GenerationTargetTests.A_dependency_of_a_library_compilation_gets_the_file_key_the_program_computes',
                            'GenerationTargetMatrixTests.Every_cell_gets_its_expected_answer',
                            'GenerationTargetMatrixTests.No_cell_is_a_target_R1_excludes',
                            'GenerationTargetMatrixTests.Every_axis_value_occurs')
            Invoke-Tests 'task1' (Class-Filter @('GenerationTargetMatrixTests', 'GenerationTargetTests', 'GeneratedLayerMatrixTests',
                                                  'GeneratedEntryApplicabilityTests', 'FunctionPointerMatrixTests', 'RunGenerationTests',
                                                  'RunGenerationPassTests', 'GeneratedCoverageTests', 'CancellationCoverageTests'))
        }
        2
        {
            Invoke-Build
            Assert-Listed @('GeneratedDependencyMatrixTests.Every_cell_gets_its_expected_answer',
                            'GeneratedDependencyMatrixTests.No_cell_narrows',
                            'GeneratedDependencyMatrixTests.Every_axis_value_occurs',
                            'GeneratedDependencyMatrixTests.Without_a_dependency_source_every_cell_answers_as_today',
                            'GeneratedDependencyMatrixTests.Members_of_one_type_get_their_own_answers_in_either_order',
                            'GeneratedDependencyMatrixTests.A_generation_reports_the_view_of_each_dependency_it_called')
            Invoke-Tests 'task2' (Class-Filter @('GeneratedDependencyMatrixTests', 'GenerationTargetMatrixTests', 'GenerationTargetTests',
                                                  'GeneratedFateTests', 'GenerationCauseTests', 'CompletionObservationMatrixTests',
                                                  'CompletionProducerFormTests', 'DelegateReturnObservationMatrixTests', 'GeneratedDocumentTests',
                                                  'GeneratorBoundaryTests', 'GenerateCommandTests', 'RunGenerationTests', 'RunGenerationPassTests',
                                                  'CancellationCoverageTests'))
            # The generator without a dependency source answers as before: its eval answers equal HEAD's on this runtime (D-0016).
            Assert-EvalAnswersAsHead 'task2'
        }
        3
        {
            Invoke-Build
            Assert-Listed @('RunGenerationPassTests.Member_reached_only_through_a_model_is_generated_and_applied',
                            'RunGenerationPassTests.ReadOnly_applies_the_stored_model_of_a_member_reached_through_a_model',
                            'RunGenerationPassTests.Chain_of_two_models_is_analysed_three_times',
                            'RunGenerationPassTests.Budget_spent_before_a_member_reached_through_a_model_leaves_it_budget',
                            'RunGenerationPassTests.Refused_member_reached_through_a_model_ends_the_rounds',
                            'RunGenerationPassTests.Removed_pairs_compare_the_first_and_the_last_analysis',
                            'RunGenerationPassTests.Target_answered_in_a_later_round_has_one_coverage_item',
                            'RunGenerationPassTests.ReadOnly_lists_a_target_reached_through_a_model_as_wanted',
                            'RunGenerationPassTests.Call_first_reached_with_a_stored_model_of_its_type_is_a_target',
                            'MetricsCommandTests.Analyses_name_every_scope_and_count_the_first_analysis')
            Assert-Unlisted @($OLD_THROUGH_MODEL_TEST)
            Assert-NoLiteral 'ConcurrencyHunter.Core' $REMOVED_CAUSE
            Assert-NoLiteral 'ConcurrencyHunter.Analysis' $REMOVED_CAUSE
            Invoke-Tests 'task3' ((Class-Filter @('RunGenerationPassTests', 'RunGenerationTests', 'GeneratedCoverageTests', 'ModelGenerationModeTests',
                                                   'GeneratedFileVersionMatrixTests', 'GeneratedEntryApplicabilityTests', 'GeneratedLayerMatrixTests',
                                                   'CancellationCoverageTests', 'WorkCancellationTests', 'ScopePipelineTests', 'NewResultTests',
                                                   'ModelCreationOrdinalMatrixTests', 'GeneratedDependencyMatrixTests')) +
                                  "|((FullyQualifiedName~.MetricsCommandTests.)&(FullyQualifiedName!~$DEMO_SNAPSHOT_TEST))")
        }
        4
        {
            Invoke-Build
            Assert-Listed @('GenerationRoundMatrixTests.Every_cell_gets_its_expected_answer',
                            'GenerationRoundMatrixTests.No_cell_keeps_an_answer_that_rests_on_a_reason_of_the_run',
                            'GenerationRoundMatrixTests.Every_axis_value_occurs',
                            'RunGenerationPassTests.Regenerated_model_with_other_effects_is_analysed_again',
                            'RunGenerationPassTests.Model_replaced_by_a_refusal_is_analysed_again',
                            'RunGenerationPassTests.Changed_model_of_a_seen_dependency_regenerates_the_member',
                            'RunGenerationPassTests.Old_answer_waiting_for_its_regeneration_is_not_written',
                            'RunGenerationPassTests.Budget_spent_before_a_regeneration_leaves_the_old_answer_unkept',
                            'RunGenerationPassTests.Member_cut_by_its_limit_after_meeting_a_dependency_is_kept_as_timeout',
                            'RunGenerationPassTests.Cancellation_while_a_dependency_is_generated_writes_no_answer_that_rests_on_it',
                            'RunGenerationPassTests.Dependency_that_becomes_a_scope_target_counts_once_and_is_compared',
                            'RunGenerationPassTests.Generation_of_a_real_library_takes_the_generated_model_of_its_dependency')
            Invoke-Tests 'task4' ((Class-Filter @('GenerationRoundMatrixTests', 'RunGenerationPassTests', 'RunGenerationTests', 'ModelGenerationModeTests',
                                                   'GeneratedFileVersionMatrixTests', 'GeneratedModelFilesTests', 'GeneratedCoverageTests',
                                                   'GeneratedDependencyMatrixTests', 'GenerationTargetMatrixTests', 'WorkCancellationTests',
                                                   'CancellationCoverageTests', 'GeneratorBoundaryTests', 'GenerateCommandTests')) +
                                  "|((FullyQualifiedName~.MetricsCommandTests.)&(FullyQualifiedName!~$DEMO_SNAPSHOT_TEST))")
        }
        5
        {
            Invoke-Build
            # The committed generated files of the demo are the current generator's and hold every answer a demo run needs; the
            # published exe is the current one.
            With-Environment @{ CONCURRENCYHUNTER_REQUIRE_E2E = '1' } {
                Invoke-Tests 'task5' (Class-Filter @('DemoGeneratedModelsTests', 'DemoExpectationTests', 'MetricsCommandTests',
                                                      'PublishedExecutableEndToEndTests', 'GeneratorBoundaryTests'))
            }
            $untracked = @(& git -C $root status --porcelain --untracked-files=all -- 'demo/.concurrency-hunter/models/generated' |
                           Where-Object { $_.StartsWith('??') })
            $global:LASTEXITCODE = 0
            if ($untracked.Count -ne 0)
            {
                Fail "generated demo files are not in the index: $($untracked -join '; ')"
            }
            Assert-Protected
            Assert-EvalsSnapshot
            Invoke-Script 'task5-rerecord' 'check-baseline-rerecord.ps1'
            Write-Host 'the rewritten baseline loses no case against HEAD'
        }
        6
        {
            Assert-RestoreProcedure
            Assert-Measurements
        }
        7
        {
            Assert-Documents
            Assert-Measurements
        }
    }

    Write-Host "PASS: task $Task"
    exit 0
}
catch
{
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
}
