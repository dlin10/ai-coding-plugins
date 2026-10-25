<#
.SYNOPSIS
The gate checks of concurrency-hunter phase 5d run B4c, one task at a time.

.DESCRIPTION
Each task of the run's plan calls this script with its number. Every dotnet command and every nested script writes its
output to a log under scratch/b4c-gate/ and is judged by its exit code; a failure prints the log's tail and exits 1. A task
that adds tests first checks that each of them is in the test list, so a gate cannot pass on a filter that matches nothing.
Outside -Evals and tasks 3, 4 and 7 the model evals switch is cleared for the tests. Whatever the script puts into the
environment it puts back.

.PARAMETER Task
The task of the plan whose checks to run, 1 to 9.

.PARAMETER Full
Runs the whole suite through check-test-baseline.ps1 (CH_ESHOP_ROOT must be set), then -Protected, then
check-baseline-rerecord.ps1 against HEAD.

.PARAMETER Evals
Runs the model evals without recording the snapshot, then compares the snapshot with HEAD.

.PARAMETER Protected
Compares with HEAD what the run must not change or must account for: the eShop snapshot and the CoreLib table equal HEAD;
the demo's expected findings differ from HEAD by exactly the two edits of R8; the demo snapshot's fingerprints equal HEAD;
every moved value of the demo snapshot is in the run record. Runs no test.

.PARAMETER Measure
Builds the CLI in Release and generates the two members of R10, each in its own process, into scratch/b4c-gate/measure.json.
Checks nothing; the gates of tasks 7 and 9 check the run record against the file.
#>
param([ValidateRange(1, 9)][int]$Task, [switch]$Full, [switch]$Evals, [switch]$Protected, [switch]$Measure)

if (@(@(($Task -ne 0), [bool]$Full, [bool]$Evals, [bool]$Protected, [bool]$Measure) | Where-Object { $_ }).Count -ne 1)
{
    Write-Host 'FAIL: name exactly one of -Task <1..9>, -Full, -Evals, -Protected and -Measure'
    exit 1
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# git show prints the committed files as UTF-8.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'
$logs = Join-Path $root 'scratch/b4c-gate'
New-Item -ItemType Directory -Force $logs | Out-Null

$RECORD = 'docs/runs/5d-B4c.md'
$SNAPSHOT = 'skills/hunt/evals/models/generator-snapshot.json'
$MEASURE_FILE = Join-Path $logs 'measure.json'
$PARSER_CTOR = 'M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})'
$SERIALIZE = 'M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)'
$OLD_HOLDER_TEST = 'Message_parser_holder_is_covered_and_its_confirmation_passes_the_bound'
$EVAL_CLASSES = @('ModelEvalTests', 'EffectsEvalTests', 'LinqOracleTests', 'AccessorEvalTests')
$SNAPSHOT_SECTIONS = @('members', 'effects', 'linq', 'accessors')

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

# Runs the tests a filter selects, with the model evals switch cleared.
function Invoke-Tests([string]$logName, [string]$filter)
{
    With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
        Invoke-Dotnet "test-$logName" @('test', $solution, '-c', 'Release', '--filter', $filter) | Out-Null
    }
    Write-Host "tests pass: $logName"
}

# Runs the eval tests a filter selects under CH_MODEL_EVALS=1, without recording the snapshot.
function Invoke-EvalTests([string]$logName, [string]$filter)
{
    With-Environment @{ CH_MODEL_EVALS = '1'; CH_MODEL_EVALS_RECORD = $null } {
        Invoke-Dotnet "evals-$logName" @('test', $solution, '-c', 'Release', '--filter', $filter) | Out-Null
    }
    Write-Host "eval tests pass: $logName"
}

function Class-Filter([string[]]$classes)
{
    return (($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|')
}

# The safety facts of the four eval classes, which tasks 3 and 4 run without comparing the snapshot.
function Invoke-EvalSafety
{
    Invoke-EvalTests 'safety' ('FullyQualifiedName~ModelEvalTests.Classified_fates_have_no_unsafe_narrowing|' +
                               'FullyQualifiedName~EffectsEvalTests.No_effect_entry_is_an_unsafe_narrowing|' +
                               'FullyQualifiedName~LinqOracleTests.No_generated_linq_entry_is_narrower_than_the_built_in|' +
                               'FullyQualifiedName~AccessorEvalTests.No_accessor_answer_is_an_unsafe_narrowing')
}

$script:listed = $null

function Listed-Tests
{
    if ($null -eq $script:listed)
    {
        $log = With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
            Invoke-Dotnet 'list-tests' @('test', $solution, '-c', 'Release', '--list-tests')
        }
        $script:listed = @(Get-Content $log | ForEach-Object { $_.Trim() })
    }
    return $script:listed
}

function Is-Listed([string]$name)
{
    return @(Listed-Tests | Where-Object { $_ -like "*.$name" -or $_ -like "*.$name(*" }).Count -gt 0
}

# Checks that each named test, Class.Method or a method the task names without its class, is in the test list.
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
    # A committed file may start with a byte order mark, which the JSON reader refuses.
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

# The run record's lines; the record must exist.
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

# A JSON value as compact text with properties sorted by name and the accesses of a finding in sorted order, so that two
# values compare equal whatever their formatting and the order of their properties and of a finding's two accesses.
function Canon($value, [string]$name = '')
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
            (ConvertTo-Json -InputObject $property.Name -Compress) + ':' + (Canon $property.Value $property.Name)
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($value -is [Collections.IEnumerable])
    {
        $items = @(foreach ($item in $value) { Canon $item })
        if ($name -eq 'accesses')
        {
            $items = @($items | Sort-Object)
        }
        return '[' + ($items -join ',') + ']'
    }
    return [string]$value
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

# Seconds as a line may write them: one decimal with a point or a comma.
function Seconds-Forms($value)
{
    $text = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0}', [double]$value)
    return @($text, $text.Replace('.', ','))
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
# appeared or disappeared in a line with its path and the word added or removed. Given a requirement pattern, the line must
# match it too.
function Assert-Recorded([Collections.Generic.List[object]]$changes, [string]$what, [string]$requirement = '')
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
            $found = @($lines | Where-Object { $_.Contains($change.Path) -and $_.Contains($old) -and $_.Contains($new) -and $_ -match $requirement })
            if ($found.Count -eq 0)
            {
                Fail "$what`: the run record has no line with $($change.Path), $old and $new$(if ($requirement) { " and its requirement" })"
            }
        }
        else
        {
            $found = @($lines | Where-Object { $_.Contains($change.Path) -and $_ -match "\b$($change.Kind)\b" -and $_ -match $requirement })
            if ($found.Count -eq 0)
            {
                Fail "$what`: the run record has no line with $($change.Path) and the word $($change.Kind)$(if ($requirement) { " and its requirement" })"
            }
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
            Fail "$what does not contain '$needle'"
        }
    }
}

