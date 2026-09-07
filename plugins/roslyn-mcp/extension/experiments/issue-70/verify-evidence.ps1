#Requires -Version 7.0
param([string]$TrialPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/scripts/Issue70.Helpers.ps1"
function Require($condition,[string]$reason) { if (!$condition) { throw $reason } }
function Json([string]$path) { Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable }
function Response([string]$path) {
    $msg=ConvertFrom-McpSse (Get-Content -Raw -LiteralPath $path)
    $r=$msg.result.content[0].text | ConvertFrom-Json -AsHashtable
    Require $r.requestSucceeded "Request failed: $path"
    return $r
}
function HashText([string]$text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant() }
function Comparison([string]$path,[bool]$errorControl) {
    $r=Response $path
    Require ($r.balancedMode -and $r.sourceGeneratorExecution -eq 'Balanced') 'Mode is not Balanced'
    Require ($r.rootSuffix -eq 'Issue70Exp') 'Wrong hive'
    $c=$r.evidence.comparison | ConvertFrom-Json -AsHashtable
    Require ($c.baselineStarted -le $c.candidateStarted) 'Candidate precedes baseline'
    foreach($source in $c.sources) {
        Require ((HashText $source.content) -eq $source.sha256 -and $source.sha256 -eq $source.diskSha256) 'Workspace/disk hash mismatch'
        Require ($source.documentId -and $source.version) 'Missing source identity'
    }
    $usage=@($c.sources | Where-Object path -like '*GeneratedUsage.cs')
    $marker=@($c.sources | Where-Object path -like '*marker.txt')
    Require ($usage.Count -eq 1 -and $usage[0].content.Contains('MemberB') -and $marker[0].content.Trim() -eq 'B') 'B input missing'
    Require ($usage[0].content.Contains('UnknownIssue70Name') -eq $errorControl) 'Incorrect control stage'
    foreach($snapshot in @($c.before,$c.after)) {
        if (!$snapshot) { continue }
        foreach($generated in $snapshot.generated) {
            Require ((HashText $generated.content) -eq $generated.sha256) 'Generated hash mismatch'
            Require (@($c.sources | Where-Object path -eq $generated.path).Count -eq 0) 'Ordinary source masquerades as generated'
        }
    }
    if($errorControl) {
        Require (@($c.before.errors | Where-Object message -like '*UnknownIssue70Name*').Count -gt 0) 'Real baseline error missing'
        if($c.after) { Require (@($c.after.errors | Where-Object message -like '*UnknownIssue70Name*').Count -gt 0) 'Candidate suppressed real error' }
    }
    return @{response=$r; comparison=$c}
}
function VerifyTrial([string]$path) {
    $checkpoint=Json "$path/checkpoint.json"
    Require ($checkpoint.stage -eq 'complete') 'Incomplete trial'
    foreach($entry in $checkpoint.artifacts.GetEnumerator()) {
        Require ((Get-Sha256Hex (Join-Path $path $entry.Key)) -eq $entry.Value) "Artifact mismatch: $($entry.Key)"
    }
    $a=Response "$path/clean-a/generated.sse"
    $aValidation=Response "$path/clean-a/validation.sse"
    Require $aValidation.success 'Clean A compiler errors'
    Require ($a.matchingMember.fullName -eq 'Issue70Fixture.GeneratedApi.MemberA') 'Missing resolved A member'
    Require ($a.matchingGeneratedDocument.content.Contains('MemberA')) 'Missing generated A'
    Require ((HashText $a.matchingGeneratedDocument.content) -eq $a.matchingGeneratedDocument.sha256) 'Clean A generated hash mismatch'
    $buildA=Response "$path/clean-a/build.sse"
    $positive=Response "$path/positive-build.sse"
    $validB=Response "$path/positive-validation.sse"
    foreach($build in @($buildA,$positive)) { Require ($build.buildSucceeded -and $build.lastBuildInfo -eq 0 -and $build.buildStartEvent -and $build.buildEndEvent) 'IDE build proof missing' }
    Require $validB.success 'IDE positive control failed'
    $cli=Json "$path/cli-b.json"
    $cliError=Json "$path/cli-error.json"
    Require ($cli.exitCode -eq 0 -and $cliError.exitCode -ne 0) 'CLI controls incorrect'
    $main=Comparison "$path/comparison.sse" $false
    $control=Comparison "$path/error-comparison.sse" $true
    $c=$main.comparison
    Require ($buildA.buildEndEvent -le $cli.at -and $cli.at -le $c.baselineStarted -and $c.candidateStarted -le $positive.buildStartEvent -and $positive.buildEndEvent -le $cliError.at -and $cliError.at -le $control.comparison.baselineStarted) 'Invalid stage ordering'
    Require ($main.response.processId -eq $control.response.processId -and $a.processId -eq $main.response.processId) 'Trial host changed'
    $stale=@($c.before.errors | Where-Object { $_.message -like '*MemberB*' }).Count -gt 0
    $worked=$stale -and $c.after -and @($c.after.errors).Count -eq 0 -and $c.after.memberB -and !$c.failure -and !$c.timedOut
    return @{trial=$checkpoint.trial; stale=$stale; worked=[bool]$worked; timedOut=($c.timedOut -or $control.comparison.timedOut); candidateMs=$c.candidateMs; baselineMs=$c.baselineMs; artifacts=$checkpoint.artifacts}
}
try {
    if(!$TrialPath -and (Test-Path "$PSScriptRoot/validated-outcome.json")) { Remove-Item -LiteralPath "$PSScriptRoot/validated-outcome.json" }
    $null=Read-ExpectedMembers
    $protocol=Json "$PSScriptRoot/protocol-manifest.json"
    foreach($file in $protocol.files.GetEnumerator()) {
        Require ((Get-Sha256Hex (Join-Path $PSScriptRoot $file.Key)) -eq $file.Value) "Measured protocol changed: $($file.Key); repeat the experiment"
    }
    $baseline=Json "$PSScriptRoot/baseline/manifest.json"
    foreach($file in $baseline.artifacts.GetEnumerator()) {
        Require ((Get-Sha256Hex (Join-Path "$PSScriptRoot/baseline" $file.Key)) -eq $file.Value.sha256) 'Baseline schema artifact changed'
    }
    $init=ConvertFrom-McpSse (Get-Content -Raw "$PSScriptRoot/baseline/mcp-initialize.sse")
    Require ($init.result.serverInfo.version -eq '1.8.0' -and $baseline.versions.programCsVersion -eq '1.8.0' -and $baseline.versions.vsixManifestVersion -eq '1.8.0') 'Pre-change server version mismatch'
    if($TrialPath) { $null=VerifyTrial $TrialPath; Write-Host 'Trial evidence passed'; exit 0 }
    $trials=@(1..3 | ForEach-Object { VerifyTrial "$PSScriptRoot/trials/trial-$_" })
    Require ((Get-Sha256Hex "$PSScriptRoot/Fixture/Consumer/marker.txt") -eq (Get-Sha256Hex "$PSScriptRoot/Fixture/templates/marker.A.txt")) 'Restore fixture A marker'
    Require ((Get-Sha256Hex "$PSScriptRoot/Fixture/Consumer/GeneratedUsage.cs") -eq (Get-Sha256Hex "$PSScriptRoot/Fixture/templates/Consumer.A.cs")) 'Restore fixture A consumer'
    $outcome=if(@($trials | Where-Object timedOut).Count) {'refresh-too-slow'} elseif(@($trials | Where-Object stale).Count -eq 0) {'not-reproduced'} elseif(@($trials | Where-Object worked).Count -eq 3) {'refresh-worked'} else {'refresh-did-not-work'}
    Write-JsonFile @{outcome=$outcome;trials=$trials} "$PSScriptRoot/evidence.json"
    $result=@{schemaVersion=1;outcome=$outcome;trials=$trials;verifiedAt=[DateTimeOffset]::UtcNow.ToString('o')}
    Write-JsonFile $result "$PSScriptRoot/validated-outcome.tmp"
    Move-Item -LiteralPath "$PSScriptRoot/validated-outcome.tmp" -Destination "$PSScriptRoot/validated-outcome.json" -Force
    Write-Host "Verified outcome: $outcome"
    exit 0
} catch { Write-Error $_; exit 1 }
