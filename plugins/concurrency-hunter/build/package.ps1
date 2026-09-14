[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../../Common/build/package.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
    -Solution 'src/ConcurrencyHunter.slnx' -Project 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj' `
    -ExecutableName 'concurrency-hunter.exe' -PluginName 'concurrency-hunter' -RequireEndToEndVariable 'CONCURRENCYHUNTER_REQUIRE_E2E' `
    -Configuration $Configuration