function Assert-Lacks([string]$what, [string]$text, [string[]]$needles)
{
    foreach ($needle in $needles)
    {
        if ($text.Contains($needle))
        {
            Fail "$what still contains '$needle'"
        }
    }
}

function Assert-Matches([string]$what, [string]$text, [string[]]$patterns)
{
    foreach ($pattern in $patterns)
    {
        if ($text -notmatch $pattern)
        {
            Fail "$what does not match '$pattern'"
        }
    }
}

# Fails when code of the generation folder reads InstanceExecutions anywhere but in DriverExecutions.cs, the one owner of
# instance membership in generation (task 2). A comment line is not a read.
function Assert-OneMembershipOwner
{
    $folder = Join-Path $root 'src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation'
    $reads = [Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-ChildItem $folder -Recurse -Filter '*.cs' -File | Where-Object { $_.Name -ne 'DriverExecutions.cs' }))
    {
        $number = 0
        foreach ($line in Get-Content $file.FullName)
        {
            $number++
            if ($line.Contains('InstanceExecutions') -and -not $line.TrimStart().StartsWith('//'))
            {
                $reads.Add("$($file.Name):$number")
            }
        }
    }
    if ($reads.Count -ne 0)
    {
        Fail "InstanceExecutions is read outside DriverExecutions.cs: $($reads -join ', ')"
    }
    Write-Host 'generation reads InstanceExecutions only in DriverExecutions.cs'
}

function Copy-Json($value)
{
    return ($value | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
}

# Fails unless demo/expected-findings.json differs from HEAD by exactly the two edits of R8 (D-0023): the finding
# protobuf-parser-factory-at-parse/factory-runs-at-parse moves to 5d-C, and the notDefect
# protobuf-parser-factory-at-parse/descriptor-unseeded, from 5d-B4c until 5d-C on the resource of the case's earlier
# notDefects, is added.
function Assert-ExpectationEdits
{
    $before = Committed-Text 'demo/expected-findings.json' | ConvertFrom-Json
    $after = Working-Text 'demo/expected-findings.json' | ConvertFrom-Json
    if ((Canon $before.schemaVersion) -cne (Canon $after.schemaVersion))
    {
        Fail 'demo/expected-findings.json schemaVersion changed'
    }

    $expected = @{}
    foreach ($list in @('findings', 'notDefects'))
    {
        $expected[$list] = [ordered]@{}
        foreach ($entry in @($before.$list))
        {
            if ($expected[$list].Contains($entry.id))
            {
                Fail "HEAD's $list holds the id $($entry.id) twice"
            }
            $expected[$list][$entry.id] = Canon $entry
        }
    }

    function Committed([string]$list, [string]$id)
    {
        $found = @($before.$list | Where-Object { $_.id -eq $id })
        if ($found.Count -ne 1)
        {
            Fail "HEAD's $list has $($found.Count) entries $id, not one"
        }
        return $found[0]
    }

    $factory = Copy-Json (Committed 'findings' 'protobuf-parser-factory-at-parse/factory-runs-at-parse')
    $factory.phase = '5d-C'
    $expected.findings[$factory.id] = Canon $factory
    $fires = Committed 'notDefects' 'protobuf-parser-factory-at-parse/factory-fires-in-type-initializers'
    $unseeded = [pscustomobject]@{ id = 'protobuf-parser-factory-at-parse/descriptor-unseeded'; phase = '5d-B4c'; until = '5d-C'; resource = $fires.resource }
    $expected.notDefects[$unseeded.id] = Canon $unseeded

    foreach ($list in @('findings', 'notDefects'))
    {
        $actual = [ordered]@{}
        foreach ($entry in @($after.$list))
        {
            if ($actual.Contains($entry.id))
            {
                Fail "$list holds the id $($entry.id) twice"
            }
            $actual[$entry.id] = Canon $entry
        }
        foreach ($id in @(@($expected[$list].Keys) + @($actual.Keys) | Sort-Object -Unique))
        {
            if (-not $actual.Contains($id))
            {
                Fail "$list lacks $id"
            }
            if (-not $expected[$list].Contains($id))
            {
                Fail "$list has $id, which R8 does not add"
            }
            if ($actual[$id] -cne $expected[$list][$id])
            {
                Fail "$list entry $id is not what R8 asks: expected $($expected[$list][$id]), found $($actual[$id])"
            }
        }
    }
    Write-Host 'demo/expected-findings.json differs from HEAD by exactly the two edits of R8'
}

# Fails unless what the run must not change equals HEAD, the expectations carry exactly the edits of R8, the demo
# snapshot's fingerprints equal HEAD (the demo's findings at 5d-B4c are those of HEAD, D-0023), and every moved value of
# the demo snapshot is in the run record.
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
    Assert-ExpectationEdits

    $demoSnapshot = 'src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json'
    $before = Committed-Text $demoSnapshot | ConvertFrom-Json
    $after = Working-Text $demoSnapshot | ConvertFrom-Json
    $old = @($before.counts.fingerprints)
    $new = @($after.counts.fingerprints)
    $removed = @($old | Where-Object { $new -notcontains $_ })
    if ($removed.Count -ne 0)
    {
        Fail "demo.json lost the fingerprints $($removed -join ', ')"
    }
    $added = @($new | Where-Object { $old -notcontains $_ })
    if ($added.Count -ne 0)
    {
        Fail "demo.json gained the fingerprints $($added -join ', '); the demo's findings at 5d-B4c are those of HEAD (D-0023)"
    }

    $changes = [Collections.Generic.List[object]]::new()
    Diff-Json $before $after '' $changes
    Assert-Recorded $changes 'demo.json'
    Write-Host 'eShop snapshot and CoreLib table equal HEAD; demo.json fingerprints equal HEAD'
}

# Every entry of the four answer sets of an evals snapshot, with the name of its set.
function Snapshot-Entries($snapshot)
{
    foreach ($section in $SNAPSHOT_SECTIONS)
    {
        foreach ($entry in @($snapshot.$section))
        {
            [pscustomobject]@{ Section = $section; Entry = $entry }
        }
    }
}

# True when the one members answer that had a model at HEAD and has none now is the MessageParser<T> constructor, answering
# incomplete with the cause unseeded-read: the one loss R9 lets lower fateGoldEntries (D-0024).
function Parser-ModelLost($before, $after)
{
    $now = @{}
    foreach ($entry in @($after.members)) { $now[$entry.member] = $entry }
    $lost = @($before.members | Where-Object { $null -ne $_.model -and $now.ContainsKey($_.member) -and $null -eq $now[$_.member].model })
    if ($lost.Count -ne 1 -or $lost[0].member -cne $PARSER_CTOR)
    {
        return $false
    }
    $answer = $now[$PARSER_CTOR]
    return $answer.modelReason -ceq 'incomplete' -and @($answer.causes) -ccontains 'unseeded-read'
}

