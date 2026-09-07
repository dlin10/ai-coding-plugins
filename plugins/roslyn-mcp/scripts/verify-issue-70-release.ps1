$ErrorActionPreference = 'Stop'
$plugin = Split-Path $PSScriptRoot -Parent
$vs = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
& "$vs/MSBuild/Current/Bin/MSBuild.exe" "$plugin/extension/src/RoslynMcpExtension.slnx" /p:Configuration=Release /t:Rebuild /restore /v:q
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot/test-generator-validation.ps1" -NoBuild
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$vsix="$plugin/extension/src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix"
$bundle="$plugin/assets/RoslynMcpExtension.vsix"
$previousBundleHash = (Get-FileHash $bundle).Hash
Copy-Item -LiteralPath $vsix -Destination $bundle -Force
Write-Host ('Bundled VSIX changed from previous artifact: ' + ($previousBundleHash -ne (Get-FileHash $bundle).Hash))
if((Get-FileHash $vsix).Hash -ne (Get-FileHash $bundle).Hash) { throw 'Bundled VSIX differs from fresh Release build' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $reader=[IO.StreamReader]::new($archive.GetEntry('extension.vsixmanifest').Open())
    try { [xml]$manifest=$reader.ReadToEnd() } finally { $reader.Dispose() }
    if($manifest.PackageManifest.Metadata.Identity.Version -ne '1.8.1') { throw 'Wrong packaged version' }
} finally { $archive.Dispose() }
$testDll="$plugin/extension/src/RoslynMcpExtension.Server.Tests/bin/Release/net10.0/RoslynMcpExtension.Server.Tests.dll"
$tests=& dotnet test $testDll --list-tests
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if(!($tests | Select-String 'PackagedValidationTests.PackagedServerPreservesSchemasAndCountWireValues')) { throw 'Packaged integration test missing' }
foreach($dll in @("$plugin/extension/src/RoslynMcpExtension.Tests/bin/Release/net48/RoslynMcpExtension.Tests.dll",$testDll)) {
    & dotnet test $dll
    if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
foreach($doc in @('README.md','extension/README.md','skills/roslyn-first/SKILL.md')) {
    $text=Get-Content -Raw "$plugin/$doc"
    foreach($phrase in @('source generators','IDE build','dotnet build','sourceGeneratedDocumentCount','not a freshness signal','10-second','without reproducing')) {
        if(!$text.Contains($phrase)) { throw "Missing documentation '$phrase' in $doc" }
    }
}
if((Get-Content "$plugin/README.md" -First 1) -ne '# Roslyn MCP 0.8.1') { throw 'Wrong plugin README version' }
& "$plugin/extension/experiments/issue-70/verify-evidence.ps1"
exit $LASTEXITCODE
