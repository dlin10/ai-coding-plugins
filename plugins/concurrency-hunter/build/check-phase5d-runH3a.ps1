<#
.SYNOPSIS
The gates of concurrency-hunter phase 5d run H3a: a forwarded task joins its async call (rule A), and objects library
bodies create stay apart per admitted call site (rule B).

.DESCRIPTION
One step per run gate. Test classes run through build/check-phase5d-runH3-gate.ps1, whose logs land in scratch/h3/gates
under an h3a- name; this script's own logs land in scratch/h3a/gates.

The normal-run oracle is scratch/h3a/baseline/{demo,eshop}-records.json: the records runs (records on, nothing admitted)
of CallSiteProbeTests on the tree the run started from, pinned below by SHA-256. A later records run passes when every
fingerprint it has is in the oracle with the same rule and confidence, and every oracle fingerprint it lacks is named in
docs/runs/5d-H3a.md.

.PARAMETER Step
baseline   - takes the oracle; run once, by the orchestrator, before task 1, then pin the printed hashes.
forwarder  - task 1's gate: the rule A matrix, the join classes, the eShop probe facts and the demo records run against
             the oracle, the demo and eShop snapshot tests, then records.
context    - task 2's gate: the rule B matrix, the context classes, the snapshot tests, records, then probe.
probe      - G4, the H3 probe narrowed by decision D-0019: its runs with the demo admitted fact left out, eShop admitted
             equal to the records, demo admitted equal to the records plus exactly the findings #400 owns, then the
             records runs against the oracle. build/check-phase5d-runH3-probe.ps1 stays #279's acceptance.
docs       - task 3's gate: records, the run record, PLAN, SPEC and the test baseline.
records    - G2: docs, expectations, baseline re-record, snapshots against HEAD and the run record, demo models.
invariants - G3: what the run leaves alone, and the pinned probe files; the probe tests may differ from their pin only
             by the skip decision D-0020 puts on the demo admitted fact, and the model eval snapshot from HEAD only in
             the implementation identity (D-0022, D-0023).
suite      - G1: publish and the whole suite through build/check-test-baseline.ps1.
evals      - G5: the existing model evals with CH_MODEL_EVALS=1 and no skips.
fix        - the Fix gate: the short check after a targeted code-review fix.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('baseline', 'forwarder', 'context', 'probe', 'docs', 'records', 'invariants', 'suite', 'evals', 'fix')]
    [string] $Step
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'scratch/h3a/gates'
$baselineDirectory = Join-Path $root 'scratch/h3a/baseline'
$normalDirectory = Join-Path $root 'scratch/h3a/normal'
$probeDirectory = Join-Path $root 'scratch/h3a/probe'
$runRecord = Join-Path $root 'docs/runs/5d-H3a.md'
$helper = Join-Path $PSScriptRoot 'check-phase5d-runH3-gate.ps1'
$core = 'src/ConcurrencyHunter.Core.Tests'
$cli = 'src/ConcurrencyHunter.Cli.Tests'
$utf8 = [Text.UTF8Encoding]::new($false)

# The oracle's files, pinned by the orchestrator from the baseline step's output.
$BASELINE_HASHES = [ordered]@{
    'demo-records.json'  = '8D4DAC2423B8D8E5FC73FAC8054CDA2ABA9711E5D0C741448E9AA0BD27CBE3EC'
    'eshop-records.json' = 'B15045738FE2F76A30B1C20AD10F96097CF8D40F8C213A037DF7C167440E07CF'
}

# Files the run must not change (plan G3): the H3 probe, its gate helper and its tests.
$PINNED_FILES = [ordered]@{
    'build/check-phase5d-runH3-probe.ps1'                             = 'CB700BBC4CCB4EFB607B3032CBA393CA7897FD9422738B332765C2A09629F1E8'
    'build/check-phase5d-runH3-gate.ps1'                              = '96EDA4D1C35DB4281834188A94EA5F3139819190686B18CACCA06B70846227D6'
    'src/ConcurrencyHunter.Core.Tests/Engine/CallSiteProbeTests.cs'   = '9FDC471276E020938ADFA9D316FBEA5AC6F716F6A6E9F47C620DED2F2F5C7F99'
}

# D-0020: the one attribute task 3 may put on the probe's demo admitted fact, which waits for #400.
$PROBE_TESTS = 'src/ConcurrencyHunter.Core.Tests/Engine/CallSiteProbeTests.cs'
$SKIPPED_FACT_ATTRIBUTE = '[Fact(Skip = "Waits for #400: one task region stands for several executions in one tree.")]'
$SKIPPED_FACT_NAME = 'Demo_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records'

