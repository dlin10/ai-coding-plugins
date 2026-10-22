<#
.SYNOPSIS
The gate checks of concurrency-hunter phase 5d run B4, one task at a time.

.DESCRIPTION
Each task of the run's plan calls this script with its number. Every dotnet command and every nested script writes its
output to a log under scratch/b4-gate/ and is judged by its exit code; a failure prints the log's tail and exits 1. A task
that adds tests first checks that each of them is in the test list, so a gate cannot pass on a filter that matches nothing.
Outside -Evals and tasks 8, 9 and 11 the model evals switch is cleared for the tests; tasks 8 and 9 set it for their one
named eval fact only. Whatever the script puts into the environment it puts back.

.PARAMETER Task
The task of the plan whose checks to run, 1 to 13.

.PARAMETER Fix
Runs the short fix gate: the test classes the run adds or changes, without the tests of installed libraries, the model
evals, the demo and eShop.

.PARAMETER Full
Runs the whole suite through check-test-baseline.ps1 (CH_ESHOP_ROOT must be set), then -Protected, then
check-baseline-rerecord.ps1 against HEAD.

.PARAMETER Evals
Runs the model evals without recording the snapshot, then compares the snapshot with HEAD.

.PARAMETER Protected
Compares with HEAD what the run must not change or must account for: the eShop snapshot, the CoreLib table, the demo's
findings, groups and fingerprints, the demo's expected findings (exactly the run's R1 edits) and every moved value of the
demo snapshot, which the run record must name. Runs no test.
#>
param([ValidateRange(1, 13)][int]$Task, [switch]$Fix, [switch]$Full, [switch]$Evals, [switch]$Protected)

if (@(@(($Task -ne 0), [bool]$Fix, [bool]$Full, [bool]$Evals, [bool]$Protected) | Where-Object { $_ }).Count -ne 1)
{
    Write-Host 'FAIL: name exactly one of -Task <1..13>, -Fix, -Full, -Evals and -Protected'
    exit 1
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# git show prints the committed files as UTF-8.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'
$logs = Join-Path $root 'scratch/b4-gate'
$recordPath = Join-Path $root 'docs/runs/5d-B4.md'
New-Item -ItemType Directory -Force $logs | Out-Null

$EXECUTE_OF_T = 'M:Polly.ResiliencePipeline.Execute``1(System.Func{``0})'
# A plain hashtable: an ordered dictionary reads an int index as a position, not as the question.
$CELLS = @{
    119 = @('Probe/ReturnInCollection/Call', 'Element/ReturnInCollection/Call', 'LibraryArg/ReturnInCollection/Call',
            'DelegateResult/ReturnInCollection/Call', 'Fresh/ReturnInCollection/Call', 'FreshHoldingProbe/ReturnInCollection/Call')
    120 = @('DelegateResult/HandToDelegate/TaskRun', 'DelegateResult/KeepOnSealedReceiver/TaskRun',
            'DelegateResult/KeepOnSealedReceiver/Enumeration', 'DelegateResult/StoreInArrayArg/TaskRun',
            'DelegateResult/StoreInArrayArg/Enumeration')
    121 = @('FreshHoldingProbe/KeepOnSealedReceiver/Call', 'FreshHoldingProbe/KeepOnSubReceiver/Call', 'FreshHoldingProbe/KeepOnLibraryArg/Call')
    132 = @('Setter/Item/ReadDeep', 'IndexerSetter/Item/ReadDeep')
}
$ENTRY_MATRIX = 'src/ConcurrencyHunter.Core.Tests/Generation/GeneratedEntryMatrixTests.cs'
$ACCESSOR_MATRIX = 'src/ConcurrencyHunter.Core.Tests/Generation/AccessorEntryMatrixTests.cs'

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
    return Get-Content $path -Raw -Encoding utf8
}

# The run record's lines; the record must exist.
function Record-Lines
{
    return @((Working-Text 'docs/runs/5d-B4.md') -split "`r?`n")
}

# The text of the run record's '## Остаток' section, or '' when the record or the section is absent.
function Remainder-Text
{
    if (-not (Test-Path $recordPath))
    {
        return ''
    }
    $text = Get-Content $recordPath -Raw -Encoding utf8
    $match = [regex]::Match($text, '(?ms)^## Остаток[ \t]*\r?$(.*?)(?=^## |\z)')
    if ($match.Success)
    {
        return $match.Groups[1].Value
    }
    return ''
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

function Copy-Json($value)
{
    return ($value | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
}

# Fails unless demo/expected-findings.json differs from HEAD by exactly the edits of R1.
function Assert-ExpectationEdits
{
    $before = Committed-Text 'demo/expected-findings.json' | ConvertFrom-Json
    $after = Working-Text 'demo/expected-findings.json' | ConvertFrom-Json
    if ((Canon $before.schemaVersion) -ne (Canon $after.schemaVersion))
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

    $execute = Copy-Json (Committed 'findings' 'polly-execute-invokes-delegate/delegate-runs-in-the-action')
    $expected.findings.Remove('polly-execute-invokes-delegate/delegate-runs-in-the-action')
    $execute.id = 'polly-execute-invokes-delegate/pipeline-decides-the-fate'
    $execute.confidence = 'medium'
    $expected.findings[$execute.id] = Canon $execute

    $null = Committed 'findings' 'polly-circuit-breaker-callback-at-execute/callback-runs-in-execute'
    $expected.findings.Remove('polly-circuit-breaker-callback-at-execute/callback-runs-in-execute')
    $breaker = Committed 'notDefects' 'polly-circuit-breaker-callback-at-execute'
    $opened = [pscustomobject]@{ id = 'polly-circuit-breaker-callback-at-execute/opened-runs-one-at-a-time'; phase = '5d-B4'; resource = $breaker.resource }
    $expected.notDefects[$opened.id] = Canon $opened

    $factory = Copy-Json (Committed 'findings' 'protobuf-parser-factory-at-parse/factory-runs-at-parse')
    $factory.phase = '5d-B4b'
    $expected.findings[$factory.id] = Canon $factory
    $parser = Committed 'notDefects' 'protobuf-parser-factory-at-parse'
    $holder = [pscustomobject]@{ id = 'protobuf-parser-factory-at-parse/holder-unconfirmed'; phase = '5d-B4'; until = '5d-B4b'; resource = $parser.resource }
    $expected.notDefects[$holder.id] = Canon $holder

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
                Fail "$list has $id, which R1 does not add"
            }
            if ($actual[$id] -ne $expected[$list][$id])
            {
                Fail "$list entry $id is not what R1 asks: expected $($expected[$list][$id]), found $($actual[$id])"
            }
        }
    }
    Write-Host 'demo/expected-findings.json differs from HEAD by exactly the edits of R1'
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
            $found = @($lines | Where-Object { $_.Contains($change.Path) -and $_.Contains($old) -and $_.Contains($new) })
            if ($found.Count -eq 0)
            {
                Fail "$what`: the run record has no line with $($change.Path), $old and $new"
            }
        }
        else
        {
            $found = @($lines | Where-Object { $_.Contains($change.Path) -and $_ -match "\b$($change.Kind)\b" })
            if ($found.Count -eq 0)
            {
                Fail "$what`: the run record has no line with $($change.Path) and the word $($change.Kind)"
            }
        }
    }
    Write-Host "$what`: $($changes.Count) moved values and elements are in the run record"
}