# Fails unless the evals snapshot keeps HEAD's entries, packages and body counts, lowers no threshold but the one R9 allows,
# gives every entry its mode, and the run record names every move with the requirement R1-R7 behind it.
function Assert-EvalsSnapshot
{
    $before = Committed-Text $SNAPSHOT | ConvertFrom-Json
    $after = Working-Text $SNAPSHOT | ConvertFrom-Json
    $lines = $null
    $fields = @('reason', 'modelReason', 'classified', 'model', 'detail', 'causes', 'reachedBodies', 'mode')
    # A moved scalar is recorded with both its values, a missing one as null; a moved structure, by its field's name.
    $scalars = @('reason', 'modelReason', 'reachedBodies', 'mode')

    foreach ($section in $SNAPSHOT_SECTIONS)
    {
        $old = @{}
        foreach ($entry in @($before.$section)) { $old[$entry.member] = $entry }
        $new = @{}
        foreach ($entry in @($after.$section)) { $new[$entry.member] = $entry }
        foreach ($member in @(@($old.Keys) + @($new.Keys) | Sort-Object -Unique))
        {
            if (-not $old.ContainsKey($member) -or -not $new.ContainsKey($member))
            {
                Fail "$section`: $member is only on one side"
            }
            $entry = $new[$member]
            if ($entry.PSObject.Properties.Name -notcontains 'mode')
            {
                Fail "$section`: $member has no mode"
            }
            if ((Canon $old[$member].implementation) -ne (Canon $entry.implementation))
            {
                Fail "$section`: the implementation of $member changed; the snapshot was recorded against another install"
            }
            $recorded = $old[$member]
            $moved = @($fields | Where-Object { (Canon $recorded.$_) -ne (Canon $entry.$_) })
            if ($moved.Count -gt 0)
            {
                if ($null -eq $lines) { $lines = Record-Lines }
                $found = @($lines | Where-Object {
                    $line = $_
                    $unnamed = @($moved | Where-Object {
                        if ($_ -in $scalars) { -not ($line.Contains((Shown $recorded.$_)) -and $line.Contains((Shown $entry.$_))) } else { -not $line.Contains($_) }
                    })
                    $line.Contains($member) -and $line -match '\bR[1-7]\b' -and $unnamed.Count -eq 0
                })
                if ($found.Count -eq 0)
                {
                    Fail ("$section`: $member moved ($($moved -join ', ')) and the run record has no line with it, its requirement R1-R7, " +
                          'both values of each moved reason, modelReason, reachedBodies and mode and the name of each other moved field')
                }
            }
        }
    }

    $changes = [Collections.Generic.List[object]]::new()
    $oldPackages = @{}
    foreach ($entry in @($before.packages)) { $oldPackages["$($entry.assembly)|$($entry.implementationVersion)"] = $entry }
    $newPackages = @{}
    foreach ($entry in @($after.packages)) { $newPackages["$($entry.assembly)|$($entry.implementationVersion)"] = $entry }
    foreach ($key in @(@($oldPackages.Keys) + @($newPackages.Keys) | Sort-Object -Unique))
    {
        if (-not $oldPackages.ContainsKey($key) -or -not $newPackages.ContainsKey($key))
        {
            Fail "the packages entry $key is only on one side"
        }
        foreach ($field in @(@($oldPackages[$key].PSObject.Properties.Name) + @($newPackages[$key].PSObject.Properties.Name) | Sort-Object -Unique))
        {
            $left = $oldPackages[$key].$field
            $right = $newPackages[$key].$field
            if ((Canon $left) -eq (Canon $right))
            {
                continue
            }
            if ($field -in @('bodies', 'externBodies'))
            {
                Fail "packages[$key].$field moved from $left to $right; the run changes no library compilation"
            }
            $changes.Add([pscustomobject]@{ Path = "packages[$key].$field"; Kind = 'value'; Old = $left; New = $right })
        }
    }

    foreach ($threshold in @('exact', 'fateGoldEntries', 'effectsExact', 'accessorsExact'))
    {
        $floor = [int]$before.$threshold
        if ($threshold -eq 'fateGoldEntries' -and (Parser-ModelLost $before $after))
        {
            $floor--
        }
        if ([int]$after.$threshold -lt $floor)
        {
            Fail "$threshold fell from $($before.$threshold) to $($after.$threshold); R9 lets only fateGoldEntries fall by one, for the MessageParser<T> constructor's model (D-0024)"
        }
        if ([int]$after.$threshold -ne [int]$before.$threshold)
        {
            $changes.Add([pscustomobject]@{ Path = $threshold; Kind = 'value'; Old = $before.$threshold; New = $after.$threshold })
        }
    }
    foreach ($group in @(@($before.linqExact.PSObject.Properties.Name) + @($after.linqExact.PSObject.Properties.Name) | Sort-Object -Unique))
    {
        if ($null -eq $before.linqExact.$group -or $null -eq $after.linqExact.$group)
        {
            Fail "linqExact.$group is only on one side"
        }
        if ([int]$after.linqExact.$group -lt [int]$before.linqExact.$group)
        {
            Fail "linqExact.$group fell from $($before.linqExact.$group) to $($after.linqExact.$group)"
        }
        if ([int]$after.linqExact.$group -ne [int]$before.linqExact.$group)
        {
            $changes.Add([pscustomobject]@{ Path = "linqExact.$group"; Kind = 'value'; Old = $before.linqExact.$group; New = $after.linqExact.$group })
        }
    }
    Assert-Recorded $changes 'evals snapshot numbers' '\bR[1-7]\b'
    Write-Host 'evals snapshot: same entries, packages and bodies, no threshold fell, every entry has its mode, every move is in the run record'
}

# Runs the four eval classes without recording the snapshot, then compares the snapshot with HEAD (G2).
function Assert-Evals
{
    Invoke-EvalTests 'model-evals' (Class-Filter $EVAL_CLASSES)
    Assert-EvalsSnapshot
}

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

# The fingerprint the last Release build wrote into the generator's intermediate folder.
function Built-Fingerprint
{
    $files = @(Get-ChildItem (Join-Path $root 'src/ConcurrencyHunter.Core/obj/Release') -Recurse -File -Filter 'GeneratorVersion.g.cs')
    if ($files.Count -ne 1)
    {
        Fail "expected one GeneratorVersion.g.cs under src/ConcurrencyHunter.Core/obj/Release, found $($files.Count)"
    }
    $match = [regex]::Match((Get-Content $files[0].FullName -Raw), '"([0-9a-f]{64})"')
    if (-not $match.Success)
    {
        Fail "$($files[0].FullName) holds no 64-hex fingerprint"
    }
    return $match.Groups[1].Value
}

