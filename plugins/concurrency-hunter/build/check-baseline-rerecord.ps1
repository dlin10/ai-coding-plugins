<#
.SYNOPSIS
Guards the re-recording of build/test-baseline.txt: a phase may lose a recorded case only where it renamed it or ran its
theory over other data, never where it stopped running it.

.DESCRIPTION
The baseline is rewritten once per phase, and a rewrite hides a deleted test as easily as it records a renamed one: both
read as a line that is no longer there. So the rewrite is compared with the committed baseline, read with
`git show HEAD:plugins/concurrency-hunter/build/test-baseline.txt`, over the set of cases each records as Passed.

Two things must hold. Every case the rewrite lost must be paired with a case the rewrite gained that is recognisably the
same test: another row of the same theory, or the same test renamed - the same class, and a method name that carries the
lost one's own name from the start, in whole words. Any new case in the class is not that: it lets a deleted test hide
behind an unrelated arrival, which is what this exists to catch. Where two gained cases carry the name equally well,
nothing says which one is the rename, and the pairing is refused rather than decided alphabetically. And none of the lost
cases may carry phase_5d_expectation in its name: the demo matcher of phase 5d, renamed from its phase 5c name in this run.
The marker is longer than the phase alone because -match ignores case, and phase_5d_ would also catch other tests that name
the phase and are free to be renamed.

A theory case is its test's name followed by one row's arguments - EffectReaderTests.Touching_a_collection_a_witness_
returned_gives_vocabulary(type: "List<object>", body: "_ = user.Make()[0];") - and an argument may hold dots of its own.
So class and method are cut at the last '.' before the first '(', where no argument has begun; a method's name cannot hold
a '('. Cut at the last '.' of the whole name, that case split inside user.Make: its class read as the method and half a
row, and no case of the real class could ever stand in for it. A row whose data changed is the other loss that is not a
deleted test: the theory still runs, over other data, and the row it ran before reads as lost. So a lost case is first
paired with a gained case of the same class and method - the name before the arguments - one row for one row. A theory
that lost two rows and gained one still reports the second, because a row deleted without a new one is a test that stopped
running. Which new row stands for which lost one is left to the order of the names, and that is not the tie refused above:
the rows of one theory are one test, so any assignment pairs as many rows and the verdict cannot turn on it, while a tie
between renames is a choice between different tests. These pairs are made before any rename is looked for, so that a
rename cannot take a theory's new row from a lost row of that theory and leave it reading as deleted. A rename is read on
the method's name alone, never into the arguments, and so a renamed theory of several rows is refused as a tie: every row
of the new name carries the old one equally well.

The plan wrote the first rule as "exactly one case may be lost", counting only that matcher. Four further renames had
already landed by then, each with a replacement asserting the new truth: Lock_on_a_per_request_object_is_a_different_
identity_DCA1001 became _DCA1003 when the protection rule arrived, Monitor_try_enter_stays_a_call and System_threading_
lock_stays_calls became _acquires_on_its_success_flag and _lowers_to_its_own_primitive when those two stopped being
ordinary calls, and Demo_measurement_has_every_field_and_eleven_steps became _twelve_steps when the solver became a step
of the pipeline. Pairing each loss with its replacement keeps the check that matters - a deleted test cannot hide - and
lets an honest rename through.
#>

$ErrorActionPreference = 'Stop'

$COMMITTED = 'HEAD:plugins/concurrency-hunter/build/test-baseline.txt'
$KEPT_MATCHER = 'phase_5d_expectation'

# How much of its name a replacement has to carry for the pairing to say "renamed" rather than "something else arrived". The
# five renames of the phase carry 18 characters and more; a pair of unrelated tests in one class carries a word or none.
$MIN_STEM = 12

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workingPath = Join-Path $root 'build/test-baseline.txt'

# The names a baseline records as Passed; an outcome of any other kind is not a case this compares.
function Get-PassedNames($Lines) {
    $names = [Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        $parts = $line -split "`t"
        if ($parts.Count -ge 2 -and $parts[1].Trim() -eq 'Passed') { $names.Add($parts[0].Trim()) }
    }

    return , $names
}

# The test a case runs: its class and method, without the arguments a theory case carries after them. Every row of one theory
# runs the same test.
function Get-TestName([string]$Case) {
    $arguments = $Case.IndexOf('(')
    return $(if ($arguments -lt 0) { $Case } else { $Case.Substring(0, $arguments) })
}

# The test class a case belongs to: everything before the last segment of its test's name. The last '.' of the case's own
# name may sit inside an argument: body: "_ = user.Make()[0];".
function Get-ClassName([string]$Case) {
    $test = Get-TestName $Case
    $method = $test.LastIndexOf('.')
    return $(if ($method -lt 0) { $test } else { $test.Substring(0, $method) })
}

