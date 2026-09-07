#Requires -Version 5.1
<#
.SYNOPSIS
  Shared Issue70Exp host helpers used by verify-environment.ps1.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Issue70Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $script:Issue70Root 'baseline\manifest.json'))) {
  $script:Issue70Root = $PSScriptRoot
}

$script:RootSuffix = 'Issue70Exp'
$script:ProbePort = 5062
$script:BaselinePort = 5052
$script:ExpectedVersion = '1.8.0'
$script:VsInstall = 'C:\Program Files\Microsoft Visual Studio\18\Community'
$script:Devenv = Join-Path $script:VsInstall 'Common7\IDE\devenv.exe'
$script:MsBuild = Join-Path $script:VsInstall 'MSBuild\Current\Bin\MSBuild.exe'
$script:ExtensionProj = (Resolve-Path (Join-Path $script:Issue70Root '..\..\src\RoslynMcpExtension\RoslynMcpExtension.csproj')).Path
$script:FixtureSln = Join-Path $script:Issue70Root 'Fixture\Fixture.sln'
$script:CheckpointDir = Join-Path $script:Issue70Root 'checkpoints'
$script:EvidenceDir = Join-Path $script:Issue70Root 'evidence'
$script:HostCheckpoint = Join-Path $script:CheckpointDir 'host.json'
$script:DeployCheckpoint = Join-Path $script:CheckpointDir 'deployment.json'
$script:InitCheckpoint = Join-Path $script:CheckpointDir 'hive-init.json'

# Get-NetTCPConnection lives in NetTCPIP; import explicitly so StrictMode scripts can call it.
Import-Module NetTCPIP -ErrorAction SilentlyContinue | Out-Null

function Get-Sha256Hex {
  param([Parameter(Mandatory)][string]$Path)
  $hash = Get-FileHash -Algorithm SHA256 -Path $Path
  return $hash.Hash.ToLowerInvariant()
}

function Write-JsonFile {
  param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Path)
  $dir = Split-Path -Parent $Path
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  ($Object | ConvertTo-Json -Depth 30) | Set-Content -Path $Path -Encoding utf8
}

function Read-JsonFile {
  param([Parameter(Mandatory)][string]$Path)
  return Get-Content -Raw -Path $Path | ConvertFrom-Json
}

function Write-ResultsAction {
  param([Parameter(Mandatory)][string]$Action)
  $path = Join-Path $script:Issue70Root 'setup-action.md'
  @"
# Issue70 environment gate

## Required action

$Action

Timestamp (UTC): $([DateTimeOffset]::UtcNow.ToString('o'))
"@ | Set-Content -Path $path -Encoding utf8
}

function Test-PortFree {
  param([int]$Port = 5062)
  try {
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($null -ne $conn) { return $false }
  }
  catch {
    # Fall back to netstat when NetTCPIP cannot load in this host.
    $hit = netstat -ano | Select-String -Pattern ":$Port\s+.*LISTENING"
    if ($hit) { return $false }
  }
  return $true
}

function Get-PortOwnerPid {
  param([int]$Port)
  try {
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) { return [int]$conn.OwningProcess }
  }
  catch {
    $line = netstat -ano | Select-String -Pattern ":$Port\s+.*LISTENING" | Select-Object -First 1
    if ($line) {
      $parts = ($line.ToString() -split '\s+') | Where-Object { $_ }
      $pidText = $parts[-1]
      if ($pidText -match '^\d+$') { return [int]$pidText }
    }
  }
  return $null
}

function Invoke-McpRaw {
  param(
    [Parameter(Mandatory)][string]$Endpoint,
    [Parameter(Mandatory)][hashtable]$Payload,
    [string]$OutFile
  )
  $headers = @{
    'Content-Type' = 'application/json'
    'Accept'       = 'application/json, text/event-stream'
  }
  $body = $Payload | ConvertTo-Json -Depth 20 -Compress
  $resp = Invoke-WebRequest -Uri $Endpoint -Method POST -Headers $headers -Body $body -UseBasicParsing -TimeoutSec 150
  if ($OutFile) {
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Set-Content -Path $OutFile -Value $resp.Content -NoNewline -Encoding utf8
  }
  return $resp.Content
}

