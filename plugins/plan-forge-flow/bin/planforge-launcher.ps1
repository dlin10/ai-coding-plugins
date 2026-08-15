$ErrorActionPreference = 'Stop'

$pluginRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Unsupported Plan Forge platform. Plan Forge 0.7.0 supports only Windows x64.'
}

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($architecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw "Unsupported Plan Forge architecture: $architecture. Plan Forge 0.7.0 supports only Windows x64."
}

$executable = Join-Path $pluginRoot 'bin/win-x64/planforge.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Plan Forge executable is missing: $executable"
}

if ($MyInvocation.ExpectingInput) {
    $input | & $executable @args
}
else {
    & $executable @args
}
exit $LASTEXITCODE
