$ErrorActionPreference = 'Stop'

$pluginRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path (Split-Path $pluginRoot -Parent) -Parent
$hook = Join-Path $pluginRoot 'hooks\roslyn-grep-nudge.ps1'

function Invoke-RoslynHook {
    param(
        [Parameter(Mandatory)]
        [string]$InputText,

        [Parameter(Mandatory)]
        [ValidateSet('codex', 'claude', 'cursor')]
        [string]$HostKind
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$hook`" -HostKind $HostKind"
    $process = [Diagnostics.Process]::Start($startInfo)
    $process.StandardInput.Write($InputText)
    $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEnd()
    $errorOutput = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Hook failed for $HostKind with exit code $($process.ExitCode): $errorOutput"
    }

    return $output.Trim()
}

function Assert-Contains {
    param([string]$Value, [string]$Expected)
    if ($Value.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "Expected output to contain '$Expected', got: $Value"
    }
}

$codexInput = @{
    cwd = $repoRoot
    hook_event_name = 'PreToolUse'
    tool_name = 'functions.exec'
    tool_input = @{
        source = 'await tools.shell_command({command:"rg ProcessOrder"});'
    }
} | ConvertTo-Json -Compress -Depth 5
$codex = Invoke-RoslynHook -InputText $codexInput -HostKind codex | ConvertFrom-Json
Assert-Contains $codex.hookSpecificOutput.additionalContext 'tool discovery'

$claudeInput = @{
    cwd = $repoRoot
    hook_event_name = 'PreToolUse'
    tool_name = 'Grep'
    tool_input = @{ pattern = 'ProcessOrder'; glob = '*.cs' }
} | ConvertTo-Json -Compress -Depth 5
$claude = Invoke-RoslynHook -InputText $claudeInput -HostKind claude | ConvertFrom-Json
Assert-Contains $claude.hookSpecificOutput.additionalContext 'STOP and use the roslyn-mcp tools first'

$cursorInput = @{
    cwd = $repoRoot
    tool_name = 'Shell'
    tool_input = @{ command = 'rg ProcessOrder src/*.cs' }
} | ConvertTo-Json -Compress -Depth 5
$cursor = Invoke-RoslynHook -InputText $cursorInput -HostKind cursor | ConvertFrom-Json
Assert-Contains $cursor.additional_context 'treat the results as candidates'

$irrelevantInput = @{
    tool_name = 'shell_command'
    tool_input = @{ command = 'git status --short' }
} | ConvertTo-Json -Compress -Depth 5
if (Invoke-RoslynHook -InputText $irrelevantInput -HostKind codex) {
    throw 'Irrelevant commands must not produce hook output.'
}

if (Invoke-RoslynHook -InputText '{not-json' -HostKind claude) {
    throw 'Malformed input must not produce hook output.'
}

Write-Host 'Roslyn hook contract tests passed.'