# D-0022/D-0023: the model eval snapshot, re-recorded after the machine's runtime update, may differ from HEAD only in
# the identity of the framework implementations that update moved. A file version spells the patch release in its third
# part's leading digits: 10.0.1426.52326 is 10.0.14.
$EVAL_SNAPSHOT = 'skills/hunt/evals/models/generator-snapshot.json'
$EVAL_SECTIONS = @('members', 'effects', 'linq', 'accessors', 'packages')
$FILE_VERSION_MOVES = @(@('^10\.0\.14\d\d\.\d+$', '^10\.0\.15\d\d\.\d+$'), @('^8\.0\.33\d\d\.\d+$', '^8\.0\.34\d\d\.\d+$'))
$PACKAGE_VERSION_MOVES = @(@('10.0.14', '10.0.15'), @('8.0.33', '8.0.34'))

# D-0019: the findings the demo admitted run keeps beyond the records; their mechanism is #400.
$DEMO_RESIDUAL = @(
    '0524864340b4353b', '0d8df6303781e5e8', '2379952072974ddb', '2cf8baf23ae18f63', '311c9df2db3d44da', '3a949021a7a660a5',
    '494eb6ff8187f085', '79f689686e9e9b59', '807b9682d3bff99c', '807deb9f1da2bbdf', '84fdefd055027cc4', 'be087dc34329ce4b',
    'beab0b89c04944ad', 'c203ff7acbe7a484', 'e38e848da2220a30', 'ec7a1c9eb38b5705')

$UNTOUCHED_PATHS = @(
    'src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn', 'demo/SCENARIOS.md', 'skills', '.claude-plugin', '.codex-plugin',
    '.cursor-plugin', 'build/check-phase5d-run[ABL]*', 'build/check-phase5d-runH1.ps1', 'build/check-phase5d-runH2*.ps1')

$JOIN_CLASSES = @(
    'Engine.OwnPassJoin', 'Engine.AwaitedRuns', 'Engine.PerPass', 'Engine.HappensBeforeTests', 'Engine.ResultJoinMatrixTests',
    'Engine.BlockingWaitAssumptionMatrixTests', 'Engine.PollyRetryAttemptMatrixTests', 'Engine.CompletionValueHeapTests',
    'ConcurrencyHunter.Core.Tests.DemoGeneratedModelsTests', 'ConcurrencyHunter.Core.Tests.DemoExpectationTests')

$EVAL_CLASSES = @(
    'Generation.ModelEvalTests', 'Generation.CoreLibGenerationEvalTests', 'Generation.EffectsEvalTests', 'Generation.LinqOracleTests',
    'Generation.AccessorEvalTests', 'Generation.NullableHeldValueMatrixTests', 'Engine.NullableUnseenWriteMatrixTests')

$NORMAL_FACTS = 'FullyQualifiedName~Engine.CallSiteProbeTests.EShop|' +
                'FullyQualifiedName~Engine.CallSiteProbeTests.Demo_with_records_withheld_and_nothing_admitted_differs_from_the_records'

$SNAPSHOT_FACTS = 'FullyQualifiedName~MetricsCommandTests.Demo_measurement_matches_the_snapshot|FullyQualifiedName~EShopSnapshotTests'

function Fail([string] $message)
{
    # Write-Host reaches the gate's output even where a caller discards the success stream.
    Write-Host "GATE ($Step): $message"
    exit 1
}

function Filter-Of([string[]] $classes)
{
    return ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
}

# Runs a filter through the H3 gate helper; with an expectation, the class must list exactly that many tests.
function Invoke-Tests([string] $name, [string] $project, [string] $filter, [string] $expect = '')
{
    $arguments = @('-NoProfile', '-File', $helper, '-Name', "h3a-$name", '-Project', $project, '-Filter', $filter)
    if ($expect) { $arguments += @('-Expect', $expect) }
    # The helper's own output goes to the host, so the function returns the log path alone.
    & pwsh @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "$name failed; see scratch/h3/gates/h3a-$name.log" }
    return Join-Path $root "scratch/h3/gates/h3a-$name.log"
}

