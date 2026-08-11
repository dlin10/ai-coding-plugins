[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

# Publishes the shipped binary and then runs the full suite against it. Publishing before testing is
# deliberate: the end-to-end transport test needs a real executable, and if it were allowed to skip
# it would skip forever and the one failure mode it guards — stray output on stdout — would ship.

$ErrorActionPreference = 'Stop'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $pluginRoot 'src/FindFiles.slnx'
$project = Join-Path $pluginRoot 'src/FindFiles.Cli/FindFiles.Cli.csproj'
$output = Join-Path $pluginRoot 'bin/win-x64'
$executable = Join-Path $output 'esfind.exe'

$manifests = @('.claude-plugin/plugin.json', '.codex-plugin/plugin.json', '.cursor-plugin/plugin.json')
$versions = foreach ($manifest in $manifests) {
    $path = Join-Path $pluginRoot $manifest
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing manifest: $manifest" }
    (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).version
}
$declared = $versions | Select-Object -Unique
if ($declared.Count -ne 1) {
    throw "Host manifests disagree on the version: $($versions -join ', ')"
}

Write-Host "Publishing find-files $declared for win-x64"
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$reported = (& $executable --version).Trim()
if ($reported -ne $declared) {
    throw "The published binary reports $reported but the manifests declare $declared. Bump <Version> in FindFiles.Cli.csproj."
}
Write-Host "Published $executable (version $reported)"

if (-not $SkipTests) {
    $env:FINDFILES_REQUIRE_E2E = '1'
    try {
        dotnet test $solution -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
    }
    finally {
        Remove-Item Env:\FINDFILES_REQUIRE_E2E -ErrorAction SilentlyContinue
    }
}

Write-Host 'find-files package complete'
