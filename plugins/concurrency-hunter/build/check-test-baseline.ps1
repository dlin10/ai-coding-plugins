[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$KeepResults = '',
    [switch]$Record
)

$previousBundleExtractRoot = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$bundleExtractDirectory = [IO.Directory]::CreateTempSubdirectory('concurrency-hunter-build-')
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleExtractDirectory.FullName
    & "$PSScriptRoot/../../Common/build/check-test-baseline.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
        -Solution 'src/ConcurrencyHunter.slnx' -Configuration $Configuration -KeepResults $KeepResults -Record:$Record
    exit $LASTEXITCODE
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleExtractRoot
    # Delete only this run's absolute temporary directory, never the caller's extraction cache.
    Remove-Item -LiteralPath $bundleExtractDirectory.FullName -Recurse -Force -ErrorAction Stop
}
