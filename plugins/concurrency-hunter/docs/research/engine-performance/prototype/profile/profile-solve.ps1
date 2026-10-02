# Items 1-2 of the profiling spike: the Driver.Run heap solve with exclusion "four", from the existing build in engine\bin2.
# Counters for the whole solve, a 3-minute sampled trace from the solve's start, then two gcdumps (right after the trace, and
# about 8 minutes into the solve) to see whether the heap's regions and points-to sets still grow. The harness exits itself
# when a stage passes 10 minutes; MAX_WS_GB is set out of reach, as asked (no memory cap).
# -Spike is the working folder: engine\bin2 holds the built engine-spike with the engine DLLs, out\pertype the decompiled CoreLib.
param([Parameter(Mandatory)][string] $Spike)
$ErrorActionPreference = 'Stop'
$S = $Spike
$P = "$S\profile"
New-Item -ItemType Directory -Force $P | Out-Null
$tools = "$env:USERPROFILE\.dotnet\tools"
$timeline = "$P\solve-four-timeline.txt"
function Mark($text) { "[$(Get-Date -Format HH:mm:ss)] $text" | Add-Content $timeline }

Set-Content $timeline ''
$env:MAX_WS_GB = '1000'
$log = "$P\solve-four.log"
$proc = Start-Process -FilePath "$S\engine\bin2\engine-spike.exe" -ArgumentList @("$S\out\pertype", 'study', 'run', 'four', 'solve') `
                      -RedirectStandardOutput $log -RedirectStandardError "$P\solve-four.err" -PassThru -NoNewWindow
$null = $proc.Handle  # keeps the exit code readable after exit
Mark "harness pid $($proc.Id)"

while (-not (Test-Path $log) -or -not (Select-String -Path $log -Pattern 'excluding four: reach' -Quiet)) {
    if ($proc.HasExited) { Mark "harness exited before the solve: $($proc.ExitCode)"; exit 1 }
    Start-Sleep -Milliseconds 250
}
$t0 = Get-Date
Mark 'solve started'

$counters = Start-Process -FilePath "$tools\dotnet-counters.exe" `
                          -ArgumentList @('collect', '-p', $proc.Id, '--counters', 'System.Runtime', '--format', 'csv', '-o', "$P\solve-four-counters.csv", '--refresh-interval', '5') `
                          -RedirectStandardOutput "$P\solve-four-counters.out" -RedirectStandardError "$P\solve-four-counters.err" -PassThru -NoNewWindow
Mark "counters pid $($counters.Id)"

& "$tools\dotnet-trace.exe" collect -p $proc.Id --profile dotnet-sampled-thread-time --duration 00:00:03:00 --format Speedscope `
    -o "$P\solve-four.nettrace" *> "$P\solve-four-trace.out"
Mark "trace done (exit $LASTEXITCODE)"

& "$tools\dotnet-gcdump.exe" collect -p $proc.Id -o "$P\solve-four-1.gcdump" *> "$P\solve-four-gcdump1.out"
Mark "gcdump 1 done (exit $LASTEXITCODE)"

while (((Get-Date) - $t0).TotalMinutes -lt 8 -and -not $proc.HasExited) { Start-Sleep -Seconds 1 }
if (-not $proc.HasExited) {
    & "$tools\dotnet-gcdump.exe" collect -p $proc.Id -o "$P\solve-four-2.gcdump" *> "$P\solve-four-gcdump2.out"
    Mark "gcdump 2 done (exit $LASTEXITCODE)"
}

$proc.WaitForExit()
Mark "harness exited: $($proc.ExitCode)"
if (-not $counters.WaitForExit(30000)) { Stop-Process -Id $counters.Id; Mark 'counters stopped' }
Mark 'end'