function Assert-Counted([string] $log, [int] $passed)
{
    $text = Get-Content -LiteralPath $log -Raw
    if ($text -notmatch "Passed:\s+$passed,") { Fail "expected $passed passed tests in $log" }
    if ($text -notmatch 'Skipped:\s+0,') { Fail "skipped tests in $log" }
}

function Assert-NoSkips([string] $log)
{
    if ((Get-Content -LiteralPath $log -Raw) -notmatch 'Skipped:\s+0,') { Fail "skipped tests in $log" }
}

function Require-EShop
{
    if (-not $env:CH_ESHOP_ROOT) { Fail 'CH_ESHOP_ROOT is required.' }
}

function Reset-Directory([string] $path)
{
    $full = [IO.Path]::GetFullPath($path)
    $scratch = [IO.Path]::GetFullPath((Join-Path $root 'scratch')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase)) { Fail "$full is outside scratch" }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Force $full | Out-Null
}

# Runs the normal-run probe facts with their records written to a folder.
function Invoke-NormalFacts([string] $name, [string] $directory, [string] $filter, [int] $passed)
{
    Reset-Directory $directory
    $previous = $env:CH_H3_PROBE_OUT
    $env:CH_H3_PROBE_OUT = $directory
    try { $log = Invoke-Tests $name $core $filter }
    finally { if ($null -eq $previous) { Remove-Item Env:CH_H3_PROBE_OUT -ErrorAction Ignore } else { $env:CH_H3_PROBE_OUT = $previous } }
    Assert-Counted $log $passed
}

# A records file's findings as fingerprint -> sorted "rule|confidence" list.
function Read-Triples([string] $path)
{
    if (-not (Test-Path -LiteralPath $path)) { Fail "missing $path" }
    $triples = @{}
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($path))
    try
    {
        foreach ($finding in $document.RootElement.GetProperty('findings').EnumerateArray())
        {
            $fingerprint = $finding.GetProperty('fingerprint').GetString()
            $value = "$($finding.GetProperty('rule').GetString())|$($finding.GetProperty('confidence').GetString())"
            $triples[$fingerprint] = @(@($triples[$fingerprint]) + $value | Where-Object { $_ } | Sort-Object)
        }
    }
    finally { $document.Dispose() }
    return $triples
}

function Record-Text
{
    if (Test-Path -LiteralPath $runRecord) { return [IO.File]::ReadAllText($runRecord) }
    return ''
}

function Assert-Baseline
{
    foreach ($file in $BASELINE_HASHES.Keys)
    {
        $path = Join-Path $baselineDirectory $file
        if ($BASELINE_HASHES[$file] -eq 'PENDING') { Fail "the oracle $file is not pinned yet" }
        if (-not (Test-Path -LiteralPath $path)) { Fail "missing oracle $path" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $BASELINE_HASHES[$file]) { Fail "oracle $file changed" }
    }
}

# R5: no new fingerprint, the same rule and confidence on every survivor, every vanished fingerprint named in the record.
function Compare-WithBaseline([string] $directory)
{
    Assert-Baseline
    $record = Record-Text
    foreach ($target in 'demo', 'eshop')
    {
        $oracle = Read-Triples (Join-Path $baselineDirectory "$target-records.json")
        $current = Read-Triples (Join-Path $directory "$target-records.json")
        foreach ($fingerprint in @($current.Keys))
        {
            if (-not $oracle.ContainsKey($fingerprint)) { Fail "$target has a new finding $fingerprint ($($current[$fingerprint] -join ', '))" }
            if ((@($oracle[$fingerprint]) -join ';') -cne (@($current[$fingerprint]) -join ';'))
            {
                Fail "$target $fingerprint changed from $($oracle[$fingerprint] -join ', ') to $($current[$fingerprint] -join ', ')"
            }
        }
        foreach ($fingerprint in @($oracle.Keys))
        {
            if (-not $current.ContainsKey($fingerprint) -and -not $record.Contains($fingerprint))
            {
                Fail "$target lost $fingerprint and docs/runs/5d-H3a.md does not name it"
            }
        }
    }
    Write-Host "Normal runs in $directory agree with the oracle."
}

# Every leaf of a JSON element as path -> raw text.
function Add-Leaves([Text.Json.JsonElement] $element, [string] $path, [hashtable] $leaves)
{
    switch ($element.ValueKind)
    {
        'Object' { foreach ($property in $element.EnumerateObject()) { Add-Leaves $property.Value ($path ? "$path.$($property.Name)" : $property.Name) $leaves } }
        'Array' { $index = 0; foreach ($item in $element.EnumerateArray()) { Add-Leaves $item "$path[$index]" $leaves; $index++ } }
        default { $leaves[$path] = $element.GetRawText().Trim('"') }
    }
}

