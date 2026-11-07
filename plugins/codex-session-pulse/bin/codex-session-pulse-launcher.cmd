@echo off
rem Starts CodexSessionPulse.exe for the Codex hooks: hooks/hooks.json runs this as
rem cmd.exe /d /c "...\codex-session-pulse-launcher.cmd" --hook [--no-start]. cmd rather than pwsh because cmd waits
rem for a GUI exe and passes the hook's stdin pipe through to it (docs/compatibility.md section 3.1). /d keeps a user's AutoRun
rem out of the hook. This is only the fast path: the executable is expected in the version's cache folder, which is
rem under %USERPROFILE% rather than %LOCALAPPDATA% because MSIX redirects the latter for processes started from Codex.
setlocal
for %%i in ("%~dp0..") do set "PLUGIN_ROOT=%%~fi"

set "ROOT=%USERPROFILE%\.codex-session-pulse"
if defined CODEX_SESSION_PULSE_HOME set "ROOT=%CODEX_SESSION_PULSE_HOME%"

rem The manifest names the release, so the cache folder can never disagree with the plugin that runs it.
set "VERSION="
for /f "usebackq tokens=1,2 delims=:, " %%a in ("%PLUGIN_ROOT%\.codex-plugin\plugin.json") do if /i "%%~a"=="version" if not defined VERSION set "VERSION=%%~b"
if not defined VERSION goto :noversion

set "EXE=%ROOT%\bin\%VERSION%\CodexSessionPulse.exe"
if exist "%EXE%" goto :run

>&2 echo codex-session-pulse: "%EXE%" is missing; build or download CodexSessionPulse.exe %VERSION% into that folder
goto :failed

:noversion
>&2 echo codex-session-pulse: no version in "%PLUGIN_ROOT%\.codex-plugin\plugin.json"

:failed
rem A hook must never fail Codex; the rollout files stay the source of truth without it.
if /i "%~1"=="--hook" exit /b 0
exit /b 1

:run
"%EXE%" %*
exit /b %ERRORLEVEL%
