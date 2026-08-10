# Grep-nudge hook for the roslyn-mcp plugin (Codex/Claude PreToolUse + Cursor postToolUse).
# Reads the hook payload on stdin. If the tool call is a text search (Grep, Shell/Bash/PowerShell
# running grep/rg/Select-String) that clearly targets C#/.NET code, emit a NON-BLOCKING reminder
# matching the host's routing model (Codex/Claude: strict gate; Cursor: two-lane router — see the
# 'roslyn-first' skill). Never blocks: it only adds context.
param(
    [ValidateSet('codex', 'claude', 'cursor')]
    [string]$HostKind
)

$ErrorActionPreference = 'SilentlyContinue'

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

$tool = [string]$payload.tool_name
$ti = $payload.tool_input
if ($null -eq $ti) { exit 0 }

switch ($tool) {
    'Grep'       { $hay = @($ti.pattern, $ti.glob, $ti.path, $ti.type) -join ' ' }
    'Bash'       { $hay = [string]$ti.command }
    'PowerShell' { $hay = [string]$ti.command }
    'Shell'      { $hay = [string]$ti.command }
    'shell_command' { $hay = [string]$ti.command }
    'exec_command'  { $hay = @($ti.cmd, $ti.command) -join ' ' }
    'functions.exec' {
        if ($ti -is [string]) {
            $hay = $ti
        }
        else {
            $hay = @($ti.code, $ti.input, $ti.source, $ti.command) -join ' '
        }
    }
    default      { exit 0 }
}
if (-not $hay) { exit 0 }

# For shell tools, only react to actual text-search commands.
if ($tool -ne 'Grep' -and ($hay -notmatch '(?i)\b(rg|grep|select-string|sls)\b')) { exit 0 }

# Nudge when the command explicitly targets C#/.NET files, or when a plain text
# search runs inside a Git repository that contains a tracked solution/project.
$touchesCSharp = ($hay -match '(?i)\.cs\b') -or ($hay -match '(?i)\*\.cs') -or `
                 ($hay -match '(?i)\.csproj\b') -or ($hay -match '(?i)\.slnx?\b') -or `
                 ($tool -eq 'Grep' -and [string]$ti.type -match '(?i)^cs$')
if (-not $touchesCSharp) {
    $workingDirectory = [string]$payload.cwd
    if (-not $workingDirectory) { $workingDirectory = [Environment]::CurrentDirectory }
    $repositoryRoot = & git -C $workingDirectory rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $repositoryRoot) {
        $projectFile = & git -C $repositoryRoot.Trim() ls-files -- '*.sln' '*.slnx' '*.csproj' 2>$null |
                       Select-Object -First 1
        $touchesCSharp = [bool]$projectFile
    }
}
if (-not $touchesCSharp) { exit 0 }

# Infer the host when -HostKind was not passed.
if (-not $HostKind) {
    if ($tool -eq 'Shell') {
        $HostKind = 'cursor'
    }
    elseif ($tool -in @('functions.exec', 'shell_command', 'exec_command')) {
        $HostKind = 'codex'
    }
    else {
        $HostKind = 'claude'
    }
}

if ($HostKind -eq 'cursor') {
    $msg = "roslyn-first: this is a text search over C#/.NET code. If it answers a precise question about a C# symbol (references, callers, definition, implementations, diagnostics, dead code), use the roslyn-mcp tools instead. If it's conceptual discovery, proceed -- but treat the results as candidates and verify any symbol claims with roslyn-mcp before asserting them (see the 'roslyn-first' skill)."
}
elseif ($HostKind -eq 'codex') {
    $msg = "roslyn-first: this is a text search over C#/.NET code. If it answers a semantic question about a C# symbol, STOP and use the roslyn-mcp tools first. If they are not loaded, use tool discovery before falling back. Use text search only when Roslyn MCP is unavailable for this repo or the query is non-semantic, and state why (see the 'roslyn-first' skill)."
}
else {
    $msg = "roslyn-first: this is a text search over C#/.NET code. If it answers a semantic question about a C# symbol (references, callers, definition, implementations, diagnostics, dead code), STOP and use the roslyn-mcp tools first (see the 'roslyn-first' skill). Fall back to text search only if roslyn-mcp is unavailable for this repo (VS not open / wrong solution) or the query is non-semantic -- and say why."
}

$hookEventName = [string]$payload.hook_event_name
if (-not $hookEventName) { $hookEventName = 'PreToolUse' }

# Codex/Claude PreToolUse use nested hookSpecificOutput.additionalContext;
# Cursor postToolUse uses top-level additional_context.
if ($HostKind -eq 'cursor') {
    $out = @{ additional_context = $msg } | ConvertTo-Json -Compress -Depth 3
}
else {
    $out = @{
        hookSpecificOutput = @{
            hookEventName     = $hookEventName
            additionalContext = $msg
        }
    } | ConvertTo-Json -Compress -Depth 5
}

Write-Output $out
exit 0