function ConvertFrom-McpSse {
  param([Parameter(Mandatory)][string]$Raw)
  $line = ($Raw -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -First 1)
  if (-not $line) { throw "No SSE data line in MCP response." }
  return ($line.Substring(5).Trim() | ConvertFrom-Json)
}

function Invoke-McpTool {
  param(
    [Parameter(Mandatory)][string]$Endpoint,
    [Parameter(Mandatory)][string]$Name,
    [hashtable]$Arguments = @{},
    [string]$OutFile
  )
  $raw = Invoke-McpRaw -Endpoint $Endpoint -OutFile $OutFile -Payload @{
    jsonrpc = '2.0'
    id      = [int](Get-Random -Minimum 1 -Maximum 100000)
    method  = 'tools/call'
    params  = @{
      name      = $Name
      arguments = $Arguments
    }
  }
  $msg = ConvertFrom-McpSse -Raw $raw
  # StrictMode: successful MCP responses omit "error"; test the NoteProperty first.
  if ($null -ne $msg.PSObject.Properties['error'] -and $null -ne $msg.error) {
    throw "MCP tool $Name error: $($msg.error | ConvertTo-Json -Compress)"
  }
  $text = $msg.result.content[0].text
  return ($text | ConvertFrom-Json)
}

function Get-SourceVersions {
  $program = Get-Content (Join-Path $script:Issue70Root '..\..\src\RoslynMcpExtension.Server\Program.cs') -Raw
  $vsix = Get-Content (Join-Path $script:Issue70Root '..\..\src\RoslynMcpExtension\source.extension.vsixmanifest') -Raw
  $programVersion = $null
  foreach ($m in [regex]::Matches($program, 'Version\s*=\s*"([^"]+)"')) {
    if ($m.Groups[1].Value -match '^\d+\.\d+\.\d+') { $programVersion = $m.Groups[1].Value; break }
  }
  $vsixVersion = $null
  $identity = [regex]::Match($vsix, '<Identity\b[^>]*\bVersion="([^"]+)"')
  if ($identity.Success) { $vsixVersion = $identity.Groups[1].Value }
  return [pscustomobject]@{ ProgramCs = $programVersion; VsixManifest = $vsixVersion }
}

function Assert-BaselineArtifacts {
  $manifestPath = Join-Path $script:Issue70Root 'baseline\manifest.json'
  $initPath = Join-Path $script:Issue70Root 'baseline\mcp-initialize.sse'
  $toolsPath = Join-Path $script:Issue70Root 'baseline\mcp-tools-list.sse'
  foreach ($p in @($manifestPath, $initPath, $toolsPath)) {
    if (-not (Test-Path $p)) { throw "Missing baseline artifact: $p" }
  }
  $manifest = Read-JsonFile $manifestPath
  $initHash = Get-Sha256Hex $initPath
  $toolsHash = Get-Sha256Hex $toolsPath
  if ($manifest.artifacts.'mcp-initialize.sse'.sha256 -ne $initHash) {
    throw "Baseline mcp-initialize.sse hash mismatch."
  }
  if ($manifest.artifacts.'mcp-tools-list.sse'.sha256 -ne $toolsHash) {
    throw "Baseline mcp-tools-list.sse hash mismatch."
  }
  $versions = Get-SourceVersions
  if ($manifest.versions.initializeServerInfoVersion -ne $script:ExpectedVersion -or
      $versions.ProgramCs -ne $script:ExpectedVersion -or
      $versions.VsixManifest -ne $script:ExpectedVersion -or
      $manifest.versions.programCsVersion -ne $script:ExpectedVersion -or
      $manifest.versions.vsixManifestVersion -ne $script:ExpectedVersion) {
    throw "Version binding failed: require initialize/Program.cs/vsixmanifest = $($script:ExpectedVersion)."
  }

  $toolsMsg = ConvertFrom-McpSse -Raw (Get-Content -Raw $toolsPath)
  $toolNames = @($toolsMsg.result.tools | ForEach-Object { $_.name })
  if ($toolNames.Count -ne 9) {
    throw "Baseline must contain exactly 9 tools; found $($toolNames.Count)."
  }
  return [pscustomobject]@{
    Manifest = $manifest
    InitHash = $initHash
    ToolsHash = $toolsHash
    ToolNames = $toolNames
  }
}

