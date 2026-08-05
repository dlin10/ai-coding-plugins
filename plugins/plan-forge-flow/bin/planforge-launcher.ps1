$ErrorActionPreference = 'Stop'

$pluginRoot = Split-Path -Parent $PSScriptRoot
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The PowerShell Plan Forge launcher is only for Windows hosts.'
}

$rid = switch ($architecture) {
    'x64' { 'win-x64' }
    'arm64' { 'win-arm64' }
    default { throw "Unsupported Plan Forge Windows architecture: $architecture" }
}

$executable = Join-Path $pluginRoot "bin/$rid/planforge.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Plan Forge executable is missing: $executable"
}

& $executable @args
exit $LASTEXITCODE
