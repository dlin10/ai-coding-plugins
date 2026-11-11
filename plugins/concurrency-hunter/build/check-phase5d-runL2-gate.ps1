param(
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $Project,
    [Parameter(Mandatory)][string] $Filter,
    [string[]] $Expect = @(),
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'scratch/l2/gates'
New-Item -ItemType Directory -Force $logs | Out-Null

function Assert-Exit([int] $Code, [string] $Log) {
    if ($Code -ne 0) {
        Get-Content -LiteralPath $Log -Tail 60
        exit $Code
    }
}

Push-Location $root
try {
    if ($Publish) {
        $log = Join-Path $logs "$Name-publish.log"
        dotnet publish src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj -c Release -r win-x64 --self-contained false -o bin/win-x64 --nologo --disable-build-servers -nodeReuse:false *> $log
        Assert-Exit $LASTEXITCODE $log
    }
    if ($Expect.Count -gt 0) {
        $log = Join-Path $logs "$Name-list.log"
        dotnet test $Project -c Release --nologo --filter $Filter --list-tests *> $log
        Assert-Exit $LASTEXITCODE $log
        foreach ($entry in $Expect) {
            if ($entry -notmatch '^(.+)=(\d+)$') { throw "Invalid expectation: $entry" }
            $class = $Matches[1]
            $count = [int] $Matches[2]
            $actual = @(Select-String -LiteralPath $log -Pattern ([regex]::Escape("$class."))).Count
            if ($actual -ne $count) {
                Get-Content -LiteralPath $log -Tail 60
                Write-Error "Expected $count tests for $class; found $actual."
                exit 1
            }
        }
    }
    $log = Join-Path $logs "$Name.log"
    dotnet test $Project -c Release --nologo --filter $Filter *> $log
    Assert-Exit $LASTEXITCODE $log
}
finally {
    Pop-Location
}
exit 0