function Initialize-Issue70Hive {
  if (-not (Test-Path $script:Devenv)) { throw "devenv.exe not found: $($script:Devenv)" }
  $initLog = Join-Path $script:EvidenceDir 'hive-init.log'
  $proc = Start-Process -FilePath $script:Devenv -ArgumentList @('/RootSuffix', $script:RootSuffix) -PassThru -WindowStyle Hidden
  $started = Get-Date
  # Allow hive folders/registry to materialize.
  $deadline = (Get-Date).AddSeconds(45)
  $hiveHit = $false
  while ((Get-Date) -lt $deadline) {
    $hives = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -like "*$($script:RootSuffix)" }
    if ($hives) { $hiveHit = $true; break }
    Start-Sleep -Seconds 2
  }
  Start-Sleep -Seconds 10
  if (-not $proc.HasExited) {
    try { $null = $proc.CloseMainWindow() } catch { Write-Verbose "CloseMainWindow failed: $_" }
    if (-not $proc.WaitForExit(60000)) {
      Stop-Process -Id $proc.Id -Force
      $proc.WaitForExit()
    }
  }
  $record = [ordered]@{
    purpose = 'hive-initialization'
    pid = $proc.Id
    startTime = $started.ToString('o')
    exitTime = (Get-Date).ToString('o')
    exitCode = $proc.ExitCode
    executable = $script:Devenv
    rootSuffix = $script:RootSuffix
    hiveFolderSeen = $hiveHit
  }
  Write-JsonFile -Object $record -Path $script:InitCheckpoint
  "Hive init PID $($proc.Id) exited; hiveFolderSeen=$hiveHit" | Set-Content $initLog
  return $record
}

function Get-Issue70HiveDirectory {
  $hit = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "18.*$($script:RootSuffix)" -or $_.Name -like "*_$($script:RootSuffix)" -or $_.Name -like "*$($script:RootSuffix)" } |
    Where-Object { $_.Name -ne $script:RootSuffix } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
  if (-not $hit) {
    throw "Issue70Exp hive directory not found under LocalAppData\Microsoft\VisualStudio."
  }
  return $hit.FullName
}

function Deploy-Issue70Probe {
  if (-not (Test-Path $script:MsBuild)) { throw "MSBuild not found: $($script:MsBuild)" }
  if (-not (Test-PortFree -Port $script:ProbePort)) {
    $owner = Get-PortOwnerPid -Port $script:ProbePort
    throw "Port $($script:ProbePort) is occupied by PID $owner. Stop that listener; do not reuse an unknown process."
  }

  $vsixOut = Join-Path (Split-Path $script:ExtensionProj) 'bin\Debug\net48\RoslynMcpExtension.vsix'
  $hiveDir = Get-Issue70HiveDirectory
  $deployPath = Join-Path $hiveDir "Extensions\DmitryLine\Roslyn MCP Extension\$($script:ExpectedVersion)"

  $msbuildArgs = @(
    $script:ExtensionProj,
    '/p:Configuration=Debug',
    '/p:DeployExtension=true',
    "/p:VSSDKTargetPlatformRegRootSuffix=$($script:RootSuffix)",
    "/p:VSINSTALLDIR=$($script:VsInstall)\",
    "/p:DevEnvDir=$($script:VsInstall)\Common7\IDE\",
    '/p:CreateVsixContainer=true',
    '/t:Rebuild',
    '/v:m'
  )
  $msbuildLog = Join-Path $script:EvidenceDir 'deploy-msbuild.log'
  & $script:MsBuild @msbuildArgs *> $msbuildLog
  $msbuildExit = $LASTEXITCODE

  if ($msbuildExit -ne 0) { throw "Experimental MSBuild deployment failed ($msbuildExit); inspect $msbuildLog before retrying." }

  if (-not (Test-Path $vsixOut)) { throw "VSIX missing after deploy build: $vsixOut" }
  if (-not (Test-Path (Join-Path $deployPath 'RoslynMcpExtension.dll'))) {
    throw "Experimental hive missing RoslynMcpExtension.dll at $deployPath"
  }

  $record = [ordered]@{
    deployedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    vsInstall = $script:VsInstall
    devenv = $script:Devenv
    rootSuffix = $script:RootSuffix
    hiveDirectory = $hiveDir
    project = $script:ExtensionProj
    vsixPath = $vsixOut
    deploymentHash = Get-Sha256Hex $vsixOut
    deploymentPath = $deployPath
    msbuildExitCode = $msbuildExit
    fixtureSolution = (Resolve-Path $script:FixtureSln).Path
  }
  Write-JsonFile -Object $record -Path $script:DeployCheckpoint
  return $record
}

