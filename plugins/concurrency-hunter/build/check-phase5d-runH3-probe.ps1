$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $env:CH_ESHOP_ROOT) { Write-Error 'CH_ESHOP_ROOT is required.'; exit 1 }

Push-Location $root
$previous = $env:CH_H3_PROBE_OUT
try {
    $status = @(git status --porcelain --untracked-files=all -- demo/.concurrency-hunter)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    foreach ($line in $status) {
        if ($line -notmatch '^([ M]{2}) (?:plugins/concurrency-hunter/)?demo/\.concurrency-hunter/models/generated/.+\.json$' -or $Matches[1] -eq '  ') {
            throw "Unexpected demo model status: $line"
        }
    }
    $diff = @(git diff HEAD --unified=0 -- demo/.concurrency-hunter/models/generated)
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed.' }
    foreach ($line in $diff) {
        if ($line -match '^[+-]' -and $line -notmatch '^(---|\+\+\+) ' -and $line -notmatch '^[+-]\s*"generator": "[0-9a-f]+",$') {
            throw "Demo models changed beyond generator: $line"
        }
    }
    $probe = [IO.Path]::GetFullPath((Join-Path $root 'scratch/h3/probe'))
    $scratch = [IO.Path]::GetFullPath((Join-Path $root 'scratch')) + [IO.Path]::DirectorySeparatorChar
    if (-not $probe.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase)) { throw 'Probe path escaped scratch.' }
    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Recurse -Force }
    New-Item -ItemType Directory -Force $probe | Out-Null
    $env:CH_H3_PROBE_OUT = $probe
    & pwsh -NoProfile -File build/check-phase5d-runH3-gate.ps1 -Name models -Project src/ConcurrencyHunter.Core.Tests -Filter 'FullyQualifiedName~ConcurrencyHunter.Core.Tests.DemoGeneratedModelsTests'
    if ($LASTEXITCODE -ne 0) { exit 1 }
    & pwsh -NoProfile -File build/check-phase5d-runH3-gate.ps1 -Name admission -Project src/ConcurrencyHunter.Core.Tests -Filter 'FullyQualifiedName~Engine.CallSiteAdmissionTests' -Expect 'CallSiteAdmissionTests=12'
    if ($LASTEXITCODE -ne 0) { exit 1 }
    & pwsh -NoProfile -File build/check-phase5d-runH3-gate.ps1 -Name probe -Project src/ConcurrencyHunter.Core.Tests -Filter 'FullyQualifiedName~Engine.CallSiteProbeTests' -Expect 'CallSiteProbeTests=4'
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $log = Get-Content -LiteralPath scratch/h3/gates/probe.log -Raw
    if ($log -notmatch 'Passed:\s+4,' -or $log -notmatch 'Skipped:\s+0,') { throw 'Probe must pass all four facts without skips.' }
    function Triples($Run) { @($Run.findings | ForEach-Object { "$($_.fingerprint)|$($_.rule)|$($_.confidence)" } | Sort-Object) }
    function Fingerprints($Run) { @($Run.findings | ForEach-Object { $_.fingerprint } | Sort-Object -Unique) }
    function Equal($Left, $Right) { (@($Left) -join "`n") -ceq (@($Right) -join "`n") }
    $summary = @('| target | run | findings | library bodies | seconds |', '| --- | --- | ---: | ---: | ---: |')
    foreach ($target in @('demo', 'eshop')) {
        $runs = @{}
        foreach ($name in @('records', 'admitted', 'control')) {
            $file = Join-Path $probe "$target-$name.json"
            if (-not (Test-Path -LiteralPath $file)) { throw "Missing $file" }
            $run = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
            $runs[$name] = $run
            if ($name -eq 'admitted') {
                if ($run.libraryBodies -lt 1 -or @($run.admitted).Count -eq 0) { throw "$target reached no admitted body." }
            }
            elseif ($run.libraryBodies -ne 0 -or @($run.admitted).Count -ne 0) { throw "$target $name admitted a body." }
            $seconds = ([double]$run.seconds).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
            $summary += "| $target | $name | $(@($run.findings).Count) | $($run.libraryBodies) | $seconds |"
        }
        $snapshot = Get-Content -LiteralPath "src/ConcurrencyHunter.Cli.Tests/Snapshots/$target.json" -Raw | ConvertFrom-Json
        if (-not (Equal (Fingerprints $runs.records) (@($snapshot.counts.fingerprints) | Sort-Object -Unique))) { throw "$target records differ from snapshot." }
        if (-not (Equal (Triples $runs.records) (Triples $runs.admitted))) { throw "$target admitted findings differ from records." }
        if (Equal (Triples $runs.records) (Triples $runs.control)) { throw "$target control does not differ from records." }
    }
    [IO.File]::WriteAllLines((Join-Path $probe 'summary.md'), $summary, [Text.UTF8Encoding]::new($false))
}
finally {
    if ($null -eq $previous) { Remove-Item Env:CH_H3_PROBE_OUT -ErrorAction Ignore } else { $env:CH_H3_PROBE_OUT = $previous }
    Pop-Location
}
exit 0
