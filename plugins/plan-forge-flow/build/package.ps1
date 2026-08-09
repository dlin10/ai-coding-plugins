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
$cursorMetadata = Get-Content (Join-Path $pluginRoot '.cursor-plugin/plugin.json') -Raw | ConvertFrom-Json
$version = $metadata.version
if ($cursorMetadata.version -ne $version) { throw "Codex and Cursor manifest versions must match" }
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

function New-PluginArchive([string]$Bundle, [string]$Archive, [string]$Rid) {
    Add-Type -AssemblyName System.IO.Compression
    $bundlePrefix = [IO.Path]::GetFullPath($Bundle).TrimEnd([char]92, [char]47) + [IO.Path]::DirectorySeparatorChar
    $pathComparison = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $stream = [IO.File]::Open($Archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zipArchive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $Bundle -File -Recurse -Force | Sort-Object FullName)) {
                if (-not $file.FullName.StartsWith($bundlePrefix, $pathComparison)) { throw "Package file escaped bundle root: $($file.FullName)" }
                $relative = $file.FullName.Substring($bundlePrefix.Length).Replace([char]92, [char]47)
                $entry = $zipArchive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $isUnixExecutable = -not $Rid.StartsWith('win-', [StringComparison]::Ordinal) -and ($relative -eq 'plugins/plan-forge-flow/bin/planforge-launcher.sh' -or $relative -eq "plugins/plan-forge-flow/bin/$Rid/planforge")
                $unixMode = if ($isUnixExecutable) { [uint32]0x81ED } else { [uint32]0x81A4 }
                $externalAttributes = [uint32]($unixMode -shl 16)
                $entry.ExternalAttributes = [BitConverter]::ToInt32([BitConverter]::GetBytes($externalAttributes), 0)
                $output = $entry.Open()
                try {
                    if (-not $Rid.StartsWith('win-', [StringComparison]::Ordinal) -and $relative -eq 'plugins/plan-forge-flow/bin/planforge-launcher.sh') {
                        $text = [IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n").Replace("`r", "`n")
                        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
                        $output.Write($bytes, 0, $bytes.Length)
                    }
                    else {
                        $input = [IO.File]::OpenRead($file.FullName)
                        try { $input.CopyTo($output) } finally { $input.Dispose() }
                    }
                }
                finally { $output.Dispose() }
            }
        }
        finally { $zipArchive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Set-ArchiveUnixOrigin([string]$Archive) {
    $bytes = [IO.File]::ReadAllBytes($Archive)
    $minimum = [Math]::Max(0, $bytes.Length - 65557)
    $end = -1
    for ($index = $bytes.Length - 22; $index -ge $minimum; $index--) {
        if ([BitConverter]::ToUInt32($bytes, $index) -eq 0x06054B50) { $end = $index; break }
    }
    if ($end -lt 0) { throw "archive has no ZIP end-of-central-directory record: $Archive" }
    $entryCount = [BitConverter]::ToUInt16($bytes, $end + 10)
    $position = [int][BitConverter]::ToUInt32($bytes, $end + 16)
    for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
        if ([BitConverter]::ToUInt32($bytes, $position) -ne 0x02014B50) { throw "archive has an invalid central directory: $Archive" }
        $bytes[$position + 5] = 3
        $nameLength = [BitConverter]::ToUInt16($bytes, $position + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $position + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $position + 32)
        $position += 46 + $nameLength + $extraLength + $commentLength
    }
    [IO.File]::WriteAllBytes($Archive, $bytes)
}