function Start-Issue70Host {
  param(
    [switch]$AllowRedeploy
  )
  $deploy = $null
  if (Test-Path $script:DeployCheckpoint) {
    $deploy = Read-JsonFile $script:DeployCheckpoint
  }
  elseif ($AllowRedeploy) {
    $null = Initialize-Issue70Hive
    $deploy = Deploy-Issue70Probe
  }
  else {
    throw "No deployment checkpoint at $($script:DeployCheckpoint). Run PrepareCleanA first."
  }

  if (-not (Test-PortFree -Port $script:ProbePort)) {
    $owner = Get-PortOwnerPid -Port $script:ProbePort
    throw "Port $($script:ProbePort) is occupied by PID $owner before host launch."
  }

  $fixture = [string]$deploy.fixtureSolution
  if (-not (Test-Path $fixture)) { throw "Fixture solution missing: $fixture" }

  $devenvArgs = @('/RootSuffix', $script:RootSuffix, $fixture)
  $proc = Start-Process -FilePath $script:Devenv -ArgumentList $devenvArgs -PassThru -WindowStyle Hidden
  $record = [ordered]@{
    pid = $proc.Id
    startTime = (Get-Date).ToString('o')
    executable = $script:Devenv
    commandLine = "$($script:Devenv) $($devenvArgs -join ' ')"
    rootSuffix = $script:RootSuffix
    fixtureSolution = $fixture
    deploymentHash = $deploy.deploymentHash
    initPidExcluded = $true
  }
  Write-JsonFile -Object $record -Path $script:HostCheckpoint
  return $record
}

function Test-HostProcessAlive {
  param($HostRecord)
  if (-not $HostRecord) { return $false }
  try {
    $p = Get-Process -Id ([int]$HostRecord.pid) -ErrorAction Stop
    return $null -ne $p
  }
  catch { return $false }
}

function Ensure-Issue70Host {
  <#
  .SYNOPSIS
    Relaunch helper: restarts the verified deployment only when its recorded PID is dead.
    Does not kill unknown processes or silently redeploy.
  #>
  $hostRestarted = $false
  $hostRec = $null
  if (Test-Path $script:HostCheckpoint) {
    $hostRec = Read-JsonFile $script:HostCheckpoint
  }

  if ($hostRec -and (Test-HostProcessAlive -HostRecord $hostRec)) {
    return [pscustomobject]@{ Host = $hostRec; HostRestarted = $false }
  }

  if (-not (Test-Path $script:DeployCheckpoint)) {
    throw "Host PID dead/missing and no deployment checkpoint available for relaunch."
  }

  $deploy = Read-JsonFile $script:DeployCheckpoint
  $fixture = [string]$deploy.fixtureSolution
  $exe = [string]$deploy.devenv
  if (-not $exe) { $exe = $script:Devenv }
  if (-not (Test-Path $exe)) { throw "Saved devenv missing: $exe" }
  if (-not (Test-Path $fixture)) { throw "Saved fixture missing: $fixture" }

  if (-not (Test-PortFree -Port $script:ProbePort)) {
    $owner = Get-PortOwnerPid -Port $script:ProbePort
    throw "Port $($script:ProbePort) occupied by PID $owner during relaunch; refusing to attach/kill unknown listener."
  }

  $devenvArgs = @('/RootSuffix', $script:RootSuffix, $fixture)
  $proc = Start-Process -FilePath $exe -ArgumentList $devenvArgs -PassThru -WindowStyle Hidden
  $hostRec = [ordered]@{
    pid = $proc.Id
    startTime = (Get-Date).ToString('o')
    executable = $exe
    commandLine = "$exe $($devenvArgs -join ' ')"
    rootSuffix = $script:RootSuffix
    fixtureSolution = $fixture
    deploymentHash = $deploy.deploymentHash
    relaunched = $true
  }
  Write-JsonFile -Object $hostRec -Path $script:HostCheckpoint
  $hostRestarted = $true
  return [pscustomobject]@{ Host = $hostRec; HostRestarted = $hostRestarted }
}