# Generates one member with the CLI in its own process and returns what R10 measures. A process past 300 s is stopped by
# its PID and recorded as stopped; a generate that fails fails the measure.
function Generate-Measured([string]$exe, [string]$key, [string]$member, [string[]]$request)
{
    $answer = Join-Path $logs "measure-$key.json"
    Remove-Item $answer -ErrorAction SilentlyContinue
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($argument in @(@('generate') + $request + @('--member', $member, '--out', $answer)))
    {
        $info.ArgumentList.Add($argument)
    }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $stopped = $false
    while (-not $process.WaitForExit(200))
    {
        if ($watch.Elapsed.TotalSeconds -gt 300)
        {
            Stop-Process -Id $process.Id -Force
            $stopped = $true
            break
        }
    }
    $process.WaitForExit()
    $watch.Stop()
    Set-Content (Join-Path $logs "measure-$key.log") -Encoding utf8 -Value ($stdout.Result + $stderr.Result)
    $result = [ordered]@{ key = $key; member = $member; request = $request; wallSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1); stopped = $stopped }
    if ($stopped)
    {
        Write-Host "R10: $member ran past 300 s and was stopped (PID $($process.Id))"
        return [pscustomobject]$result
    }
    if ($process.ExitCode -ne 0)
    {
        Fail "generate of $member exited $($process.ExitCode); log: $(Join-Path $logs "measure-$key.log")"
    }
    $document = Get-Content $answer -Raw -Encoding utf8 | ConvertFrom-Json
    $generation = $document.generation
    $result.mode = $generation.mode
    $result.reason = $document.reason
    $result.generationSeconds = $generation.seconds
    $result.reachedBodies = $generation.reachedBodies
    $result.stages = $generation.stages
    $result.attempts = @($generation.attempts)
    $result.implementation = if ($null -eq $generation.implementation) { $null } else {
        [ordered]@{ assemblyVersion = $generation.implementation.assemblyVersion; fileVersion = $generation.implementation.fileVersion
                    mvid = $generation.implementation.mvid }
    }
    Write-Host "R10: $member in mode $($result.mode), reason $(Shown $result.reason), $($result.wallSeconds) s"
    return [pscustomobject]$result
}

# Builds the CLI in Release and measures the two members of R10 into scratch/b4c-gate/measure.json (task 7).
function Measure-Members
{
    Invoke-Dotnet 'build-cli' @('build', (Join-Path $root 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj'), '-c', 'Release',
                                '--disable-build-servers', '-nodeReuse:false') | Out-Null
    $exe = Join-Path $root 'src/ConcurrencyHunter.Cli/bin/Release/net10.0/win-x64/concurrency-hunter.exe'
    if (-not (Test-Path $exe))
    {
        Fail "$exe does not exist after the build"
    }
    # SerializeToUtf8Bytes is asked for as the effects evals ask for it, so its answer opens the same assembly.
    $gold = @(Working-Text 'skills/hunt/evals/models/effects-gold.json' | ConvertFrom-Json | Where-Object { $_.id -eq $SERIALIZE })
    if ($gold.Count -ne 1)
    {
        Fail "effects-gold.json has $($gold.Count) entries $SERIALIZE, not one"
    }
    $serializeRequest = @('--assembly', $gold[0].assembly, '--version', $gold[0].version)
    if ($gold[0].package) { $serializeRequest += @('--package', $gold[0].package) }
    if ($gold[0].framework) { $serializeRequest += @('--framework', $gold[0].framework) }

    $members = @(
        Generate-Measured $exe 'serialize' $SERIALIZE $serializeRequest
        Generate-Measured $exe 'parser' $PARSER_CTOR @('--assembly', 'Google.Protobuf', '--version', '3.30.2', '--package', 'Google.Protobuf')
    )
    [ordered]@{ fingerprint = Built-Fingerprint; members = $members } | ConvertTo-Json -Depth 16 | Set-Content $MEASURE_FILE -Encoding utf8
    Write-Host "R10: written $MEASURE_FILE"
}

# One measured member of measure.json by its key.
function Measured($measured, [string]$key)
{
    $found = @($measured.members | Where-Object { $_.key -eq $key })
    if ($found.Count -ne 1)
    {
        Fail "$MEASURE_FILE has $($found.Count) members '$key', not one"
    }
    return $found[0]
}

# Fails unless the line of a measured member is in the section: one line with its full id, its mode and reason, its total
# seconds (the process's or the generation's) and, for each attempt, the bodies of its fate run and of its confirmation run.
function Assert-MeasuredLine([string[]]$lines, $measured)
{
    if ($measured.stopped)
    {
        $found = @($lines | Where-Object { $_.Contains($measured.member) -and $_.Contains('300') })
        if ($found.Count -eq 0)
        {
            Fail "## Замеры has no line with $($measured.member) and the 300 s it was stopped at"
        }
        return
    }
    # In order: the answer's mode and reason, each stage before the first attempt as name=seconds, then per attempt its mode,
    # its fate and confirmation bodies and each of its stages as name=seconds.
    $sequence = [Collections.Generic.List[string]]::new()
    $sequence.Add((Word-Pattern (Shown $measured.mode)))
    $sequence.Add((Word-Pattern (Shown $measured.reason)))
    foreach ($stage in @($measured.stages.PSObject.Properties))
    {
        $sequence.Add((Stage-Pattern $stage.Name $stage.Value))
    }
    foreach ($attempt in @($measured.attempts))
    {
        $sequence.Add((Word-Pattern (Shown $attempt.mode)))
        $sequence.Add((Word-Pattern (Shown $attempt.reachedBodies)))
        $sequence.Add((Word-Pattern (Shown $attempt.confirmationBodies)))
        foreach ($stage in @($attempt.stages.PSObject.Properties))
        {
            $sequence.Add((Stage-Pattern $stage.Name $stage.Value))
        }
    }
    $seconds = @(Seconds-Forms $measured.wallSeconds) + @(Seconds-Forms $measured.generationSeconds)
    $found = @($lines | Where-Object {
        $line = $_
        $at = 0
        $ordered = $true
        foreach ($pattern in $sequence)
        {
            $match = [regex]::new($pattern).Match($line, $at)
            if (-not $match.Success)
            {
                $ordered = $false
                break
            }
            $at = $match.Index + $match.Length
        }
        $line.Contains($measured.member) -and $ordered -and @($seconds | Where-Object { $line.Contains($_) }).Count -gt 0
    })
    if ($found.Count -eq 0)
    {
        Fail ("## Замеры has no line with $($measured.member) and its total seconds ($($measured.wallSeconds) or $($measured.generationSeconds)) " +
              "that holds, in this order: $($sequence -join ' ')")
    }
}

# A value as a whole word of a line.
function Word-Pattern([string]$value)
{
    return '(?<![\w.])' + [regex]::Escape($value) + '(?![\w])'
}

# A stage's seconds as a line writes them: name=seconds with one decimal, a point or a comma.
function Stage-Pattern([string]$name, $value)
{
    $text = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0}', [double]$value)
    return '(?<![\w])' + [regex]::Escape($name) + '\s*=\s*' + [regex]::Escape($text).Replace('\.', '[.,]') + '(?![\d])'
}

