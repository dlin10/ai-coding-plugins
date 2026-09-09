# A suite that has quietly lost or skipped a test still reports success. This is the gate that notices:
# it records identity and outcome for every test *case* — the trx carries a parameterised row
# individually, which --list-tests does not — and fails when a case the baseline saw pass no longer does.
#
# Renaming a test, or changing what one asserts, stays legitimate. Regenerating the baseline with -Record
# is how that becomes a deliberate edit rather than an accident, and the failure below names every case
# that went missing so the edit can be checked.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Record
)

$ErrorActionPreference = 'Continue'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $pluginRoot 'src/CacheDetective.slnx'
$baselinePath = Join-Path $PSScriptRoot 'test-baseline.txt'
$logFileName = 'baseline.trx'

# Twenty-one cases skip for want of these two, and a suite that skipped them is not the suite the
# baseline recorded. Refusing here beats passing on a run that checked a fraction of what it claims.
$required = @('CD_ESHOP_ROOT', 'CD_TEST_SQL_CONN')
$absent = @($required | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
if ($absent.Count -ne 0) {
    Write-Output "Cannot check the baseline: $($absent -join ', ') is not set. The suite would skip the gated cases and report success."
    exit 1
}

# Each test project writes its own TestResults/baseline.trx. Clearing them first keeps a trx from an
# earlier run out of a run whose project failed to build.
$stale = @(Get-ChildItem -Path (Join-Path $pluginRoot 'src') -Recurse -File -Filter $logFileName -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'TestResults' })
foreach ($file in $stale) { Remove-Item -LiteralPath $file.FullName -Force }

dotnet test $solution -c $Configuration --nologo --logger "trx;LogFileName=$logFileName"
$testExitCode = $LASTEXITCODE

$results = @(Get-ChildItem -Path (Join-Path $pluginRoot 'src') -Recurse -File -Filter $logFileName -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'TestResults' })
if ($results.Count -eq 0) {
    Write-Output "The run produced no $logFileName; dotnet test exited $testExitCode."
    exit 1
}

$current = @{}
foreach ($file in $results) {
    $document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
    foreach ($result in $document.TestRun.Results.UnitTestResult) {
        # NotExecuted is what a skip looks like in a trx; anything that is neither a pass nor a skip —
        # Timeout, Aborted, Error — is a failure by any reading that matters here.
        $outcome = switch ($result.outcome) {
            'Passed' { 'Passed' }
            'NotExecuted' { 'Skipped' }
            default { 'Failed' }
        }
        $current[$result.testName] = $outcome
    }
}

# Everything the run recorded is now in $current, and this script names every case it has anything to
# say about. Leaving the trx behind would leave an untracked TestResults directory in the repository
# after every gate.
foreach ($file in $results) {
    Remove-Item -LiteralPath $file.FullName -Force
    if (@(Get-ChildItem -LiteralPath $file.DirectoryName -Force).Count -eq 0) {
        Remove-Item -LiteralPath $file.DirectoryName -Force
    }
}

$lines = @($current.Keys | Sort-Object -CaseSensitive | ForEach-Object { "$_`t$($current[$_])" })

if ($Record) {
    # A baseline recorded from a red run enshrines the failure as the expected outcome, and every later
    # gate then compares against it and passes.
    if ($testExitCode -ne 0) {
        Write-Output "Refusing to record a baseline from a run that failed: dotnet test exited $testExitCode. Fix the run, then record."
        exit 1
    }

    Set-Content -LiteralPath $baselinePath -Value $lines
    $passed = @($current.Values | Where-Object { $_ -eq 'Passed' }).Count
    Write-Output "Recorded $($lines.Count) cases ($passed passed) in $baselinePath."
    exit 0
}

if (-not (Test-Path -LiteralPath $baselinePath)) {
    Write-Output "No baseline at $baselinePath. Record one with build/check-test-baseline.ps1 -Record."
    exit 1
}

$lost = New-Object System.Collections.Generic.List[string]
foreach ($line in Get-Content -LiteralPath $baselinePath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "`t", 2
    if ($parts.Count -ne 2) {
        Write-Output "Malformed baseline line: $line"
        exit 1
    }
    # A case the baseline recorded as Skipped or Failed is allowed to improve; the reverse is not.
    if ($parts[1] -ne 'Passed') { continue }
    if (-not $current.ContainsKey($parts[0])) {
        $lost.Add("missing: $($parts[0])")
    }
    elseif ($current[$parts[0]] -ne 'Passed') {
        $lost.Add("$($current[$parts[0]].ToLowerInvariant()): $($parts[0])")
    }
}

if ($lost.Count -ne 0) {
    foreach ($case in $lost) { Write-Output $case }
    Write-Output "$($lost.Count) case(s) the baseline recorded as Passed no longer pass. Rerecord the baseline only when the change was deliberate."
    exit 1
}

$passing = @($current.Values | Where-Object { $_ -eq 'Passed' }).Count
$failing = @($current.Values | Where-Object { $_ -eq 'Failed' }).Count
# The verdict the baseline gives is its own: this gate answers "did anything that used to pass stop
# passing", and a case the baseline never saw pass is a different question. But a red run is a red run
# whatever that answer is — a newly added test that fails satisfies every baseline case and must still
# take the gate down, or this script would launder a failing suite into a passing gate.
Write-Output "Every case the baseline recorded as Passed still passes ($passing passing, $failing failing of $($current.Count) cases run; dotnet test exited $testExitCode)."
if ($testExitCode -ne 0) {
    Write-Output "The run itself failed: dotnet test exited $testExitCode. No case the baseline recorded regressed, so the failure is in a case it never saw pass."
    exit 1
}

exit 0