function Wait-Issue70ProbeReady {
  param(
    [Parameter(Mandatory)]$HostRecord,
    [int]$TimeoutSeconds = 120
  )
  $endpoint = "http://localhost:$($script:ProbePort)/mcp"
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $lastError = $null
  while ((Get-Date) -lt $deadline) {
    try {
      $identity = Invoke-McpTool -Endpoint $endpoint -Name 'issue70_probe' -Arguments @{ operation = 'identity' } `
        -OutFile (Join-Path $script:EvidenceDir 'probe-identity.json.sse')
      if (-not $identity.requestSucceeded) { throw $identity.errorMessage }

      if ([int]$identity.processId -ne [int]$HostRecord.pid) {
        throw "Probe PID $($identity.processId) does not match host checkpoint PID $($HostRecord.pid)."
      }
      if ($identity.rootSuffix -ne $script:RootSuffix -and ("$($identity.registryRoot)" -notlike "*$($script:RootSuffix)*")) {
        throw "Probe hive mismatch. RegistryRoot=$($identity.registryRoot) RootSuffix=$($identity.rootSuffix)"
      }
      $expectedSln = [string]$HostRecord.fixtureSolution
      if (-not [string]::Equals($identity.solutionFullName, $expectedSln, [StringComparison]::OrdinalIgnoreCase)) {
        # Solution may still be loading.
        throw "Solution not ready. Got '$($identity.solutionFullName)', expected '$expectedSln'."
      }
      if (-not $identity.workspaceReady) {
        throw "Workspace not ready yet (projects=$($identity.projectCount), docs=$($identity.documentCount))."
      }
      return $identity
    }
    catch {
      $lastError = $_
      Start-Sleep -Seconds 3
    }
  }
  throw "Probe readiness timed out after $TimeoutSeconds seconds. Last error: $lastError"
}

function Restore-FixtureCleanA {
  $fx = Join-Path $script:Issue70Root 'Fixture'
  [IO.File]::WriteAllText((Join-Path $fx 'Consumer\marker.txt'), [IO.File]::ReadAllText((Join-Path $fx 'templates\marker.A.txt')))
  [IO.File]::WriteAllText((Join-Path $fx 'Consumer\GeneratedUsage.cs'), [IO.File]::ReadAllText((Join-Path $fx 'templates\Consumer.A.cs')))
}

function Read-ExpectedMembers {
  $path = Join-Path $script:Issue70Root 'Fixture\expected-members.json'
  $expected = Read-JsonFile $path
  $genSrc = Get-Content (Join-Path $script:Issue70Root 'Fixture\Generator\Issue70Generator.cs') -Raw
  $markerA = Get-Content (Join-Path $script:Issue70Root 'Fixture\templates\marker.A.txt') -Raw
  $markerB = Get-Content (Join-Path $script:Issue70Root 'Fixture\templates\marker.B.txt') -Raw
  $consumerA = Get-Content (Join-Path $script:Issue70Root 'Fixture\templates\Consumer.A.cs') -Raw
  $consumerB = Get-Content (Join-Path $script:Issue70Root 'Fixture\templates\Consumer.B.cs') -Raw
  $control = Get-Content (Join-Path $script:Issue70Root 'Fixture\Consumer\ControlApi.cs') -Raw

  if ($genSrc -notmatch 'MemberA' -or $genSrc -notmatch 'MemberB') {
    throw "Generator source must contain literal MemberA and MemberB."
  }
  if ($expected.cleanA -ne 'Issue70Fixture.GeneratedApi.MemberA') { throw "expected-members.cleanA mismatch" }
  if ($expected.B -ne 'Issue70Fixture.GeneratedApi.MemberB') { throw "expected-members.B mismatch" }
  if ($expected.control -ne 'Issue70Fixture.ControlApi.Stable') { throw "expected-members.control mismatch" }
  if ($consumerA -notmatch 'MemberA' -or $consumerB -notmatch 'MemberB') {
    throw "Consumer templates must reference MemberA/MemberB respectively."
  }
  if ($control -notmatch 'Stable') { throw "ControlApi.Stable missing." }
  if ($markerA.Trim() -ne 'A' -or $markerB.Trim() -ne 'B') { throw "marker templates must be A/B." }

  return $expected
}