function Snapshot-Leaves([string] $text)
{
    $leaves = @{}
    $document = [Text.Json.JsonDocument]::Parse($text)
    try
    {
        foreach ($section in 'counts', 'coverage')
        {
            $element = [Text.Json.JsonElement]::new()
            if ($document.RootElement.TryGetProperty($section, [ref] $element)) { Add-Leaves $element $section $leaves }
        }
    }
    finally { $document.Dispose() }
    return $leaves
}

function Assert-Snapshots
{
    $recordLines = @((Record-Text) -split "`r?`n")
    foreach ($target in 'demo', 'eshop')
    {
        $relative = "src/ConcurrencyHunter.Cli.Tests/Snapshots/$target.json"
        $headText = (& git -C $root show "HEAD:./$relative") -join "`n"
        if ($LASTEXITCODE -ne 0) { Fail "git show HEAD:$relative failed" }
        $head = Snapshot-Leaves $headText
        $work = Snapshot-Leaves ([IO.File]::ReadAllText((Join-Path $root $relative)))
        $headPrints = @($head.Keys | Where-Object { $_ -like 'counts.fingerprints*' } | ForEach-Object { $head[$_] })
        $workPrints = @($work.Keys | Where-Object { $_ -like 'counts.fingerprints*' } | ForEach-Object { $work[$_] })
        foreach ($fingerprint in $workPrints) { if ($fingerprint -notin $headPrints) { Fail "$target snapshot gained $fingerprint" } }
        foreach ($fingerprint in $headPrints)
        {
            if ($fingerprint -notin $workPrints -and -not ($recordLines -match [regex]::Escape($fingerprint))) { Fail "$target snapshot lost $fingerprint unnamed" }
        }
        $paths = @($head.Keys) + @($work.Keys) | Where-Object { $_ -notlike 'counts.fingerprints*' } | Sort-Object -Unique
        foreach ($path in $paths)
        {
            $old = if ($head.ContainsKey($path)) { $head[$path] } else { '(none)' }
            $new = if ($work.ContainsKey($path)) { $work[$path] } else { '(none)' }
            if ($old -ceq $new) { continue }
            $named = @($recordLines | Where-Object { $_.Contains($path) -and $_.Contains($old) -and $_.Contains($new) })
            if ($named.Count -eq 0) { Fail "$target $path moved from $old to $new; docs/runs/5d-H3a.md names no line with the path and both values" }
        }
    }
}

function Assert-DemoModels
{
    $status = @(& git -C $root status --porcelain --untracked-files=all -- demo/.concurrency-hunter)
    if ($LASTEXITCODE -ne 0) { Fail 'git status failed' }
    foreach ($line in $status)
    {
        if ($line -notmatch '^([ M]{2}) (?:plugins/concurrency-hunter/)?demo/\.concurrency-hunter/models/generated/.+\.json$' -or $Matches[1] -eq '  ')
        {
            Fail "unexpected demo model status: $line"
        }
    }
    $diff = @(& git -C $root diff HEAD --unified=0 -- demo/.concurrency-hunter/models/generated)
    if ($LASTEXITCODE -ne 0) { Fail 'git diff failed' }
    foreach ($line in $diff)
    {
        if ($line -match '^[+-]' -and $line -notmatch '^(---|\+\+\+) ' -and $line -notmatch '^[+-]\s*"generator": "[0-9a-f]+",$')
        {
            Fail "demo models changed beyond generator: $line"
        }
    }
}

function Invoke-Script([string] $script)
{
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot $script)
    if ($LASTEXITCODE -ne 0) { Fail "$script failed" }
}

function Step-Records
{
    Invoke-Script 'check-docs.ps1'
    Invoke-Script 'check-demo-expectations.ps1'
    Invoke-Script 'check-baseline-rerecord.ps1'
    & git -C $root diff --quiet HEAD -- demo/expected-findings.json
    if ($LASTEXITCODE -ne 0) { Fail 'demo/expected-findings.json differs from HEAD' }
    Assert-Snapshots
    Assert-DemoModels
    # R7, D-0022: the run record names the eval snapshot re-record and the runtime update behind it.
    $record = Record-Text
    foreach ($term in 'generator-snapshot.json', '10.0.15', '8.0.34')
    {
        if (-not $record.Contains($term)) { Fail "docs/runs/5d-H3a.md does not name the eval snapshot re-record ($term)" }
    }
    Write-Host 'Records agree.'
}

