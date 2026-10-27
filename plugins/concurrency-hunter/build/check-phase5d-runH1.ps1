<#
.SYNOPSIS
The gate checks of concurrency-hunter phase 5d run H1, one task at a time.

.DESCRIPTION
Each task of the run's plan calls this script with its number. Every dotnet command and every nested script writes its
output to a log under scratch/h1-gate/ and is judged by its exit code; a failure prints the log's tail and exits 1. A task
that adds tests first checks that each test it names is in the test list, so a gate cannot pass on a filter that matches
nothing. Outside -Evals the model evals switch is cleared for the tests, and every gate but task 6 and -Full clears
CH_ESHOP_ROOT for them, so the eShop tests skip. Whatever the script puts into the environment it puts back. The gates of
tasks 1 to 5 do not run the tests that read the demo's committed generated files, which the generator version of these
tasks makes stale until task 6 records them again.

.PARAMETER Task
The task of the plan whose checks to run, 1 to 7.

.PARAMETER Fix
The short check of a code-review fix: a Release build and the test classes the plan's Fix gate names.

.PARAMETER Full
Runs the whole suite through check-test-baseline.ps1 (CH_ESHOP_ROOT must be set), then -Protected, then
check-baseline-rerecord.ps1 against HEAD.

.PARAMETER Evals
Runs the model evals against the committed snapshot without recording it, and checks that the snapshot equals HEAD's.

.PARAMETER Protected
Compares with HEAD what the run must not change or must account for: the CoreLib table equals HEAD; the eShop snapshot
equals HEAD but for its deciding gaps; the demo snapshot keeps every fingerprint of HEAD, gains only those of the R6 cases,
and has HEAD's number of groups plus those of the R6 cases (D-0023); every moved value of both snapshots is in the run
record. Runs no test.
#>
param([ValidateRange(1, 7)][int]$Task, [switch]$Fix, [switch]$Full, [switch]$Evals, [switch]$Protected)

