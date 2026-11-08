<#
.SYNOPSIS
Writes the demo's committed generated models, demo/.concurrency-hunter/models/generated/.

.DESCRIPTION
Builds the executable in Release, then runs `metrics --target demo/Demo.slnx --generation on --recording`, which answers the targets of the
demo's scope calls from every generation round, plus the dependency members generation reaches, and writes final answers below the repository
root. `--recording` gives the run the recording's limit instead of the run's, so that one run generates everything.
The members of the shared frameworks Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0 that the demo reaches are the frameworks'
census, which ships in the built-in models (ADR 0012, amendment of 2026-11-07), so the committed files hold package members only. The
framework folders below the generated folder are removed before the run, and the run is refused when it writes one again: then the census
does not answer a framework member the demo reaches, and build/generate-frameworks.ps1 regenerates it.

The run is refused when coverage left a target of a scope call at `budget`: such a member was cut by the recording's limit, its answer
depends on the machine and would not be written. Dependency members do not appear in scope coverage. A second run, `--generation read`, then
checks that the committed files are enough: it is refused when it lists a target of a scope call with the cause `not-generated`, including
a target whose nonfinal answer was not saved by the generating run.

Exit code: 0 when the files are written and a run that only reads them would generate nothing, 1 otherwise.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
# MSBuild nodes that outlive a redirected dotnet command hold its pipe open for their idle timeout.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$CLI_PROJECT = 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj'
$TARGET = 'demo/Demo.slnx'
$GENERATED_DIR = 'demo/.concurrency-hunter/models/generated'
$BUDGET = 'budget'
$NOT_GENERATED = 'not-generated'
$FRAMEWORKS = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path $root 'scratch'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$generatedRoot = Join-Path $root $GENERATED_DIR

function Show-Tail([string]$Log) { Get-Content -LiteralPath $Log -Tail 40 -Encoding utf8 | ForEach-Object { Write-Host $_ } }

# Runs dotnet with its whole output in a log and stops on a non-zero exit, printing the log's tail.
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $scratch "record-demo-models-$Name.log"
    & dotnet @Arguments *> $log
    if ($LASTEXITCODE -ne 0) {
        Show-Tail $log
        throw "dotnet $($Arguments[0]) ($Name) exited $LASTEXITCODE; log: $log"
    }
}

# One measurement of the demo; returns the parsed JSON. $Extra holds further metrics arguments.
function Invoke-Metrics([string]$Name, [string]$Generation, [string[]]$Extra = @()) {
    $out = Join-Path $scratch "demo-$Name.json"
    Remove-Item -LiteralPath $out -ErrorAction Ignore
    Invoke-Dotnet $Name (@('run', '--project', $CLI_PROJECT, '-c', 'Release', '--no-build', '--',
                           'metrics', '--target', $TARGET, '--generation', $Generation, '--out', $out) + $Extra)
    if (-not (Test-Path -LiteralPath $out)) { throw "metrics wrote no $out." }
    return Get-Content -LiteralPath $out -Raw -Encoding utf8 | ConvertFrom-Json
}

# The generation targets of every scope that carry the cause, as "scope member file".
function Get-Refusals($Measurement, [string]$Cause) {
    @($Measurement.coverage | ForEach-Object {
            $scope = $_.scopeId
            @($_.generationRefusals) | Where-Object { $null -ne $_ -and $_.cause -eq $Cause } | ForEach-Object { "$scope $($_.member) ($($_.file))" }
        })
}

Push-Location $root
try {
    # 1. Build.
    Invoke-Dotnet 'build' @('build', $CLI_PROJECT, '-c', 'Release', '--disable-build-servers', '-nodeReuse:false')

    # 2. Generate, without the framework folders, which the census answers.
    foreach ($framework in $FRAMEWORKS) {
        Remove-Item -LiteralPath (Join-Path $generatedRoot $framework) -Recurse -Force -ErrorAction Ignore
    }
    $generating = Invoke-Metrics 'models' 'on' @('--recording')
    $frameworkFiles = @($FRAMEWORKS | ForEach-Object { Join-Path $generatedRoot $_ } | Where-Object { Test-Path -LiteralPath $_ } |
                        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Filter '*.json' -File })
    if ($frameworkFiles.Count -gt 0) {
        Write-Host "The run generated members of the shared frameworks, which the census should answer; regenerate it with build/generate-frameworks.ps1:"
        $frameworkFiles | ForEach-Object { Write-Host "  $($_.FullName)" }
        exit 1
    }

    # 3. Nothing the recording's limit cut.
    $cut = @(Get-Refusals $generating $BUDGET)
    if ($cut.Count -gt 0) {
        Write-Host "The generating run left $($cut.Count) member(s) at $BUDGET; their answers depend on the machine and are not written:"
        $cut | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    $files = @(Get-ChildItem -LiteralPath $generatedRoot -Recurse -Filter '*.json' -File -ErrorAction Ignore)
    if ($files.Count -eq 0) {
        Write-Host "The generating run wrote no file below $GENERATED_DIR."
        exit 1
    }

    # 4. The committed files are enough: a run that only reads them would generate nothing.
    $reading = Invoke-Metrics 'read' 'read'
    $missing = @(Get-Refusals $reading $NOT_GENERATED)
    if ($missing.Count -gt 0) {
        Write-Host "A run that only reads $GENERATED_DIR would generate $($missing.Count) member(s):"
        $missing | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host "$($files.Count) generated file(s) below $GENERATED_DIR; a run that only reads them generates nothing."
    exit 0
}
finally {
    Pop-Location
}