function Triples-Text([hashtable] $triples)
{
    return (@($triples.Keys | Sort-Object) | ForEach-Object { "$_|$(@($triples[$_]) -join ';')" }) -join "`n"
}

# The admitted run keeps every records finding with its rule and confidence, and beyond them exactly the allowed ones.
function Compare-Admitted([string] $target, [hashtable] $records, [hashtable] $admitted, [string[]] $allowed)
{
    foreach ($fingerprint in @($records.Keys))
    {
        if (-not $admitted.ContainsKey($fingerprint)) { Fail "$target admitted run lost $fingerprint" }
        if ((@($records[$fingerprint]) -join ';') -cne (@($admitted[$fingerprint]) -join ';'))
        {
            Fail "$target admitted run changed $fingerprint from $($records[$fingerprint] -join ', ') to $($admitted[$fingerprint] -join ', ')"
        }
    }
    $extra = @($admitted.Keys | Where-Object { -not $records.ContainsKey($_) } | Sort-Object)
    $expected = @($allowed | Sort-Object)
    if (($extra -join ', ') -cne ($expected -join ', '))
    {
        Fail "$target admitted run has [$($extra -join ', ')] beyond the records; expected exactly [$($expected -join ', ')]"
    }
}

# G4 as D-0019 narrows it: the H3 probe's checks over its runs, with the demo admitted fact (#400) left out.
function Step-Probe
{
    Require-EShop
    Assert-DemoModels
    Invoke-Tests 'probe-models' $core 'FullyQualifiedName~ConcurrencyHunter.Core.Tests.DemoGeneratedModelsTests' | Out-Null
    Invoke-Tests 'probe-admission' $core 'FullyQualifiedName~Engine.CallSiteAdmissionTests' 'CallSiteAdmissionTests=12' | Out-Null
    # The fixture writes all three runs of a target whichever of its facts runs.
    Invoke-NormalFacts 'probe-runs' $probeDirectory $NORMAL_FACTS 3
    $summary = @('| target | run | findings | library bodies | seconds |', '| --- | --- | ---: | ---: | ---: |')
    foreach ($target in 'demo', 'eshop')
    {
        $runs = @{}
        foreach ($name in 'records', 'admitted', 'control')
        {
            $path = Join-Path $probeDirectory "$target-$name.json"
            if (-not (Test-Path -LiteralPath $path)) { Fail "missing $path" }
            $run = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $bodies = @($run.admitted).Count
            if ($name -eq 'admitted') { if ($run.libraryBodies -lt 1 -or $bodies -eq 0) { Fail "$target reached no admitted body" } }
            elseif ($run.libraryBodies -ne 0 -or $bodies -ne 0) { Fail "$target $name admitted a body" }
            $seconds = ([double]$run.seconds).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
            $summary += "| $target | $name | $(@($run.findings).Count) | $($run.libraryBodies) | $seconds |"
            $runs[$name] = Read-Triples $path
        }
        $allowed = if ($target -eq 'demo') { $DEMO_RESIDUAL } else { @() }
        Compare-Admitted $target $runs['records'] $runs['admitted'] $allowed
        if ((Triples-Text $runs['records']) -ceq (Triples-Text $runs['control'])) { Fail "$target control does not differ from the records" }
    }
    [IO.File]::WriteAllLines((Join-Path $probeDirectory 'summary.md'), [string[]] $summary, $utf8)
    Compare-WithBaseline $probeDirectory
}

