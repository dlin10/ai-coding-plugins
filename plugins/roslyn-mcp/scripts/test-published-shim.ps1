#Requires -Version 7
# Starts the shim the way a host does, through the plugin's launcher with no arguments, on a given roslyn-mcp.exe placed
# in a private cache, and loads a one-project solution through it. That proves the asset unpacks where the launcher
# says, carries Roslyn's build hosts and answers. The fixture is left unrestored and the daemon must restore it: a
# restore check that cannot evaluate a project, or a restore that fails, still lets the load succeed, and only the
# load's restore report shows it (docs/adr/0006). The release workflow runs it on the asset before publishing it; run
# it on a local `dotnet publish` the same way. It leaves nothing running: the daemon goes by its own process ID.
# See docs/adr/0008.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcher = Join-Path $pluginRoot 'bin\roslyn-mcp-launcher.cmd'
$version = (Get-Content -Raw (Join-Path $pluginRoot '.codex-plugin\plugin.json') | ConvertFrom-Json).version
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$answerTimeout = [TimeSpan]::FromSeconds(120)

# A pipe of this version that already answers is a daemon serving this user's sessions, which the test would share and
# then kill.
$pipeName = "roslyn-mcp-$version-$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
$probe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
try {
    $probe.Connect(200)
    throw "A roslyn-mcp $version daemon already serves this user on $pipeName; stop it before running this test"
}
catch [TimeoutException] { }
finally { $probe.Dispose() }

# Short, so that the unpacked build hosts stay inside MAX_PATH wherever LOCALAPPDATA is.
$root = Join-Path $env:LOCALAPPDATA ('rmcp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$cache = Join-Path $root "roslyn-mcp\bin\$version"
$fixture = Join-Path $root 'fixture'
$solution = Join-Path $fixture 'Fixture.slnx'
$project = Join-Path $fixture 'Lib\Lib.csproj'
$source = Join-Path $fixture 'Lib\Greeter.cs'
$shim = $null
$daemonId = $null
$succeeded = $false

function Start-Launcher([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\cmd.exe'))
    foreach ($argument in @('/d', '/c', $launcher) + $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.Environment['LOCALAPPDATA'] = $root
    return [Diagnostics.Process]::Start($start)
}

function Send-Message($Message) {
    $shim.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 10 -Compress))
    $shim.StandardInput.Flush()
}

function Invoke-Request([string]$Method, $Parameters) {
    $id = ++$script:nextId
    Send-Message @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters }
    while ($true) {
        $read = $shim.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($answerTimeout)) { throw "No answer to $Method within $answerTimeout" }
        if ($null -eq $read.Result) { throw "The shim closed stdout while $Method was pending" }
        $message = $read.Result | ConvertFrom-Json
        if ($message.id -ne $id) { continue }
        if ($message.error) { throw "$Method failed: $($message.error | ConvertTo-Json -Depth 10 -Compress)" }
        return $message.result
    }
}

function Invoke-Tool([string]$Name, $Arguments) {
    $result = Invoke-Request 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw "$Name failed: $($result | ConvertTo-Json -Depth 10 -Compress)" }
    return $result.content[0].text | ConvertFrom-Json
}