# Fails unless measure.json was made by the generator of the current sources from the implementations R10 names, and the
# section ## Замеры of the run record agrees with it and with the evals snapshot (tasks 7 and 9, G3).
function Assert-Measurements
{
    if (-not (Test-Path $MEASURE_FILE))
    {
        Fail "$MEASURE_FILE does not exist; run -Measure once the evals snapshot is rewritten"
    }
    $measured = Get-Content $MEASURE_FILE -Raw -Encoding utf8 | ConvertFrom-Json
    $current = Source-Fingerprint
    if ($measured.fingerprint -ne $current)
    {
        Fail "$MEASURE_FILE was made by the generator $($measured.fingerprint), and the sources build $current; run -Measure again"
    }
    $snapshotNow = Working-Text $SNAPSHOT | ConvertFrom-Json
    $committed = Committed-Text $SNAPSHOT | ConvertFrom-Json
    $serializeMeasure = Measured $measured 'serialize'
    $parserMeasure = Measured $measured 'parser'

    $recorded = @(Snapshot-Entries $snapshotNow | Where-Object { $_.Entry.member -eq $SERIALIZE })
    if ($recorded.Count -eq 0)
    {
        Fail "the evals snapshot has no entry $SERIALIZE"
    }
    foreach ($field in @('assemblyVersion', 'fileVersion', 'mvid'))
    {
        if ($serializeMeasure.stopped -or $serializeMeasure.implementation.$field -ne $recorded[0].Entry.implementation.$field)
        {
            Fail "the measured $SERIALIZE has $field $($serializeMeasure.implementation.$field), the snapshot's $($recorded[0].Entry.implementation.$field)"
        }
    }
    foreach ($field in @('assemblyVersion', 'fileVersion'))
    {
        if ($parserMeasure.stopped -or $parserMeasure.implementation.$field -ne '3.30.2.0')
        {
            Fail "the measured $PARSER_CTOR has $field $($parserMeasure.implementation.$field), not 3.30.2.0"
        }
    }

    $lines = Section-Lines 'Замеры'
    Assert-MeasuredLine $lines $serializeMeasure
    Assert-MeasuredLine $lines $parserMeasure

    # The table of off answers: one row per member of the snapshot answered in mode off, with its bodies and HEAD's.
    $expected = @{}
    foreach ($item in @(Snapshot-Entries $snapshotNow | Where-Object { $_.Entry.mode -eq 'off' }))
    {
        $member = $item.Entry.member
        $head = @(@($committed.($item.Section)) | Where-Object { $_.member -eq $member })
        $row = "$(Shown $item.Entry.reachedBodies)|$(if ($head.Count -eq 0) { 'null' } else { Shown $head[0].reachedBodies })"
        if ($expected.ContainsKey($member) -and $expected[$member] -ne $row)
        {
            Fail "$member is answered in mode off in two sets with different bodies; the table cannot hold one row for it"
        }
        $expected[$member] = $row
    }
    $header = -1
    for ($index = 0; $index -lt $lines.Count; $index++)
    {
        if (($lines[$index] -replace '\s+', ' ').Trim() -eq '| member | reachedBodies | HEAD reachedBodies |')
        {
            $header = $index
            break
        }
    }
    if ($header -lt 0)
    {
        Fail '## Замеры has no table with the header | member | reachedBodies | HEAD reachedBodies |'
    }
    $actual = @{}
    for ($index = $header + 1; $index -lt $lines.Count -and $lines[$index].TrimStart().StartsWith('|'); $index++)
    {
        $cells = @($lines[$index].Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim().Trim('`').Trim() })
        if (@($cells | Where-Object { $_ -notmatch '^:?-+:?$' }).Count -eq 0)
        {
            continue
        }
        if ($cells.Count -ne 3)
        {
            Fail "the table row '$($lines[$index])' does not have three cells"
        }
        if ($actual.ContainsKey($cells[0]))
        {
            Fail "the table has two rows for $($cells[0])"
        }
        $actual[$cells[0]] = "$($cells[1])|$($cells[2])"
    }
    foreach ($member in @(@($expected.Keys) + @($actual.Keys) | Sort-Object -Unique))
    {
        if (-not $actual.ContainsKey($member))
        {
            Fail "the table has no row for $member, answered in mode off ($($expected[$member]))"
        }
        if (-not $expected.ContainsKey($member))
        {
            Fail "the table has a row for $member, which the snapshot does not answer in mode off"
        }
        if ($actual[$member] -ne $expected[$member])
        {
            Fail "the table row of $member says $($actual[$member]); the snapshot and HEAD say $($expected[$member])"
        }
    }
    Write-Host "the table holds the $($expected.Count) answers in mode off"

    $off = @($parserMeasure.attempts | Where-Object { $_.mode -eq 'off' })
    if ($off.Count -ne 1)
    {
        Fail "the measured $PARSER_CTOR has $($off.Count) attempts in mode off, not one"
    }
    $confirmations = @($lines | ForEach-Object { [regex]::Match($_, 'MessageParser<T> confirmation: (\d+) vs 402') } | Where-Object { $_.Success })
    if ($confirmations.Count -ne 1)
    {
        Fail "## Замеры has $($confirmations.Count) lines 'MessageParser<T> confirmation: <n> vs 402', not one"
    }
    if ([string]$confirmations[0].Groups[1].Value -ne (Shown $off[0].confirmationBodies))
    {
        Fail "the confirmation line says $($confirmations[0].Groups[1].Value); measure.json's off attempt says $(Shown $off[0].confirmationBodies)"
    }

    $timeouts = @(Snapshot-Entries $snapshotNow | Where-Object { $_.Entry.reason -eq 'timeout' } | ForEach-Object { $_.Entry.member } | Sort-Object -Unique)
    $written = @($lines | ForEach-Object {
        $at = $_.IndexOf('timeout:')
        if ($at -ge 0 -and $_.Substring(0, $at) -match '^[\s\-*`]*$')
        {
            ($_.Substring($at + 'timeout:'.Length).Trim() -split '\s+')[0].Trim('`')
        }
    } | Sort-Object -Unique)
    if (($timeouts -join "`n") -ne ($written -join "`n"))
    {
        Fail "the timeout: lines name $($written.Count) members ($($written -join ', ')); the snapshot answers timeout for $($timeouts.Count) ($($timeouts -join ', '))"
    }
    Write-Host 'measure.json and the run record agree'
}