# One snapshot entry: equal apart from its implementation, which is equal or a framework implementation the runtime
# update moved (the same assembly version, a file or implementation version from an old release to its successor).
function Assert-SnapshotEntry([string] $section, [Text.Json.Nodes.JsonNode] $old, [Text.Json.Nodes.JsonNode] $new)
{
    $key = if ($section -eq 'packages') { 'implementationVersion' } else { 'implementation' }
    $oldRest = $old.DeepClone().AsObject()
    $newRest = $new.DeepClone().AsObject()
    [void]$oldRest.Remove($key)
    [void]$newRest.Remove($key)
    if (-not [Text.Json.Nodes.JsonNode]::DeepEquals($oldRest, $newRest)) { Fail "$EVAL_SNAPSHOT changed a $section answer: $($old.ToJsonString())" }
    if ([Text.Json.Nodes.JsonNode]::DeepEquals($old[$key], $new[$key])) { return }
    if ($section -eq 'packages')
    {
        $from = [string]$old[$key]
        $to = [string]$new[$key]
        $moved = $null -eq $old['package'] -and @($PACKAGE_VERSION_MOVES | Where-Object { $from -eq $_[0] -and $to -eq $_[1] }).Count -eq 1
    }
    else
    {
        $from = [string]$old[$key]['fileVersion']
        $to = [string]$new[$key]['fileVersion']
        $moved = [string]$old[$key]['assemblyVersion'] -ceq [string]$new[$key]['assemblyVersion'] -and
                 @($FILE_VERSION_MOVES | Where-Object { $from -match $_[0] -and $to -match $_[1] }).Count -eq 1
    }
    if (-not $moved) { Fail "$EVAL_SNAPSHOT moved a $section implementation beyond the runtime update: $($old.ToJsonString())" }
}

# G3 for the eval snapshot, from the two texts: every entry passes Assert-SnapshotEntry, everything else is equal.
function Compare-EvalSnapshot([string] $headText, [string] $workText)
{
    $head = [Text.Json.Nodes.JsonNode]::Parse($headText).AsObject()
    $work = [Text.Json.Nodes.JsonNode]::Parse($workText).AsObject()
    if ((@($head | ForEach-Object Key) -join ',') -cne (@($work | ForEach-Object Key) -join ',')) { Fail "$EVAL_SNAPSHOT changed its sections" }
    foreach ($property in $head)
    {
        $name = $property.Key
        if ($name -notin $EVAL_SECTIONS)
        {
            if (-not [Text.Json.Nodes.JsonNode]::DeepEquals($property.Value, $work[$name])) { Fail "$EVAL_SNAPSHOT changed $name" }
            continue
        }
        $old = $property.Value.AsArray()
        $new = $work[$name].AsArray()
        if ($old.Count -ne $new.Count) { Fail "$EVAL_SNAPSHOT changed the number of $name entries" }
        for ($i = 0; $i -lt $old.Count; $i++) { Assert-SnapshotEntry $name $old[$i] $new[$i] }
    }
}

function Assert-EvalSnapshot
{
    $headText = (& git -C $root show "HEAD:./$EVAL_SNAPSHOT") -join "`n"
    if ($LASTEXITCODE -ne 0) { Fail "git show HEAD:$EVAL_SNAPSHOT failed" }
    Compare-EvalSnapshot $headText ([IO.File]::ReadAllText((Join-Path $root $EVAL_SNAPSHOT)))
}

# G3 for the probe tests: equal to their pin, or equal once D-0020's one skip attribute is put back to [Fact].
function Assert-ProbeTests([string] $pin)
{
    $text = [IO.File]::ReadAllText((Join-Path $root $PROBE_TESTS))
    $pattern = [regex]::Escape($SKIPPED_FACT_ATTRIBUTE) + "(?=\r?\n[ ]*public async Task $SKIPPED_FACT_NAME\(\))"
    if ([regex]::Matches($text, [regex]::Escape($SKIPPED_FACT_ATTRIBUTE)).Count -gt 1) { Fail "$PROBE_TESTS holds the skip attribute more than once" }
    $restored = [regex]::Replace($text, $pattern, '[Fact]')
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($restored)))
    if ($hash -ne $pin) { Fail "$PROBE_TESTS changed beyond the skip attribute of $SKIPPED_FACT_NAME" }
}

# The SPEC labels whose paragraphs a diff against HEAD touches.
function Touched-SpecLabels
{
    $head = @((& git -C $root show 'HEAD:./docs/SPEC.md') -split "`r?`n")
    if ($LASTEXITCODE -ne 0) { Fail 'git show HEAD:docs/SPEC.md failed' }
    $labelAt = @{}
    $current = ''
    for ($i = 0; $i -lt $head.Count; $i++)
    {
        if ($head[$i] -match '^\*\*(TD-[0-9]+[a-z]?)\.\*\*') { $current = $Matches[1] }
        $labelAt[$i + 1] = $current
    }
    $labels = [Collections.Generic.HashSet[string]]::new()
    foreach ($line in @(& git -C $root diff HEAD --unified=0 -- docs/SPEC.md))
    {
        if ($line -match '^@@ -(\d+)(?:,(\d+))? ')
        {
            $start = [int]$Matches[1]
            $count = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
            # A pure insertion (count 0) sits after line $start.
            $lines = if ($count -eq 0) { @($start) } else { $start..($start + $count - 1) }
            foreach ($number in $lines) { if ($labelAt.ContainsKey($number)) { [void]$labels.Add($labelAt[$number]) } }
        }
    }
    return $labels
}

