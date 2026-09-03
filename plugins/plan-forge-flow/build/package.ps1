[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [switch]$InstallBinaries
)

$ErrorActionPreference = 'Stop'
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) -or
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'Plan Forge packaging supports only Windows x64.'
}
$rid = 'win-x64'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $pluginRoot 'src/PlanForgeFlow.sln'
$project = Join-Path $pluginRoot 'src/PlanForge/PlanForge.csproj'
$metadata = Get-Content (Join-Path $pluginRoot '.codex-plugin/plugin.json') -Raw | ConvertFrom-Json
$claudeMetadata = Get-Content (Join-Path $pluginRoot '.claude-plugin/plugin.json') -Raw | ConvertFrom-Json
$cursorMetadata = Get-Content (Join-Path $pluginRoot '.cursor-plugin/plugin.json') -Raw | ConvertFrom-Json
$version = $metadata.version
if ($claudeMetadata.version -ne $version -or $cursorMetadata.version -ne $version) { throw "Codex, Claude, and Cursor manifest versions must match" }
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
# bin/win-x64 is absent until the first local publish: the executable is not tracked, it is a
# release asset. Only a foreign RID directory is a packaging fault.
$foreignRids = @(Get-ChildItem -LiteralPath (Join-Path $pluginRoot 'bin') -Directory -Force | Where-Object { $_.Name -ne $rid })
if ($foreignRids.Count -gt 0) {
    throw "Tracked distribution must contain only bin/$rid; found $($foreignRids.Name -join ', ')"
}

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
    "-p:Version=$version",
    '-p:PublishTrimmed=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:SuppressTrimAnalysisWarnings=false'
)
function New-PluginArchive([string]$Bundle, [string]$Archive) {
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
                $output = $entry.Open()
                try {
                    $input = [IO.File]::OpenRead($file.FullName)
                    try { $input.CopyTo($output) } finally { $input.Dispose() }
                }
                finally { $output.Dispose() }
            }
        }
        finally { $zipArchive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Copy-InstalledBinary([string]$Source, [string]$Destination) {
    $attempts = 12
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq $attempts) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Test-PublishedServer([string]$Executable) {
    # The published binary is an MCP stdio server, so the smoke test is a protocol handshake:
    # initialize, then tools/list must name every tool the workflow calls.
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"package-verify","version":"1.0.0"}}}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"ui://planforge/plan.html"}}')
        # A call that fails before the run folder is even open, which is the failure the tool result
        # used to describe as "An error occurred" and nothing more.
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"forge.plan.review","arguments":{"workspaceRoot":"relative/path","runId":"none","planDraft":"x","model":"m"}}}')
        $process.StandardInput.Flush()

        $tools = $null
        $canvas = $null
        $failure = $null
        while ($null -eq $tools -or $null -eq $canvas -or $null -eq $failure) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(60000)) { throw 'published executable did not answer the MCP handshake within 60s' }
            $line = $read.Result
            if ($null -eq $line) { throw 'published executable closed stdout during the MCP handshake' }
            $message = $line | ConvertFrom-Json
            if ($message.id -eq 2) {
                if ($message.error) { throw "published executable failed tools/list: $($message.error.message)" }
                $tools = @($message.result.tools)
            }
            if ($message.id -eq 3) {
                if ($message.error) { throw "published executable failed resources/read: $($message.error.message)" }
                $canvas = @($message.result.contents)[0]
            }
            if ($message.id -eq 4) {
                if ($message.error) { throw "the failing call came back as a protocol error rather than a tool result: $($message.error.message)" }
                $failure = $message.result
            }
        }

        foreach ($required in @('forge.begin', 'forge.models', 'forge.plan.review', 'forge.plan.show', 'forge.plan.confirm', 'forge.build.next', 'forge.review.code', 'forge.review.fix', 'forge.status', 'forge.log.append', 'forge.work.start', 'forge.work.poll', 'forge.work.fetch')) {
            if ($tools.name -notcontains $required) { throw "published executable does not expose $required" }
        }
        # The canvas is two halves that only work together: the tool has to point at the resource,
        # and the resource has to come back as an MCP App. Either half alone renders nothing.
        $planShow = $tools | Where-Object { $_.name -eq 'forge.plan.show' } | Select-Object -First 1
        if ($planShow._meta.ui.resourceUri -ne 'ui://planforge/plan.html') {
            throw 'forge.plan.show does not carry the canvas resource in _meta.ui'
        }
        $uiTools = @($tools | Where-Object { $_._meta.ui })
        if ($uiTools.Count -ne 1) {
            throw "expected exactly one tool with _meta.ui, found $($uiTools.Count): $($uiTools.name -join ', ')"
        }
        if ($canvas.mimeType -ne 'text/html;profile=mcp-app') {
            throw "canvas resource has the wrong mime type: $($canvas.mimeType)"
        }
        if ($canvas.text -notmatch 'ui/notifications/tool-result') {
            throw 'canvas resource does not listen for the tool result it renders'
        }
        # Services a tool takes from the container are bound by type and must never reach the
        # schema: an orchestrator asked for `roots` has nothing to send, and forge.begin is the one
        # tool whose whole argument list is one path, so it is where the leak would show first.
        $begin = $tools | Where-Object { $_.name -eq 'forge.begin' } | Select-Object -First 1
        $beginProperties = @($begin.inputSchema.properties.PSObject.Properties.Name)
        if ($beginProperties.Count -ne 1 -or $beginProperties[0] -ne 'workspaceRoot') {
            throw "forge.begin should take workspaceRoot and nothing else, and takes: $($beginProperties -join ', ')"
        }
        $models = $tools | Where-Object { $_.name -eq 'forge.models' } | Select-Object -First 1
        $modelsProperties = @($models.inputSchema.properties.PSObject.Properties.Name)
        foreach ($parameter in @('workspaceRoot', 'runId', 'vendor')) {
            if ($modelsProperties -notcontains $parameter) { throw "forge.models schema is missing $parameter" }
        }
        if (@($models.inputSchema.required) -contains 'vendor') { throw 'forge.models schema incorrectly requires vendor' }
        $workStart = $tools | Where-Object { $_.name -eq 'forge.work.start' } | Select-Object -First 1
        $workStartProperties = @($workStart.inputSchema.properties.PSObject.Properties.Name)
        foreach ($parameter in @('act', 'planDraft', 'userGrantedRound')) {
            if ($workStartProperties -notcontains $parameter) { throw "forge.work.start schema is missing $parameter" }
        }
        foreach ($parameter in @('effort', 'vendor', 'planDraft', 'findings', 'deferred', 'userGrantedRound')) {
            if (@($workStart.inputSchema.required) -contains $parameter) { throw "forge.work.start schema incorrectly requires $parameter" }
        }
        $codeReview = $tools | Where-Object { $_.name -eq 'forge.review.code' } | Select-Object -First 1
        $codeReviewProperties = @($codeReview.inputSchema.properties.PSObject.Properties.Name)
        foreach ($parameter in @('model', 'effort', 'vendor', 'userGrantedRound')) {
            if ($codeReviewProperties -notcontains $parameter) { throw "forge.review.code schema is missing $parameter" }
        }
        foreach ($parameter in @('criticVendor', 'criticModel', 'criticEffort', 'builderVendor', 'builderModel', 'builderEffort')) {
            if ($codeReviewProperties -contains $parameter) { throw "forge.review.code schema still exposes the pre-0.10 $parameter" }
        }
        $reviewFix = $tools | Where-Object { $_.name -eq 'forge.review.fix' } | Select-Object -First 1
        $reviewFixProperties = @($reviewFix.inputSchema.properties.PSObject.Properties.Name)
        foreach ($parameter in @('findings', 'deferred', 'model', 'effort', 'vendor')) {
            if ($reviewFixProperties -notcontains $parameter) { throw "forge.review.fix schema is missing $parameter" }
        }
        # Declared nullable but listed as required is a contract with no encoding that works: omitting
        # the key is refused server-side, and at least one host drops the `null` literal while
        # serializing and sends `"revision": ,` which never parses. See issue #44.
        $optional = @{
            'forge.plan.review' = @('effort', 'vendor', 'revision', 'deferred', 'userGrantedRound')
            'forge.build.next'  = @('effort', 'vendor')
            'forge.review.code' = @('effort', 'vendor', 'userGrantedRound')
            'forge.review.fix'  = @('effort', 'vendor', 'deferred')
            'forge.log.append'  = @('level', 'detail')
        }
        foreach ($name in $optional.Keys) {
            $tool = $tools | Where-Object { $_.name -eq $name } | Select-Object -First 1
            if ($null -eq $tool) { throw "published executable does not expose $name" }
            foreach ($parameter in $optional[$name]) {
                if (@($tool.inputSchema.properties.PSObject.Properties.Name) -notcontains $parameter) {
                    throw "$name schema is missing $parameter"
                }
                if (@($tool.inputSchema.required) -contains $parameter) {
                    throw "$name schema incorrectly requires the optional $parameter"
                }
            }
        }
        # The reason has to reach the caller, not only the run log: this one names the argument.
        if (-not $failure.isError) { throw 'a failing tool call did not come back as an error result' }
        $reason = @($failure.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join ''
        if ($reason -notmatch 'relative/path') {
            throw "a failing tool call did not surface its reason; it said: $reason"
        }
    }
    finally {
        # Kill() without the entire-process-tree overload: that overload is .NET Core only, and this
        # script is documented to run on Windows PowerShell 5.1, which is .NET Framework.
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function Test-PluginArchive([string]$Archive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipArchive = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($required in @(
                '.agents/plugins/marketplace.json',
                '.claude-plugin/marketplace.json',
                '.cursor-plugin/marketplace.json',
                'plugins/plan-forge-flow/.codex-plugin/plugin.json',
                'plugins/plan-forge-flow/.claude-plugin/plugin.json',
                'plugins/plan-forge-flow/.cursor-plugin/plugin.json',
                'plugins/plan-forge-flow/.mcp.json',
                'plugins/plan-forge-flow/bin/planforge-launcher.cmd',
                'plugins/plan-forge-flow/skills/forge/SKILL.md',
                'plugins/plan-forge-flow/skills/forge/references/CONTEXT-FORMAT.md',
                'plugins/plan-forge-flow/skills/forge/references/ADR-FORMAT.md',
                'plugins/plan-forge-flow/prompts/roslyn-contract.md',
                'plugins/plan-forge-flow/prompts/scope-contract.md',
                'plugins/plan-forge-flow/prompts/requirements-contract.md',
                'plugins/plan-forge-flow/prompts/orchestration-contract.md'
            )) {
            if ($null -eq $zipArchive.GetEntry($required)) { throw "archive is missing $required" }
        }
        $shippedExecutables = @($zipArchive.Entries | Where-Object {
                $_.FullName -match '^plugins/plan-forge-flow/bin/[^/]+/planforge(?:\.exe)?$'
            })
        if ($shippedExecutables.Count -ne 1 -or $shippedExecutables[0].FullName -ne 'plugins/plan-forge-flow/bin/win-x64/planforge.exe') {
            throw "archive must contain only the win-x64 executable"
        }
        # The prompts ship in this bundle and never in the bare release asset, so an executable
        # fetched from the release has nothing above it to walk up to and the launcher has to name
        # the folder. The other half of the contract is PromptLibrary.RootVariable, pinned by
        # PromptRootTests; this half guards what actually ships.
        $launcherEntry = $zipArchive.GetEntry('plugins/plan-forge-flow/bin/planforge-launcher.cmd')
        $reader = [IO.StreamReader]::new($launcherEntry.Open())
        try { $launcherScript = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($launcherScript -notmatch 'PLANFORGE_PROMPTS') {
            throw 'the bundled launcher does not tell the executable where the prompts are'
        }
        # Every vendor the registry can build needs its two role prompts, or that vendor fails at
        # the first act rather than at install time.
        foreach ($vendor in @('claude', 'codex', 'cursor')) {
            foreach ($role in @('critic', 'builder')) {
                $path = "plugins/plan-forge-flow/prompts/$vendor/$role.md"
                if ($null -eq $zipArchive.GetEntry($path)) { throw "archive is missing $path" }
            }
        }
        # The 1.x bundle shipped hooks, twelve agent descriptors and a parallel cursor tree; none of
        # that survives the move to an MCP server, and a stray copy would be silently loaded.
        foreach ($prefix in @('plugins/plan-forge-flow/hooks/', 'plugins/plan-forge-flow/scripts/',
                'plugins/plan-forge-flow/agents/', 'plugins/plan-forge-flow/cursor/')) {
            $stale = @($zipArchive.Entries | Where-Object { $_.FullName.StartsWith($prefix, [StringComparison]::Ordinal) })
            if ($stale.Count -gt 0) { throw "archive still carries $prefix from the pre-MCP layout" }
        }
    }
    finally { $zipArchive.Dispose() }
}

$publish = Join-Path $staging "publish-$rid"
$bundleName = "plan-forge-flow-$version-$rid"
$bundle = Join-Path $staging $bundleName
$archive = Join-Path $outputRoot "$bundleName.zip"
New-Item -ItemType Directory -Force -Path $publish | Out-Null
& dotnet @commonPublish '--runtime' $rid '--output' $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

$expectedExecutable = 'planforge.exe'
$promptsFolder = 'prompts'
$entries = @(Get-ChildItem -LiteralPath $publish -Force)
$unexpected = @($entries | Where-Object {
        ($_.PSIsContainer -and $_.Name -ne $promptsFolder) -or (-not $_.PSIsContainer -and $_.Name -ne $expectedExecutable)
    })
if ($unexpected.Count -gt 0) {
    throw "publish output for $rid must contain only $expectedExecutable and $promptsFolder/; found $(($unexpected | ForEach-Object Name) -join ', ')"
}
if (-not (Test-Path -LiteralPath (Join-Path $publish $expectedExecutable) -PathType Leaf)) {
    throw "publish output for $rid is missing $expectedExecutable"
}
if (-not (Test-Path -LiteralPath (Join-Path $publish "$promptsFolder/claude/critic.md") -PathType Leaf)) {
    throw "publish output for $rid is missing the role prompts"
}

Test-PublishedServer -Executable (Join-Path $publish $expectedExecutable)

if ($InstallBinaries) {
    $installed = Join-Path $pluginRoot "bin/$rid"
    New-Item -ItemType Directory -Force -Path $installed | Out-Null
    Copy-InstalledBinary -Source (Join-Path $publish $expectedExecutable) -Destination (Join-Path $installed $expectedExecutable)
}

$bundlePlugin = Join-Path $bundle 'plugins/plan-forge-flow'
New-Item -ItemType Directory -Force -Path (Join-Path $bundle '.agents/plugins'), (Join-Path $bundle '.claude-plugin'), (Join-Path $bundle '.cursor-plugin'), (Join-Path $bundlePlugin 'bin') | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginRoot '.codex-plugin') -Destination $bundlePlugin -Recurse
Copy-Item -LiteralPath (Join-Path $pluginRoot '.claude-plugin') -Destination $bundlePlugin -Recurse
Copy-Item -LiteralPath (Join-Path $pluginRoot '.cursor-plugin') -Destination $bundlePlugin -Recurse
foreach ($directory in @('assets', 'prompts')) {
    Copy-Item -LiteralPath (Join-Path $pluginRoot $directory) -Destination $bundlePlugin -Recurse
}
foreach ($file in @(
        'skills/forge/SKILL.md',
        'skills/forge/references/CONTEXT-FORMAT.md',
        'skills/forge/references/ADR-FORMAT.md'
    )) {
    $destination = Join-Path $bundlePlugin $file
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginRoot $file) -Destination $destination
}
foreach ($file in @('README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', '.mcp.json')) {
    Copy-Item -LiteralPath (Join-Path $pluginRoot $file) -Destination $bundlePlugin
}
$bundleRidBin = Join-Path $bundlePlugin "bin/$rid"
New-Item -ItemType Directory -Force -Path $bundleRidBin | Out-Null
Copy-Item -LiteralPath (Join-Path $publish $expectedExecutable) -Destination (Join-Path $bundleRidBin $expectedExecutable)
Copy-Item -LiteralPath (Join-Path $pluginRoot 'bin/planforge-launcher.cmd') -Destination (Join-Path $bundlePlugin 'bin/planforge-launcher.cmd')
$marketplace = [ordered]@{
    name      = 'plan-forge-flow-bundle'
    interface = [ordered]@{ displayName = 'Plan Forge Flow bundle' }
    plugins   = @(
        [ordered]@{
            name     = 'plan-forge-flow'
            source   = [ordered]@{ source = 'local'; path = './plugins/plan-forge-flow' }
            policy   = [ordered]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
            category = 'Productivity'
        }
    )
}
$marketplace | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle '.agents/plugins/marketplace.json') -Encoding utf8