try {
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    Copy-Item -LiteralPath $Executable -Destination (Join-Path $cache 'roslyn-mcp.exe')
    [IO.Directory]::CreateDirectory((Split-Path $source)) | Out-Null
    Set-Content -LiteralPath $project -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    Set-Content -LiteralPath $source -Value "namespace Fixture;`r`n`r`npublic sealed class Greeter`r`n{`r`n    public string Greet(string name) => `"Hello, `" + name;`r`n}"
    Set-Content -LiteralPath $solution -Value '<Solution><Project Path="Lib/Lib.csproj" /></Solution>'

    # Arguments are forwarded, and the asset names the version its pipe and its cache folder are keyed by.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $probeRun = Start-Launcher @('--version')
    $reported = $probeRun.StandardOutput.ReadToEnd().Trim()
    $probeRun.WaitForExit()
    if ($reported -ne $version -and -not $reported.StartsWith("$version+")) {
        throw "The asset reports '$reported' ($($probeRun.StandardError.ReadToEnd())); the plugin is $version"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $cache 'roslyn-mcp'))) { throw "The asset did not unpack into $cache" }
    Write-Host "First start, unpacking included: $([int]$watch.Elapsed.TotalMilliseconds) ms; reports $reported"

    $watch.Restart()
    $shim = Start-Launcher @()
    $stderr = $shim.StandardError.ReadToEndAsync()
    $initialized = Invoke-Request 'initialize' @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'roslyn-mcp-smoke'; version = '1' }
    }
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    if ($initialized.serverInfo.name -ne 'roslyn-mcp' -or $initialized.serverInfo.version -ne $version) {
        throw "Unexpected serverInfo: $($initialized.serverInfo | ConvertTo-Json -Compress)"
    }
    $tools = @((Invoke-Request 'tools/list' @{}).tools | ForEach-Object name)
    if ($tools -notcontains 'roslyn_load') { throw "tools/list has no roslyn_load: $($tools -join ', ')" }
    Write-Host "Initialize and tools/list: $([int]$watch.Elapsed.TotalMilliseconds) ms; $($tools.Count) tools"

    $watch.Restart()
    $load = Invoke-Tool 'roslyn_load' @{ solution = $solution }
    for ($attempt = 0; $load.status -eq 'loading' -and $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 2
        $load = Invoke-Tool 'roslyn_load' @{ solution = $solution }
    }
    $daemonId = $load.daemon.processId
    if (-not $load.requestSucceeded -or $load.status -ne 'loaded' -or $load.projectCount -ne 1) {
        throw "roslyn_load did not load the fixture: $($load | ConvertTo-Json -Depth 10 -Compress)"
    }
    if ($load.partial -or @($load.restore.restored) -notcontains $project -or @($load.restore.failed).Count -gt 0) {
        throw "The daemon did not restore the fixture: $($load.restore | ConvertTo-Json -Depth 10 -Compress)"
    }
    Write-Host "roslyn_load, daemon start and restore included: $([int]$watch.Elapsed.TotalMilliseconds) ms; daemon $daemonId, exitsWithJob $($load.daemon.exitsWithJob)"

    $symbols = Invoke-Tool 'roslyn_get_document_symbols' @{ filePath = $source }
    $names = @($symbols.members | ForEach-Object name)
    if (-not $symbols.requestSucceeded -or $names -notcontains 'Greeter' -or $names -notcontains 'Greet') {
        throw "roslyn_get_document_symbols did not answer from the fixture: $($symbols | ConvertTo-Json -Depth 10 -Compress)"
    }
    Write-Host "roslyn_get_document_symbols: $($names -join ', ')"
    $succeeded = $true
}
finally {
    if ($shim) {
        # Closing stdin is how a host ends the session; the launcher's cmd exits with the shim.
        try { $shim.StandardInput.Close() } catch { }
        if (-not $shim.WaitForExit(10000)) { $shim.Kill($true) }
        if (-not $succeeded -and $stderr -and $stderr.Wait(5000)) { Write-Host "The launcher's stderr:`n$($stderr.Result)" }
    }
    if ($daemonId) {
        # The daemon broke away from the shim's job; its build hosts are its children.
        try { [Diagnostics.Process]::GetProcessById($daemonId).Kill($true) } catch [ArgumentException] { }
    }
    Start-Sleep -Milliseconds 500
    # Nothing may still run from the private root: the shim and the daemon are the asset's copy, and the build hosts
    # start from its unpacked files.
    $left = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0)
    })
    foreach ($process in $left) {
        Write-Warning "Left running: $($process.ProcessId) $($process.CommandLine)"
        try { [Diagnostics.Process]::GetProcessById($process.ProcessId).Kill($true) } catch [ArgumentException] { }
    }
    for ($attempt = 0; (Test-Path -LiteralPath $root) -and $attempt -lt 10; $attempt++) {
        try { Remove-Item -LiteralPath $root -Recurse -Force } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($left.Count -gt 0) { throw "$($left.Count) processes were left running from $root" }
}
Write-Host "The shim answered through the launcher, and nothing is left running."
