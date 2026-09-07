#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/scripts/Issue70.Helpers.ps1"
$fixture = Join-Path $PSScriptRoot 'Fixture'
$endpoint = 'http://localhost:5062/mcp'

function Capture([string]$operation, [string]$path) {
    $r = Invoke-McpTool -Endpoint $endpoint -Name issue70_probe -Arguments @{operation=$operation} -OutFile $path
    if (!$r.requestSucceeded) { throw ($r | ConvertTo-Json -Depth 10) }
    return $r
}
function Converge {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $r = Capture workspace_text (Join-Path $trialPath 'workspace-current.sse')
        $matches = @($r.workspaceTexts | Where-Object {
            $_.fromWorkspaceDocument -and $_.content -ceq [IO.File]::ReadAllText($_.filePath)
        })
        if ($matches.Count -eq 3) { return }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'workspace-text-not-observed'
}
function Checkpoint([string]$stage) {
    $files = @{}
    Get-ChildItem $trialPath -File -Recurse | Where-Object Name -notin @('checkpoint.json','checkpoint.tmp','workspace-current.sse') | ForEach-Object {
        $files[[IO.Path]::GetRelativePath($trialPath,$_.FullName)] = Get-Sha256Hex $_.FullName
    }
    $record = @{trial=$trial; stage=$stage; host=Read-JsonFile $script:HostCheckpoint; artifacts=$files}
    Write-JsonFile $record (Join-Path $trialPath 'checkpoint.tmp')
    Move-Item -LiteralPath (Join-Path $trialPath 'checkpoint.tmp') -Destination (Join-Path $trialPath 'checkpoint.json') -Force
}

for ($trial=1; $trial -le 3; $trial++) {
    $trialPath = Join-Path $PSScriptRoot "trials/trial-$trial"
    if (Test-Path $trialPath) {
        $existing = if(Test-Path "$trialPath/checkpoint.json") { Read-JsonFile "$trialPath/checkpoint.json" } else { @{stage="incomplete"} }
        if ($existing.stage -eq 'complete') {
            & "$PSScriptRoot/verify-evidence.ps1" -TrialPath $trialPath
            if ($LASTEXITCODE -ne 0) { throw 'Completed trial evidence failed verification' }
            continue
        }
        $archive = "$trialPath-incomplete-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
        if (![IO.Path]::GetFullPath($archive).StartsWith([IO.Path]::GetFullPath("$PSScriptRoot/trials/"))) { throw 'Archive outside trials' }
        Move-Item -LiteralPath $trialPath -Destination $archive
    }
    New-Item -ItemType Directory "$trialPath/clean-a" -Force | Out-Null
    Restore-FixtureCleanA
    Converge
    $null = Capture build "$trialPath/clean-a/build.sse"
    # Ordinary validation/compilation first; no refresh operation in this baseline.
    $readyDeadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        $a = Invoke-McpTool -Endpoint $endpoint -Name roslyn_validate_file -Arguments @{filePath="$fixture/Consumer/GeneratedUsage.cs"} -OutFile "$trialPath/clean-a/validation.sse"
        if($a.success) { break }
        Start-Sleep -Milliseconds 300
    } while([DateTime]::UtcNow -lt $readyDeadline)
    if (!$a.success) { throw 'Clean A has errors' }
    $generated = Invoke-McpTool -Endpoint $endpoint -Name issue70_probe -Arguments @{operation='generated_documents';expectedMemberFullName='Issue70Fixture.GeneratedApi.MemberA'} -OutFile "$trialPath/clean-a/generated.sse"
    if (!$generated.requestSucceeded -or !$generated.matchingGeneratedDocument -or !$generated.matchingMember) { throw 'Missing generated clean A' }
    Checkpoint clean-a
    [IO.File]::WriteAllText("$fixture/Consumer/marker.txt", [IO.File]::ReadAllText("$fixture/templates/marker.B.txt"))
    [IO.File]::WriteAllText("$fixture/Consumer/GeneratedUsage.cs", [IO.File]::ReadAllText("$fixture/templates/Consumer.B.cs"))
    Converge
    dotnet build "$fixture/Fixture.sln" --no-restore *> "$trialPath/cli-b.log"
    $code=$LASTEXITCODE
    Write-JsonFile @{exitCode=$code; at=[DateTimeOffset]::UtcNow.ToString('o')} "$trialPath/cli-b.json"
    if ($code -ne 0) { throw 'CLI B failed' }
    Checkpoint cli-b
    $null=Capture compare "$trialPath/comparison.sse"
    Checkpoint comparison
    $null=Capture build "$trialPath/positive-build.sse"
    $positive=Invoke-McpTool -Endpoint $endpoint -Name roslyn_validate_file -Arguments @{filePath="$fixture/Consumer/GeneratedUsage.cs"} -OutFile "$trialPath/positive-validation.sse"
    if (!$positive.success) { throw 'Positive IDE control failed' }
    Checkpoint positive
    Add-Content "$fixture/Consumer/GeneratedUsage.cs" "`npublic class IntentionalError { int Fail() => UnknownIssue70Name; }"
    Converge
    dotnet build "$fixture/Fixture.sln" --no-restore *> "$trialPath/cli-error.log"
    $code=$LASTEXITCODE
    Write-JsonFile @{exitCode=$code; at=[DateTimeOffset]::UtcNow.ToString('o')} "$trialPath/cli-error.json"
    if ($code -eq 0) { throw 'Expected CLI control failure absent' }
    $null=Capture compare "$trialPath/error-comparison.sse"
    [IO.File]::WriteAllText("$fixture/Consumer/GeneratedUsage.cs", [IO.File]::ReadAllText("$fixture/templates/Consumer.B.cs"))
    Checkpoint complete
    & "$PSScriptRoot/verify-evidence.ps1" -TrialPath $trialPath
    if ($LASTEXITCODE -ne 0) { throw "Trial $trial evidence rejected" }
    Write-Host "Trial $trial verified"
}
Restore-FixtureCleanA
& "$PSScriptRoot/verify-evidence.ps1"
exit $LASTEXITCODE
