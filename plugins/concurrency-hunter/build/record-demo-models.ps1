<#
.SYNOPSIS
Writes the demo's committed generated models, demo/.concurrency-hunter/models/generated/.

.DESCRIPTION
Builds the executable in Release, then runs `metrics --target demo/Demo.slnx --generation on`, which generates the model of every
library member the demo's direct opaque calls reach that no layer describes and writes it below the repository root. The project
entries named in $STRIPPED_MEMBERS are taken out of demo/.concurrency-hunter/models/demo.json for that run, because a project entry
that makes a member opaque keeps the member from being a generation target; the file is put back byte for byte in a `finally`, whatever
the run does, from a copy kept in scratch/.

The run is refused when it left a member at `budget`: such a member was cut by the run's limit, its answer depends on the machine and
would not be written. A second run, `--generation read`, then checks that the committed files are enough: it is refused when it lists a
generation target with the cause `not-generated`, a member a generating run would have generated.

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
# Project entries that keep a member from being generated, by member id; the demo cases that need the member's generated model beside
# them (project-opaque-switches-off-generated) are the reason each is here.
$STRIPPED_MEMBERS = @('M:Microsoft.Extensions.Options.Options.Create``1(``0)')

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path $root 'scratch'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$projectModels = Join-Path $root 'demo/.concurrency-hunter/models/demo.json'
$aside = Join-Path $scratch 'demo-models.demo.json.aside'
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

# One measurement of the demo; returns the parsed JSON.
function Invoke-Metrics([string]$Name, [string]$Generation) {
    $out = Join-Path $scratch "demo-$Name.json"
    Remove-Item -LiteralPath $out -ErrorAction Ignore
    Invoke-Dotnet $Name @('run', '--project', $CLI_PROJECT, '-c', 'Release', '--no-build', '--',
                          'metrics', '--target', $TARGET, '--generation', $Generation, '--out', $out)
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

    # 2. Generate, with the stripped project entries out of demo.json.
    $original = [IO.File]::ReadAllBytes($projectModels)
    [IO.File]::WriteAllBytes($aside, $original)
    $generating = $null
    try {
        $node = [Text.Json.Nodes.JsonNode]::Parse([Text.Encoding]::UTF8.GetString($original).TrimStart([char]0xFEFF))
        $models = $node['models'].AsArray()
        $before = $models.Count
        for ($index = $models.Count - 1; $index -ge 0; $index--) {
            if ($STRIPPED_MEMBERS -contains $models[$index]['member'].GetValue[string]()) { $models.RemoveAt($index) }
        }
        if ($before - $models.Count -ne $STRIPPED_MEMBERS.Count) { throw "$projectModels does not hold exactly the entries to strip." }
        [IO.File]::WriteAllText($projectModels, $node.ToJsonString(), [Text.UTF8Encoding]::new($false))
        $generating = Invoke-Metrics 'models' 'on'
    }
    finally {
        [IO.File]::WriteAllBytes($projectModels, $original)
    }
    if ((Get-FileHash -LiteralPath $projectModels).Hash -ne (Get-FileHash -LiteralPath $aside).Hash) {
        throw "$projectModels was not restored; the copy is $aside."
    }
    Remove-Item -LiteralPath $aside

    # 3. Nothing the run's limit cut.
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