New-Item -ItemType Directory -Force $logs | Out-Null
Push-Location $root
try
{
    switch ($Step)
    {
        'baseline'
        {
            Require-EShop
            $filter = 'FullyQualifiedName~Engine.CallSiteProbeTests.Demo_with_records_withheld_and_nothing_admitted_differs_from_the_records|' +
                      'FullyQualifiedName~Engine.CallSiteProbeTests.EShop_with_records_withheld_and_nothing_admitted_differs_from_the_records'
            Invoke-NormalFacts 'baseline' $baselineDirectory $filter 2
            foreach ($file in $BASELINE_HASHES.Keys)
            {
                Write-Output "$file $((Get-FileHash -LiteralPath (Join-Path $baselineDirectory $file) -Algorithm SHA256).Hash)"
            }
        }
        'forwarder'
        {
            Require-EShop
            if (-not (Record-Text).Contains('Обычный прогон')) { Fail 'docs/runs/5d-H3a.md has no section «Обычный прогон»' }
            Invoke-Tests 'forwarder-matrix' $core 'FullyQualifiedName~Engine.TaskForwarderJoinMatrixTests' 'TaskForwarderJoinMatrixTests=641' | Out-Null
            Invoke-Tests 'forwarder-admission' $core 'FullyQualifiedName~Engine.CallSiteAdmissionTests' 'CallSiteAdmissionTests=12' | Out-Null
            Invoke-Tests 'forwarder-joins' $core (Filter-Of $JOIN_CLASSES) | Out-Null
            Invoke-NormalFacts 'forwarder-normal' $normalDirectory $NORMAL_FACTS 3
            Compare-WithBaseline $normalDirectory
            Assert-NoSkips (Invoke-Tests 'forwarder-snapshots' $cli $SNAPSHOT_FACTS)
            Step-Records
        }
        'context'
        {
            Require-EShop
            Invoke-Tests 'context-matrix' $core 'FullyQualifiedName~Engine.CallSiteContextMatrixTests' 'CallSiteContextMatrixTests=105' | Out-Null
            Invoke-Tests 'context-admission' $core 'FullyQualifiedName~Engine.CallSiteAdmissionTests' 'CallSiteAdmissionTests=12' | Out-Null
            Invoke-Tests 'context-classes' $core (Filter-Of @('Engine.PointsToTests', 'Engine.SpawnHeapTests', 'FindingIdentityTests',
                                                              'Engine.OwnPassJoinMatrixTests', 'Engine.TaskForwarderJoinMatrixTests')) | Out-Null
            Assert-NoSkips (Invoke-Tests 'context-snapshots' $cli $SNAPSHOT_FACTS)
            Step-Records
            Step-Probe
        }
        'probe' { Step-Probe }
        'docs'
        {
            Step-Records
            $record = Record-Text
            if (-not $record.Contains('Обычный прогон')) { Fail 'docs/runs/5d-H3a.md has no section «Обычный прогон»' }
            if (-not $record.Contains('#400')) { Fail 'docs/runs/5d-H3a.md does not name #400' }
            foreach ($fingerprint in $DEMO_RESIDUAL) { if (-not $record.Contains($fingerprint)) { Fail "docs/runs/5d-H3a.md does not name $fingerprint" } }
            $text = [IO.File]::ReadAllText((Join-Path $root $PROBE_TESTS))
            if (-not $text.Contains($SKIPPED_FACT_ATTRIBUTE)) { Fail "$PROBE_TESTS has no skip attribute on $SKIPPED_FACT_NAME" }
            $summary = Join-Path $probeDirectory 'summary.md'
            if (-not (Test-Path -LiteralPath $summary)) { Fail 'missing scratch/h3a/probe/summary.md' }
            $rows = @(Get-Content -LiteralPath $summary | Select-Object -Skip 2 | Where-Object { $_ -match '^\|' })
            if ($rows.Count -ne 6) { Fail "summary.md has $($rows.Count) data rows, not 6" }
            foreach ($row in $rows)
            {
                $cells = @($row.Trim('|').Split('|') | ForEach-Object { $_.Trim() })
                $pattern = '\|\s*' + (($cells[0..3] | ForEach-Object { [regex]::Escape($_) }) -join '\s*\|\s*') + '\s*\|'
                if ($record -notmatch $pattern) { Fail "docs/runs/5d-H3a.md has no probe row for $($cells[0..3] -join ' / ')" }
            }
            if (-not ([IO.File]::ReadAllText((Join-Path $root 'docs/PLAN.md'))).Contains('5d run H3a')) { Fail 'docs/PLAN.md has no 5d run H3a row' }
            # A returned set unrolls into strings; -notin compares whole labels, never substrings.
            $labels = @(Touched-SpecLabels)
            foreach ($label in 'TD-034a', 'TD-060', 'TD-067') { if ($label -notin $labels) { Fail "the SPEC diff does not touch $label" } }
            $baselineText = [IO.File]::ReadAllText((Join-Path $root 'build/test-baseline.txt'))
            foreach ($class in 'TaskForwarderJoinMatrixTests', 'CallSiteContextMatrixTests', 'CallSiteAdmissionTests', 'CallSiteProbeTests')
            {
                if (-not $baselineText.Contains($class)) { Fail "build/test-baseline.txt has no $class" }
            }
        }
        'records' { Step-Records }
        'invariants'
        {
            $changed = @(& git -C $root status --porcelain -- @UNTOUCHED_PATHS)
            if ($LASTEXITCODE -ne 0) { Fail 'git status failed' }
            $snapshotLine = '^ M (?:plugins/concurrency-hunter/)?' + [regex]::Escape($EVAL_SNAPSHOT) + '$'
            $changed = @($changed | Where-Object { $_ -notmatch $snapshotLine })
            if ($changed.Count -ne 0) { Fail "changed: $($changed -join '; ')" }
            Assert-EvalSnapshot
            foreach ($file in $PINNED_FILES.Keys)
            {
                if ($file -eq $PROBE_TESTS) { Assert-ProbeTests $PINNED_FILES[$file]; continue }
                if ((Get-FileHash -LiteralPath (Join-Path $root $file) -Algorithm SHA256).Hash -ne $PINNED_FILES[$file]) { Fail "$file changed" }
            }
            Write-Host 'Invariants hold.'
        }
        'suite'
        {
            Require-EShop
            $publish = Join-Path $logs 'suite-publish.log'
            & dotnet publish src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj -c Release -r win-x64 --self-contained false -o bin/win-x64 `
                --nologo --disable-build-servers -nodeReuse:false *> $publish
            if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $publish -Tail 60; Fail 'publish failed' }
            $baseline = Join-Path $logs 'suite-baseline.log'
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-test-baseline.ps1') *> $baseline
            if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $baseline -Tail 60; Fail 'check-test-baseline failed' }
        }
        'evals'
        {
            $log = Join-Path $logs 'evals.log'
            $env:CH_MODEL_EVALS = '1'
            Remove-Item Env:CH_MODEL_EVALS_RECORD -ErrorAction Ignore
            try { & dotnet test $core -c Release --nologo --filter (Filter-Of $EVAL_CLASSES) *> $log }
            finally { Remove-Item Env:CH_MODEL_EVALS -ErrorAction Ignore }
            if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 60; Fail 'model evals failed' }
            Assert-NoSkips $log
        }
        'fix'
        {
            Invoke-Tests 'fix-forwarder-matrix' $core 'FullyQualifiedName~Engine.TaskForwarderJoinMatrixTests' 'TaskForwarderJoinMatrixTests=641' | Out-Null
            Invoke-Tests 'fix-context-matrix' $core 'FullyQualifiedName~Engine.CallSiteContextMatrixTests' 'CallSiteContextMatrixTests=105' | Out-Null
            Invoke-Tests 'fix-admission' $core 'FullyQualifiedName~Engine.CallSiteAdmissionTests' 'CallSiteAdmissionTests=12' | Out-Null
            Invoke-Tests 'fix-classes' $core (Filter-Of @('Engine.OwnPassJoinTests', 'Engine.AwaitedRunsTests', 'Engine.PerPassClimbTests',
                                                          'Engine.PointsToTests', 'ConcurrencyHunter.Core.Tests.DemoGeneratedModelsTests')) | Out-Null
            Assert-DemoModels
        }
    }
}
finally
{
    Pop-Location
}
exit 0
