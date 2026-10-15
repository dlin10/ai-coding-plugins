# Exercises the real wrappers with a substitute Common script; no build or installed cache is touched.
[CmdletBinding()]
param([string]$BuildRoot = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$previousBundleRoot = [Environment]::GetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'Process')
$fixture = [IO.Directory]::CreateTempSubdirectory('concurrency-hunter-bundle-tests-')
$plugin = Join-Path $fixture.FullName 'plugins/concurrency-hunter'
$build = Join-Path $plugin 'build'
$common = Join-Path $fixture.FullName 'plugins/Common/build'
$seenRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

try {
    New-Item -ItemType Directory -Path $build, $common | Out-Null
    $callerCache = Join-Path $fixture.FullName 'caller cache'
    New-Item -ItemType Directory -Path $callerCache | Out-Null
    $sentinel = Join-Path $callerCache 'must-survive.txt'
    [IO.File]::WriteAllText($sentinel, 'caller-owned')
    $substitute = @'
param($PluginRoot, $Solution, $Project, $ExecutableName, $PluginName, $RequireEndToEndVariable,
      $Configuration, $KeepResults, [switch]$Record)
$settings = Get-Content -LiteralPath (Join-Path $PluginRoot 'case.json') -Raw | ConvertFrom-Json
$root = [Environment]::GetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'Process')
if ([string]::IsNullOrWhiteSpace($root) -or $root -eq $settings.Previous) {
    throw 'The wrapper did not isolate bundle extraction.'
}
$childRoot = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -Command '[Environment]::GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", "Process")'
if ($LASTEXITCODE -ne 0 -or $childRoot -cne $root) { throw 'The child did not inherit the extraction root.' }
$bundle = Join-Path $root 'concurrency-hunter/test-bundle'
New-Item -ItemType Directory -Path $bundle | Out-Null
[IO.File]::WriteAllText((Join-Path $bundle 'native.dll'), 'extracted file')
[pscustomobject]@{ Root = $root; Configuration = $Configuration; Solution = $Solution;
    KeepResults = $KeepResults; Record = [bool]$Record } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PluginRoot 'observation.json')
if ($settings.Outcome -eq 'throw') { throw 'Simulated build failure.' }
if ($settings.Outcome -eq 'exit') { exit 23 }
exit 0
'@
    $passed = 0
    foreach ($script in @('package.ps1', 'check-test-baseline.ps1')) {
        Copy-Item -LiteralPath (Join-Path $BuildRoot $script) -Destination (Join-Path $build $script)
        [IO.File]::WriteAllText((Join-Path $common $script), $substitute)
        $outcomes = if ($script -eq 'package.ps1') { @('success', 'throw') } else { @('success', 'throw', 'exit') }
        foreach ($mode in @('unset', 'empty', 'custom')) {
            $callerRoot = switch ($mode) { 'unset' { $null }; 'empty' { '' }; 'custom' { $callerCache } }
            foreach ($outcome in $outcomes) {
                $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $callerRoot
                [pscustomobject]@{ Previous = $callerRoot; Outcome = $outcome } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $plugin 'case.json')
                $observation = Join-Path $plugin 'observation.json'
                if (Test-Path -LiteralPath $observation) { Remove-Item -LiteralPath $observation }
                $arguments = @{ Configuration = 'Debug' }
                if ($script -eq 'check-test-baseline.ps1') {
                    $arguments.KeepResults = 'results with spaces'
                    $arguments.Record = $true
                }
                $failure = $null
                try { & (Join-Path $build $script) @arguments }
                catch { $failure = $_.Exception.Message }
                $code = $LASTEXITCODE
                if (-not (Test-Path -LiteralPath $observation)) { throw "$script/$mode/$outcome did not run in isolation: $failure" }
                $observed = Get-Content -LiteralPath $observation -Raw | ConvertFrom-Json
                if (Test-Path -LiteralPath $observed.Root) { throw "$script/$mode/$outcome left extracted files behind." }
                if (-not $seenRoots.Add($observed.Root)) { throw 'Two runs shared an extraction directory.' }
                if (-not [object]::Equals($callerRoot, [Environment]::GetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'Process'))) {
                    throw "$script/$mode/$outcome did not restore the caller environment."
                }
                if ((Get-Content -LiteralPath $sentinel -Raw) -cne 'caller-owned') { throw 'The caller cache was modified.' }
                if ($observed.Configuration -cne 'Debug' -or $observed.Solution -cne 'src/ConcurrencyHunter.slnx') {
                    throw 'Wrapper arguments were not forwarded.'
                }
                if ($script -eq 'check-test-baseline.ps1' -and
                    ($observed.KeepResults -cne 'results with spaces' -or -not $observed.Record)) {
                    throw 'Baseline arguments were not forwarded.'
                }
                if ($outcome -eq 'throw') {
                    if ($failure -cne 'Simulated build failure.') { throw "The original error was lost: $failure" }
                }
                elseif ($failure -or $code -ne $(if ($outcome -eq 'exit') { 23 } else { 0 })) {
                    throw "$script/$mode/$outcome changed the exit status: $code / $failure"
                }
                $passed++
                Write-Host "PASS $script / $mode / $outcome"
            }
        }
    }
    Write-Host "$passed bundle extraction checks passed."
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleRoot
    # Only remove the absolute, freshly created fixture directory, never the caller's extraction root.
    Remove-Item -LiteralPath $fixture.FullName -Recurse -Force
}
exit 0
