[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Record
)

& "$PSScriptRoot/../../Common/build/check-test-baseline.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
    -Solution 'src/ConcurrencyHunter.slnx' -Configuration $Configuration -Record:$Record
exit $LASTEXITCODE
