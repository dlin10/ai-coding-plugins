[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$Shard
)

# Prints the `dotnet test --filter` expression of one CI shard. test-shards.json names the classes of
# every shard but the last; the last one runs everything those do not name. A new test class, or a
# misspelt name in the file, therefore still runs in exactly one shard instead of in none.
$ErrorActionPreference = 'Stop'
$lists = @((Get-Content (Join-Path $PSScriptRoot 'test-shards.json') -Raw | ConvertFrom-Json).shards)
if ($Shard -lt 1 -or $Shard -gt $lists.Count + 1) { throw "Shard must be 1..$($lists.Count + 1), got $Shard" }

# The vendor CLIs the integration tests drive exist on no runner. The trailing dot ends the class name,
# so a class is never matched by another whose name only starts the same.
$notIntegration = 'Category!=Integration'
if ($Shard -le $lists.Count) {
    $named = @($lists[$Shard - 1] | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
    "$notIntegration&($named)"
}
else {
    $rest = @($lists | ForEach-Object { $_ } | ForEach-Object { "FullyQualifiedName!~$_." }) -join '&'
    "$notIntegration&$rest"
}
