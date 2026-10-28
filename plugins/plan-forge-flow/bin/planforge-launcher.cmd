@echo off
rem Bootstraps the Plan Forge Flow MCP server. cmd rather than PowerShell because this process
rem outlives nothing but the server it starts: a host launches one per session whether the flow is
rem used or not, and it then sits there as the server's parent until the session ends. powershell.exe
rem costs around 50 MB of working set to do that; cmd.exe stays under 10. A short-lived inbox
rem PowerShell helper prepares the cache and exits before the server starts.
rem stdout belongs to the MCP protocol, so every message here goes to stderr, and the manifests pass
rem /d so that a user's AutoRun cannot write to it either.
setlocal
rem `claude mcp list`, which the server runs to see what a worker would load, has no flag to leave
rem one plugin out, so the server marks it instead; return before resolving or downloading the
rem executable so this server is absent while every unrelated plugin and MCP server is still listed.
if /i "%PLANFORGE_SELF_EXCLUDED%"=="1" exit /b 0

for %%i in ("%~dp0..") do set "PLUGIN_ROOT=%%~fi"
if exist "%PLUGIN_ROOT%\prompts\" set "PLANFORGE_PROMPTS=%PLUGIN_ROOT%\prompts"

set "EXE=%~dp0win-x64\planforge.exe"
if exist "%EXE%" goto :run

rem package.ps1 asserts the two manifest versions agree, so either one names the release.
rem Reading JSON a line at a time is crude, and the crudeness is bounded by matching the key rather
rem than the word: a manifest this cannot read yields no version rather than the wrong one, and no
rem version is refused below rather than guessed at.
set "VERSION="
for /f "usebackq tokens=1,2 delims=:, " %%a in ("%PLUGIN_ROOT%\.claude-plugin\plugin.json") do if /i "%%~a"=="version" if not defined VERSION set "VERSION=%%~b"
if not defined VERSION (
    >&2 echo plan-forge-flow: no version in "%PLUGIN_ROOT%\.claude-plugin\plugin.json", so there is no release to fetch
    exit /b 1
)

rem The cache is keyed by version so that the per-commit plugin folders a host writes on every
rem update share one copy of the executable rather than fetching their own.
set "CACHE=%LOCALAPPDATA%\plan-forge-flow\bin\%VERSION%"
set "EXE=%CACHE%\planforge.exe"
rem stdin is reserved for MCP or the resume hook, and stdout for the protocol. The helper must
rem inherit neither. Even a failed maintenance pass must allow an existing executable to start.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0planforge-cache.ps1" <nul 1>&2
if exist "%EXE%" goto :run
>&2 echo plan-forge-flow: could not prepare "%EXE%"; check the cache helper and access to github.com
exit /b 1

:run
"%EXE%" %*
exit /b %ERRORLEVEL%
