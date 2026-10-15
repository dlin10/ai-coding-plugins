[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$previousBundleExtractRoot = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$bundleExtractDirectory = [IO.Directory]::CreateTempSubdirectory('concurrency-hunter-build-')
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleExtractDirectory.FullName
    & "$PSScriptRoot/../../Common/build/package.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
        -Solution 'src/ConcurrencyHunter.slnx' -Project 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj' `
        -ExecutableName 'concurrency-hunter.exe' -PluginName 'concurrency-hunter' -RequireEndToEndVariable 'CONCURRENCYHUNTER_REQUIRE_E2E' `
        -Configuration $Configuration
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleExtractRoot
    # Delete only this run's absolute temporary directory, never the caller's extraction cache.
    Remove-Item -LiteralPath $bundleExtractDirectory.FullName -Recurse -Force -ErrorAction Stop
}