$cursorMarketplace = [ordered]@{
    name    = 'plan-forge-flow-bundle'
    owner   = [ordered]@{ name = 'Dmitry Linetsky' }
    plugins = @(
        [ordered]@{
            name        = 'plan-forge-flow'
            source      = './plugins/plan-forge-flow'
            description = 'Windows x64-only plan hardening, independent review, and stepwise implementation for Codex, Claude Code 2.1.232 and newer, and Cursor 3.15.6 and newer.'
        }
    )
}
$cursorMarketplace | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle '.cursor-plugin/marketplace.json') -Encoding utf8
$claudeMarketplace = [ordered]@{
    name    = $cursorMarketplace.name
    owner   = $cursorMarketplace.owner
    plugins = $cursorMarketplace.plugins
}
$claudeMarketplace | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle '.claude-plugin/marketplace.json') -Encoding utf8

foreach ($requiredPath in @(
        (Join-Path $bundlePlugin '.codex-plugin/plugin.json'),
        (Join-Path $bundlePlugin '.claude-plugin/plugin.json'),
        (Join-Path $bundlePlugin '.cursor-plugin/plugin.json'),
        (Join-Path $bundlePlugin '.mcp.json'),
        (Join-Path $bundlePlugin 'skills/forge/SKILL.md'),
        (Join-Path $bundlePlugin 'skills/forge/references/CONTEXT-FORMAT.md'),
        (Join-Path $bundlePlugin 'skills/forge/references/ADR-FORMAT.md'),
        (Join-Path $bundlePlugin 'prompts/roslyn-contract.md'),
        (Join-Path $bundlePlugin 'prompts/scope-contract.md'),
        (Join-Path $bundlePlugin 'prompts/requirements-contract.md'),
        (Join-Path $bundlePlugin 'prompts/orchestration-contract.md'),
        (Join-Path $bundlePlugin "bin/$rid/$expectedExecutable"),
        (Join-Path $bundlePlugin 'bin/planforge-launcher.cmd'),
        (Join-Path $bundlePlugin 'prompts/claude/critic.md'),
        (Join-Path $bundlePlugin 'prompts/claude/builder.md'),
        (Join-Path $bundlePlugin 'prompts/codex/critic.md'),
        (Join-Path $bundlePlugin 'prompts/codex/builder.md'),
        (Join-Path $bundlePlugin 'prompts/cursor/critic.md'),
        (Join-Path $bundlePlugin 'prompts/cursor/builder.md'),
        (Join-Path $bundle '.agents/plugins/marketplace.json'),
        (Join-Path $bundle '.claude-plugin/marketplace.json'),
        (Join-Path $bundle '.cursor-plugin/marketplace.json')
    )) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "bundle for $rid is missing $requiredPath" }
}
New-PluginArchive -Bundle $bundle -Archive $archive
Test-PluginArchive -Archive $archive
Write-Host "created $archive"

Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Created one Plan Forge Flow $version win-x64 bundle under $outputRoot"
