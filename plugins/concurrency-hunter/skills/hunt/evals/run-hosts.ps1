# Runs /concurrency-hunter:hunt on the demo headless in Claude Code, Codex and Cursor at once, then records what
# each host produced: evals/<host>/run.json always, and evals/<host>/report.md only from a completed bundle.
#
# The recipes are the ones that reached CompleteWithFindings in phase 1a. Codex runs the plugin from its installed
# cache rather than from --plugin-dir, so the cache is mirrored from this checkout first and verified by hash; a
# stale published executable is refused, because every host would analyze with it.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$HOST_TIMEOUT = [TimeSpan]::FromMinutes(15)
$PLUGIN_VERSION = '0.1.0'
$MARKETPLACE = 'dlin10-ai-coding-plugins'
$RUN_ID_PATTERN = 'concurrency-hunter[\\/]+runs[\\/]+[^\\/"\s]+[\\/]+(\d{8}-\d{6}-[0-9a-f]{6})'
$TOOLS = @('run_start', 'run_poll', 'get_groups', 'submit_narrative', 'render_report')

$evals = $PSScriptRoot
$plugin = (Resolve-Path (Join-Path $evals '../../..')).Path
$demo = (Resolve-Path (Join-Path $plugin 'demo')).Path
$executable = Join-Path $plugin 'bin/win-x64/concurrency-hunter.exe'
$cache = Join-Path $env:USERPROFILE ".codex\plugins\cache\$MARKETPLACE\concurrency-hunter\$PLUGIN_VERSION"
$runsRoot = Join-Path $env:LOCALAPPDATA 'concurrency-hunter\runs'
$logs = Join-Path $env:TEMP 'concurrency-hunter-hosts'

function Fail([string]$message) {
    Write-Output "run-hosts: $message"
    exit 1
}

# A published executable older than any source file would give every host last build's analysis.
if (-not (Test-Path -LiteralPath $executable)) {
    Fail "$executable is missing. Run build/package.ps1 first."
}
$publishedAt = (Get-Item -LiteralPath $executable).LastWriteTimeUtc
$newer = @(Get-ChildItem -LiteralPath (Join-Path $plugin 'src') -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $publishedAt })
if ($newer.Count -ne 0) {
    Fail "$executable is older than $($newer.Count) source file(s), for example $($newer[0].FullName). Run build/package.ps1 first."
}

# Mirror the plugin into Codex's installed cache and prove the copy with hashes.
New-Item -ItemType Directory -Force -Path $cache | Out-Null
foreach ($directory in '.claude-plugin', '.codex-plugin', '.cursor-plugin', 'bin', 'skills') {
    $source = Join-Path $plugin $directory
    $destination = Join-Path $cache $directory
    & robocopy $source $destination /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Fail "Mirroring $directory into $cache failed (robocopy exit $LASTEXITCODE); is a host session holding the plugin?"
    }
}
foreach ($file in 'codex.mcp.json', 'global.json', 'CONTEXT.md') {
    Copy-Item -LiteralPath (Join-Path $plugin $file) -Destination (Join-Path $cache $file) -Force
}

