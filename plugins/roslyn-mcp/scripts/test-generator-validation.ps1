param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$plugin = Split-Path $PSScriptRoot -Parent
& "$plugin/extension/experiments/issue-70/verify-evidence.ps1"
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$outcome = Get-Content -Raw "$plugin/extension/experiments/issue-70/validated-outcome.json" | ConvertFrom-Json
if($outcome.schemaVersion -ne 1 -or $outcome.outcome -notin @('not-reproduced','refresh-did-not-work','refresh-too-slow')) { throw 'This implementation requires a verified fallback outcome' }
if(!$NoBuild) {
    $vs = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
    & "$vs/MSBuild/Current/Bin/MSBuild.exe" "$plugin/extension/src/RoslynMcpExtension.slnx" /p:Configuration=Release /restore /v:q
    if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$dll="$plugin/extension/src/RoslynMcpExtension.Tests/bin/Release/net48/RoslynMcpExtension.Tests.dll"
$shared=[Reflection.Assembly]::LoadFrom("$plugin/extension/src/RoslynMcpExtension.Tests/bin/Release/net48/RoslynMcpExtension.Shared.dll")
$type=$shared.GetType('RoslynMcpExtension.Shared.ValidateFileResult')
$expected=@('Success','FilePath','ProjectName','Errors','Warnings','AnalyzerDiagnostics','RequestSucceeded','ErrorCode','ErrorMessage','SourceGeneratedDocumentCount')
if(Compare-Object ($type.GetProperties().Name | Sort-Object) ($expected | Sort-Object)) { throw 'Unexpected result property shape' }
if($type.GetProperty('SourceGeneratedDocumentCount').PropertyType -ne [Nullable[int]]) { throw 'Count must be nullable int' }
$names = & dotnet test $dll --list-tests --filter FullyQualifiedName~ValidateFileGeneratorTests
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if(@($names | Select-String 'ValidateFileGeneratorTests\.').Count -lt 12) { throw 'Missing generator regression tests' }
& dotnet test $dll --filter FullyQualifiedName~ValidateFileGeneratorTests
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$latency=Join-Path (Split-Path $dll) 'issue-70/validation-latency.json'
$measurements=Get-Content -Raw $latency | ConvertFrom-Json
foreach($scenario in @('fault','timeout')) {
    $entry=@($measurements | Where-Object scenario -eq $scenario)
    if($entry.Count -ne 1 -or $entry[0].elapsedMilliseconds -gt 5000) { throw "Missing or over-budget $scenario test-host measurement (5s validation/scheduler ceiling; optional test budget 100ms)" }
}
Write-Host "Latency report: $latency"
