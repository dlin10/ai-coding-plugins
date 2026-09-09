[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $pluginRoot 'src/CacheDetective.slnx'
$project = Join-Path $pluginRoot 'src/CacheDetective.Cli/CacheDetective.Cli.csproj'
$output = Join-Path $pluginRoot 'bin/win-x64'
$executable = Join-Path $output 'cachedet.exe'
$manifests = @('.claude-plugin/plugin.json', '.codex-plugin/plugin.json', '.cursor-plugin/plugin.json')

$versions = foreach ($manifest in $manifests) {
    $path = Join-Path $pluginRoot $manifest
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing manifest: $manifest" }
    (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).version
}

$declared = @($versions | Select-Object -Unique)
if ($declared.Count -ne 1) {
    throw "Host manifests disagree on the version: $($versions -join ', ')"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

Write-Host "Publishing cache-detective $($declared[0]) for win-x64"
dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$buildHostDirectories = @(Get-ChildItem -LiteralPath $output -Directory -Filter 'BuildHost-*')
foreach ($directory in $buildHostDirectories) {
    $nonPdbFiles = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File |
        Where-Object Extension -ne '.pdb')
    if ($nonPdbFiles.Count -ne 0) {
        throw "BuildHost output contains non-PDB companion files that cannot be removed: $($nonPdbFiles.FullName -join ', ')"
    }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}

$publishedItems = @(Get-ChildItem -LiteralPath $output -Recurse)
if ($publishedItems.Count -ne 1 -or $publishedItems[0].FullName -ne $executable) {
    throw "Expected exactly one published item, cachedet.exe; found: $($publishedItems.FullName -join ', ')"
}

$reported = (& $executable --version).Trim()
if ($reported -ne $declared[0]) {
    throw "The published binary reports $reported but the manifests declare $($declared[0]). Bump <Version> in CacheDetective.Cli.csproj."
}

Write-Host "Published $executable (version $reported)"
$env:CACHEDETECTIVE_REQUIRE_E2E = '1'
try {
    dotnet test $solution -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
}
finally {
    Remove-Item Env:\CACHEDETECTIVE_REQUIRE_E2E -ErrorAction SilentlyContinue
}

Write-Host 'cache-detective package complete'
