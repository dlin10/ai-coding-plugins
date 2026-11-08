<#
.SYNOPSIS
Publishes the executable, checks its version and runs the full suite, as CI does.

.DESCRIPTION
-Release, which the CI workflow passes, refuses a framework census generated on another engine (ADR 0022): the census record,
skills/hunt/evals/models/framework-census.tsv, names the engine fingerprint the census was generated with, the generator version without the
built-in models, and the tree's is computed by Core's ComputeEngineFingerprint target. Without -Release a stale census is named and the
build goes on, so a change to the engine can be tested before the census is regenerated with build/generate-frameworks.ps1.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$census = Join-Path $root 'skills/hunt/evals/models/framework-census.tsv'
$recorded = if (Test-Path -LiteralPath $census) {
    Get-Content -LiteralPath $census -TotalCount 5 | Where-Object { $_.StartsWith('# engine ') } | ForEach-Object { $_.Substring('# engine '.Length) } |
        Select-Object -First 1
}
$fingerprint = & dotnet msbuild (Join-Path $root 'src/ConcurrencyHunter.Core/ConcurrencyHunter.Core.csproj') -restore -t:ComputeEngineFingerprint `
    -getProperty:EngineFingerprint -nologo --disable-build-servers -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "The engine fingerprint was not computed: $fingerprint" }
$engine = @($fingerprint) | Where-Object { $_ -match '^[0-9a-f]{64}$' } | Select-Object -Last 1
if (-not $engine) { throw "ComputeEngineFingerprint printed no fingerprint: $fingerprint" }
if ($recorded -ne $engine) {
    $stale = "The framework census in $census was generated on engine $(if ($recorded) { $recorded } else { 'none' }), and the tree's engine is " +
             "$engine; regenerate it with build/generate-frameworks.ps1 before a release."
    if ($Release) { throw $stale }
    Write-Warning $stale
}

$previousBundleExtractRoot = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$bundleExtractDirectory = [IO.Directory]::CreateTempSubdirectory('concurrency-hunter-build-')
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleExtractDirectory.FullName
    & "$PSScriptRoot/../../Common/build/package.ps1" -PluginRoot $root `
        -Solution 'src/ConcurrencyHunter.slnx' -Project 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj' `
        -ExecutableName 'concurrency-hunter.exe' -PluginName 'concurrency-hunter' -RequireEndToEndVariable 'CONCURRENCYHUNTER_REQUIRE_E2E' `
        -Configuration $Configuration
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleExtractRoot
    # Delete only this run's absolute temporary directory, never the caller's extraction cache.
    Remove-Item -LiteralPath $bundleExtractDirectory.FullName -Recurse -Force -ErrorAction Stop
}
