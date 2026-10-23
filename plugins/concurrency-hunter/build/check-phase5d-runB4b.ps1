<#
.SYNOPSIS
The gate checks of concurrency-hunter phase 5d run B4b, one task at a time.

.DESCRIPTION
Each task of the run's plan calls this script with its number. Every dotnet command and every nested script writes its
output to a log under scratch/b4b-gate/ and is judged by its exit code; a failure prints the log's tail and exits 1. A task
that adds tests first checks that each of them is in the test list, so a gate cannot pass on a filter that matches nothing.
Outside -Evals and tasks 4 and 6 the model evals switch is cleared for the tests; task 4 sets it for its one named eval fact
only. Whatever the script puts into the environment it puts back.

.PARAMETER Task
The task of the plan whose checks to run, 1 to 7.

.PARAMETER Fix
Runs the short fix gate: the test classes the run adds or changes, without the tests of installed libraries, the model
evals, the demo and eShop.

.PARAMETER Full
Runs the whole suite through check-test-baseline.ps1 (CH_ESHOP_ROOT must be set), then -Protected, then
check-baseline-rerecord.ps1 against HEAD.

.PARAMETER Evals
Runs the model evals without recording the snapshot, then compares the snapshot with HEAD.

.PARAMETER Protected
Compares with HEAD what the run must not change or must account for: the eShop snapshot and the CoreLib table equal HEAD;
the demo's expected findings differ by exactly the two edits of R6; the demo's findings, groups and fingerprints equal HEAD;
every moved value of the demo snapshot is in the run record. Runs no test.
#>
param([ValidateRange(1, 7)][int]$Task, [switch]$Fix, [switch]$Full, [switch]$Evals, [switch]$Protected)

if (@(@(($Task -ne 0), [bool]$Fix, [bool]$Full, [bool]$Evals, [bool]$Protected) | Where-Object { $_ }).Count -ne 1)
{
    Write-Host 'FAIL: name exactly one of -Task <1..7>, -Fix, -Full, -Evals and -Protected'
    exit 1
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# git show prints the committed files as UTF-8.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'
$logs = Join-Path $root 'scratch/b4b-gate'
$recordPath = Join-Path $root 'docs/runs/5d-B4b.md'
New-Item -ItemType Directory -Force $logs | Out-Null

$PARSER_CTOR = 'M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})'
$R5_MEASURE = Join-Path $logs 'r5-measure.json'

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
    return (Get-Content $path -Raw -Encoding utf8).TrimStart([char]0xFEFF)
}

# The run record's lines; the record must exist.
function Record-Lines
{
    return @((Working-Text 'docs/runs/5d-B4b.md') -split "`r?`n")
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

function Copy-Json($value)
{
    return ($value | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
}

# Fails unless demo/expected-findings.json differs from HEAD by exactly the two edits of R6: the finding
# protobuf-parser-factory-at-parse/factory-runs-at-parse moves to 5d-B4c, and the notDefect
# protobuf-parser-factory-at-parse/factory-fires-in-type-initializers (5d-B4b until 5d-B4c) is added.
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

    $factory = Copy-Json (Committed 'findings' 'protobuf-parser-factory-at-parse/factory-runs-at-parse')
    $factory.phase = '5d-B4c'
    $expected.findings[$factory.id] = Canon $factory
    $holder = Committed 'notDefects' 'protobuf-parser-factory-at-parse/holder-unconfirmed'
    $fires = [pscustomobject]@{ id = 'protobuf-parser-factory-at-parse/factory-fires-in-type-initializers'; phase = '5d-B4b'; until = '5d-B4c'; resource = $holder.resource }
    $expected.notDefects[$fires.id] = Canon $fires

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
                Fail "$list has $id, which R6 does not add"
            }
            if ($actual[$id] -ne $expected[$list][$id])
            {
                Fail "$list entry $id is not what R6 asks: expected $($expected[$list][$id]), found $($actual[$id])"
            }
        }
    }
    Write-Host 'demo/expected-findings.json differs from HEAD by exactly the edits of R6'
}

# Fails unless what the run must not change equals HEAD, the expectations carry exactly the edits of R6, and every moved
# value of the demo snapshot is in the run record.
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
    Write-Host 'eShop snapshot, CoreLib table and demo findings equal HEAD; expectations carry exactly R6'
}