function Test-PluginArchive([string]$Archive, [string]$Rid) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipArchive = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($required in @(
            '.agents/plugins/marketplace.json',
            '.cursor-plugin/marketplace.json',
            'plugins/plan-forge-flow/.codex-plugin/plugin.json',
            'plugins/plan-forge-flow/.cursor-plugin/plugin.json'
        )) {
            if ($null -eq $zipArchive.GetEntry($required)) { throw "archive for $Rid is missing $required" }
        }
        if ($Rid.StartsWith('win-', [StringComparison]::Ordinal)) { return }
        $paths = @(
            'plugins/plan-forge-flow/bin/planforge-launcher.sh',
            "plugins/plan-forge-flow/bin/$Rid/planforge"
        )
        foreach ($path in $paths) {
            $entry = $zipArchive.GetEntry($path)
            if ($null -eq $entry) { throw "archive for $Rid is missing $path" }
            $attributes = [BitConverter]::ToUInt32([BitConverter]::GetBytes($entry.ExternalAttributes), 0)
            $mode = ($attributes -shr 16) -band 0x1FF
            if ($mode -ne 0x1ED) { throw "archive entry $path must have Unix mode 0755; found $([Convert]::ToString($mode, 8))" }
        }
        $launcher = $zipArchive.GetEntry('plugins/plan-forge-flow/bin/planforge-launcher.sh')
        $stream = $launcher.Open()
        try {
            $memory = [IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose() }
        }
        finally { $stream.Dispose() }
        if ($bytes -contains 13) { throw "archive launcher for $Rid contains CR bytes" }
    }
    finally { $zipArchive.Dispose() }

    if ($Rid.StartsWith('win-', [StringComparison]::Ordinal)) { return }

    $bytes = [IO.File]::ReadAllBytes($Archive)
    $minimum = [Math]::Max(0, $bytes.Length - 65557)
    $end = -1
    for ($index = $bytes.Length - 22; $index -ge $minimum; $index--) {
        if ([BitConverter]::ToUInt32($bytes, $index) -eq 0x06054B50) { $end = $index; break }
    }
    if ($end -lt 0) { throw "archive has no ZIP end-of-central-directory record: $Archive" }
    $entryCount = [BitConverter]::ToUInt16($bytes, $end + 10)
    $position = [int][BitConverter]::ToUInt32($bytes, $end + 16)
    for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
        if ($bytes[$position + 5] -ne 3) { throw "archive entry $entryIndex for $Rid is not marked as Unix-created" }
        $nameLength = [BitConverter]::ToUInt16($bytes, $position + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $position + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $position + 32)
        $position += 46 + $nameLength + $extraLength + $commentLength
    }
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
        $verification = & (Join-Path $publish $expectedExecutable) run doctor --workspace $pluginRoot | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $verification.ok -or -not $verification.data.git.ok -or -not $verification.data.dotnet.ok) {
            throw "published $rid executable failed the doctor contract"
        }
    }

    if ($InstallBinaries) {
        $installed = Join-Path $pluginRoot "bin/$rid"
        New-Item -ItemType Directory -Force -Path $installed | Out-Null
        Copy-Item -LiteralPath (Join-Path $publish $expectedExecutable) -Destination (Join-Path $installed $expectedExecutable)
    }

    $bundlePlugin = Join-Path $bundle 'plugins/plan-forge-flow'
    New-Item -ItemType Directory -Force -Path (Join-Path $bundle '.agents/plugins'), (Join-Path $bundle '.cursor-plugin'), (Join-Path $bundlePlugin 'bin') | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginRoot '.codex-plugin') -Destination $bundlePlugin -Recurse
    Copy-Item -LiteralPath (Join-Path $pluginRoot '.cursor-plugin') -Destination $bundlePlugin -Recurse
    foreach ($directory in @('skills', 'agents', 'cursor', 'assets', 'hooks')) {
        Copy-Item -LiteralPath (Join-Path $pluginRoot $directory) -Destination $bundlePlugin -Recurse
    }
    foreach ($file in @('README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $pluginRoot $file) -Destination $bundlePlugin
    }
    foreach ($launcher in @('planforge-launcher.sh', 'planforge-launcher.ps1')) {
        Copy-Item -LiteralPath (Join-Path $pluginRoot "bin/$launcher") -Destination (Join-Path $bundlePlugin "bin/$launcher")
    }
    $bundleRidBin = Join-Path $bundlePlugin "bin/$rid"
    New-Item -ItemType Directory -Force -Path $bundleRidBin | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish $expectedExecutable) -Destination (Join-Path $bundleRidBin $expectedExecutable)
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

    $cursorMarketplace = [ordered]@{
        name = 'plan-forge-flow-bundle'
        owner = [ordered]@{ name = 'Dmitry Linetsky' }
        plugins = @(
            [ordered]@{
                name = 'plan-forge-flow'
                source = './plugins/plan-forge-flow'
                description = 'Plan hardening, independent advisory review, and stepwise local implementation for Cursor 3.15.6 and newer.'
            }
        )
    }
    $cursorMarketplace | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle '.cursor-plugin/marketplace.json') -Encoding utf8

    foreach ($requiredPath in @(
        (Join-Path $bundlePlugin '.codex-plugin/plugin.json'),
        (Join-Path $bundlePlugin '.cursor-plugin/plugin.json'),
        (Join-Path $bundlePlugin 'cursor/commands/forge.md'),
        (Join-Path $bundlePlugin 'cursor/skills/forge/SKILL.md'),
        (Join-Path $bundlePlugin 'cursor/agents/forge-reviewer.md'),
        (Join-Path $bundlePlugin 'cursor/agents/forge-builder.md'),
        (Join-Path $bundlePlugin 'hooks/hooks.json'),
        (Join-Path $bundlePlugin "bin/$rid/$expectedExecutable"),
        (Join-Path $bundle '.agents/plugins/marketplace.json'),
        (Join-Path $bundle '.cursor-plugin/marketplace.json')
    )) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "bundle for $rid is missing $requiredPath" }
    }
    New-PluginArchive -Bundle $bundle -Archive $archive -Rid $rid
    if (-not $rid.StartsWith('win-', [StringComparison]::Ordinal)) { Set-ArchiveUnixOrigin -Archive $archive }
    Test-PluginArchive -Archive $archive -Rid $rid
    Write-Host "created $archive"
}

Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Created $($Rids.Count) Plan Forge Flow $version bundles under $outputRoot"