# The table row of a question in docs/QUESTIONS.md.
function Question-Row([string[]]$questions, [int]$number)
{
    $rows = @($questions | Where-Object { $_.StartsWith("| $number |") })
    if ($rows.Count -ne 1)
    {
        Fail "docs/QUESTIONS.md has $($rows.Count) rows for question $number, not one"
    }
    return $rows[0]
}

# Fails unless the documents describe the run as R11 asks and the run record's measurements agree with measure.json.
function Assert-Documents
{
    Invoke-Script 'check-docs' 'check-docs.ps1'

    $adrs = @(Get-ChildItem (Join-Path $root 'docs/adr') -Filter '0026-*.md' -File)
    if ($adrs.Count -ne 1)
    {
        Fail "docs/adr has $($adrs.Count) files 0026-*.md, not one"
    }
    $adr = Get-Content $adrs[0].FullName -Raw -Encoding utf8
    foreach ($number in @('0006', '0013', '0017', '0019', '0024', '0025'))
    {
        if ($adr -notmatch "ADR $number\b|\b$number-[a-z]")
        {
            Fail "$($adrs[0].Name) does not name ADR $number"
        }
    }

    $spec = Working-Text 'docs/SPEC.md'
    Assert-Contains 'docs/SPEC.md' $spec @('120 с на член')
    Assert-Lacks 'docs/SPEC.md' $spec @('60 с на член', 'предел члена — 60 с')
    $g6 = @($spec -split "`r?`n" | Where-Object { $_.StartsWith('Ответ G-6.') })
    if ($g6.Count -ne 1)
    {
        Fail "docs/SPEC.md has $($g6.Count) paragraphs 'Ответ G-6.', not one"
    }
    Assert-Contains 'G-6' $g6[0] @('`schemaVersion` 4', '`mode`', '`stages`', '`attempts`')
    Assert-Matches 'G-6' $g6[0] @('`analysis-failed`[^`]{0,12}`timeout`')
    $td034b = [regex]::Match($spec, '(?s)\*\*TD-034b\.\*\*.*?(?=\*\*TD-034c\.\*\*)')
    if (-not $td034b.Success)
    {
        Fail 'docs/SPEC.md has no TD-034b before TD-034c'
    }
    # The off treatment reads "оставляет инициализатор вне набора" or "инициализатор, оставленный вне набора".
    Assert-Matches 'TD-034b' $td034b.Value @('инертн', 'оставл\w*\s+(\S+\s+){0,2}вне\s+набора', 'initializer-work-not-run')

    $context = Working-Text 'CONTEXT.md'
    Assert-Matches 'CONTEXT.md' $context @('(?m)^\*\*Inert type initializer\*\*:', '(?m)^\*\*Recipe value\*\*:')

    $questions = @((Working-Text 'docs/QUESTIONS.md') -split "`r?`n")
    $row101 = Question-Row $questions 101
    if ($row101.StartsWith('| 101 | Закрыт'))
    {
        Fail 'the row of question 101 is closed; it stays open (D-0023)'
    }
    Assert-Contains 'question 101' $row101 @('NullValueDescriptor', '5d-C')
    $committedQuestions = @((Committed-Text 'docs/QUESTIONS.md') -split "`r?`n")
    if ((Question-Row $questions 178) -ne (Question-Row $committedQuestions 178))
    {
        Fail 'the row of question 178 differs from HEAD'
    }

    $plan = @((Working-Text 'docs/PLAN.md') -split "`r?`n")
    $rowB4c = @($plan | Where-Object { $_ -match '^\| \*\*5d run B4c\*\* \|' })
    if ($rowB4c.Count -ne 1 -or $rowB4c[0] -notmatch '^\| \*\*5d run B4c\*\* \| Построено')
    {
        Fail "docs/PLAN.md has no single row of 5d run B4c that says 'Построено'"
    }
    $rowC = @($plan | Where-Object { $_ -match '^\| \*\*5d run C\*\* \|' })
    if ($rowC.Count -ne 1)
    {
        Fail "docs/PLAN.md has $($rowC.Count) rows of 5d run C, not one"
    }
    Assert-Contains 'the PLAN row of 5d run C' $rowC[0] @('protobuf-parser-factory-at-parse')

    $scenarios = @((Working-Text 'demo/SCENARIOS.md') -split "`r?`n" | Where-Object { $_.Contains('`protobuf-parser-factory-at-parse`') -and $_.StartsWith('|') })
    if ($scenarios.Count -ne 1)
    {
        Fail "demo/SCENARIOS.md has $($scenarios.Count) rows of protobuf-parser-factory-at-parse, not one"
    }
    Assert-Contains 'the SCENARIOS row of protobuf-parser-factory-at-parse' $scenarios[0] @('`5d-B4c`', '/descriptor-unseeded', '`5d-C`', '0026')

    Assert-Contains 'skills/hunt/evals/models/README.md' (Working-Text 'skills/hunt/evals/models/README.md') @('`mode`', '5d-B4c.md')

    $recordText = Working-Text $RECORD
    foreach ($heading in @('## Model evals', '## Замеры', '## Demo', '## Генерация CoreLib', '## Остаток'))
    {
        if ($recordText -notmatch "(?m)^$([regex]::Escape($heading))[ \t]*\r?$")
        {
            Fail "$RECORD has no section '$heading'"
        }
    }
    Assert-Contains $RECORD $recordText @('4da13b0', 'generate-corelib.ps1 -Since')
    Assert-Measurements
    Write-Host 'docs hold the run'
}

