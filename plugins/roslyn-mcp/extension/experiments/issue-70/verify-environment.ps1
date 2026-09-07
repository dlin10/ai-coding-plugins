#Requires -Version 5.1
<#
.SYNOPSIS
  Issue70 live-VS environment gate (PrepareCleanA / IdentityOnly).

.PARAMETER IdentityOnly
  Verify baseline hashes, process/deployment/hive/solution identity and Balanced mode.
  Does not edit fixture files, build, or request compilation/generated documents.

.PARAMETER PrepareCleanA
  Default mode. Restores versioned A inputs, converges workspace, IDE-builds via probe,
  requires generated MemberA tree, writes environment.json.
#>
[CmdletBinding(DefaultParameterSetName = 'PrepareCleanA')]
param(
  [Parameter(ParameterSetName = 'PrepareCleanA')]
  [switch]$PrepareCleanA,

  [Parameter(ParameterSetName = 'IdentityOnly')]
  [switch]$IdentityOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
. (Join-Path $here 'scripts\Issue70.Helpers.ps1')

# Default parameter set is PrepareCleanA even when switch omitted.
if ($PSCmdlet.ParameterSetName -eq 'PrepareCleanA') {
  $PrepareCleanA = $true
}
if ($PrepareCleanA -and $IdentityOnly) {
  throw "PrepareCleanA and IdentityOnly are mutually exclusive."
}

function Emit-MachineResult {
  param([hashtable]$Object)
  $Object | ConvertTo-Json -Depth 40 -Compress
}

try {
  if (-not (Test-Path (Join-Path $script:Issue70Root '../../src/RoslynMcpExtension/Services/Issue70ProbeService.cs'))) {
    throw 'Live environment gate retired after probe removal. Use offline verify-evidence.ps1; see RESULTS.md for explicit re-preparation.'
  }
  New-Item -ItemType Directory -Force -Path $script:CheckpointDir, $script:EvidenceDir | Out-Null

  $baseline = Assert-BaselineArtifacts
  $expected = Read-ExpectedMembers
  $endpoint5062 = "http://localhost:$($script:ProbePort)/mcp"

  # Ensure fixture-local port config (ignored).
  $cfg = Join-Path $script:Issue70Root 'Fixture\.roslynmcp.json'
  if (-not (Test-Path $cfg)) {
    '{ "port": 5062 }' | Set-Content -Path $cfg -Encoding utf8
  }

  if ($IdentityOnly) {
    $envPath = Join-Path $script:Issue70Root 'environment.json'
    if (-not (Test-Path $envPath)) {
      Write-ResultsAction "environment.json missing. Run verify-environment.ps1 (PrepareCleanA) once before IdentityOnly."
      throw "environment.json missing for IdentityOnly."
    }
    $env = Read-JsonFile $envPath

    $ensure = Ensure-Issue70Host
    $hostRec = $ensure.Host
    $identity = Wait-Issue70ProbeReady -HostRecord $hostRec -TimeoutSeconds 120

    $toolsRaw = Invoke-McpRaw -Endpoint $endpoint5062 -OutFile (Join-Path $script:EvidenceDir 'identityonly-tools-list.sse') -Payload @{
      jsonrpc = '2.0'; id = 1; method = 'tools/list'; params = @{}
    }
    $toolsMsg = ConvertFrom-McpSse -Raw $toolsRaw
    $names = @($toolsMsg.result.tools | ForEach-Object { $_.name })
    if ($names -notcontains 'roslyn_validate_file' -or $names -notcontains 'issue70_probe') {
      throw "Port 5062 tools/list missing ordinary or probe tools."
    }

    $genState = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
      -Arguments @{ operation = 'generator_state' } `
      -OutFile (Join-Path $script:EvidenceDir 'identityonly-generator-state.sse')
    if (-not $genState.requestSucceeded) { throw "generator_state failed: $($genState.errorMessage)" }
    if (-not $genState.balancedMode -or $genState.sourceGeneratorExecution -ne 'Balanced') {
      Write-ResultsAction "Effective SourceGeneratorExecution is '$($genState.sourceGeneratorExecution)'. $($genState.manualBalancedAction)"
      throw "Balanced mode required; unresolved/unsupported mode is blocked."
    }

    # Validate retained preparation clean-A hashes (do not rebuild/regenerate).
    if (-not $env.cleanA -or -not $env.cleanA.generatedDocumentSha256) {
      throw "environment.json missing clean-A generated document hash."
    }

    $result = [ordered]@{
      mode = 'IdentityOnly'
      ok = $true
      hostRestarted = [bool]$ensure.HostRestarted
      processId = $identity.processId
      registryRoot = $identity.registryRoot
      rootSuffix = $identity.rootSuffix
      solutionFullName = $identity.solutionFullName
      deploymentHash = $hostRec.deploymentHash
      sourceGeneratorExecution = $genState.sourceGeneratorExecution
      baselineInitSha256 = $baseline.InitHash
      baselineToolsSha256 = $baseline.ToolsHash
      retainedCleanAGeneratedSha256 = $env.cleanA.generatedDocumentSha256
      retainedCleanAMember = $env.cleanA.expectedMember
    }
    Emit-MachineResult $result
    exit 0
  }

  # -------------------- PrepareCleanA --------------------
  # Port 5062 must be free before a new launch. An already-verified Issue70Exp host whose
  # probe identity matches the host checkpoint may keep its owned listener.
  if (-not (Test-Path $script:DeployCheckpoint)) {
    if (-not (Test-PortFree -Port $script:ProbePort)) {
      $owner = Get-PortOwnerPid -Port $script:ProbePort
      Write-ResultsAction "Port $($script:ProbePort) is occupied by PID $owner. Stop that listener and retry. Do not reuse an unknown process."
      throw "Port $($script:ProbePort) occupied."
    }
    Write-Verbose "Initializing Issue70Exp hive..."
    $null = Initialize-Issue70Hive
    Write-Verbose "Deploying probe into Issue70Exp only..."
    $null = Deploy-Issue70Probe
    Write-Verbose "Launching experimental host with Fixture.sln..."
    $null = Start-Issue70Host
  }
  else {
    if (-not (Test-PortFree -Port $script:ProbePort)) {
      $owner = Get-PortOwnerPid -Port $script:ProbePort
      $owned = $false
      if (Test-Path $script:HostCheckpoint) {
        $existingHost = Read-JsonFile $script:HostCheckpoint
        if (Test-HostProcessAlive -HostRecord $existingHost) {
          try {
            $probeId = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
              -Arguments @{ operation = 'identity' } `
              -OutFile (Join-Path $script:EvidenceDir 'prepare-port-owner-identity.sse')
            if ($probeId.requestSucceeded -and [int]$probeId.processId -eq [int]$existingHost.pid) {
              $owned = $true
            }
          }
          catch {
            $owned = $false
          }
        }
      }
      if (-not $owned) {
        Write-ResultsAction "Port $($script:ProbePort) is occupied by PID $owner. Stop that listener and retry. Do not reuse an unknown process."
        throw "Port $($script:ProbePort) occupied."
      }
    }
    $ensure = Ensure-Issue70Host
    if (-not $ensure.Host) { throw "Failed to ensure Issue70 host." }
  }

  $hostRec = Read-JsonFile $script:HostCheckpoint
  # Reject reusing pre-deployment initialization PID.
  if (Test-Path $script:InitCheckpoint) {
    $init = Read-JsonFile $script:InitCheckpoint
    if ([int]$hostRec.pid -eq [int]$init.pid) {
      Write-ResultsAction "Host PID equals hive-init PID $($init.pid). Launch a fresh detached devenv after deployment."
      throw "Host PID must not reuse initialization PID."
    }
  }

  $identity = Wait-Issue70ProbeReady -HostRecord $hostRec -TimeoutSeconds 120

  # Ordinary tools/list on probe port + probe response.
  $toolsRaw = Invoke-McpRaw -Endpoint $endpoint5062 -OutFile (Join-Path $script:EvidenceDir 'prepare-tools-list.sse') -Payload @{
    jsonrpc = '2.0'; id = 1; method = 'tools/list'; params = @{}
  }
  $toolsMsg = ConvertFrom-McpSse -Raw $toolsRaw
  $names = @($toolsMsg.result.tools | ForEach-Object { $_.name })
  foreach ($required in $baseline.ToolNames) {
    if ($names -notcontains $required) { throw "Probe port missing ordinary tool $required" }
  }
  if ($names -notcontains 'issue70_probe') { throw "Probe port missing issue70_probe tool." }

  # Ensure Balanced via experimental settings API; fail gate if unsupported.
  $balanced = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
    -Arguments @{ operation = 'ensure_balanced' } `
    -OutFile (Join-Path $script:EvidenceDir 'prepare-ensure-balanced.sse')
  if (-not $balanced.requestSucceeded -or -not $balanced.balancedMode) {
    $action = $balanced.manualBalancedAction
    if (-not $action) { $action = "Open Issue70Exp Tools > Options > Text Editor > C# > Advanced and set source generator execution to Balanced." }
    Write-ResultsAction "$action`nProbe error: $($balanced.errorMessage)`nSettingsAction: $($balanced.settingsAction)"
    throw "Unable to establish Balanced source-generator mode on this VS version."
  }

  # Restore A inputs and wait for workspace text convergence (30s).
  Restore-FixtureCleanA
  $deadline = (Get-Date).AddSeconds(30)
  $markerOk = $false
  $workspaceSnap = $null
  while ((Get-Date) -lt $deadline) {
    $workspaceSnap = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
      -Arguments @{ operation = 'workspace_text' } `
      -OutFile (Join-Path $script:EvidenceDir 'prepare-workspace-text.sse')
    $marker = @($workspaceSnap.workspaceTexts | Where-Object { $_.relativePath -eq 'marker.txt' } | Select-Object -First 1)
    $usage = @($workspaceSnap.workspaceTexts | Where-Object { $_.relativePath -eq 'GeneratedUsage.cs' } | Select-Object -First 1)
    if ($marker -and $usage -and $marker[0].content.Trim() -eq 'A' -and ($usage[0].content -match 'MemberA')) {
      $markerOk = $true
      break
    }
    Start-Sleep -Seconds 2
  }
  if (-not $markerOk) {
    Write-ResultsAction "Workspace did not converge to clean-A marker/MemberA within 30 seconds."
    throw "Clean-A workspace convergence failed."
  }

  $build = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
    -Arguments @{ operation = 'build' } `
    -OutFile (Join-Path $script:EvidenceDir 'prepare-build.sse')
  if (-not $build.requestSucceeded -or -not $build.buildSucceeded -or $build.lastBuildInfo -ne 0) {
    Write-ResultsAction "IDE build failed or timed out. LastBuildInfo=$($build.lastBuildInfo) Timeout=$($build.buildTimedOut) Error=$($build.errorMessage)"
    throw "Clean-A IDE build failed."
  }

  $generated = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
    -Arguments @{ operation = 'generated_documents'; expectedMemberFullName = $expected.cleanA } `
    -OutFile (Join-Path $script:EvidenceDir 'prepare-generated-documents.sse')
  if (-not $generated.requestSucceeded) {
    throw "generated_documents probe failed: $($generated.errorMessage)"
  }
  if (-not $generated.generatedDocuments -or @($generated.generatedDocuments).Count -lt 1) {
    Write-ResultsAction "No generated trees after clean-A build. Ordinary source stand-ins are not accepted."
    throw "Empty generated-tree set fails preparation."
  }
  if (-not $generated.matchingGeneratedDocument) {
    Write-ResultsAction "Generated trees present but none contain expected member $($expected.cleanA)."
    throw "Expected generated member missing."
  }
  if (-not $generated.matchingMember -or $generated.matchingMember.fullName -ne $expected.cleanA) {
    Write-ResultsAction "Resolved member mismatch for $($expected.cleanA)."
    throw "Matching resolved member missing."
  }

  # Reject ordinary-source stand-in: Matching content must look generated and not equal consumer source.
  $consumerPath = Join-Path $script:Issue70Root 'Fixture\Consumer\GeneratedUsage.cs'
  $consumerText = Get-Content -Raw $consumerPath
  if ($generated.matchingGeneratedDocument.content.Trim() -eq $consumerText.Trim()) {
    throw "Generated document content equals consumer source stand-in; failing preparation."
  }

  $genStateFinal = Invoke-McpTool -Endpoint $endpoint5062 -Name 'issue70_probe' `
    -Arguments @{ operation = 'generator_state' } `
    -OutFile (Join-Path $script:EvidenceDir 'prepare-generator-state-final.sse')

  $environment = [ordered]@{
    preparedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    mode = 'PrepareCleanA'
    endpoint = $endpoint5062
    baseline = @{
      endpoint = $baseline.Manifest.endpoint
      initSha256 = $baseline.InitHash
      toolsSha256 = $baseline.ToolsHash
      toolCount = 9
      versions = $baseline.Manifest.versions
    }
    host = @{
      processId = $identity.processId
      startTime = $hostRec.startTime
      executable = $hostRec.executable
      commandLine = $hostRec.commandLine
      rootSuffix = $identity.rootSuffix
      registryRoot = $identity.registryRoot
      solutionFullName = $identity.solutionFullName
      deploymentHash = $hostRec.deploymentHash
    }
    generatorMode = @{
      sourceGeneratorExecution = $genStateFinal.sourceGeneratorExecution
      balancedMode = [bool]$genStateFinal.balancedMode
      workspacesAssemblyIdentity = $genStateFinal.workspacesAssemblyIdentity
      workspaceConfigurationServiceType = $genStateFinal.workspaceConfigurationServiceType
      sourceGeneratorExecutionEnumType = $genStateFinal.sourceGeneratorExecutionEnumType
      settingsKey = $balanced.settingsKey
      settingsAction = $balanced.settingsAction
      settingsKeyExisted = $null
      settingsPriorValue = $null
      rawEvidencePath = 'evidence/prepare-generator-state-final.sse'
    }
    cleanA = @{
      expectedMember = $expected.cleanA
      controlMember = $expected.control
      generatedDocumentName = $generated.matchingGeneratedDocument.hintName
      generatedDocumentSha256 = $generated.matchingGeneratedDocument.sha256
      generatedDocumentContent = $generated.matchingGeneratedDocument.content
      matchingMember = $generated.matchingMember
      buildLastBuildInfo = $build.lastBuildInfo
      workspaceMarkerSha256 = @($workspaceSnap.workspaceTexts | Where-Object { $_.relativePath -eq 'marker.txt' })[0].sha256
      workspaceUsageSha256 = @($workspaceSnap.workspaceTexts | Where-Object { $_.relativePath -eq 'GeneratedUsage.cs' })[0].sha256
      rawResponsePaths = @{
        identity = 'evidence/probe-identity.json.sse'
        toolsList = 'evidence/prepare-tools-list.sse'
        ensureBalanced = 'evidence/prepare-ensure-balanced.sse'
        workspaceText = 'evidence/prepare-workspace-text.sse'
        build = 'evidence/prepare-build.sse'
        generatedDocuments = 'evidence/prepare-generated-documents.sse'
      }
    }
    probe = @{
      version = $identity.probeVersion
      assemblyHash = $identity.probeAssemblyHash
    }
  }

  Write-JsonFile -Object $environment -Path (Join-Path $script:Issue70Root 'environment.json')

  $result = [ordered]@{
    mode = 'PrepareCleanA'
    ok = $true
    hostRestarted = $false
    processId = $identity.processId
    rootSuffix = $identity.rootSuffix
    expectedMember = $expected.cleanA
    generatedDocumentSha256 = $generated.matchingGeneratedDocument.sha256
    sourceGeneratorExecution = $genStateFinal.sourceGeneratorExecution
    environmentPath = (Join-Path $script:Issue70Root 'environment.json')
  }
  Emit-MachineResult $result
  exit 0
}
catch {
  $msg = $_.Exception.Message
  Write-ResultsAction $msg
  Write-Error $msg
  Emit-MachineResult @{ mode = $(if ($IdentityOnly) { 'IdentityOnly' } else { 'PrepareCleanA' }); ok = $false; error = $msg }
  exit 1
}
