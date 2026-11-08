# Measures a running overlay at rest: CPU over the whole span (from TotalProcessorTime, as a share of one core and of
# the machine), the working set sampled every 10 s, and every TCP connection or UDP endpoint the process owns at each
# sample (spec §10: idle CPU < 1 %, working set <= 80 MB, no network). Start it with a Codex window open and leave the
# machine alone meanwhile (docs/compatibility.md 13.1).
# Usage, pwsh 7: ./build/measure-overlay.ps1 [-ProcessId <pid>] [-Minutes 5]
[CmdletBinding()]
param(
    [int]$ProcessId,
    [double]$Minutes = 5
)

$ErrorActionPreference = 'Stop'
if (-not $ProcessId) {
    $overlays = @(Get-CimInstance Win32_Process -Filter "Name = 'CodexSessionPulse.exe'" | Where-Object CommandLine -like '*--overlay*')
    if ($overlays.Count -ne 1) { throw "Expected one running overlay, found $($overlays.Count); pass -ProcessId" }
    $ProcessId = $overlays[0].ProcessId
}

$process = Get-Process -Id $ProcessId
$cpuStart = $process.TotalProcessorTime
$clock = [Diagnostics.Stopwatch]::StartNew()
$workingSets = [Collections.Generic.List[double]]::new()
$connections = [Collections.Generic.List[string]]::new()
$end = [DateTime]::UtcNow.AddMinutes($Minutes)

do {
    $process.Refresh()
    if ($process.HasExited) { throw "The overlay exited during the measurement" }
    $workingSets.Add($process.WorkingSet64 / 1MB)
    foreach ($tcp in @(Get-NetTCPConnection -OwningProcess $ProcessId -ErrorAction SilentlyContinue)) {
        $connections.Add("tcp $($tcp.LocalAddress):$($tcp.LocalPort) -> $($tcp.RemoteAddress):$($tcp.RemotePort) $($tcp.State)")
    }
    foreach ($udp in @(Get-NetUDPEndpoint -OwningProcess $ProcessId -ErrorAction SilentlyContinue)) {
        $connections.Add("udp $($udp.LocalAddress):$($udp.LocalPort)")
    }
    Start-Sleep -Seconds 10
} while ([DateTime]::UtcNow -lt $end)

$process.Refresh()
$cpu = ($process.TotalProcessorTime - $cpuStart).TotalMilliseconds / $clock.Elapsed.TotalMilliseconds
[pscustomobject]@{
    ProcessId = $ProcessId
    Minutes = [Math]::Round($clock.Elapsed.TotalMinutes, 2)
    CpuPercentOfOneCore = [Math]::Round($cpu * 100, 3)
    CpuPercentOfMachine = [Math]::Round($cpu * 100 / [Environment]::ProcessorCount, 3)
    WorkingSetMinMB = [Math]::Round(($workingSets | Measure-Object -Minimum).Minimum, 1)
    WorkingSetMaxMB = [Math]::Round(($workingSets | Measure-Object -Maximum).Maximum, 1)
    Samples = $workingSets.Count
    Network = if ($connections.Count) { ($connections | Select-Object -Unique) -join '; ' } else { 'none' }
}