try
{
    if ($Protected)
    {
        Assert-Protected
        Write-Output 'check-phase5d-runB4c protected ok'
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
        Write-Output 'check-phase5d-runB4c full ok'
        exit 0
    }

    if ($Evals)
    {
        Assert-Evals
        Write-Output 'check-phase5d-runB4c evals ok'
        exit 0
    }

    if ($Measure)
    {
        Measure-Members
        Write-Output 'check-phase5d-runB4c measure ok'
        exit 0
    }

    switch ($Task)
    {
        1
        {
            Assert-Listed @('TypeInitializerTreatmentMatrixTests.Every_cell_gets_its_expected_treatment',
                            'TypeInitializerTreatmentMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'InertInitializerWorkMatrixTests.Every_cell_gets_its_expected_treatment',
                            'InertInitializerWorkMatrixTests.Every_cell_starts_its_work_as_expected',
                            'InertInitializerWorkMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'An_inert_initializers_object_is_one_object_per_process',
                            'A_merged_instance_runs_if_any_of_its_types_runs_its_initializer',
                            'A_delegate_an_inert_initializer_hands_to_unseen_code_has_it_as_handing_execution')
            Assert-EShopReady
            Invoke-Tests 'engine' (Class-Filter @('TypeInitializerTreatmentMatrixTests', 'InertInitializerWorkMatrixTests', 'ReachableSetTests',
                                                  'PointsToTests', 'OwnershipAndConstructionTests', 'ConstructionSetTests', 'MustHeldLockTests',
                                                  'ConflictRuleTests', 'FindingIdentityTests', 'LateReceiverTests', 'DiSemanticsTests',
                                                  'CanonicalOrderTests', 'WalkSchedulingTests', 'EngineCounterTests', 'SpawnExecutionTests',
                                                  'TimerTests', 'TreeConfinedTests', 'OpaqueDelegateExecutionTests', 'CandidateIndexTests',
                                                  'EngineCancellationTests', 'StageCancellationTests', 'WorkCancellationTests',
                                                  'ReportSkeletonTests', 'ProgramIndexTests', 'EventLoweringTests', 'IrConstructionTests',
                                                  'CoreLibGenerationSetMatrixTests', 'ConstructedValueTypeMatrixTests',
                                                  'DefaultComparerSelectionMatrixTests', 'ComparisonWitnessMatrixTests',
                                                  'NullableUnseenWriteMatrixTests', 'ReachabilityRuleSelectionTests', 'CancellationCoverageTests',
                                                  'EShopSnapshotTests'))
        }
        2
        {
            Assert-Listed @('InertInitializerSharingMatrixTests.Every_cell_answers_as_without_the_initializer',
                            'InertInitializerSharingMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'AllocationsTests.An_object_an_inert_initializers_helper_made_is_the_librarys_before',
                            'AllocationsTests.An_object_made_by_an_inert_initializers_helper_and_by_setup_is_classified_as_with_running_initializers',
                            'GenerationHandoffsTests.A_probe_an_inert_initializer_hands_to_unseen_code_is_not_handed',
                            'ConfirmationAccessesTests.Each_execution_role_is_collected_as_its_readers_read_it')
            Assert-OneMembershipOwner
            Invoke-Tests 'inert-generation' (Class-Filter @('AllocationsTests', 'LibraryStaticHandoffMatrixTests', 'InertInitializerSharingMatrixTests',
                                                            'GenerationHandoffsTests', 'GenerationCauseTests', 'EffectReaderTests',
                                                            'GeneratedEntryTests', 'GeneratedEntryMatrixTests', 'GeneratedFateTests',
                                                            'GeneratedStructEffectMatrixTests', 'AutoAccessorGenerationMatrixTests',
                                                            'ComparisonWitnessMatrixTests', 'ConfirmationAccessesTests', 'DriverExecutionsTests',
                                                            'ValueProvenanceTests', 'ValueObservationTests', 'NotRunClassificationTests',
                                                            'PackageReachabilityTests', 'ReachabilityRuleSelectionTests',
                                                            'TypeInitializerTreatmentMatrixTests', 'ModelGeneratorCancellationTests',
                                                            'CancellationCoverageTests'))
        }
        3
        {
            Assert-Listed @('InitializerModeSwitchTests.A_member_whose_runs_fit_answers_with_inert_initializers',
                            'InitializerModeSwitchTests.A_fate_run_past_the_bound_with_inert_initializers_is_generated_again_with_them_left_out',
                            'InitializerModeSwitchTests.A_confirmation_past_the_bound_with_inert_initializers_is_generated_again_with_them_left_out',
                            'InitializerModeSwitchTests.Past_the_bound_in_both_modes_is_closure_bound',
                            'InitializerModeSwitchTests.A_confirmation_past_the_bound_in_both_modes_leaves_the_holder_unknown_execution',
                            'InitializerModeSwitchTests.CoreLib_is_generated_with_running_initializers_only',
                            'InitializerModeSwitchTests.The_test_override_off_runs_both_attempts',
                            'InitializerModeSwitchTests.Only_the_last_seeding_pass_and_the_confirmation_decide_the_switch',
                            'InitializerModeSwitchTests.The_off_copy_is_compiled_once_per_library_and_released_with_it',
                            'ReachableSetTests.Edges_are_not_recorded_by_default',
                            'ReachableSetTests.Every_kind_of_reach_records_its_edge',
                            'ReachableSetTests.A_body_reached_twice_records_both_edges',
                            'ReachableSetTests.A_delegate_target_registered_after_its_invocations_records_an_edge_from_each',
                            'ReachableSetTests.A_trigger_added_to_a_built_construction_records_its_edge',
                            'ReachableSetTests.Roots_startup_members_and_root_constructions_have_no_source',
                            'RecipeStorageOpeningMatrixTests.Every_cell_opens_as_expected',
                            'RecipeStorageOpeningMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'LibraryCompilationTests.Waiting_for_an_off_copy_another_attempt_opens_stops_with_the_waiters_token',
                            'LibraryCompilationTests.A_waiter_whose_opener_was_cancelled_opens_the_off_copy_itself',
                            'LibraryCompilationTests.An_off_copy_cancelled_by_its_opener_is_opened_again_by_the_next_attempt',
                            'InitializerReachedBodyScanMatrixTests.Every_cell_gets_its_expected_answer',
                            'InitializerReachedBodyScanMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'InitializerWorkStartMatrixTests.Every_cell_gets_its_expected_cause',
                            'InitializerWorkStartMatrixTests.Off_refuses_wherever_running_initializers_start_work',
                            'InitializerWorkStartMatrixTests.Every_axis_value_occurs_in_a_cell')
            Invoke-Tests 'mode-switch' (Class-Filter @('InitializerModeSwitchTests', 'InitializerReachedBodyScanMatrixTests', 'InitializerWorkStartMatrixTests',
                                                       'RecipeStorageOpeningMatrixTests', 'OpenedFieldsTests', 'LibraryCompilationTests',
                                                       'ReachableSetTests', 'StructPlaceLoweringTests', 'ProgramIndexTests', 'MustHeldLockTests',
                                                       'ModelGenerationModeTests', 'AccessorDriverTests', 'GeneratedFateTests', 'SeedTests',
                                                       'ComparisonWitnessMatrixTests', 'AllocationsTests', 'EffectReaderTests',
                                                       'GenerationCauseTests', 'GeneratedEntryTests', 'LibraryStaticHandoffMatrixTests',
                                                       'InertInitializerSharingMatrixTests', 'GeneratedStructEffectMatrixTests',
                                                       'AutoAccessorGenerationMatrixTests', 'ConfirmationAccessesTests',
                                                       'ModelGeneratorCancellationTests', 'CancellationCoverageTests'))
            Invoke-EvalSafety
        }
        4
        {
            Assert-Listed @('RecipeValueMatrixTests.Every_cell_gets_its_expected_recipe_value',
                            'RecipeValueMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'RecipeValueMatrixTests.No_cell_leaves_a_rejected_candidate_behind',
                            'RecipeValueMatrixTests.A_static_without_a_recipe_value_leaves_the_driver_synthesized',
                            'RecipeValueDiscoveryMatrixTests.Every_cell_gets_its_recipe_value_as_expected',
                            'RecipeValueDiscoveryMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'RecipeValueUnseededMatrixTests.Every_cell_reads_unseeded_as_expected',
                            'RecipeValueUnseededMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'RecipeValueProvenanceMatrixTests.Every_cell_answers_as_its_run_twin',
                            'RecipeValueProvenanceMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'RecipeStorageOpeningMatrixTests.Every_writable_off_storage_gets_its_recipe_value',
                            'InitializerModeSwitchTests.A_recipe_value_in_a_static_readonly_lock_is_one_object_per_process',
                            'InitializerModeSwitchTests.A_static_only_the_confirmation_loads_gets_a_recipe_value',
                            'InitializerModeSwitchTests.A_store_through_an_unseeded_static_gives_incomplete',
                            'InitializerModeSwitchTests.A_static_the_off_copy_cannot_write_is_unseeded',
                            'InitializerModeSwitchTests.A_whole_struct_stored_through_a_ref_to_an_unseeded_static_gives_incomplete')
            Invoke-Tests 'recipe-values' (Class-Filter @('RecipeValueMatrixTests', 'RecipeValueDiscoveryMatrixTests', 'RecipeValueUnseededMatrixTests',
                                                         'RecipeValueProvenanceMatrixTests', 'RecipeStorageOpeningMatrixTests',
                                                         'InitializerModeSwitchTests', 'InitializerReachedBodyScanMatrixTests', 'GeneratedFateTests',
                                                         'SeedTests', 'ValueSourceMatrixTests', 'CollectionValueMatrixTests',
                                                         'DriverRollbackMatrixTests', 'DriverSynthesisTests', 'KeyJournalTests', 'ParameterKindsTests',
                                                         'ComparisonWitnessMatrixTests', 'AllocationsTests', 'EffectReaderTests',
                                                         'GenerationCauseTests', 'GeneratedEntryTests', 'LibraryStaticHandoffMatrixTests',
                                                         'GeneratedStructEffectMatrixTests', 'AutoAccessorGenerationMatrixTests',
                                                         'ConfirmationAccessesTests', 'ModelGeneratorCancellationTests', 'CancellationCoverageTests'))
            Invoke-EvalSafety
        }
        5
        {
            Assert-Listed @('LibraryStaticFormMatrixTests.Every_cell_gets_its_expected_answer',
                            'LibraryStaticFormMatrixTests.No_cell_gives_a_model_where_running_initializers_refuse',
                            'LibraryStaticFormMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'LibraryStaticFormMatrixTests.An_initializer_handing_a_region_that_holds_a_seed_differs_from_run_only_in_its_refusal')
            Invoke-Tests 'static-forms' (Class-Filter @('LibraryStaticFormMatrixTests', 'InitializerModeSwitchTests', 'RecipeValueMatrixTests',
                                                        'InitializerReachedBodyScanMatrixTests', 'InitializerWorkStartMatrixTests',
                                                        'LibraryStaticHandoffMatrixTests'))
        }
        6
        {
            Assert-Listed @('GenerationRecordTests.Stage_seconds_add_up_to_the_generation_seconds',
                            'GenerationRecordTests.An_answer_after_a_retry_records_both_attempts',
                            'GenerationRecordTests.Top_level_reached_bodies_are_those_of_the_answers_attempt',
                            'GenerationRecordTests.A_refusal_before_any_attempt_has_no_mode',
                            'GenerationRecordTests.A_timeout_answer_carries_the_stages_up_to_the_cut',
                            'GenerationRecordTests.The_member_limit_covers_both_attempts',
                            'GenerationRecordTests.An_outside_cancellation_is_not_a_timeout',
                            'GenerateCommandTests.A_generation_past_the_member_limit_writes_a_timeout_answer',
                            'RunGenerationTests.Default_limits_are_the_server_constants')
            Invoke-Tests 'generation-record' (Class-Filter @('GenerationRecordTests', 'GeneratedDocumentTests', 'RunGenerationTests',
                                                             'RunGenerationPassTests', 'GeneratedModelFilesTests', 'GeneratedCoverageTests',
                                                             'ModelGenerationModeTests', 'GeneratedFileVersionMatrixTests', 'EntryComparatorTests',
                                                             'UnsafeNarrowingTests', 'GenerateCommandTests', 'ModelGeneratorCancellationTests',
                                                             'StageCancellationTests', 'WorkCancellationTests', 'ModelEntryWriterTests',
                                                             'InitializerModeSwitchTests', 'CancellationCoverageTests', 'GeneratorBoundaryTests'))
        }
        7
        {
            if (Is-Listed "ModelEvalTests.$OLD_HOLDER_TEST")
            {
                Fail "ModelEvalTests.$OLD_HOLDER_TEST is still in the test list; its claim is reversed and it is renamed"
            }
            $renamed = @(Listed-Tests | Where-Object { $_ -like '*.ModelEvalTests.Message_parser_holder_is_*' } |
                         ForEach-Object { ($_ -split '\(')[0] } | Sort-Object -Unique)
            if ($renamed.Count -ne 1)
            {
                Fail "expected one renamed test ModelEvalTests.Message_parser_holder_is_..., found $($renamed.Count): $($renamed -join ', ')"
            }
            Write-Host "renamed: $($renamed[0])"
            Assert-Evals
            Assert-Measurements
        }
        8
        {
            Assert-Contains 'DemoExpectationTests.cs' (Working-Text 'src/ConcurrencyHunter.Core.Tests/DemoExpectationTests.cs') @('CURRENT_POINT = "5d-B4c"')
            Invoke-Tests 'demo' (Class-Filter @('DemoExpectationTests', 'DemoGeneratedModelsTests', 'MetricsCommandTests', 'PublishedExecutableEndToEndTests',
                                                'ExpectationFileRuleTests', 'ExpectationMatcherTests', 'GenerateCommandTests'))
            Invoke-Script 'check-demo-expectations' 'check-demo-expectations.ps1'
            Assert-Protected
            Assert-Contains $RECORD (Working-Text $RECORD) @('## Demo', 'Точка сравнения — `5d-B4c`')
            Invoke-Script 'rerecord' 'check-baseline-rerecord.ps1'
            Write-Host 'the rewritten baseline loses no case against HEAD'
        }
        9
        {
            Assert-Documents
        }
    }

    Write-Output "check-phase5d-runB4c task $Task ok"
    exit 0
}
catch
{
    $message = $_.Exception.Message
    if ($message.StartsWith('GATE: '))
    {
        Write-Host "FAIL: $($message.Substring(6))"
    }
    else
    {
        Write-Host "FAIL: $message"
        Write-Host $_.ScriptStackTrace
    }
    exit 1
}
