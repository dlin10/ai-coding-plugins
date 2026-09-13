[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Record
)

& "$PSScriptRoot/../../Common/build/check-test-baseline.ps1" -PluginRoot (Resolve-Path "$PSScriptRoot/..").Path `
    -Solution 'src/CacheDetective.slnx' -RequiredEnvironment 'CD_ESHOP_ROOT', 'CD_TEST_SQL_CONN' `
    -Configuration $Configuration -Record:$Record
exit $LASTEXITCODE