# Fails unless what the run must not change equals HEAD and every moved value of the demo snapshot is in the run record.
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

    $snapshot = 'src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json'
    $before = Committed-Text $snapshot | ConvertFrom-Json
    $after = Working-Text $snapshot | ConvertFrom-Json
    foreach ($count in @('findings', 'groups', 'fingerprints'))
    {
        if ($null -eq $before.counts.$count -or (Canon $after.counts.$count) -ne (Canon $before.counts.$count))
        {
            Fail "demo.json counts.$count differs from HEAD"
        }
    }

    Assert-ExpectationEdits

    $changes = [Collections.Generic.List[object]]::new()
    Diff-Json $before $after '' $changes
    Assert-Recorded $changes 'demo.json'
    Write-Host 'eShop snapshot, CoreLib table and demo findings equal HEAD; expectations carry exactly R1'
}

# Fails unless the evals snapshot keeps HEAD's entries and packages, lowers no threshold, names no CS0104 or CS0051,
# keeps the body counts of every package but System.Text.Json and AutoMapper, and the run record names every move.
function Assert-EvalsSnapshot
{
    $path = 'skills/hunt/evals/models/generator-snapshot.json'
    $before = Committed-Text $path | ConvertFrom-Json
    $after = Working-Text $path | ConvertFrom-Json
    $lines = $null
    $fields = @('reason', 'modelReason', 'classified', 'model', 'detail', 'causes', 'reachedBodies')

    foreach ($section in @('members', 'effects', 'linq', 'accessors'))
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
            if ([string]$entry.detail -match 'CS0104|CS0051')
            {
                Fail "$section`: the detail of $member names CS0104 or CS0051: $($entry.detail)"
            }
            if ((Canon $old[$member].implementation) -ne (Canon $entry.implementation))
            {
                Fail "$section`: the implementation of $member changed; the snapshot was recorded against another install"
            }
            $moved = @($fields | Where-Object { (Canon $old[$member].$_) -ne (Canon $entry.$_) })
            if ($moved.Count -gt 0)
            {
                if ($null -eq $lines) { $lines = Record-Lines }
                $found = @($lines | Where-Object { $_.Contains($member) -and $_ -match '\bR(4|5|6|7|8|9|10)\b' })
                if ($found.Count -eq 0)
                {
                    Fail "$section`: $member moved ($($moved -join ', ')) and the run record has no line with it and its requirement R4-R10"
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
        $assembly = $key.Split('|')[0]
        foreach ($field in @(@($oldPackages[$key].PSObject.Properties.Name) + @($newPackages[$key].PSObject.Properties.Name) | Sort-Object -Unique))
        {
            $left = $oldPackages[$key].$field
            $right = $newPackages[$key].$field
            if ((Canon $left) -eq (Canon $right))
            {
                continue
            }
            if ($field -in @('bodies', 'externBodies') -and $assembly -notin @('System.Text.Json', 'AutoMapper'))
            {
                Fail "packages[$key].$field moved from $left to $right; only System.Text.Json and AutoMapper may move their bodies"
            }
            $changes.Add([pscustomobject]@{ Path = "packages[$key].$field"; Kind = 'value'; Old = $left; New = $right })
        }
    }

    foreach ($threshold in @('exact', 'fateGoldEntries', 'effectsExact', 'accessorsExact'))
    {
        if ([int]$after.$threshold -lt [int]$before.$threshold)
        {
            Fail "$threshold fell from $($before.$threshold) to $($after.$threshold)"
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
    Assert-Recorded $changes 'evals snapshot numbers'
    Write-Host 'evals snapshot: same entries and packages, no threshold fell, every move is in the run record'
}

# The C# statement that starts at a marker, up to its ';' outside string literals.
function Statement-At([string]$text, [string]$marker)
{
    $start = $text.IndexOf($marker)
    if ($start -lt 0)
    {
        Fail "no '$marker' found"
    }
    $inString = $false
    for ($index = $start; $index -lt $text.Length; $index++)
    {
        $char = $text[$index]
        if ($char -eq '"' -and $text[$index - 1] -ne '\')
        {
            $inString = -not $inString
            continue
        }
        if (-not $inString -and $char -eq ';')
        {
            return $text.Substring($start, $index - $start + 1)
        }
    }
    Fail "no end of the statement at '$marker'"
}

# The cells a matrix test file lists as wider.
function Wider-Cells([string]$relativeFile)
{
    $statement = Statement-At (Working-Text $relativeFile) 'Wider ='
    return @([regex]::Matches($statement, '"([A-Za-z]+/[A-Za-z]+/[A-Za-z]+)"') | ForEach-Object { $_.Groups[1].Value })
}

# Fails when a cell of the question stays wider without its name in the run record's '## Остаток' section.
function Assert-WiderInRemainder([int]$question, [string]$relativeFile)
{
    $wider = Wider-Cells $relativeFile
    $kept = @($CELLS[$question] | Where-Object { $wider -contains $_ })
    if ($kept.Count -eq 0)
    {
        Write-Host "question $question`: no cell left wider"
        return
    }
    $remainder = Remainder-Text
    foreach ($cell in $kept)
    {
        if (-not $remainder.Contains($cell))
        {
            Fail "question $question`: $cell is still wider and the run record's '## Остаток' does not name it"
        }
    }
    Write-Host "question $question`: $($kept.Count) cells left wider, each named in '## Остаток'"
}

function Question-Row([string[]]$questions, [int]$number)
{
    $rows = @($questions | Where-Object { $_ -match "^\| $number \|" })
    if ($rows.Count -ne 1)
    {
        Fail "docs/QUESTIONS.md has $($rows.Count) rows for question $number, not one"
    }
    return $rows[0]
}

function Row-Phase([string]$row)
{
    $cells = $row.TrimEnd() -split '\|'
    return $cells[$cells.Count - 2].Trim()
}

function Is-Closed([string]$row)
{
    return $row -match '^\| \d+ \| Закрыт →'
}

# Fails unless the text holds each needle; the item names what is missing.
function Assert-Holds([string]$what, [string]$text, [Collections.IDictionary]$items)
{
    foreach ($item in $items.GetEnumerator())
    {
        foreach ($needle in $item.Value)
        {
            if (-not $text.Contains($needle))
            {
                Fail "$what does not hold $($item.Key): '$needle'"
            }
        }
    }
}

# A SPEC section from its bold label up to the next one, with its whitespace collapsed.
function Spec-Section([string]$spec, [string]$label, [string]$next)
{
    $match = [regex]::Match($spec, "(?s)\*\*$label\.\*\*(.*?)(?=\*\*$next\.\*\*)")
    if (-not $match.Success)
    {
        Fail "docs/SPEC.md has no section $label before $next"
    }
    return $match.Groups[1].Value -replace '\s+', ' '
}

$EVAL_SETS = @('members', 'effects', 'linq', 'accessors')

# What a snapshot entry answers now: 'модель' for a model, otherwise its reason in backticks.
function Entry-Answer($entry)
{
    if ($null -ne $entry.model)
    {
        return 'модель'
    }
    if ($entry.reason)
    {
        return "``$($entry.reason)``"
    }
    return "``$($entry.modelReason)``"
}

# The sentence of a QUESTIONS cause row that lists the cause's answers of the snapshot and their causes codes.
function Cause-Listing($snapshot, [string]$cause)
{
    $sets = @()
    $histograms = @()
    foreach ($set in $EVAL_SETS)
    {
        $entries = @($snapshot.$set | Where-Object { $_.modelReason -eq $cause })
        if ($entries.Count -eq 0)
        {
            continue
        }
        [string[]]$ids = @($entries | ForEach-Object { $_.member })
        [Array]::Sort($ids, [StringComparer]::Ordinal)
        $sets += "$set ($($ids.Count)): " + (($ids | ForEach-Object { "``````$_``````" }) -join '; ')
        $counts = @{}
        foreach ($code in @($entries | ForEach-Object { $_.causes }))
        {
            $counts[$code] = 1 + [int]$counts[$code]
        }
        [string[]]$codes = @($counts.Keys)
        [Array]::Sort($codes, [StringComparer]::Ordinal)
        $ordered = @($codes | Sort-Object -Stable -Descending { $counts[$_] })
        $histograms += "$set — " + (($ordered | ForEach-Object { "``$_`` $($counts[$_])" }) -join ', ')
    }
    return "Все ответы этой причины по снимку на конец 5d run B4: $($sets -join ' '). Коды ``causes`` (число ответов с кодом): $($histograms -join '; ')."
}

# Fails unless a cause row lists the snapshot's answers of its cause and names each member that left the cause since HEAD,
# with what it answers now, and names no member that did not leave.
function Assert-CauseRow([string]$row, [int]$question, [string]$cause, $snapshot, $committed)
{
    if (-not $row.Contains((Cause-Listing $snapshot $cause)))
    {
        Fail "question $question does not list the '$cause' answers of the evals snapshot and their causes codes as the snapshot has them"
    }
    $left = [regex]::Match($row, 'Ушли из этой причины со времени записи строки: (.*?)\. \|')
    $named = @{}
    if ($left.Success)
    {
        foreach ($match in [regex]::Matches($left.Groups[1].Value, '```(M:.+?)``` \((\w+)\) — (модель|`[^`]+`)'))
        {
            $set = $match.Groups[2].Value
            $member = $match.Groups[1].Value
            $named["$set|$member"] = $true
            $entry = @($snapshot.$set | Where-Object { $_.member -eq $member })
            if ($entry.Count -ne 1)
            {
                Fail "question $question names $member ($set) as left, and the snapshot has $($entry.Count) such entries"
            }
            if ($entry[0].modelReason -eq $cause)
            {
                Fail "question $question names $member ($set) as left, and it still answers '$cause'"
            }
            if ((Entry-Answer $entry[0]) -ne $match.Groups[3].Value)
            {
                Fail "question $question names $member ($set) with $($match.Groups[3].Value), and it answers $(Entry-Answer $entry[0])"
            }
        }
    }
    foreach ($set in $EVAL_SETS)
    {
        foreach ($before in @($committed.$set | Where-Object { $_.modelReason -eq $cause }))
        {
            $now = @($snapshot.$set | Where-Object { $_.member -eq $before.member })
            if ($now.Count -eq 1 -and $now[0].modelReason -ne $cause -and -not $named.ContainsKey("$set|$($before.member)"))
            {
                Fail "question $question does not name $($before.member) ($set), which left '$cause' in the run"
            }
        }
    }
}

# A member's answer in the committed demo models: its refusal, or its model.
function Demo-Answer([string]$member)
{
    $folder = Join-Path $root 'demo/.concurrency-hunter/models/generated'
    foreach ($file in Get-ChildItem $folder -Recurse -Filter '*.json')
    {
        $entry = @((Get-Content $file.FullName -Raw -Encoding utf8 | ConvertFrom-Json).members | Where-Object { $_.member -eq $member })
        if ($entry.Count -eq 1)
        {
            return $entry[0]
        }
    }
    Fail "no committed demo model answers $member"
}

# Fails unless a SCENARIOS row's 'Сегодня …' sentence names each member's committed demo answer: the refusal's reason, every
# number of its detail and the causes the sentence gives it, or the fate of each delegate of the model.
function Assert-Today([string]$case, [string]$row, [object[]]$answers)
{
    $today = [regex]::Match($row, '\| Сегодня (.*?)\. (?=Так до|До `)')
    if (-not $today.Success)
    {
        Fail "the SCENARIOS row of $case has no 'Сегодня …' sentence"
    }
    $sentence = $today.Groups[1].Value
    foreach ($answer in $answers)
    {
        foreach ($name in $answer.Names)
        {
            if (-not $sentence.Contains($name))
            {
                Fail "the 'Сегодня' sentence of $case does not name $name"
            }
        }
        $committed = Demo-Answer $answer.Member
        $words = @()
        if ($null -ne $committed.refusal)
        {
            $words += "``$($committed.refusal.reason)``"
            $words += @([regex]::Matches($committed.refusal.detail, '\d+') | ForEach-Object { $_.Value })
            foreach ($code in $answer.Causes)
            {
                if (@($committed.refusal.causes) -notcontains $code)
                {
                    Fail "the 'Сегодня' sentence of $case gives $($answer.Member) the cause $code, which its committed answer has not"
                }
                $words += "``$code``"
            }
        }
        else
        {
            $words += @($committed.model.fates.PSObject.Properties | ForEach-Object { "``$($_.Value.fate)``" })
        }
        foreach ($word in $words)
        {
            if (-not $sentence.Contains($word))
            {
                Fail "the 'Сегодня' sentence of $case does not say $word of the committed answer of $($answer.Member)"
            }
        }
    }
}

# Fails unless each document holds what task 13 names.
function Assert-Documents
{
    Invoke-Script 'check-docs' 'check-docs.ps1'

    $questions = @((Working-Text 'docs/QUESTIONS.md') -split "`r?`n")
    $remainder = Remainder-Text

    $row = Question-Row $questions 122
    if (-not (Is-Closed $row))
    {
        Fail 'question 122 is not closed'
    }

    $widerEntry = Wider-Cells $ENTRY_MATRIX
    $widerAccessor = Wider-Cells $ACCESSOR_MATRIX
    $named = @()
    foreach ($question in $CELLS.Keys)
    {
        $wider = if ($question -eq 132) { $widerAccessor } else { $widerEntry }
        $kept = @($CELLS[$question] | Where-Object { $wider -contains $_ })
        $row = Question-Row $questions $question
        if (Is-Closed $row)
        {
            if ($kept.Count -ne 0)
            {
                Fail "question $question is closed while $($kept -join ', ') stay wider"
            }
            continue
        }
        if ((Row-Phase $row) -ne '5d run B5')
        {
            Fail "question $question is open with phase '$(Row-Phase $row)', not '5d run B5'"
        }
        if ($kept.Count -eq 0)
        {
            Fail "question $question is open while none of its cells stays wider"
        }
        foreach ($cell in $CELLS[$question])
        {
            $inRow = $row.Contains($cell)
            $isKept = $kept -contains $cell
            if ($inRow -ne $isKept)
            {
                Fail "question $question`: the row names $cell $(if ($inRow) { 'though it is exact' } else { 'not, though it stays wider' })"
            }
            if ($isKept -and -not $remainder.Contains($cell))
            {
                Fail "question $question`: $cell stays wider and '## Остаток' does not name it"
            }
        }
        $named += $kept
    }
    foreach ($cell in @($widerEntry + $widerAccessor))
    {
        if ($named -notcontains $cell)
        {
            Fail "$cell is listed wider and no open question row names it"
        }
    }

    foreach ($question in @(123, 182, 185))
    {
        $row = Question-Row $questions $question
        if (-not (Is-Closed $row) -and (Row-Phase $row) -ne '5d run B5')
        {
            Fail "question $question is neither closed nor narrowed to '5d run B5'"
        }
    }

    $row = Question-Row $questions 129
    $fact = (Working-Text 'src/ConcurrencyHunter.Core.Tests/Generation/AccessorEvalTests.cs').Contains('BackgroundWorker_event_accessors_match_their_gold')
    $remaining = $remainder.Contains('add_DoWork') -and $remainder.Contains('add_RunWorkerCompleted')
    if (Is-Closed $row)
    {
        if (-not $fact -or $remaining)
        {
            Fail 'question 129 is closed, but the eval fact is missing or the remainder names the BackgroundWorker members'
        }
    }
    elseif (-not $remaining -or $fact -or (Row-Phase $row) -ne '5d run B5')
    {
        Fail "question 129 is open, which needs both BackgroundWorker members in '## Остаток', no eval fact and phase '5d run B5'"
    }

    $snapshot = Working-Text 'skills/hunt/evals/models/generator-snapshot.json' | ConvertFrom-Json
    $committed = Committed-Text 'skills/hunt/evals/models/generator-snapshot.json' | ConvertFrom-Json
    $causeRows = @{ 111 = 'unknown-touch'; 112 = 'vocabulary'; 113 = 'library-state' }
    foreach ($question in @(111, 112, 113))
    {
        $row = Question-Row $questions $question
        if ((Is-Closed $row) -or (Row-Phase $row) -ne '5d run B5')
        {
            Fail "question $question is not open with phase '5d run B5'"
        }
        Assert-CauseRow $row $question $causeRows[$question] $snapshot $committed
    }
    $row = Question-Row $questions 101
    if ((Row-Phase $row) -ne '5d run B4b')
    {
        Fail "question 101's phase is not '5d run B4b'"
    }
    # The three reasons the holder of MessageParser<T> is not confirmed, as row 101 and the SCENARIOS row word them.
    $holderReasons = [ordered]@{
        'the trigger the driver does not call' = @('триггер `ParseFrom(ReadOnlySpan<byte>)` драйвер не вызывает')
        'the trigger cap'                      = @('кандидатов в триггеры 25 при пределе 24')
        'the closure bound'                    = @('выходит за границу замыкания')
    }
    Assert-Holds 'question 101' $row ([ordered]@{ 'the demo case' = @('`protobuf-parser-factory-at-parse`', '`5d-B4b`') })
    Assert-Holds 'question 101' $row $holderReasons
    $row = Question-Row $questions 186
    if ((Row-Phase $row) -ne '8')
    {
        Fail "question 186's phase is not '8'"
    }
    Assert-Holds 'question 186' $row ([ordered]@{
        'the question'  = @('Точность модели по составу получателя (ADR 0024)',
                            'Решить, когда модель, построенная по тому, как программа собрала получатель, применяется в месте вызова')
        'the demo case' = @('`polly-execute-invokes-delegate`')
    })

    $spec = Working-Text 'docs/SPEC.md'
    foreach ($needle in @('`5d-B3c`', '`5d-B4b`', '0024', 'CS0104', 'CS0051'))
    {
        if (-not $spec.Contains($needle))
        {
            Fail "docs/SPEC.md does not contain $needle"
        }
    }
    $header = @(($spec -split "`r?`n") | Where-Object { $_.StartsWith('| Решения |') })
    if ($header.Count -ne 1 -or -not $header[0].Contains('adr/0024-a-library-model-holds-for-every-receiver-of-its-member.md'))
    {
        Fail "the decisions row of SPEC's header does not cite ADR 0024"
    }
    # Each rule the run adds to TD-034a and TD-034b, as the section words it.
    Assert-Holds "SPEC's TD-034a" (Spec-Section $spec 'TD-034a' 'TD-034b') ([ordered]@{
        'the rule of ADR 0024' = @('Модель члена одна для любого получателя ([ADR 0024]',
                                   'не зависят от того, как программа построила получатель',
                                   'остаётся `unknown-execution`')
    })
    Assert-Holds "SPEC's TD-034b" (Spec-Section $spec 'TD-034b' 'TD-034c') ([ordered]@{
        'R4: the rollback of cancelled work'         = @('Работа, которую синтез начинает и отменяет',
                                                         'ни записи seed-оператора',
                                                         'драйвер после отмены тот же, каким он был бы без этой работы',
                                                         'путь отменённого seed-а записан в `unseeded`')
        'R4: the rebuild without a trigger'          = @('отбрасывает триггер и пересобирает драйвер без него',
                                                         'непокрытый член держателя, на том же месте среди 24 триггеров')
        'R4: the 16-level bound of cancelled work'   = @('ограничена шестнадцатью уровнями',
                                                         'в том числе когда цепочку прошла отменённая работа')
        'R5: axis 4 names the concrete classes'      = @('конкретный класс таблицы ADR 0010 (`List<T>`, `Dictionary<TKey, TValue>`, `ConcurrentDictionary<TKey, TValue>`, `ConcurrentQueue<T>`, `ConcurrentStack<T>`, `ConcurrentBag<T>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`) — сам этот класс, никогда не подкласс драйвера',
                                                         'с двумя вставками элемента его членом таблицы')
        'R5: the Sub_ axis excludes them'            = @('незапечатанный конкретный класс вне таблицы ADR 0010')
        'R5: every place, the lambda return'         = @('Конкретная коллекция ADR 0010 строится так везде', 'возврат пробной лямбды')
        'R5: the seed and witness exception'         = @('Seed поля, возврат и `out` свидетеля получают одну вставку seed-значения',
                                                         'иначе у поля нет seed-а, а коллекция свидетеля пуста')
        'R5: element seeds reach the elements'       = @('seed-ы элементов доходят до этих элементов')
        'R6: provenance of a fresh collection'       = @('коллекция имён наблюдаемых значений',
                                                         'Возврат делегата называется `returns:D` и через границу дочернего исполнения и исполнения перечисления',
                                                         'то, чего анализ не видит, остаётся `unknown-touch`')
        'R6: keeping through objects the call made'  = @('созданный объект не называется удержанным значением',
                                                         'в `KeepingChain` не входит',
                                                         'запись в библиотечное состояние, существовавшее до вызова, остаётся `library-state`')
        'R7: the own access path'                    = @('Чей это доступ, решает собственный путь доступа',
                                                         'доступ к тому же региону по пути от аргумента — доступ к аргументу',
                                                         '`keeps.this: [arg:value]` (у индексатора `[arg:key, arg:value]`)',
                                                         'каждое настоящее чтение seed-а получателя остаётся `reads-deep(this)`')
        'R8: one this of one run of a body'          = @('Один прогон тела имеет одну `this`',
                                                         'остаётся seed-ом слота и после того, как контексты тела слились')
        'R9: the CS0104, CS0050 and CS0051 repair'   = @('(CS0104), квалифицируется через `global::`', 'CS0050, CS0051',
                                                         'Ни одно тело, которое компилируется без этой правки, с ней `extern` не становится')
    })
    $floor = @(($spec -split "`r?`n") | Where-Object { $_.Contains('Правило порогов') })
    if ($floor.Count -ne 1)
    {
        Fail "docs/SPEC.md has $($floor.Count) lines with 'Правило порогов', not one"
    }
    # Each cause R4-R9 the run changes recorded answers by, as SPEC's threshold rule and the README's Floor rule word it.
    $specCauses = [ordered]@{
        'R4' = @('отменённой работы', 'пересборки')
        'R5' = @('конкретной коллекции ADR 0010')
        'R6' = @('коллекция имён наблюдаемых значений', 'границу исполнения', 'удержание через объекты')
        'R7' = @('собственному пути доступа')
        'R8' = @('слияния его контекстов')
        'R9' = @('CS0104', 'CS0051')
    }
    foreach ($cause in $specCauses.GetEnumerator())
    {
        foreach ($needle in $cause.Value)
        {
            if (-not $floor[0].Contains($needle))
            {
                Fail "SPEC's threshold rule does not name the cause of $($cause.Key): '$needle'"
            }
        }
    }

    $readme = Working-Text 'skills/hunt/evals/models/README.md'
    $floorRule = [regex]::Match($readme, '(?ms)^- \*\*Floor rule.*?(?=^- \*\*)')
    if (-not $floorRule.Success -or -not $floorRule.Value.Contains('5d-B4.md'))
    {
        Fail "the README's Floor rule does not name docs/runs/5d-B4.md"
    }
    # The Floor rule wraps its lines; compare its words.
    $floorWords = $floorRule.Value -replace '\s+', ' '
    $readmeCauses = [ordered]@{
        'R4' = @('cancelled work', 'rebuild without a trigger')
        'R5' = @('concrete ADR 0010 collection')
        'R6' = @("observed values' names", 'execution boundary', 'objects the call made')
        'R7' = @("access's own path")
        'R8' = @('contexts merge')
        'R9' = @('CS0104', 'CS0051')
    }
    foreach ($cause in $readmeCauses.GetEnumerator())
    {
        foreach ($needle in $cause.Value)
        {
            if (-not $floorWords.Contains($needle))
            {
                Fail "the README's Floor rule does not name the cause of $($cause.Key): '$needle'"
            }
        }
    }

    $research = Working-Text 'docs/research/library-models.md'
    $synthesis = [regex]::Match($research, '(?ms)^## Driver synthesis(.*?)(?=^## |\z)')
    if (-not $synthesis.Success -or $synthesis.Groups[1].Value -notmatch 'ADR 0010|concrete collection')
    {
        Fail "docs/research/library-models.md 'Driver synthesis' does not name the value of a concrete ADR 0010 collection"
    }

    $scenarios = @((Working-Text 'demo/SCENARIOS.md') -split "`r?`n")
    # The members each case's 'Сегодня …' sentence answers for, with the words that name them and the causes it gives them.
    $todayAnswers = @{
        'polly-execute-invokes-delegate'            = @(
            @{ Member = 'M:Polly.ResiliencePipeline.Execute``1(System.Func{``0})'; Names = @('`Execute<T>(Func<T>)`')
               Causes = @('callback-value-in-library-call', 'receiver-member-unseen', 'unseeded-read') })
        'polly-circuit-breaker-callback-at-execute' = @(
            @{ Member = 'M:Polly.CircuitBreakerResiliencePipelineBuilderExtensions.AddCircuitBreaker(Polly.ResiliencePipelineBuilder,Polly.CircuitBreaker.CircuitBreakerStrategyOptions)'
               Names = @('`AddCircuitBreaker`'); Causes = @() },
            @{ Member = 'M:Polly.ResiliencePipelineBuilder.Build'; Names = @('`Build`'); Causes = @() },
            @{ Member = 'M:Polly.ResiliencePipeline.Execute(System.Action)'; Names = @('`Execute(Action)`'); Causes = @() },
            @{ Member = 'M:Polly.CircuitBreaker.CircuitBreakerStrategyOptions`1.set_FailureRatio(System.Double)'
               Names = @('три сеттера `CircuitBreakerStrategyOptions<T>`'); Causes = @('probe-accessed-elsewhere') },
            @{ Member = 'M:Polly.CircuitBreaker.CircuitBreakerStrategyOptions`1.set_MinimumThroughput(System.Int32)'
               Names = @('три сеттера `CircuitBreakerStrategyOptions<T>`'); Causes = @('probe-accessed-elsewhere') },
            @{ Member = 'M:Polly.CircuitBreaker.CircuitBreakerStrategyOptions`1.set_OnOpened(System.Func{Polly.CircuitBreaker.OnCircuitOpenedArguments{`0},System.Threading.Tasks.ValueTask})'
               Names = @('три сеттера `CircuitBreakerStrategyOptions<T>`'); Causes = @('probe-accessed-elsewhere') })
        'protobuf-parser-factory-at-parse'          = @(
            @{ Member = 'M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})'; Names = @('модель конструктора `MessageParser<T>`'); Causes = @() },
            @{ Member = 'M:Google.Protobuf.MessageParser`1.ParseFrom(System.Byte[])'; Names = @('`ParseFrom(byte[])`'); Causes = @() })
    }
    # What each case's row says about the library's code (ADR 0024).
    $caseReasons = @{
        'polly-execute-invokes-delegate'            = [ordered]@{
            'the overload'                      = @('вызов идёт в `Execute<T>(Func<T>)`')
            'the fate the strategies decide'    = @('судьба колбэка зависит от стратегий конвейера')
        }
        'polly-circuit-breaker-callback-at-execute' = [ordered]@{
            'OnOpened running one at a time'    = @('`OnOpened` исполняется по одному в собственном `ScheduledTaskExecutor`')
            'Execute waiting for it'            = @('и `Execute` ждёт его')
        }
        'protobuf-parser-factory-at-parse'          = $holderReasons
    }
    foreach ($case in @('polly-execute-invokes-delegate', 'polly-circuit-breaker-callback-at-execute', 'protobuf-parser-factory-at-parse'))
    {
        $rows = @($scenarios | Where-Object { $_ -match "^\| ``$case`` \|" })
        if ($rows.Count -ne 1)
        {
            Fail "demo/SCENARIOS.md has $($rows.Count) rows for $case, not one"
        }
        if (-not $rows[0].Contains('0024'))
        {
            Fail "the SCENARIOS row of $case does not cite ADR 0024"
        }
        Assert-Today $case $rows[0] $todayAnswers[$case]
        Assert-Holds "the SCENARIOS row of $case" $rows[0] $caseReasons[$case]
        if ($case -eq 'polly-execute-invokes-delegate')
        {
            if (-not $rows[0].Contains('Execute<T>(Func<T>)'))
            {
                Fail 'the SCENARIOS row of polly-execute-invokes-delegate does not name Execute<T>(Func<T>)'
            }
            $timeoutLeft = $remainder.Contains($EXECUTE_OF_T)
            if ($rows[0].Contains('timeout') -ne $timeoutLeft)
            {
                Fail "the SCENARIOS row of polly-execute-invokes-delegate $(if ($timeoutLeft) { 'does not say' } else { 'still says' }) timeout"
            }
        }
    }

    $plan = @((Working-Text 'docs/PLAN.md') -split "`r?`n")
    $index = @{}
    foreach ($run in @('B4', 'B4b', 'B5', 'C'))
    {
        $at = @(for ($i = 0; $i -lt $plan.Count; $i++) { if ($plan[$i] -match "^\| \*\*5d run $run\*\* \|") { $i } })
        if ($at.Count -ne 1)
        {
            Fail "docs/PLAN.md has $($at.Count) table rows for 5d run $run, not one"
        }
        $index[$run] = $at[0]
    }
    if (-not ($index['B4'] -lt $index['B4b'] -and $index['B4b'] -lt $index['B5'] -and $index['B5'] -lt $index['C']))
    {
        Fail 'the table rows of docs/PLAN.md are not in the order B4, B4b, B5, C'
    }
    if ($plan[$index['B4']] -notmatch '^\| \*\*5d run B4\*\* \| Построено')
    {
        Fail "docs/PLAN.md's row of 5d run B4 does not start with 'Построено'"
    }
    Assert-Holds "docs/PLAN.md's row of 5d run B4b" $plan[$index['B4b']] ([ordered]@{
        'question 101'              = @('(вопрос 101)')
        'the High finding of Protobuf' = @('`High`-находка `protobuf-parser-factory-at-parse`', '`phase` `5d-B4b`')
    })
    Assert-Holds "docs/PLAN.md's row of 5d run B5" $plan[$index['B5']] ([ordered]@{
        'the cause classes 111-113' = @('`unknown-touch`', '`library-state`', '`vocabulary`', '(вопросы 111–113')
        'the remainder of B4'       = @('остаток B4', '(вопрос 120)')
    })
    # Section 2 counts the runs of phase 5d and lists them in the order of the table.
    $tableRuns = @($plan | ForEach-Object { if ($_ -match '^\| \*\*5d run (\w+)\*\* \|') { $Matches[1] } })
    $section = @($plan | Where-Object { $_.StartsWith('Фаза 5d проходит в ') })
    if ($section.Count -ne 1)
    {
        Fail "docs/PLAN.md has $($section.Count) lines 'Фаза 5d проходит в …', not one"
    }
    $order = [regex]::Match($section[0], 'Раны идут в порядке ([\w, ]+)\.')
    if (-not $order.Success -or ($order.Groups[1].Value -split ', ' -join ',') -ne ($tableRuns -join ','))
    {
        Fail "section 2 of docs/PLAN.md does not list the runs in the order of the table: $($tableRuns -join ', ')"
    }
    $countWords = @{ 19 = 'девятнадцати'; 20 = 'двадцати'; 21 = 'двадцати одном'; 22 = 'двадцати двух' }
    if (-not $section[0].StartsWith("Фаза 5d проходит в $($countWords[$tableRuns.Count]) ранах."))
    {
        Fail "section 2 of docs/PLAN.md does not count the $($tableRuns.Count) runs of the table"
    }

    $record = Working-Text 'docs/runs/5d-B4.md'
    foreach ($section in @('## Model evals', '## Demo', '## Остаток'))
    {
        if ($record -notmatch "(?m)^$([regex]::Escape($section))[ \t]*\r?$")
        {
            Fail "docs/runs/5d-B4.md has no section '$section'"
        }
    }
    if (-not $record.Contains('generate-corelib.ps1 -Since'))
    {
        Fail 'docs/runs/5d-B4.md does not hold the output of generate-corelib.ps1 -Since'
    }
    Write-Host 'docs hold the run'
}

try
{
    if ($Fix)
    {
        $classes = @('DriverRollbackMatrixTests', 'ValueSourceMatrixTests', 'CollectionValueMatrixTests', 'ParameterKindsTests',
                     'DriverSynthesisTests', 'SeedTests', 'WitnessTests', 'CandidacyTests', 'DriverCompilationTests',
                     'GeneratedEntryTests', 'EffectReaderTests', 'ValueProvenanceTests', 'GeneratedEntryMatrixTests',
                     'AccessorEntryMatrixTests', 'ReceiverSeedAccessMatrixTests', 'GenerationCauseTests', 'GeneratedFateTests',
                     'AccessorDriverTests', 'LibraryCompilationTests', 'ExpectationFileRuleTests', 'ExpectationMatcherTests',
                     'RunGenerationTests')
        Invoke-Tests 'fix' "($(Class-Filter $classes))&FullyQualifiedName!~Installed_"
        Write-Output 'check-phase5d-runB4 fix gate ok'
        exit 0
    }

    if ($Protected)
    {
        Assert-Protected
        Write-Output 'check-phase5d-runB4 protected ok'
        exit 0
    }

    if ($Full)
    {
        if (-not $env:CH_ESHOP_ROOT)
        {
            Fail 'CH_ESHOP_ROOT is not set; the eShop tests would skip'
        }
        With-Environment @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null } {
            Invoke-Script 'full-baseline' 'check-test-baseline.ps1'
        }
        Write-Host 'baseline: no recorded Passed case is missing, skipped or failed'
        Assert-Protected
        Invoke-Script 'full-rerecord' 'check-baseline-rerecord.ps1'
        Write-Host 'the rewritten baseline loses no case against HEAD'
        Write-Output 'check-phase5d-runB4 full ok'
        exit 0
    }

    if ($Evals -or $Task -eq 11)
    {
        Invoke-EvalTests 'model-evals' (Class-Filter @('ModelEvalTests', 'EffectsEvalTests', 'LinqOracleTests', 'AccessorEvalTests'))
        Assert-EvalsSnapshot
        Write-Output "check-phase5d-runB4 $(if ($Evals) { 'evals' } else { 'task 11' }) ok"
        exit 0
    }

    switch ($Task)
    {
        1
        {
            Invoke-Tests 'expectations' (Class-Filter @('ExpectationFileRuleTests', 'ExpectationMatcherTests', 'DemoExpectationTests'))
            Invoke-Script 'check-demo-expectations' 'check-demo-expectations.ps1'
            Assert-ExpectationEdits
        }
        2
        {
            Assert-Listed @('DriverRollbackMatrixTests.Every_site_leaves_the_driver_it_would_build_without_the_work',
                            'DriverRollbackMatrixTests.Every_site_occurs_in_a_cell',
                            'DriverRollbackMatrixTests.A_failed_array_seed_leaves_no_assignment_in_the_source',
                            'DriverRollbackMatrixTests.Seed_operations_name_only_statements_in_the_source',
                            'DriverRollbackMatrixTests.A_rebuild_keeps_the_members_past_the_trigger_cap_uncovered',
                            'DriverRollbackMatrixTests.An_uncompilable_member_of_one_holder_stays_a_trigger_of_the_other',
                            'ValueSourceMatrixTests.No_seed_cell_leaves_a_statement_for_its_unseeded_path')
            if ((Working-Text 'src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation/DriverSynthesizer.cs').Contains('DropTriggers'))
            {
                Fail 'DriverSynthesizer.cs still holds DropTriggers'
            }
            Invoke-Tests 'rollback' (Class-Filter @('DriverRollbackMatrixTests', 'ValueSourceMatrixTests', 'SeedTests', 'DriverSynthesisTests',
                                                    'WitnessTests', 'CandidacyTests', 'DriverCompilationTests', 'ComparisonWitnessMatrixTests',
                                                    'GeneratedFateTests', 'AccessorDriverTests', 'ModelGeneratorCancellationTests'))
        }
        3
        {
            Assert-Listed @('CollectionValueMatrixTests.Every_cell_gets_its_expected_value',
                            'CollectionValueMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'CollectionValueMatrixTests.No_cell_declares_a_subclass_of_its_collection')
            Invoke-Tests 'collection-value' (Class-Filter @('CollectionValueMatrixTests', 'ParameterKindsTests', 'DriverSynthesisTests', 'SeedTests',
                                                            'WitnessTests', 'GeneratedEntryTests', 'EffectReaderTests', 'ValueProvenanceTests',
                                                            'ValueSourceMatrixTests', 'CandidacyMatrixTests', 'DriverRollbackMatrixTests'))
        }
        4
        {
            Invoke-Tests 'fresh-collection' (Class-Filter @('GeneratedEntryMatrixTests', 'ValueProvenanceTests', 'GeneratedEntryTests',
                                                            'EffectReaderTests', 'GenerationCauseTests', 'CollectionValueMatrixTests'))
            Assert-WiderInRemainder 119 $ENTRY_MATRIX
        }
        5
        {
            Invoke-Tests 'delegate-return' (Class-Filter @('GeneratedEntryMatrixTests', 'ValueProvenanceTests', 'GeneratedEntryTests',
                                                           'EffectReaderTests', 'GenerationCauseTests', 'CompletionObservationMatrixTests',
                                                           'DelegateReturnObservationMatrixTests'))
            Assert-WiderInRemainder 120 $ENTRY_MATRIX
        }
        6
        {
            Invoke-Tests 'keeping-chain' (Class-Filter @('GeneratedEntryMatrixTests', 'ValueProvenanceTests', 'GeneratedEntryTests',
                                                         'EffectReaderTests', 'AccessorEntryMatrixTests', 'DelegateSlotEntryTests',
                                                         'GenerationCauseTests', 'GeneratedStructEffectMatrixTests'))
            Assert-WiderInRemainder 121 $ENTRY_MATRIX
        }
        7
        {
            Assert-Listed @('ReceiverSeedAccessMatrixTests.A_shared_region_leaves_each_access_to_its_own_path',
                            'ReceiverSeedAccessMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'ReceiverSeedAccessMatrixTests.No_cell_narrows_unsafely',
                            'GeneratedEntryTests.A_read_of_the_value_a_setter_stores_is_a_read_of_its_argument')
            Invoke-Tests 'receiver-seed' (Class-Filter @('ReceiverSeedAccessMatrixTests', 'AccessorEntryMatrixTests', 'GeneratedEntryTests',
                                                         'GenerationCauseTests', 'EffectReaderTests', 'GeneratedStructEffectMatrixTests'))
            Assert-WiderInRemainder 132 $ACCESSOR_MATRIX
        }
        8
        {
            $fixture = @(Listed-Tests | Where-Object { $_ -like '*.GeneratedEntryTests.A_field_like_event_subscription_*' })
            $fact = Is-Listed 'AccessorEvalTests.BackgroundWorker_event_accessors_match_their_gold'
            if ($fixture.Count -gt 0 -and $fact)
            {
                Invoke-Tests 'background-worker-fixture' 'FullyQualifiedName~GeneratedEntryTests.A_field_like_event_subscription_'
                Invoke-EvalTests 'background-worker' 'FullyQualifiedName~AccessorEvalTests.BackgroundWorker_event_accessors_match_their_gold'
            }
            else
            {
                $remainder = Remainder-Text
                if (-not ($remainder.Contains('add_DoWork') -and $remainder.Contains('add_RunWorkerCompleted')))
                {
                    Fail "neither the fixture and the eval fact of task 8 nor a '## Остаток' naming both BackgroundWorker members"
                }
                Write-Host "BackgroundWorker: both members stay in '## Остаток'"
            }
        }
        9
        {
            Assert-Listed @('LibraryCompilationTests.An_ambiguous_type_name_after_a_using_compiles_whole',
                            'LibraryCompilationTests.A_local_function_with_a_display_class_in_its_signature_compiles_whole',
                            'ModelEvalTests.The_compile_fix_makes_no_compiled_body_extern')
            Invoke-Tests 'library-compilation' (Class-Filter @('LibraryCompilationTests', 'OpenedFieldsTests', 'DriverCompilationTests',
                                                               'StageCancellationTests', 'WorkCancellationTests'))
            Invoke-EvalTests 'compile-fix' 'FullyQualifiedName~ModelEvalTests.The_compile_fix_makes_no_compiled_body_extern'
        }
        10
        {
            Invoke-Dotnet 'build-cli' @('build', (Join-Path $root 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj'), '-c', 'Release',
                                        '--disable-build-servers', '-nodeReuse:false') | Out-Null
            $answer = Join-Path $logs 'execute-t.json'
            Remove-Item $answer -ErrorAction SilentlyContinue
            Invoke-Dotnet 'generate-execute-t' @('run', '--project', (Join-Path $root 'src/ConcurrencyHunter.Cli'), '-c', 'Release', '--no-build', '--',
                                                 'generate', '--assembly', 'Polly.Core', '--version', '8.7.0', '--package', 'Polly.Core',
                                                 '--member', $EXECUTE_OF_T, '--out', $answer) | Out-Null
            $document = Get-Content $answer -Raw | ConvertFrom-Json
            $fast = [double]$document.generation.seconds -le 40 -and $document.modelReason -eq 'unknown-touch' -and
                    $document.classified.callback.fate -eq 'unknown-execution'
            if ($fast)
            {
                Write-Host "Execute<T>: $($document.generation.seconds) s, unknown-touch, callback unknown-execution"
            }
            elseif (-not (Remainder-Text).Contains($EXECUTE_OF_T))
            {
                Fail "Execute<T> took $($document.generation.seconds) s with modelReason '$($document.modelReason)' and callback '$($document.classified.callback.fate)', and '## Остаток' does not name it"
            }
            else
            {
                Write-Host "Execute<T>: $($document.generation.seconds) s; '## Остаток' names it"
            }
            Invoke-Tests 'generation-limits' (Class-Filter @('RunGenerationTests', 'RunGenerationPassTests', 'ModelGenerationModeTests'))
        }
        12
        {
            if (-not (Remainder-Text).Contains($EXECUTE_OF_T))
            {
                Assert-Listed @('DemoGeneratedModelsTests.Polly_execute_of_T_is_no_timeout')
            }
            Invoke-Tests 'demo' (Class-Filter @('DemoGeneratedModelsTests', 'DemoExpectationTests', 'MetricsCommandTests', 'PublishedExecutableEndToEndTests'))

            $untracked = @(& git -C $root status --porcelain --untracked-files=all -- 'demo/.concurrency-hunter/models/generated' | Where-Object { $_ -like '`?`? *' })
            $global:LASTEXITCODE = 0
            if ($untracked.Count -ne 0)
            {
                Fail "new generated files in the demo: $($untracked -join '; ')"
            }

            Assert-Protected
            Invoke-Script 'rerecord' 'check-baseline-rerecord.ps1'
            Write-Host 'the rewritten baseline loses no case against HEAD'
        }
        13
        {
            Assert-Documents
        }
    }

    Write-Output "check-phase5d-runB4 task $Task ok"
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
