[CmdletBinding()]
param(
    [string[]]$Rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [switch]$InstallBinaries
)

$ErrorActionPreference = 'Stop'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $pluginRoot 'src/PlanForgeFlow.sln'
$project = Join-Path $pluginRoot 'src/PlanForgeFlow.Cli/PlanForgeFlow.Cli.csproj'
$metadata = Get-Content (Join-Path $pluginRoot '.codex-plugin/plugin.json') -Raw | ConvertFrom-Json
$version = $metadata.version
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $pluginRoot 'artifacts' }
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $pluginRoot '..\..'))
$parentRoot = [IO.Path]::GetFullPath((Join-Path $pluginRoot '..'))
$pathRoot = [IO.Path]::GetPathRoot($outputRoot)
foreach ($protectedPath in @($pluginRoot, $workspaceRoot, $parentRoot, $pathRoot)) {
    if ([StringComparer]::OrdinalIgnoreCase.Equals($outputRoot.TrimEnd([char]92, [char]47), $protectedPath.TrimEnd([char]92, [char]47))) {
        throw "OutputRoot must be a dedicated package directory, not $protectedPath"
    }
}
$packageMarkerContent = 'plan-forge-flow-package-root-v1'
$packageMarker = Join-Path $outputRoot '.planforge-flow-package-root'
$staging = Join-Path $outputRoot 'staging'

function Assert-SafePackageTree([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    for ($current = [IO.DirectoryInfo]::new($full); $null -ne $current; $current = $current.Parent) {
        if ($current.Exists -and (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "OutputRoot or an ancestor is a reparse point: $($current.FullName)"
        }
    }
    if (Test-Path -LiteralPath $full) {
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($full)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "OutputRoot contains a reparse point: $($entry.FullName)"
                }
                if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            }
        }
    }
}

Assert-SafePackageTree $outputRoot
if (Test-Path -LiteralPath $outputRoot) {
    $existingItems = @(Get-ChildItem -LiteralPath $outputRoot -Force)
    if ($existingItems.Count -gt 0) {
        if (-not (Test-Path -LiteralPath $packageMarker -PathType Leaf) -or (Get-Content -LiteralPath $packageMarker -Raw).Trim() -ne $packageMarkerContent) {
            throw "OutputRoot exists and is not an owned Plan Forge Flow package directory: $outputRoot"
        }
    }
    Assert-SafePackageTree $outputRoot
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRoot, $staging | Out-Null
Set-Content -LiteralPath $packageMarker -Value $packageMarkerContent -Encoding utf8

$commonPublish = @(
    'publish', $project,
    '--configuration', $Configuration,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:SuppressTrimAnalysisWarnings=false'
)
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$hostRid = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    "win-$hostArchitecture"
}
elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
    "linux-$hostArchitecture"
}
elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
    "osx-$hostArchitecture"
}
else {
    $null
}

foreach ($rid in $Rids) {
    $publish = Join-Path $staging "publish-$rid"
    $bundleName = "plan-forge-flow-$version-$rid"
    $bundle = Join-Path $staging $bundleName
    $archive = Join-Path $outputRoot "$bundleName.zip"
    New-Item -ItemType Directory -Force -Path $publish | Out-Null
    & dotnet @commonPublish '--runtime' $rid '--output' $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $expectedExecutable = if ($rid.StartsWith('win-', [StringComparison]::Ordinal)) { 'planforge.exe' } else { 'planforge' }
    $entries = @(Get-ChildItem -LiteralPath $publish -Force)
    if ($entries.Count -ne 1 -or $entries[0].PSIsContainer -or $entries[0].Name -ne $expectedExecutable) {
        $names = ($entries | ForEach-Object Name) -join ', '
        throw "publish output for $rid must contain only $expectedExecutable; found $names"
    }

    if ($rid -eq $hostRid) {
        $verification = & (Join-Path $publish $expectedExecutable) plan start --workspace $pluginRoot | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $verification.ok -or $verification.data.mode -ne 'plan') {
            throw "published $rid executable failed the plan start contract"
        }
    }

    if ($InstallBinaries) {
        $installed = Join-Path $pluginRoot "bin/$rid"
        New-Item -ItemType Directory -Force -Path $installed | Out-Null
        Copy-Item -LiteralPath (Join-Path $publish $expectedExecutable) -Destination (Join-Path $installed $expectedExecutable)
    }

    $bundlePlugin = Join-Path $bundle 'plugins/plan-forge-flow'
    New-Item -ItemType Directory -Force -Path (Join-Path $bundle '.agents/plugins'), (Join-Path $bundlePlugin 'bin') | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginRoot '.codex-plugin') -Destination $bundlePlugin -Recurse
    foreach ($directory in @('skills', 'agents', 'assets', 'hooks')) {
        Copy-Item -LiteralPath (Join-Path $pluginRoot $directory) -Destination $bundlePlugin -Recurse
    }
    foreach ($file in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $pluginRoot $file) -Destination $bundlePlugin
    }
    foreach ($launcher in @('planforge-launcher.sh', 'planforge-launcher.ps1')) {
        Copy-Item -LiteralPath (Join-Path $pluginRoot "bin/$launcher") -Destination (Join-Path $bundlePlugin "bin/$launcher")
    }
    $bundleRidBin = Join-Path $bundlePlugin "bin/$rid"
    New-Item -ItemType Directory -Force -Path $bundleRidBin | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish $expectedExecutable) -Destination (Join-Path $bundleRidBin $expectedExecutable)
    if (-not $rid.StartsWith('win-', [StringComparison]::Ordinal) -and (Get-Command chmod -ErrorAction SilentlyContinue)) {
        & chmod +x (Join-Path $bundlePlugin 'bin/planforge-launcher.sh')
        & chmod +x (Join-Path $bundleRidBin $expectedExecutable)
    }

    $marketplace = [ordered]@{
        name = 'plan-forge-flow-bundle'
        interface = [ordered]@{ displayName = 'Plan Forge Flow bundle' }
        plugins = @(
            [ordered]@{
                name = 'plan-forge-flow'
                source = [ordered]@{ source = 'local'; path = './plugins/plan-forge-flow' }
                policy = [ordered]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
                category = 'Productivity'
            }
        )
    }
    $marketplace | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle '.agents/plugins/marketplace.json') -Encoding utf8

    if (-not (Test-Path -LiteralPath (Join-Path $bundlePlugin "bin/$rid/$expectedExecutable") -PathType Leaf)) { throw "bundle for $rid does not contain the RID-specific executable" }
    $zip = Get-Command zip -ErrorAction SilentlyContinue
    if ($zip) {
        Push-Location $bundle
        try { & $zip.Source -q -r $archive '.agents' 'plugins' } finally { Pop-Location }
    }
    else {
        $archiveEntries = @(Get-ChildItem -LiteralPath $bundle -Force | ForEach-Object FullName)
        Compress-Archive -Path $archiveEntries -DestinationPath $archive -CompressionLevel Optimal
    }
    Write-Host "created $archive"
}

Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Created $($Rids.Count) Plan Forge Flow $version bundles under $outputRoot"
