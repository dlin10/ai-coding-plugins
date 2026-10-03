<#
.SYNOPSIS
Append a named CoreLib engine measurement, explicitly opted in for this invocation only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Label,
    [scriptblock]$TestCommand
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$beforeBench = [Environment]::GetEnvironmentVariable('CH_ENGINE_BENCH')
$beforeLabel = [Environment]::GetEnvironmentVariable('CH_ENGINE_BENCH_LABEL')
$code = 1
try {
    $env:CH_ENGINE_BENCH = '1'
    $env:CH_ENGINE_BENCH_LABEL = $Label
    if ($TestCommand) {
        & $TestCommand
        $code = $LASTEXITCODE
    }
    else {
        $root = (Resolve-Path "$PSScriptRoot/..").Path
        $logs = Join-Path $root 'scratch'
        New-Item -ItemType Directory -Force -Path $logs | Out-Null
        $log = Join-Path $logs "corelib-bench-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
        Write-Host "CoreLib benchmark '$Label'; output: $log"
        dotnet test (Join-Path $root 'src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj') -c Release --disable-build-servers -nodeReuse:false --filter 'FullyQualifiedName=ConcurrencyHunter.Core.Tests.Engine.CoreLibBenchmarkTests.CoreLib_benchmark_records_a_point' *> $log
        $code = $LASTEXITCODE
        Get-Content -LiteralPath $log -Tail 50
    }
}
finally {
    if ($null -eq $beforeBench) { Remove-Item Env:CH_ENGINE_BENCH -ErrorAction SilentlyContinue }
    else { $env:CH_ENGINE_BENCH = $beforeBench }
    if ($null -eq $beforeLabel) { Remove-Item Env:CH_ENGINE_BENCH_LABEL -ErrorAction SilentlyContinue }
    else { $env:CH_ENGINE_BENCH_LABEL = $beforeLabel }
}

exit $code
