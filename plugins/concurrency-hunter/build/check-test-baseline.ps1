[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$KeepResults = '',
    [switch]$Record
)

& "$PSScriptRoot/../../Common/build/check-test-baseline.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
    -Solution 'src/ConcurrencyHunter.slnx' -Configuration $Configuration -KeepResults $KeepResults -Record:$Record
exit $LASTEXITCODE