function Hashes([string]$root, [string]$relative) {
    $base = Join-Path $root $relative
    $result = @{}
    foreach ($item in @(Get-ChildItem -LiteralPath $base -Recurse -File)) {
        $result[$item.FullName.Substring($base.Length).TrimStart('\', '/')] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
    return $result
}

$installedExecutable = Join-Path $cache 'bin/win-x64/concurrency-hunter.exe'
if (-not (Test-Path -LiteralPath $installedExecutable) -or
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash) {
    Fail "The installed executable in $cache does not match $executable."
}
$sourceSkills = Hashes $plugin 'skills'
$installedSkills = Hashes $cache 'skills'
$skillMismatches = @(@($sourceSkills.Keys) + @($installedSkills.Keys) | Sort-Object -Unique |
    Where-Object { $sourceSkills[$_] -ne $installedSkills[$_] })
if ($skillMismatches.Count -ne 0) {
    Fail "Installed skill files differ from the checkout: $($skillMismatches -join ', ')"
}

# Each host runs through a small script in its own pwsh, so its arguments are passed by PowerShell rather than
# re-quoted on a command line, and the whole process tree can be killed at the timeout.
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$wrappers = @{
    'claude-code' = @'
param([string]$Demo, [string]$Plugin, [string]$Tools)
Remove-Item Env:CLAUDECODE -ErrorAction SilentlyContinue
& claude -p "/concurrency-hunter:hunt $Demo" --plugin-dir $Plugin --allowedTools 'Read,Glob,Grep,Agent,Task,mcp__plugin_concurrency-hunter_concurrency-hunter' --output-format stream-json --verbose
exit $LASTEXITCODE
'@
    'codex' = @'
param([string]$Demo, [string]$Plugin, [string]$Tools)
$approvals = foreach ($tool in $Tools -split ',') {
    '-c'
    "plugins.concurrency-hunter@dlin10-ai-coding-plugins.mcp_servers.concurrency-hunter.tools.$tool.approval_mode=""approve"""
}
"`$concurrency-hunter:hunt $Demo" | & codex exec - --skip-git-repo-check --json -C $Demo @approvals
exit $LASTEXITCODE
'@
    'cursor' = @'
param([string]$Demo, [string]$Plugin, [string]$Tools)
& "$env:LOCALAPPDATA\cursor-agent\cursor-agent.ps1" -p --plugin-dir $Plugin --workspace $Demo --trust --approve-mcps --force --output-format stream-json "/concurrency-hunter:hunt $Demo"
exit $LASTEXITCODE
'@
}

$shell = (Get-Process -Id $PID).Path
$hosts = @('claude-code', 'codex', 'cursor')
$runs = @{}
foreach ($name in $hosts) {
    $wrapper = Join-Path $logs "$name.invoke.ps1"
    Set-Content -LiteralPath $wrapper -Value $wrappers[$name] -Encoding utf8
    $output = Join-Path $logs "$name.out.log"
    $errors = Join-Path $logs "$name.err.log"
    Remove-Item -LiteralPath $output, $errors -Force -ErrorAction SilentlyContinue
    $arguments = @('-NoProfile', '-NonInteractive', '-File', "`"$wrapper`"", '-Demo', "`"$demo`"", '-Plugin', "`"$plugin`"",
                   '-Tools', ($TOOLS -join ','))
    $invokedAt = [DateTimeOffset]::UtcNow
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WorkingDirectory $demo -NoNewWindow -PassThru `
                             -RedirectStandardOutput $output -RedirectStandardError $errors
    # Touching the handle now keeps ExitCode readable after the process exits.
    $null = $process.Handle
    $runs[$name] = [pscustomobject]@{ Process = $process; InvokedAt = $invokedAt; Output = $output; Errors = $errors; TimedOut = $false }
    Write-Output "run-hosts: started $name (pid $($process.Id)); logs in $logs"
}

foreach ($name in $hosts) {
    $run = $runs[$name]
    $remaining = $run.InvokedAt + $HOST_TIMEOUT - [DateTimeOffset]::UtcNow
    $milliseconds = [Math]::Max(0, [int]$remaining.TotalMilliseconds)
    if (-not $run.Process.WaitForExit($milliseconds)) {
        $run.TimedOut = $true
        $run.Process.Kill($true)
        $run.Process.WaitForExit()
    }
}

foreach ($name in $hosts) {
    $run = $runs[$name]
    $text = @($run.Output, $run.Errors | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Content -LiteralPath $_ -Raw }) -join "`n"
    $found = [regex]::Matches($text, $RUN_ID_PATTERN)
    $runId = if ($found.Count -eq 0) { $null } else { $found[$found.Count - 1].Groups[1].Value }
    $bundle = $null
    if ($runId -and (Test-Path -LiteralPath $runsRoot)) {
        $bundle = @(Get-ChildItem -LiteralPath $runsRoot -Directory |
            ForEach-Object { Join-Path $_.FullName $runId } |
            Where-Object { Test-Path -LiteralPath (Join-Path $_ 'report.md') }) | Select-Object -First 1
    }

    if ($bundle) {
        $status = 'complete'
        $reason = "bundle $runId"
    }
    elseif ($name -eq 'cursor' -and ($text.Contains('usage limit') -or $text.Contains('Named models unavailable'))) {
        $status = 'skipped'
        $reason = if ($text.Contains('usage limit')) { 'Cursor reported a usage limit.' } else { 'Cursor reported that named models are unavailable.' }
    }
    else {
        $status = 'failed'
        $exit = if ($run.TimedOut) { "killed after $($HOST_TIMEOUT.TotalMinutes) minutes" } else { "exited $($run.Process.ExitCode)" }
        $reason = if (-not $runId) { "No run id in the output; the host $exit." }
                  else { "Run $runId has no bundle under $runsRoot; the host $exit." }
    }

    $hostDirectory = Join-Path $evals $name
    New-Item -ItemType Directory -Force -Path $hostDirectory | Out-Null
    if ($status -eq 'complete') {
        Copy-Item -LiteralPath (Join-Path $bundle 'report.md') -Destination (Join-Path $hostDirectory 'report.md') -Force
    }

    [ordered]@{
        status = $status
        reason = $reason
        runId = $runId
        bundle = $bundle
        invokedAt = $run.InvokedAt.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $hostDirectory 'run.json') -Encoding utf8
    Write-Output "run-hosts: $name $status - $reason"
}

exit 0
