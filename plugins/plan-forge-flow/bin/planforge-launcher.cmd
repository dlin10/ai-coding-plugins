@echo off
rem Bootstraps the Plan Forge Flow MCP server. cmd rather than PowerShell because this process
rem outlives nothing but the server it starts: a host launches one per session whether the flow is
rem used or not, and it then sits there as the server's parent until the session ends. powershell.exe
rem costs around 50 MB of working set to do that; cmd.exe stays under 10. Nothing else runs here --
rem the download below is curl.exe, which Windows has shipped since 10 1803, so the plugin carries
rem no interpreter of its own.
rem stdout belongs to the MCP protocol, so every message here goes to stderr, and the manifests pass
rem /d so that a user's AutoRun cannot write to it either.
setlocal
for %%i in ("%~dp0..") do set "PLUGIN_ROOT=%%~fi"
if exist "%PLUGIN_ROOT%\prompts\" set "PLANFORGE_PROMPTS=%PLUGIN_ROOT%\prompts"

set "EXE=%~dp0win-x64\planforge.exe"
if exist "%EXE%" goto :run

rem package.ps1 asserts the three manifest versions agree, so any one of them names the release.
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
if exist "%EXE%" goto :run

set "URL=https://github.com/dlin10/ai-coding-plugins/releases/download/plan-forge-flow-v%VERSION%/planforge.exe"
set "DOWNLOAD=%CACHE%\planforge.%RANDOM%.download"
>&2 echo plan-forge-flow: downloading planforge.exe %VERSION% from %URL%
if not exist "%CACHE%" mkdir "%CACHE%" 2>nul
rem System32 rather than whatever curl is on PATH, and its own output pushed to stderr because
rem stdout belongs to the protocol. Downloaded beside its destination and moved into place, so a
rem killed download never leaves a half-written executable for the next session to run.
"%SystemRoot%\System32\curl.exe" -fLsS -o "%DOWNLOAD%" "%URL%" 1>&2
if errorlevel 1 goto :fetchfailed
move /y "%DOWNLOAD%" "%EXE%" >nul 2>&1
if exist "%EXE%" goto :run

:fetchfailed
if exist "%DOWNLOAD%" del /f /q "%DOWNLOAD%" >nul 2>&1
rem A concurrent host may have won the race to the cache path; its download is as good as ours.
if exist "%EXE%" goto :run
rem Only an empty one goes: rd refuses a directory a concurrent download has put something in.
rd "%CACHE%" >nul 2>&1
>&2 echo plan-forge-flow: could not fetch %URL%
>&2 echo plan-forge-flow: download it by hand to "%EXE%", or check that %SystemRoot%\System32\curl.exe exists
exit /b 1

:run
"%EXE%" %*
exit /b %ERRORLEVEL%
