# Item 3 of the profiling spike: the executions stage of Driver.Run with exclusion "all", from the existing build in engine\bin2.
# Counters for the whole run; once ExecutionModel.Build is running and the working set passes 2 GB, a stack snapshot and a gcdump;
# then the run goes on with no memory cap (MAX_WS_GB out of reach, as asked). The only stop is a guard for the machine: when the
# system's free commit falls under 1.5 GB, this harness process (its exact PID) is stopped, so that VS and other sessions do not
# fail allocations. A second gcdump is taken at 4 GB if the system has at least 7 GB of commit free then.
# -Spike is the working folder: engine\bin2 holds the built engine-spike with the engine DLLs, out\pertype the decompiled CoreLib.
param([Parameter(Mandatory)][string] $Spike)
$ErrorActionPreference = 'Stop'
$S = $Spike
$P = "$S\profile"
New-Item -ItemType Directory -Force $P | Out-Null
$tools = "$env:USERPROFILE\.dotnet\tools"
$timeline = "$P\exec-all-timeline.txt"
function Mark($text) { "[$(Get-Date -Format HH:mm:ss.fff)] $text" | Add-Content $timeline }
function FreeCommitGb { (Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory / 1MB }

Set-Content $timeline ''
$env:MAX_WS_GB = '1000'
$log = "$P\exec-all.log"
$proc = Start-Process -FilePath "$S\engine\bin2\engine-spike.exe" -ArgumentList @("$S\out\pertype", 'study', 'run', 'all', 'solve') `
                      -RedirectStandardOutput $log -RedirectStandardError "$P\exec-all.err" -PassThru -NoNewWindow
$null = $proc.Handle
Mark "harness pid $($proc.Id); free commit $([math]::Round((FreeCommitGb), 1)) GB"

while (-not (Test-Path $log) -or -not (Select-String -Path $log -Pattern 'excluding all: reach' -Quiet)) {
    if ($proc.HasExited) { Mark "harness exited before the solve: $($proc.ExitCode)"; exit 1 }
    Start-Sleep -Milliseconds 250
}
Mark 'heap solve started'
$counters = Start-Process -FilePath "$tools\dotnet-counters.exe" `
                          -ArgumentList @('collect', '-p', $proc.Id, '--counters', 'System.Runtime', '--format', 'csv', '-o', "$P\exec-all-counters.csv", '--refresh-interval', '1') `
                          -RedirectStandardOutput "$P\exec-all-counters.out" -RedirectStandardError "$P\exec-all-counters.err" -PassThru -NoNewWindow
Mark "counters pid $($counters.Id)"

while (-not (Select-String -Path $log -Pattern 'heap solve:' -Quiet)) {
    if ($proc.HasExited) { Mark "harness exited during the solve: $($proc.ExitCode)"; exit 1 }
    Start-Sleep -Milliseconds 100
}
Mark 'executions started'

$samples = "$P\exec-all-ws.csv"
'time,working_set_mb,private_mb,free_commit_gb' | Set-Content $samples
$dumped = 0
$guardTick = 0
while (-not $proc.HasExited) {
    $proc.Refresh()
    $ws = $proc.WorkingSet64 / 1MB
    $private = $proc.PrivateMemorySize64 / 1MB
    $freeCommit = if ($guardTick++ % 5 -eq 0 -or $script:lastFree -lt 3) { $script:lastFree = FreeCommitGb; $script:lastFree } else { $script:lastFree }
    "$(Get-Date -Format HH:mm:ss.fff),$([int]$ws),$([int]$private),$([math]::Round($freeCommit, 2))" | Add-Content $samples
    if ($freeCommit -lt 1.5) {
        Mark "machine guard: free commit $([math]::Round($freeCommit, 2)) GB, working set $([int]$ws) MB; stopping harness pid $($proc.Id)"
        Stop-Process -Id $proc.Id -Force
        break
    }
    if ($dumped -eq 0 -and $ws -ge 2048) {
        Mark "working set $([int]$ws) MB: stacks, then gcdump 1"
        & "$tools\dotnet-stack.exe" report -p $proc.Id *> "$P\exec-all-stacks-1.txt"
        Mark "stacks done (exit $LASTEXITCODE)"
        & "$tools\dotnet-gcdump.exe" collect -p $proc.Id -t 900 -o "$P\exec-all-1.gcdump" *> "$P\exec-all-gcdump1.out"
        Mark "gcdump 1 done (exit $LASTEXITCODE)"
        $dumped = 1
    }
    elseif ($dumped -eq 1 -and $ws -ge 4096) {
        $dumped = 2
        $free = FreeCommitGb
        if ($free -ge 7) {
            Mark "working set $([int]$ws) MB, free commit $([math]::Round($free, 1)) GB: stacks, then gcdump 2"
            & "$tools\dotnet-stack.exe" report -p $proc.Id *> "$P\exec-all-stacks-2.txt"
            & "$tools\dotnet-gcdump.exe" collect -p $proc.Id -t 900 -o "$P\exec-all-2.gcdump" *> "$P\exec-all-gcdump2.out"
            Mark "gcdump 2 done (exit $LASTEXITCODE)"
        }
        else {
            Mark "working set $([int]$ws) MB, free commit $([math]::Round($free, 1)) GB: no second gcdump"
        }
    }
    Start-Sleep -Milliseconds 200
}

$proc.WaitForExit()
Mark "harness exited: $($proc.ExitCode)"
if (-not $counters.WaitForExit(30000)) { Stop-Process -Id $counters.Id; Mark 'counters stopped' }
Mark 'end'
