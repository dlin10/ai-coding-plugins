[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../../Common/build/package.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
    -Solution 'src/CacheDetective.slnx' -Project 'src/CacheDetective.Cli/CacheDetective.Cli.csproj' `
    -ExecutableName 'cachedet.exe' -PluginName 'cache-detective' -RequireEndToEndVariable 'CACHEDETECTIVE_REQUIRE_E2E' `
    -Configuration $Configuration