if (@(@(($Task -ne 0), [bool]$Fix, [bool]$Full, [bool]$Evals, [bool]$Protected) | Where-Object { $_ }).Count -ne 1)
{
    Write-Host 'FAIL: name exactly one of -Task <1..7>, -Fix, -Full, -Evals and -Protected'
    exit 1
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'src/ConcurrencyHunter.slnx'
$logs = Join-Path $root 'scratch/h1-gate'
New-Item -ItemType Directory -Force $logs | Out-Null

$EVAL_CLASSES = @('ModelEvalTests', 'EffectsEvalTests', 'LinqOracleTests', 'AccessorEvalTests')
$DEMO_SNAPSHOT_TEST = 'Demo_measurement_matches_the_snapshot'
$R6_CASES = @('project-model-awaited-runs', 'project-model-receiver-type')

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

# Runs the tests a filter selects, with the model evals switch cleared and, unless -WithEShop, CH_ESHOP_ROOT cleared so that
# the eShop tests skip.
function Invoke-Tests([string]$logName, [string]$filter, [switch]$WithEShop)
{
    $environment = @{ CH_MODEL_EVALS = $null; CH_MODEL_EVALS_RECORD = $null }
    if (-not $WithEShop)
    {
        $environment['CH_ESHOP_ROOT'] = $null
    }
    $started = Get-Date
    With-Environment $environment {
        Invoke-Dotnet "test-$logName" @('test', $solution, '-c', 'Release', '--no-build', '--filter', $filter) | Out-Null
    }
    Write-Host ("tests pass: $logName in {0:0} s" -f ((Get-Date) - $started).TotalSeconds)
}

# A filter selecting every test of the named classes; a class is matched as a whole name segment.
function Class-Filter([string[]]$classes)
{
    return (($classes | ForEach-Object { "(FullyQualifiedName~.$_.)" }) -join '|')
}

$METRICS_WITHOUT_SNAPSHOT = "((FullyQualifiedName~.MetricsCommandTests.)&(FullyQualifiedName!~$DEMO_SNAPSHOT_TEST))"

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

# Checks that a class the task adds has tests in the test list.
function Assert-ClassListed([string[]]$classes)
{
    foreach ($class in $classes)
    {
        if (@(Listed-Tests | Where-Object { $_ -like "*.$class.*" }).Count -eq 0)
        {
            Fail "no test of $class is in the test list"
        }
    }
    Write-Host "listed: tests of $($classes -join ', ')"
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

# Fails when a file differs from HEAD.
function Assert-Unchanged([string]$relativePath)
{
    & git -C $root diff --quiet HEAD -- $relativePath
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0)
    {
        Fail "$relativePath differs from HEAD"
    }
    Write-Host "$relativePath equals HEAD"
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

# Whether an id of demo/expected-findings.json belongs to a case of R6: the case's id, or one of its own ids after a slash.
function Is-R6Id([string]$id)
{
    return @($R6_CASES | Where-Object { $id -eq $_ -or $id.StartsWith("$_/") }).Count -gt 0
}

$EXPECTATIONS = 'demo/expected-findings.json'
$DEMO_MODELS = 'demo/.concurrency-hunter/models/demo.json'
$CATALOG = 'demo/SCENARIOS.md'

# The entries of one list of the expectations file whose ids R6 owns, or does not own.
function Expectation-Entries([string]$text, [string]$list, [bool]$r6)
{
    return @(($text | ConvertFrom-Json).$list | Where-Object { (Is-R6Id ([string]$_.id)) -eq $r6 })
}

# Fails unless the entries of a list that R6 does not own are HEAD's, in HEAD's order.
function Assert-OthersAsHead([string]$what, $head, $working)
{
    if ((Canon @($head)) -ne (Canon @($working)))
    {
        Fail "$what outside the R6 cases differs from HEAD"
    }
}

# The R6 cases are written before the code (task 1): their files and registration, their records, their catalog rows and
# their expectations; nothing else of the demo's expectations, records or catalog changes.
function Assert-DemoCases
{
    foreach ($file in @('demo/Demo.Web/Cases/ProjectModelAwaitedRuns.cs', 'demo/Demo.Web/Cases/ProjectModelReceiverType.cs'))
    {
        Working-Text $file | Out-Null
    }
    Assert-Contains 'demo/Demo.Web/Program.cs' (Working-Text 'demo/Demo.Web/Program.cs') @('ProjectModelAwaitedRuns', 'ProjectModelReceiverType')
    Invoke-Dotnet 'demo-build' @('build', (Join-Path $root 'demo/Demo.slnx'), '-c', 'Release', '--nologo') | Out-Null
    Write-Host 'demo: builds'
    Invoke-Script 'demo-expectations' 'check-demo-expectations.ps1' @('-Complete')
    Write-Host 'demo: expectations agree with the case files and the catalog'

    $head = Committed-Text $EXPECTATIONS
    $working = Working-Text $EXPECTATIONS
    foreach ($list in @('findings', 'notDefects'))
    {
        Assert-OthersAsHead "$EXPECTATIONS, $list," (Expectation-Entries $head $list $false) (Expectation-Entries $working $list $false)
        if ((Expectation-Entries $head $list $true).Count -ne 0)
        {
            Fail "HEAD's $EXPECTATIONS already holds R6 entries in $list"
        }
    }
    $findings = @(Expectation-Entries $working 'findings' $true)
    $notDefects = @(Expectation-Entries $working 'notDefects' $true)
    foreach ($entry in @($findings) + @($notDefects))
    {
        if ($entry.phase -ne '5d')
        {
            Fail "$EXPECTATIONS, $($entry.id), has phase '$($entry.phase)', not 5d"
        }
    }
    $awaited = @($notDefects | Where-Object { $_.id -eq 'project-model-awaited-runs' })
    if ($awaited.Count -ne 1 -or -not (Canon $awaited[0]).Contains('Done'))
    {
        Fail "$EXPECTATIONS has no notDefects entry project-model-awaited-runs on Done"
    }
    if (@($findings | Where-Object { $_.id -like 'project-model-awaited-runs*' }).Count -ne 0)
    {
        Fail "$EXPECTATIONS expects a finding of project-model-awaited-runs"
    }
    $receiver = @($findings | Where-Object { $_.id -eq 'project-model-receiver-type' })
    if ($receiver.Count -ne 1)
    {
        Fail "$EXPECTATIONS has no finding project-model-receiver-type"
    }
    # A resource is named by the object's region, not by the field that holds it (SPEC 12.2): which object is which, the
    # analyzer's findings check in task 6 (DemoExpectationTests). Here: the unknown effect of the opaque call against the read
    # is the finding, on the same field of another object than the one the keyed record covers.
    $operations = @($receiver[0].accesses | ForEach-Object { $_.operation } | Sort-Object)
    if ((Canon $operations) -ne (Canon @('read', 'unknown-effect')))
    {
        Fail "$EXPECTATIONS, project-model-receiver-type, is not the unknown effect of the opaque call against the read"
    }
    if ($receiver[0].confidence -notin @('medium', 'low'))
    {
        Fail "$EXPECTATIONS, project-model-receiver-type, expects confidence '$($receiver[0].confidence)'; a deciding gap caps it below high"
    }
    $stringWriter = @($notDefects | Where-Object { $_.id -eq 'project-model-receiver-type/string-writer' })
    if ($stringWriter.Count -ne 1 -or (Canon $stringWriter[0].resource) -eq (Canon $receiver[0].resource) -or
        (Canon $stringWriter[0].resource.accessPath) -ne (Canon $receiver[0].resource.accessPath))
    {
        Fail "$EXPECTATIONS has no notDefects entry project-model-receiver-type/string-writer on the same field of the other object"
    }
    Write-Host 'demo: the R6 expectations are written and the others are HEAD''s'

    $headModels = @((Committed-Text $DEMO_MODELS | ConvertFrom-Json).models)
    $models = @((Working-Text $DEMO_MODELS | ConvertFrom-Json).models)
    $execute = @($models | Where-Object { ([string]$_.member).StartsWith('M:Polly.ResiliencePipeline.ExecuteAsync(') })
    $write = @($models | Where-Object { $_.member -eq 'M:System.IO.TextWriter.Write(System.Object)' })
    $others = @($models | Where-Object { $execute -notcontains $_ -and $write -notcontains $_ })
    Assert-OthersAsHead "$DEMO_MODELS, models," $headModels $others
    $awaitedFates = @($execute | ForEach-Object { $_.fates.PSObject.Properties.Value } |
                      Where-Object { $_.fate -eq 'invoke-now' -and $_.runs -eq 'sequential-awaited' })
    if ($execute.Count -ne 1 -or $awaitedFates.Count -ne 1)
    {
        Fail "$DEMO_MODELS has no single ExecuteAsync record whose callback is invoke-now with runs sequential-awaited"
    }
    $keyed = @($write | Where-Object { $_.receiver -eq 'T:System.IO.StringWriter' -and @($_.effects.value) -contains 'reads-deep' })
    $opaque = @($write | Where-Object { $null -eq $_.receiver -and $_.opaque -eq $true })
    if ($write.Count -ne 2 -or $keyed.Count -ne 1 -or $opaque.Count -ne 1)
    {
        Fail "$DEMO_MODELS does not hold the two TextWriter.Write(object) records: keyed by StringWriter with reads-deep, and opaque without a key"
    }
    Write-Host 'demo: the R6 records are written and the others are HEAD''s'

    $headRows = @((Committed-Text $CATALOG) -split "`r?`n")
    $rows = @((Working-Text $CATALOG) -split "`r?`n")
    $added = @($rows | Where-Object { $_ -match '^\|\s*`(project-model-awaited-runs|project-model-receiver-type)`\s*\|' })
    if ($added.Count -ne 2)
    {
        Fail "$CATALOG does not have one row for each R6 case"
    }
    $rest = @($rows | Where-Object { $added -notcontains $_ })
    if (($rest -join "`n").TrimEnd() -ne ($headRows -join "`n").TrimEnd())
    {
        Fail "$CATALOG changes more than the two R6 rows"
    }
    $anchor = [Array]::FindIndex([string[]]$rows, [Predicate[string]]{ param($row) $row -match '^\|\s*`exception-leaves-spawning-helper`\s*\|' })
    foreach ($row in $added)
    {
        $index = [Array]::IndexOf([string[]]$rows, $row)
        if ($anchor -lt 0 -or $index -le $anchor -or @($rows[($anchor + 1)..$index] | Where-Object { $_ -match '^##\s' }).Count -ne 0)
        {
            Fail "$CATALOG, the row of an R6 case, is not after exception-leaves-spawning-helper in the phase 5d table"
        }
    }
    Write-Host 'demo: the catalog has the two R6 rows at the end of the phase 5d table and nothing else changed'
}

$RECORD = 'docs/runs/5d-H1.md'
$DEMO_SNAPSHOT_FILE = 'src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json'
$ESHOP_SNAPSHOT_FILE = 'src/ConcurrencyHunter.Cli.Tests/Snapshots/eshop.json'

function Record-Lines
{
    return @((Working-Text $RECORD) -split "`r?`n")
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
    foreach ($key in @('scopeId', 'member', 'id', 'callee'))
    {
        if ($items.Count -gt 0 -and @($items | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties.Name -contains $key }).Count -eq $items.Count)
        {
            return $key
        }
    }
    return $null
}

# Collects every difference between two JSON values: a moved value by its path, an array element that appeared or
# disappeared by the path of the element. An array that appears or disappears whole is its elements appearing or
# disappearing; an empty one that appears is a value moved from null.
function Diff-Json($old, $new, [string]$path, [Collections.Generic.List[object]]$changes)
{
    if ($old -is [pscustomobject] -and $new -is [pscustomobject])
    {
        $names = @(@($old.PSObject.Properties.Name) + @($new.PSObject.Properties.Name) | Sort-Object -Unique)
        foreach ($name in $names)
        {
            $inner = if ($path) { "$path.$name" } else { $name }
            # Assigned directly: an if statement's output would unroll an array, an empty one into nothing.
            $left = $null
            if ($old.PSObject.Properties.Name -contains $name) { $left = $old.$name }
            $right = $null
            if ($new.PSObject.Properties.Name -contains $name) { $right = $new.$name }
            Diff-Json $left $right $inner $changes
        }
        return
    }
    if (($null -eq $old -and $new -is [array] -and $new.Count -gt 0) -or ($old -is [array] -and $old.Count -gt 0 -and $null -eq $new))
    {
        $from = $old
        if ($null -eq $from) { $from = @() }
        $to = $new
        if ($null -eq $to) { $to = @() }
        Diff-Json $from $to $path $changes
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

# A snapshot with every coverage item's deciding gaps taken out.
function Without-DecidingGaps($snapshot)
{
    foreach ($item in @($snapshot.coverage))
    {
        if ($item.PSObject.Properties.Name -contains 'decidingGaps')
        {
            $item.PSObject.Properties.Remove('decidingGaps')
        }
    }
    return $snapshot
}

# The CoreLib table equals HEAD; the eShop snapshot equals HEAD but for its deciding gaps; the demo snapshot keeps every
# fingerprint of HEAD, gains as many as the R6 cases expect findings, and has HEAD's groups plus those the R6 findings form —
# its composition of groups it does not hold, and the gate does not compare (D-0023); every moved value of both snapshots is
# in the run record.
function Assert-Protected
{
    Assert-Unchanged 'skills/hunt/evals/models/corelib-generation.tsv'

    $eshopBefore = Committed-Text $ESHOP_SNAPSHOT_FILE | ConvertFrom-Json
    $eshopAfter = Working-Text $ESHOP_SNAPSHOT_FILE | ConvertFrom-Json
    $changes = [Collections.Generic.List[object]]::new()
    Diff-Json $eshopBefore $eshopAfter '' $changes
    if ((Canon $eshopBefore) -ne (Canon (Without-DecidingGaps (Working-Text $ESHOP_SNAPSHOT_FILE | ConvertFrom-Json))))
    {
        Fail "$ESHOP_SNAPSHOT_FILE differs from HEAD in more than coverage[*].decidingGaps"
    }
    Assert-Recorded $changes 'eshop.json'

    $before = Committed-Text $DEMO_SNAPSHOT_FILE | ConvertFrom-Json
    $after = Working-Text $DEMO_SNAPSHOT_FILE | ConvertFrom-Json
    $old = @($before.counts.fingerprints)
    $new = @($after.counts.fingerprints)
    $lost = @($old | Where-Object { $new -notcontains $_ })
    if ($lost.Count -ne 0)
    {
        Fail "demo.json lost fingerprints of HEAD: $($lost -join ', ')"
    }
    $expected = @(Expectation-Entries (Working-Text $EXPECTATIONS) 'findings' $true)
    $gained = @($new | Where-Object { $old -notcontains $_ })
    if ($gained.Count -ne $expected.Count)
    {
        Fail "demo.json gained $($gained.Count) fingerprints; the R6 cases expect $($expected.Count) findings"
    }
    $r6Groups = @($expected | ForEach-Object { "$($_.rule)|$(Canon $_.resource)" } | Sort-Object -Unique).Count
    if ([int]$after.counts.groups -ne [int]$before.counts.groups + $r6Groups)
    {
        Fail "demo.json counts.groups is $($after.counts.groups); HEAD's $($before.counts.groups) plus the $r6Groups groups of the R6 findings is $([int]$before.counts.groups + $r6Groups)"
    }
    $changes = [Collections.Generic.List[object]]::new()
    Diff-Json $before $after '' $changes
    Assert-Recorded $changes 'demo.json'
    Write-Host 'CoreLib table equals HEAD; eshop.json equals HEAD but for its deciding gaps; demo.json keeps HEAD''s fingerprints and groups and adds those of R6'
}

# The section of a markdown text whose heading starts with the given text.
function Markdown-Section([string]$what, [string]$text, [string]$heading)
{
    $match = [regex]::Match($text, "(?ms)^##\s+$([regex]::Escape($heading)).*?(?=^##\s|\z)")
    if (-not $match.Success)
    {
        Fail "$what has no section '## $heading'"
    }
    return $match.Value
}

# The one line of a text that starts with the given text.
function Line-Starting([string]$what, [string]$text, [string]$start)
{
    $lines = @($text -split "`r?`n" | Where-Object { $_.StartsWith($start) })
    if ($lines.Count -ne 1)
    {
        Fail "$what has $($lines.Count) lines starting with '$start'"
    }
    return $lines[0]
}

# Fails when a document has a line naming a run H1 to H3 that HEAD's text does not have: check-docs.ps1 knows no such names.
function Assert-NoRunNames([string]$relativePath)
{
    $head = @((Committed-Text $relativePath) -split "`r?`n")
    $named = @((Working-Text $relativePath) -split "`r?`n" | Where-Object { $_ -match '\bH[1-3]\b' -and $head -notcontains $_ })
    if ($named.Count -ne 0)
    {
        Fail "$relativePath names a run H: $($named[0].Substring(0, [Math]::Min(160, $named[0].Length)))"
    }
}

# What task 7 makes the documents say (R9).
function Assert-Documents
{
    Invoke-Script 'docs' 'check-docs.ps1'

    $prd = Working-Text 'docs/PRD.md'
    $requirement = Line-Starting 'docs/PRD.md' $prd '**FR-21.'
    Assert-Lacks 'docs/PRD.md, FR-21,' $requirement @('Модель не доказывает защиту или порядок.')
    Assert-Contains 'docs/PRD.md, FR-21,' $requirement @('sequential-awaited')

    $spec = Working-Text 'docs/SPEC.md'
    # A name inside a longer code span, `coverage[].decidingGaps`, is named all the same.
    Assert-Contains 'docs/SPEC.md' $spec @('`runs`', '`receiver`', '`sequential-awaited`', 'decidingGaps', '1.4')
    Assert-Lacks 'docs/SPEC.md, its header of decisions,' (Line-Starting 'docs/SPEC.md' $spec '| Решения |') @('0029')
    Assert-NoRunNames 'docs/SPEC.md'
    Assert-NoRunNames 'CONTEXT.md'
    # The full meaning of the relation stays in SPEC; what the engine does not prove yet is PLAN's limit (F-0022).
    if (@($spec -split '(?<=[.!?])\s+' | Where-Object { $_.Contains('sequential-awaited') -and $_ -match 'следующ' }).Count -eq 0)
    {
        Fail 'docs/SPEC.md has no sentence that says what sequential-awaited orders before the next run'
    }
    if (@($spec -split "`r?`n" | Where-Object { $_ -match 'сгенерированн' -and $_.Contains('`receiver`') -and $_.Contains('`runs`') }).Count -eq 0)
    {
        Fail 'docs/SPEC.md has no line of the generated answer rule that refuses both receiver and runs'
    }

    $plan = Working-Text 'docs/PLAN.md'
    Assert-Contains 'docs/PLAN.md, the row of 5d run H1,' (Line-Starting 'docs/PLAN.md' $plan '| **5d run H1** |') @('| **5d run H1** | Построено')
    Assert-Contains 'docs/PLAN.md, the row of 5d run H2,' (Line-Starting 'docs/PLAN.md' $plan '| **5d run H2** |') @('RetryPolicy', 'AsyncRetryPolicy', '36 → 2', 'WebhookClient')
    $phases = Markdown-Section 'docs/PLAN.md' $plan '2.'
    Assert-Contains 'docs/PLAN.md, section 2' $phases @('A11, H1, H2, L1, L2, H3, C')
    # The date alone proves nothing: section 2 already dates the runs H1 to H3 to 2026-10-27.
    $sentences = @($phases -split '(?<=[.!?])\s+')
    if (@($sentences | Where-Object { $_.Contains('Polly') -and $_ -match '\bH2\b' -and $_.Contains('2026-10-27') }).Count -eq 0)
    {
        Fail 'docs/PLAN.md, section 2, has no sentence that moves the Polly records to H2 on 2026-10-27'
    }
    if (@($sentences | Where-Object { $_ -match 'черновик' -and $_.Contains('0029') }).Count -ne 0)
    {
        Fail 'docs/PLAN.md, section 2, still calls ADR 0029 a draft'
    }
    $limits = Markdown-Section 'docs/PLAN.md' $plan '3.'
    Assert-Contains 'docs/PLAN.md, section 3' $limits @('`sequential`', '`sequential-awaited`', 'multicast')
    if ($limits -notmatch 'синхронн')
    {
        Fail 'docs/PLAN.md, section 3, does not name the synchronous part of the next run as a limit'
    }

    Assert-Contains 'docs/QUESTIONS.md, question 188,' (Line-Starting 'docs/QUESTIONS.md' (Working-Text 'docs/QUESTIONS.md') '| 188 |') @('Закрыт → ADR 0029')

    # PowerShell names are case-insensitive: a local named like $RECORD would hide the record's path from Record-Lines.
    $recordText = Working-Text $RECORD
    foreach ($number in 1..23)
    {
        Assert-Contains $RECORD $recordText @(('D-{0:D4}' -f $number))
    }
    Write-Host 'documents: PRD, SPEC, PLAN, QUESTIONS and the run record say what R9 asks'
}

$FIX_CLASSES = @('RecordRunsReceiverMatrixTests', 'ReceiverRecordSelectionMatrixTests', 'ReceiverRecordLoweringTests', 'AwaitedRunsMatrixTests',
                 'AwaitedRunsBoundaryMatrixTests', 'AwaitedRunsTests', 'DecidingGapTests', 'ModelVocabularyTests', 'ProjectFateEntryTests',
                 'ModelEntryWriterTests', 'BuiltInModelFileTests', 'LibrarySemanticsTableTests', 'ProjectModelTests', 'ProjectModelIdentityTests',
                 'GeneratedEntryApplicabilityTests', 'GeneratedLayerMatrixTests', 'GeneratedEntryAcceptanceMatrixTests', 'GeneratedModelFilesTests',
                 'RunGenerationTests', 'RunGenerationPassTests', 'DelegateFateTests', 'SpawnExecutionTests', 'HappensBeforeTests',
                 'OpaqueDelegateExecutionTests', 'ResultJoinMatrixTests', 'TaskResultRunMatrixTests', 'GapConfidenceTests', 'SemanticGapTests',
                 'ReportRendererTests', 'CancellationCoverageTests', 'WorklistTests')

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
        Assert-Listed @('ModelEvalTests.Answers_match_the_recorded_snapshot')
        With-Environment @{ CH_MODEL_EVALS = '1'; CH_MODEL_EVALS_RECORD = $null } {
            Invoke-Dotnet 'evals' @('test', $solution, '-c', 'Release', '--no-build', '--filter', (Class-Filter $EVAL_CLASSES)) | Out-Null
        }
        Write-Host 'eval tests pass against the committed snapshot'
        Assert-Unchanged 'skills/hunt/evals/models/generator-snapshot.json'
        Write-Host 'PASS: evals'
        exit 0
    }

    if ($Fix)
    {
        Invoke-Build
        Invoke-Tests 'fix' ((Class-Filter $FIX_CLASSES) + "|$METRICS_WITHOUT_SNAPSHOT")
        Write-Host 'PASS: fix gate'
        exit 0
    }

    switch ($Task)
    {
        1
        {
            Assert-DemoCases
        }
        2
        {
            Invoke-Build
            Assert-ClassListed @('RecordRunsReceiverMatrixTests')
            Assert-Listed @('ProjectFateEntryTests.Fated_entries_differing_only_in_runs_disagree',
                            'ProjectFateEntryTests.Explicit_concurrent_runs_is_alike_to_none',
                            'ModelEntryWriterTests.Entry_and_fate_properties_have_the_fixed_order',
                            'ModelEntryWriterTests.A_complete_entry_has_the_expected_bytes')
            Assert-Unchanged 'src/ConcurrencyHunter.Core.Tests/Providers/BuiltInTable.txt'
            Invoke-Tests 'task2' (Class-Filter @('RecordRunsReceiverMatrixTests', 'ModelVocabularyTests', 'ProjectFateEntryTests', 'ModelEntryWriterTests',
                                                  'BuiltInModelFileTests', 'LibrarySemanticsTableTests', 'ProjectModelTests', 'ProjectModelIdentityTests',
                                                  'GeneratedModelFilesTests', 'GeneratedEntryAcceptanceMatrixTests', 'GeneratedEntryApplicabilityTests',
                                                  'GeneratedLayerMatrixTests', 'TaskResultVocabularyMatrixTests', 'DelegateFateTests', 'RunGenerationTests',
                                                  'CancellationCoverageTests'))
        }
        3
        {
            Invoke-Build
            Assert-ClassListed @('ReceiverRecordSelectionMatrixTests', 'ReceiverRecordLoweringTests')
            Assert-Listed @('RunGenerationPassTests.Call_a_keyed_opaque_record_answers_is_no_generation_target',
                            'RunGenerationPassTests.Call_left_opaque_by_disagreeing_keys_is_no_generation_target',
                            'RunGenerationPassTests.Member_with_keyed_and_uncovered_calls_is_a_target_only_through_the_uncovered_one',
                            'RunGenerationPassTests.Member_whose_calls_no_keyed_record_covers_stays_a_generation_target',
                            'LibrarySemanticsTableTests.Repeated_member_fails_the_build')
            Invoke-Tests 'task3' (Class-Filter @('ReceiverRecordSelectionMatrixTests', 'ReceiverRecordLoweringTests', 'RecordRunsReceiverMatrixTests',
                                                  'LibrarySemanticsTableTests', 'ProjectModelTests', 'ProjectModelIdentityTests', 'ProjectFateEntryTests',
                                                  'ModelVocabularyTests', 'ModelLockTests', 'GeneratedLayerMatrixTests', 'GeneratedEntryApplicabilityTests',
                                                  'GeneratedModelFilesTests', 'RunGenerationTests', 'RunGenerationPassTests', 'DelegateCombinationLoweringTests',
                                                  'EventLoweringTests', 'DelegateFateTests', 'CancellationCoverageTests'))
        }
        4
        {
            Invoke-Build
            Assert-ClassListed @('AwaitedRunsMatrixTests', 'AwaitedRunsBoundaryMatrixTests')
            Assert-Listed @('AwaitedRunsTests.Built_in_sequential_awaited_record_has_both_effects',
                            'AwaitedRunsTests.Sync_part_of_a_run_still_meets_the_continuation_of_another',
                            'AwaitedRunsTests.Sequential_runs_read_as_concurrent',
                            'AwaitedRunsTests.Wait_of_a_call_task_that_passes_the_gate_is_no_unproven_join',
                            'AwaitedRunsTests.Wait_of_a_call_task_that_fails_the_gate_stays_an_unproven_join',
                            'AwaitedRunsTests.Relation_of_a_generated_layer_call_proves_no_order',
                            'AwaitedRunsTests.Holder_triggers_keep_todays_reading',
                            'AwaitedRunsTests.ContinueWith_on_a_call_task_keeps_todays_reading')
            # The two engine matrices alone first, so the log shows their time (the plan sizes them at about 90 s).
            Invoke-Tests 'task4-matrices' (Class-Filter @('AwaitedRunsMatrixTests', 'AwaitedRunsBoundaryMatrixTests'))
            Invoke-Tests 'task4' (Class-Filter @('AwaitedRunsTests', 'DelegateFateTests', 'SpawnExecutionTests', 'HappensBeforeTests',
                                                  'OpaqueDelegateExecutionTests', 'ResultJoinMatrixTests', 'TaskResultRunMatrixTests', 'WorklistTests',
                                                  'CompletionObservationMatrixTests', 'RecordRunsReceiverMatrixTests', 'ReceiverRecordLoweringTests',
                                                  'ProjectFateEntryTests', 'RunGenerationPassTests', 'CancellationCoverageTests'))
        }
        5
        {
            Invoke-Build
            Assert-Listed @('DecidingGapTests.Gap_deciding_the_operation_check_is_listed_with_it',
                            'DecidingGapTests.Gap_deciding_the_overlap_check_is_listed_with_it',
                            'DecidingGapTests.Gap_deciding_the_protection_check_is_listed_with_it',
                            'DecidingGapTests.Gap_deciding_only_a_capped_occurrence_of_a_high_finding_is_listed',
                            'DecidingGapTests.Gap_on_the_call_path_that_decides_nothing_is_not_listed',
                            'DecidingGapTests.Call_a_record_makes_known_is_no_deciding_gap',
                            'DecidingGapTests.Call_a_record_declares_opaque_can_be_a_deciding_gap',
                            'DecidingGapTests.Gap_deciding_two_findings_lists_both_by_fingerprint',
                            'DecidingGapTests.Two_gaps_deciding_one_finding_are_two_entries',
                            'DecidingGapTests.Call_sites_are_the_gaps_own_sites',
                            'DecidingGapTests.Checks_of_one_callee_from_different_occurrences_are_united',
                            'DecidingGapTests.Call_sites_are_one_per_path_and_line_ordered_by_path_then_line_number',
                            'DecidingGapTests.Checks_are_listed_operation_overlap_protection',
                            'DecidingGapTests.Deciding_gaps_are_ordered_by_materiality_then_callee',
                            'DecidingGapTests.Report_lists_the_deciding_gaps_after_the_semantic_gaps_of_their_scope',
                            'DecidingGapTests.Scope_without_deciding_gaps_has_an_empty_list_and_no_report_section',
                            'MetricsCommandTests.Demo_measurement_has_every_field_and_thirteen_steps',
                            'SemanticGapTests.Measurement_carries_the_deciding_gaps_of_each_scope',
                            'ReportRendererTests.Findings_json_property_names_are_the_base_ones_with_spawn_and_timer_segments_in_paths')
            Invoke-Tests 'task5' ((Class-Filter @('DecidingGapTests', 'SemanticGapTests', 'GapConfidenceTests', 'ReportRendererTests',
                                                   'CancellationCoverageTests')) + "|$METRICS_WITHOUT_SNAPSHOT")
        }
        6
        {
            Invoke-Build
            Assert-EShopReady
            Assert-Listed @('DemoGapCoverageTests.Project_model_case_is_opaque_by_the_project_layer')
            # The committed generated files of the demo are the current generator's and hold every answer a demo run needs; the
            # published exe is the current one; both snapshots are measured anew.
            With-Environment @{ CONCURRENCYHUNTER_REQUIRE_E2E = '1' } {
                Invoke-Tests 'task6' (Class-Filter @('DemoGeneratedModelsTests', 'DemoExpectationTests', 'DemoGapCoverageTests', 'MetricsCommandTests',
                                                      'PublishedExecutableEndToEndTests', 'EShopSnapshotTests', 'SemanticGapTests',
                                                      'GeneratorBoundaryTests')) -WithEShop
            }
            $untracked = @(& git -C $root status --porcelain --untracked-files=all -- 'demo/.concurrency-hunter/models/generated' |
                           Where-Object { $_.StartsWith('??') })
            $global:LASTEXITCODE = 0
            if ($untracked.Count -ne 0)
            {
                Fail "generated demo files are not in the index: $($untracked -join '; ')"
            }
            Assert-Protected
            Invoke-Script 'task6-rerecord' 'check-baseline-rerecord.ps1'
            Write-Host 'the rewritten baseline loses no case against HEAD'
        }
        7
        {
            Assert-Documents
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