# Fails unless the evals snapshot keeps HEAD's entries, packages and body counts, lowers no threshold, and the run record
# names every move.
function Assert-EvalsSnapshot
{
    $path = 'skills/hunt/evals/models/generator-snapshot.json'
    $before = Committed-Text $path | ConvertFrom-Json
    $after = Working-Text $path | ConvertFrom-Json
    $lines = $null
    $fields = @('reason', 'modelReason', 'classified', 'model', 'detail', 'causes', 'reachedBodies')
    # A moved scalar is recorded with both its values; a moved structure, by its field's name.
    $scalars = @('reason', 'modelReason', 'reachedBodies')

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
            if ((Canon $old[$member].implementation) -ne (Canon $entry.implementation))
            {
                Fail "$section`: the implementation of $member changed; the snapshot was recorded against another install"
            }
            $moved = @($fields | Where-Object { (Canon $old[$member].$_) -ne (Canon $entry.$_) })
            if ($moved.Count -gt 0)
            {
                if ($null -eq $lines) { $lines = Record-Lines }
                $recorded = $old[$member]
                $found = @($lines | Where-Object {
                    $line = $_
                    $unnamed = @($moved | Where-Object {
                        if ($_ -in $scalars) { -not ($line.Contains((Shown $recorded.$_)) -and $line.Contains((Shown $entry.$_))) } else { -not $line.Contains($_) }
                    })
                    $line.Contains($member) -and $line -match '\bR[1-4]\b' -and $unnamed.Count -eq 0
                })
                if ($found.Count -eq 0)
                {
                    Fail ("$section`: $member moved ($($moved -join ', ')) and the run record has no line with it, its requirement R1-R4, " +
                          'both values of each moved reason, modelReason and reachedBodies and the name of each other moved field')
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
    Assert-Recorded $changes 'evals snapshot numbers' '\bR[1-4]\b'
    Write-Host 'evals snapshot: same entries, packages and bodies, no threshold fell, every move is in the run record'
}

# Builds the CLI in Release and generates the MessageParser<T> constructor of Google.Protobuf 3.30.2 in its own process,
# measuring the wall time and the peak working set and private memory of that process (R5). Writes the numbers and the
# factory's fate to scratch/b4b-gate/r5-measure.json; the measure has no threshold, so it fails only when generate does.
function Measure-R5
{
    Invoke-Dotnet 'build-cli' @('build', (Join-Path $root 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj'), '-c', 'Release',
                                '--disable-build-servers', '-nodeReuse:false') | Out-Null
    $exe = Join-Path $root 'src/ConcurrencyHunter.Cli/bin/Release/net10.0/win-x64/concurrency-hunter.exe'
    if (-not (Test-Path $exe))
    {
        Fail "$exe does not exist after the build"
    }
    $answer = Join-Path $logs 'r5-answer.json'
    Remove-Item $answer -ErrorAction SilentlyContinue

    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($argument in @('generate', '--assembly', 'Google.Protobuf', '--version', '3.30.2', '--package', 'Google.Protobuf',
                            '--member', $PARSER_CTOR, '--out', $answer))
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
    $peakWorkingSet = 0L
    $peakPrivate = 0L
    while (-not $process.WaitForExit(100))
    {
        try
        {
            $process.Refresh()
            $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
            $peakPrivate = [Math]::Max($peakPrivate, $process.PeakPagedMemorySize64)
        }
        catch [InvalidOperationException]
        {
            # The process exited between the wait and the read.
        }
        if ($watch.Elapsed.TotalSeconds -gt 300)
        {
            $process.Kill()
            Fail "generate of $PARSER_CTOR ran past 300 s and was stopped (PID $($process.Id))"
        }
    }
    $process.WaitForExit()
    $watch.Stop()
    Set-Content (Join-Path $logs 'r5-generate.log') -Encoding utf8 -Value ($stdout.Result + $stderr.Result)
    if ($process.ExitCode -ne 0)
    {
        Fail "generate of $PARSER_CTOR exited $($process.ExitCode); log: $(Join-Path $logs 'r5-generate.log')"
    }

    $document = Get-Content $answer -Raw -Encoding utf8 | ConvertFrom-Json
    $factory = $document.classified.factory
    $measure = [ordered]@{
        member = $PARSER_CTOR
        package = 'Google.Protobuf 3.30.2'
        seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
        generationSeconds = $document.generation.seconds
        peakWorkingSetBytes = $peakWorkingSet
        peakPrivateBytes = $peakPrivate
        reachedBodies = $document.generation.reachedBodies
        fate = $factory.fate
        holder = $factory.holder
        modelFate = $document.model.fates.factory.fate
    }
    $measure | ConvertTo-Json | Set-Content $R5_MEASURE -Encoding utf8
    Write-Host "R5: $($measure.seconds) s, peak working set $([Math]::Round($peakWorkingSet / 1MB)) MB, peak private $([Math]::Round($peakPrivate / 1MB)) MB, factory $($factory.fate) $($factory.holder)"
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

# Fails unless the documents describe the run as R8 asks.
function Assert-Documents
{
    Invoke-Script 'check-docs' 'check-docs.ps1'

    $questions = @((Working-Text 'docs/QUESTIONS.md') -split "`r?`n")
    $row101 = Question-Row $questions 101
    Assert-Contains 'question 101' $row101 @('`protobuf-parser-factory-at-parse`', '`5d-B4c`', '2006', 'ParseFrom')
    if (-not $row101.TrimEnd().EndsWith('| 5d run B4c |'))
    {
        Fail "question 101's phase is not '5d run B4c'"
    }
    Assert-Contains 'question 143' (Question-Row $questions 143) @('| 143 | Закрыт → ADR 0019; SPEC TD-008, TD-034b.')
    $row187 = Question-Row $questions 187
    Assert-Contains 'question 187' $row187 @('ForMessage', 'ParseContext', 'WriteContext', 'incomplete')
    if (-not $row187.TrimEnd().EndsWith('| 5d run B5 |'))
    {
        Fail "question 187's phase is not '5d run B5'"
    }

    $plan = @((Working-Text 'docs/PLAN.md') -split "`r?`n")
    $tableRuns = @($plan | ForEach-Object { if ($_ -match '^\| \*\*5d run (\w+)\*\* \|') { $Matches[1] } })
    $index = @{}
    for ($position = 0; $position -lt $tableRuns.Count; $position++)
    {
        $index[$tableRuns[$position]] = $position
    }
    $order = @('B4', 'B4b', 'B4c', 'B4d', 'B5', 'C')
    foreach ($run in $order)
    {
        if (@($tableRuns | Where-Object { $_ -ceq $run }).Count -ne 1)
        {
            Fail "docs/PLAN.md has no single table row of 5d run $run"
        }
    }
    for ($position = 1; $position -lt $order.Count; $position++)
    {
        if ($index[$order[$position - 1]] -ge $index[$order[$position]])
        {
            Fail "the table rows of docs/PLAN.md are not in the order $($order -join ', ')"
        }
    }
    $rowB4b = @($plan | Where-Object { $_ -match '^\| \*\*5d run B4b\*\* \|' })[0]
    Assert-Contains "docs/PLAN.md's row of 5d run B4b" $rowB4b @('143', '101', '0019', '0025')
    $rowB4c = @($plan | Where-Object { $_ -match '^\| \*\*5d run B4c\*\* \|' })[0]
    Assert-Contains "docs/PLAN.md's row of 5d run B4c" $rowB4c @('`High`-находка `protobuf-parser-factory-at-parse`', '`phase` `5d-B4c`', '101')
    $rowB4d = @($plan | Where-Object { $_ -match '^\| \*\*5d run B4d\*\* \|' })[0]
    Assert-Contains "docs/PLAN.md's row of 5d run B4d" $rowB4d @('reached-through-model', 'снятая граница проверена тестом')
    $section = @($plan | Where-Object { $_.StartsWith('Фаза 5d проходит в ') })
    if ($section.Count -ne 1 -or -not $section[0].StartsWith('Фаза 5d проходит в двадцати двух ранах.'))
    {
        Fail "docs/PLAN.md has no single line 'Фаза 5d проходит в двадцати двух ранах.'"
    }
    $order = [regex]::Match($section[0], 'Раны идут в порядке ([\w, ]+)\.')
    if (-not $order.Success -or ($order.Groups[1].Value -split ', ' -join ',') -ne ($tableRuns -join ','))
    {
        Fail "section 2 of docs/PLAN.md does not list the runs in the order of the table: $($tableRuns -join ', ')"
    }
    $limits = @($plan | Where-Object { $_.StartsWith('- ') -and $_ -match 'TD-034[bc]' })
    if (@($limits | Where-Object { $_.Contains('5d run B4b)') -or $_.Contains('5d run B4c)') }).Count -ne 0)
    {
        Fail 'section 3 of docs/PLAN.md still names 5d run B4b or B4c for a limit'
    }
    if (@($limits | Where-Object { $_.Contains('5d run B4d)') }).Count -lt 4)
    {
        Fail 'section 3 of docs/PLAN.md does not name 5d run B4d for the four limits'
    }

    $spec = Working-Text 'docs/SPEC.md'
    Assert-Lacks 'docs/SPEC.md' $spec @('Только генерация CoreLib использует dispatch по конструируемым типам',
                                         'Генерация CoreLib строит reachable set по конструируемым типам')
    Assert-Contains 'docs/SPEC.md' $spec @('`5d-B4b`, `5d-B4c`')
    # SPEC 12.1 item 8a states every threshold of the snapshot at the end of the run, each in its own wording.
    $snapshot = Working-Text 'skills/hunt/evals/models/generator-snapshot.json' | ConvertFrom-Json
    $item8a = @($spec -split "`r?`n" | Where-Object { $_.StartsWith('8a. ') })
    if ($item8a.Count -ne 1)
    {
        Fail "docs/SPEC.md has $($item8a.Count) lines of item 8a, not one"
    }
    $groups = @($snapshot.linqExact.PSObject.Properties | Sort-Object Name -CaseSensitive | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '
    Assert-Contains 'SPEC 12.1 item 8a' $item8a[0] @("``exact`` снапшота ($($snapshot.exact))", "``fateGoldEntries`` ($($snapshot.fateGoldEntries))",
                                                    "``effectsExact`` ($($snapshot.effectsExact),", "``linqExact`` ($groups)",
                                                    "``accessorsExact`` ($($snapshot.accessorsExact) из ")

    Assert-Contains 'skills/hunt/evals/models/README.md' (Working-Text 'skills/hunt/evals/models/README.md') @('5d-B4b.md')
    Assert-Contains 'docs/research/library-models.md' (Working-Text 'docs/research/library-models.md') @('ReadOnlySpan')

    $scenarios = @((Working-Text 'demo/SCENARIOS.md') -split "`r?`n" | Where-Object { $_.Contains('`protobuf-parser-factory-at-parse`') -and $_.StartsWith('|') })
    if ($scenarios.Count -ne 1)
    {
        Fail "demo/SCENARIOS.md has $($scenarios.Count) rows of protobuf-parser-factory-at-parse, not one"
    }
    Assert-Contains 'the SCENARIOS row of protobuf-parser-factory-at-parse' $scenarios[0] @('0025', '`5d-B4c`')
    Assert-Lacks 'the SCENARIOS row of protobuf-parser-factory-at-parse' $scenarios[0] @('25 при пределе 24')

    $record = Working-Text 'docs/runs/5d-B4b.md'
    foreach ($heading in @('## Model evals', '## Demo', '## Генерация CoreLib', '## Остаток'))
    {
        if ($record -notmatch "(?m)^$([regex]::Escape($heading))[ \t]*\r?$")
        {
            Fail "docs/runs/5d-B4b.md has no section '$heading'"
        }
    }
    Assert-Contains 'docs/runs/5d-B4b.md' $record @('generate-corelib.ps1 -Since', '101', '187', 'Точка сравнения — `5d-B4b`', 'R5', '2006')
    if (-not (Test-Path $R5_MEASURE))
    {
        Fail "$R5_MEASURE does not exist; the gate of task 4 writes it"
    }
    $measured = Get-Content $R5_MEASURE -Raw -Encoding utf8 | ConvertFrom-Json
    $seconds = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $measured.seconds)
    # A peak is recorded in bytes or in whole megabytes.
    $peaks = @($measured.peakWorkingSetBytes, $measured.peakPrivateBytes)
    $lines = @($record -split "`r?`n" | Where-Object {
        $line = $_
        $line.Contains('R5') -and ($line.Contains($seconds) -or $line.Contains($seconds.Replace('.', ','))) -and $line.Contains([string]$measured.fate) -and
        @($peaks | Where-Object { -not ($line.Contains([string]$_) -or $line -match "\b$([Math]::Round([double]$_ / 1MB))\s*(MB|МБ)") }).Count -eq 0
    })
    if ($lines.Count -eq 0)
    {
        Fail ("docs/runs/5d-B4b.md has no line with R5, the measured $seconds s, both peaks ($($peaks -join ' and ') bytes) " +
              "and the fate $($measured.fate) of $R5_MEASURE")
    }
    Write-Host 'docs hold the run'
}

try
{
    if ($Fix)
    {
        $classes = @('ReachabilityRuleSelectionTests', 'PackageReachabilityTests', 'SpanTriggerArgumentMatrixTests',
                     'ObjectMemberTriggerMatrixTests', 'ConfirmationAccessesTests', 'ScopePipelineTests', 'GeneratedFateTests',
                     'DriverSynthesisTests', 'DriverRollbackMatrixTests', 'ValueProvenanceTests', 'NotRunClassificationTests',
                     'AccessorDriverTests', 'ValueSourceMatrixTests', 'ParameterKindsTests', 'TypeShapeTests', 'GeneratedEntryTests',
                     'AllocationsTests', 'ExpectationFileRuleTests', 'ExpectationMatcherTests', 'LibraryStaticHandoffMatrixTests',
                     'GeneratedStructEffectMatrixTests', 'CancellationCoverageTests')
        Invoke-Tests 'fix' "($(Class-Filter $classes))&FullyQualifiedName!~Installed_"
        Write-Output 'check-phase5d-runB4b fix gate ok'
        exit 0
    }

    if ($Protected)
    {
        Assert-Protected
        Write-Output 'check-phase5d-runB4b protected ok'
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
        Write-Output 'check-phase5d-runB4b full ok'
        exit 0
    }

    if ($Evals -or $Task -eq 6)
    {
        Invoke-EvalTests 'model-evals' (Class-Filter @('ModelEvalTests', 'EffectsEvalTests', 'LinqOracleTests', 'AccessorEvalTests'))
        Assert-EvalsSnapshot
        Write-Output "check-phase5d-runB4b $(if ($Evals) { 'evals' } else { 'task 6' }) ok"
        exit 0
    }

    switch ($Task)
    {
        1
        {
            Assert-Listed @('ReachabilityRuleSelectionTests.Every_cell_gets_its_expected_rule',
                            'ReachabilityRuleSelectionTests.Every_axis_value_occurs_in_a_cell',
                            'PackageReachabilityTests.A_package_interface_call_reaches_only_the_overrides_of_constructed_types',
                            'PackageReachabilityTests.A_package_call_into_a_body_outside_the_set_is_an_opaque_call',
                            'PackageReachabilityTests.A_package_whose_method_a_recognizer_claims_is_refused_whole')
            Invoke-Tests 'reachability' (Class-Filter @('ReachabilityRuleSelectionTests', 'PackageReachabilityTests', 'GeneratedFateTests',
                                                        'DriverSynthesisTests', 'GeneratedEntryTests', 'GeneratedEntryMatrixTests',
                                                        'EffectReaderTests', 'ValueProvenanceTests', 'NotRunClassificationTests',
                                                        'CoreLibDiagnosticsOffTests', 'CoreLibRecognizedTypeTests',
                                                        'CoreLibGenerationSetMatrixTests', 'ConstructedValueTypeMatrixTests', 'ScopePipelineTests'))
        }
        2
        {
            Assert-Listed @('SpanTriggerArgumentMatrixTests.Every_cell_gets_its_expected_trigger',
                            'SpanTriggerArgumentMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'SpanTriggerArgumentMatrixTests.No_cell_is_left_out_by_Allowed',
                            'SpanTriggerArgumentMatrixTests.A_ref_struct_other_than_a_span_leaves_its_member_uncovered',
                            'SpanTriggerArgumentMatrixTests.A_pointer_argument_leaves_its_member_uncovered')
            Invoke-Tests 'span-triggers' (Class-Filter @('SpanTriggerArgumentMatrixTests', 'DriverSynthesisTests', 'GeneratedFateTests',
                                                         'DriverRollbackMatrixTests', 'ValueSourceMatrixTests', 'AccessorDriverTests',
                                                         'ParameterKindsTests', 'TypeShapeTests', 'ValueProvenanceTests', 'PackageReachabilityTests'))
        }
        3
        {
            Assert-Listed @('ObjectMemberTriggerMatrixTests.Every_cell_gets_its_expected_trigger_set',
                            'ObjectMemberTriggerMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'ObjectMemberTriggerMatrixTests.A_message_parser_shaped_holder_fits_the_24_triggers')
            Invoke-Tests 'object-members' (Class-Filter @('ObjectMemberTriggerMatrixTests', 'SpanTriggerArgumentMatrixTests', 'DriverSynthesisTests',
                                                          'DriverRollbackMatrixTests', 'GeneratedFateTests', 'ValueProvenanceTests',
                                                          'NotRunClassificationTests', 'AccessorDriverTests', 'ValueSourceMatrixTests'))
        }
        4
        {
            Assert-Listed @('ConfirmationAccessesTests.Each_execution_role_is_collected_as_its_readers_read_it',
                            'ConfirmationAccessesTests.The_confirmation_collects_no_access_in_a_triggers_execution',
                            'ConfirmationAccessesTests.Holder_inputs_are_the_same_as_with_every_execution_collected',
                            'ScopePipelineTests.An_execution_filter_leaves_out_the_accesses_of_the_executions_it_refuses',
                            'GeneratedFateTests.Holder_whose_confirmation_run_passes_the_bound_is_unknown_execution',
                            'ModelEvalTests.Message_parser_holder_is_covered_and_its_confirmation_passes_the_bound')
            if (Is-Listed 'GeneratedFateTests.A_holder_whose_confirmation_reaches_between_1500_and_3000_bodies_is_confirmed')
            {
                Fail 'GeneratedFateTests.A_holder_whose_confirmation_reaches_between_1500_and_3000_bodies_is_confirmed is still in the test list'
            }
            $generator = Working-Text 'src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation/ModelGenerator.cs'
            Assert-Contains 'ModelGenerator.cs' $generator @('CLOSURE_BOUND = 1500')
            Assert-Lacks 'ModelGenerator.cs' $generator @('CONFIRMATION_BOUND')
            Assert-Contains 'DriverExecutions.cs' (Working-Text 'src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation/DriverExecutions.cs') @('TreeActionOf')
            Invoke-Tests 'confirmation' (Class-Filter @('ConfirmationAccessesTests', 'ScopePipelineTests', 'GeneratedFateTests', 'ValueProvenanceTests',
                                                        'GeneratedEntryTests', 'AllocationsTests', 'NotRunClassificationTests', 'DriverSynthesisTests',
                                                        'EngineCancellationTests', 'StageCancellationTests', 'ModelGeneratorCancellationTests'))
            Invoke-EvalTests 'parser-holder' ('FullyQualifiedName~ModelEvalTests.Message_parser_constructor_finishes_under_the_bound|' +
                                              'FullyQualifiedName~ModelEvalTests.Message_parser_holder_is_covered_and_its_confirmation_passes_the_bound')
            Measure-R5
        }
        5
        {
            Assert-Listed @('LibraryStaticHandoffMatrixTests.Every_cell_gets_its_expected_answer',
                            'LibraryStaticHandoffMatrixTests.Every_axis_value_occurs_in_a_cell',
                            'GeneratedStructEffectMatrixTests.No_cell_gets_a_model_that_misses_its_effect')
            Invoke-Tests 'static-handoff' (Class-Filter @('LibraryStaticHandoffMatrixTests', 'GeneratedStructEffectMatrixTests', 'CancellationCoverageTests',
                                                          'PackageReachabilityTests', 'EffectReaderTests', 'GeneratedEntryTests',
                                                          'GeneratedEntryMatrixTests', 'GenerationHandoffsTests', 'DelegateSlotEntryTests',
                                                          'ValueProvenanceTests', 'ModelGeneratorCancellationTests'))
            Assert-Contains 'DemoExpectationTests.cs' (Working-Text 'src/ConcurrencyHunter.Core.Tests/DemoExpectationTests.cs') @('CURRENT_POINT = "5d-B4b"')
            Assert-Contains 'PhaseOrder.cs' (Working-Text 'src/ConcurrencyHunter.Core.Tests/Expectations/PhaseOrder.cs') @('"B4b", "B4c"')
            Invoke-Tests 'demo' (Class-Filter @('DemoExpectationTests', 'DemoGeneratedModelsTests', 'MetricsCommandTests', 'PublishedExecutableEndToEndTests',
                                                'ExpectationFileRuleTests', 'ExpectationMatcherTests'))
            Invoke-Script 'check-demo-expectations' 'check-demo-expectations.ps1'

            $untracked = @(& git -C $root status --porcelain --untracked-files=all -- 'demo/.concurrency-hunter/models/generated' | Where-Object { $_ -like '`?`? *' })
            $global:LASTEXITCODE = 0
            if ($untracked.Count -ne 0)
            {
                Fail "new generated files in the demo: $($untracked -join '; ')"
            }

            Assert-Protected
            Assert-Contains 'docs/runs/5d-B4b.md' (Working-Text 'docs/runs/5d-B4b.md') @('## Demo', 'Точка сравнения — `5d-B4b`')
            Invoke-Script 'rerecord' 'check-baseline-rerecord.ps1'
            Write-Host 'the rewritten baseline loses no case against HEAD'
        }
        7
        {
            Assert-Documents
        }
    }

    Write-Output "check-phase5d-runB4b task $Task ok"
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
