<#
.SYNOPSIS
Guards the re-recording of build/test-baseline.txt: a phase may lose a recorded case only where it renamed it, never
where it stopped running it.

.DESCRIPTION
The baseline is rewritten once per phase, and a rewrite hides a deleted test as easily as it records a renamed one: both
read as a line that is no longer there. So the rewrite is compared with the committed baseline, read with
`git show HEAD:plugins/concurrency-hunter/build/test-baseline.txt`, over the set of cases each records as Passed.

Two things must hold. Every case the rewrite lost must be paired with a case the rewrite gained that is recognisably the
same test renamed - the same class, and a method name that carries the lost one's own name from the start, in whole
words. Any new case in the class is not that: it lets a deleted test hide behind an unrelated arrival, which is what this
exists to catch. Where two gained cases carry the name equally well, nothing says which one is the rename, and the
pairing is refused rather than decided alphabetically. And exactly one of the lost cases must carry phase_4b_ in its name:
the demo matcher, which this phase renames when it switches from the phase 4b expectations to the phase 5a ones.

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
$RENAMED_MATCHER = 'phase_4b_'

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

# The test class a case belongs to: everything before the last segment of its name.
function Get-ClassName([string]$Case) {
    $method = $Case.LastIndexOf('.')
    return $(if ($method -lt 0) { $Case } else { $Case.Substring(0, $method) })
}

# The method a case names: the last segment of it.
function Get-MethodName([string]$Case) {
    $method = $Case.LastIndexOf('.')
    return $(if ($method -lt 0) { $Case } else { $Case.Substring($method + 1) })
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
$lost = @($committed | Where-Object { $working -notcontains $_ } | Sort-Object)
$gained = @($working | Where-Object { $committed -notcontains $_ } | Sort-Object)

$problems = [Collections.Generic.List[string]]::new()
$pairings = [Collections.Generic.List[string]]::new()
$unclaimed = [Collections.Generic.List[string]]::new([string[]]$gained)
foreach ($case in $lost) {
    $class = Get-ClassName $case
    $candidates = @($unclaimed | Where-Object { (Get-ClassName $_) -eq $class } |
        ForEach-Object { [PSCustomObject]@{ Case = $_; Stem = Get-SharedStem $case $_ } } |
        Where-Object { $_.Stem.Length -ge $MIN_STEM } |
        Sort-Object -Property @{ Expression = { $_.Stem.Length }; Descending = $true }, Case)

    if ($candidates.Count -eq 0) {
        $problems.Add("$case is gone and $class gained no case that carries its name: a recorded test stopped running")
        continue
    }

    if ($candidates.Count -gt 1 -and $candidates[0].Stem.Length -eq $candidates[1].Stem.Length) {
        $names = ($candidates | Where-Object { $_.Stem.Length -eq $candidates[0].Stem.Length } | ForEach-Object { Get-MethodName $_.Case }) -join ', '
        $problems.Add("$case is gone and $names carry its name equally well: nothing says which one renamed it")
        continue
    }

    $replacement = $candidates[0]
    [void]$unclaimed.Remove($replacement.Case)
    $pairings.Add("$case -> $($replacement.Case) (on '$($replacement.Stem)')")
}

$matcher = @($lost | Where-Object { $_ -match $RENAMED_MATCHER })
if ($matcher.Count -ne 1) {
    $problems.Add("exactly one lost case must be the renamed demo matcher carrying $RENAMED_MATCHER; $($matcher.Count) were lost")
}

if ($problems.Count -gt 0) {
    Write-Output "The re-recorded baseline does not hold:"
    foreach ($problem in $problems) { Write-Output "  $problem" }
    exit 1
}

Write-Output "$($working.Count) cases recorded as Passed, $($committed.Count) before; $($lost.Count) renamed, each with its replacement:"
foreach ($pairing in $pairings) { Write-Output "  $pairing" }
exit 0
