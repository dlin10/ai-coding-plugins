# Measures the warm hook the way Codex runs it, minus pwsh: cmd.exe /d /c "<plugin>\bin\codex-session-pulse-launcher.cmd"
# --hook with a payload on stdin, while an overlay of the same exe runs in a scratch home, so every hook connects and
# delivers. Reports the median and the minimum of the wall time from process start to exit (docs/compatibility.md 13.1).
# Run after build/package.ps1, with pwsh 7: ./build/measure-hook.ps1 [-Executable <path>] [-Count 25] [-PluginRoot <path>]
[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '../artifacts/publish/win-x64/CodexSessionPulse.exe'),
    [int]$Count = 25,
    [string]$PluginRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$PluginRoot = (Resolve-Path -LiteralPath $PluginRoot).Path
$launcher = Join-Path $PluginRoot 'bin/codex-session-pulse-launcher.cmd'
$version = (Get-Content -LiteralPath (Join-Path $PluginRoot '.codex-plugin/plugin.json') -Raw | ConvertFrom-Json).version
$scratchHome = Join-Path ([IO.Path]::GetTempPath()) ('csp-measure-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$payload = '{"hook_event_name":"UserPromptSubmit","session_id":"s","transcript_path":null,"prompt":"measure"}'

New-Item -ItemType Directory -Path (Join-Path $scratchHome "bin/$version"), (Join-Path $scratchHome 'codex/sessions') | Out-Null
Copy-Item -LiteralPath $Executable -Destination (Join-Path $scratchHome "bin/$version")
[IO.File]::WriteAllText((Join-Path $scratchHome 'settings.json'), '{"schemaVersion":1,"codexProcessNames":["csp-no-such-process"],"codexExecutablePathFilters":[]}')

function New-Start([string]$File, [string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($File, $Arguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Environment['CODEX_SESSION_PULSE_HOME'] = $scratchHome
    $start.Environment['CODEX_HOME'] = Join-Path $scratchHome 'codex'
    return $start
}

$overlay = [Diagnostics.Process]::Start((New-Start (Join-Path $scratchHome "bin/$version/CodexSessionPulse.exe") '--overlay'))
try {
    $log = Join-Path $scratchHome 'logs/overlay.log'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not ((Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -Pattern 'pipeline start' -Quiet))) {
        if ([DateTime]::UtcNow -gt $deadline -or $overlay.HasExited) { throw 'The overlay did not start' }
        Start-Sleep -Milliseconds 100
    }

    $command = Join-Path $env:SystemRoot 'System32/cmd.exe'
    $times = foreach ($run in 0..$Count) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $hook = [Diagnostics.Process]::Start((New-Start $command "/d /c `"`"$launcher`" --hook`""))
        $hook.StandardInput.Write($payload)
        $hook.StandardInput.Close()
        $hook.WaitForExit()
        $watch.Stop()
        if ($hook.ExitCode -ne 0) { throw "The hook exited with $($hook.ExitCode)" }
        # The first run warms the file cache and is not counted.
        if ($run -gt 0) { $watch.Elapsed.TotalMilliseconds }
        $hook.Dispose()
    }

    $delivered = @(Select-String -LiteralPath $log -Pattern 'hook UserPromptSubmit').Count
    $sorted = @($times | Sort-Object)
    $median = if ($sorted.Count % 2) { $sorted[[int][Math]::Floor($sorted.Count / 2)] } else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    [pscustomobject]@{
        Executable = $Executable
        Runs = $sorted.Count
        MedianMs = [Math]::Round($median, 1)
        MinMs = [Math]::Round($sorted[0], 1)
        MaxMs = [Math]::Round($sorted[-1], 1)
        Delivered = $delivered
    }
}
finally {
    if (-not $overlay.HasExited) { $overlay.Kill(); $overlay.WaitForExit() }
    Remove-Item -LiteralPath $scratchHome -Recurse -Force -ErrorAction SilentlyContinue
}