# The method a case names: the last segment of its test's name, without arguments.
function Get-MethodName([string]$Case) {
    $test = Get-TestName $Case
    $method = $test.LastIndexOf('.')
    return $(if ($method -lt 0) { $test } else { $test.Substring($method + 1) })
}

# What two method names have in common from the start, in whole words: a rename keeps the name it renames and changes its end,
# so `Monitor_try_enter_stays_a_call` and `Monitor_try_enter_acquires_on_its_success_flag` share `Monitor_try_enter_`, while two
# tests that merely sit in one class share nothing worth the name. The stem is cut back to the last word boundary so that a
# coincidence inside a word cannot pass for one.
function Get-SharedStem([string]$Lost, [string]$Gained) {
    $first = Get-MethodName $Lost
    $second = Get-MethodName $Gained
    $shared = 0
    while ($shared -lt $first.Length -and $shared -lt $second.Length -and $first[$shared] -eq $second[$shared]) { $shared++ }
    $stem = $first.Substring(0, $shared)
    $boundary = $stem.LastIndexOf('_')
    return $(if ($boundary -lt 0) { '' } else { $stem.Substring(0, $boundary + 1) })
}

if (-not (Test-Path $workingPath)) {
    Write-Output "build/test-baseline.txt is missing; record it with build/check-test-baseline.ps1 -Record."
    exit 1
}

# Both sides are read as UTF-8 whichever shell runs this. Windows PowerShell 5.1 decodes a file in the system's ANSI code page
# and a process's output in the OEM one, while pwsh 7 uses UTF-8 for both, and the baseline carries non-ASCII characters in the
# display names of theory cases: read by the shell's default, the two sides of the comparison stop being the same names and
# every such case reads as lost. The encoding is stated here rather than inherited.
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    $committedLines = & git -C $root show $COMMITTED
    $gitExit = $LASTEXITCODE
}
finally {
    [Console]::OutputEncoding = $previousEncoding
}

if ($gitExit -ne 0) {
    Write-Output "git show $COMMITTED failed with exit code $gitExit."
    exit 1
}

$committed = Get-PassedNames $committedLines
$working = Get-PassedNames (Get-Content $workingPath -Encoding UTF8)
# Compared by the exact name, as the baseline records it: theory rows whose arguments differ only in case are two cases.
$lost = @($committed | Where-Object { $working -cnotcontains $_ } | Sort-Object)
$gained = @($working | Where-Object { $committed -cnotcontains $_ } | Sort-Object)

$problems = [Collections.Generic.List[string]]::new()
$pairings = [Collections.Generic.List[string]]::new()
$unclaimed = [Collections.Generic.List[string]]::new([string[]]$gained)
$unpaired = [Collections.Generic.List[string]]::new()
foreach ($case in $lost) {
    $test = Get-TestName $case
    $row = @($unclaimed | Where-Object { (Get-TestName $_) -ceq $test }) | Select-Object -First 1
    if ($null -eq $row) {
        $unpaired.Add($case)
        continue
    }

    [void]$unclaimed.Remove($row)
    $pairings.Add("$case -> $row (the same test over other data)")
}

foreach ($case in $unpaired) {
    $class = Get-ClassName $case
    $candidates = @($unclaimed | Where-Object { (Get-ClassName $_) -eq $class } |
        ForEach-Object { [PSCustomObject]@{ Case = $_; Stem = Get-SharedStem $case $_ } } |
        Where-Object { $_.Stem.Length -ge $MIN_STEM } |
        Sort-Object -Property @{ Expression = { $_.Stem.Length }; Descending = $true }, Case)

    if ($candidates.Count -eq 0) {
        $problems.Add("$case is gone and no case $class gained is left to carry its name: a recorded test stopped running")
        continue
    }

    if ($candidates.Count -gt 1 -and $candidates[0].Stem.Length -eq $candidates[1].Stem.Length) {
        $names = ($candidates | Where-Object { $_.Stem.Length -eq $candidates[0].Stem.Length } | ForEach-Object { $_.Case.Substring($class.Length + 1) }) -join ', '
        $problems.Add("$case is gone and $names carry its name equally well: nothing says which one renamed it")
        continue
    }

    $replacement = $candidates[0]
    [void]$unclaimed.Remove($replacement.Case)
    $pairings.Add("$case -> $($replacement.Case) (on '$($replacement.Stem)')")
}

$matcher = @($lost | Where-Object { $_ -match $KEPT_MATCHER })
if ($matcher.Count -ne 0) {
    $problems.Add("the demo matcher carrying $KEPT_MATCHER is not renamed in this run; lost: $($matcher -join ', ')")
}

if ($problems.Count -gt 0) {
    Write-Output "The re-recorded baseline does not hold:"
    foreach ($problem in $problems) { Write-Output "  $problem" }
    exit 1
}

Write-Output "$($working.Count) cases recorded as Passed, $($committed.Count) before; $($lost.Count) lost, each with its replacement:"
foreach ($pairing in $pairings) { Write-Output "  $pairing" }
exit 0
