@echo off
rem Starts CodexSessionPulse.exe for the Codex hooks: hooks/hooks.json runs this as
rem cmd.exe /d /c "...\codex-session-pulse-launcher.cmd" --hook [--no-start]. cmd rather than pwsh because cmd waits
rem for a GUI exe and passes the hook's stdin pipe through to it (docs/compatibility.md section 3.1). /d keeps a user's
rem AutoRun out of the hook. The fast path is "if exist" and nothing else; only a missing exe calls the PowerShell helper,
rem which downloads the release asset of this version. The cache is under %USERPROFILE% rather than %LOCALAPPDATA%
rem because MSIX redirects the latter for processes started from Codex (docs/compatibility.md section 3.2).
setlocal
for %%i in ("%~dp0..") do set "PLUGIN_ROOT=%%~fi"

set "ROOT=%USERPROFILE%\.codex-session-pulse"
if defined CODEX_SESSION_PULSE_HOME set "ROOT=%CODEX_SESSION_PULSE_HOME%"

rem The manifest names the release, so the cache folder can never disagree with the plugin that runs it.
set "VERSION="
for /f "usebackq delims=" %%l in ("%PLUGIN_ROOT%\.codex-plugin\plugin.json") do if not defined VERSION (
    set "LINE=%%l"
    call :version
)
if not defined VERSION goto :noversion

set "EXE=%ROOT%\bin\%VERSION%\CodexSessionPulse.exe"
if exist "%EXE%" goto :run

rem SessionEnd is synchronous and has 3 s; it never starts anything, so it never waits for a download either.
if /i "%~2"=="--no-start" goto :missing

rem stdin belongs to the exe (the hook payload) and stdout to Codex, so the helper inherits neither.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0codex-session-pulse-cache.ps1" <nul 1>&2
if exist "%EXE%" goto :run
>&2 echo codex-session-pulse: could not prepare "%EXE%"; check the messages above and access to github.com
goto :failed

:missing
>&2 echo codex-session-pulse: "%EXE%" is missing; the next SessionStart or prompt downloads CodexSessionPulse.exe %VERSION%
goto :failed

:noversion
>&2 echo codex-session-pulse: no top-level "version" in "%PLUGIN_ROOT%\.codex-plugin\plugin.json"
goto :failed

:run
rem Without this a missing .NET 10 Desktop Runtime makes the apphost of a GUI exe show its "install .NET" dialog and wait
rem for a click, inside a hook; with it the host writes to stderr and exits with 0x800080xx (docs/compatibility.md 13.2).
set "DOTNET_DISABLE_GUI_ERRORS=1"
"%EXE%" %*
set "CODE=%ERRORLEVEL%"
rem 0x80008000 to 0x800080FF: the .NET host failed before the app ran.
if %CODE% GEQ -2147450880 if %CODE% LEQ -2147450625 goto :noruntime
if /i "%~1"=="--hook" exit /b 0
exit /b %CODE%

:noruntime
>&2 echo codex-session-pulse: CodexSessionPulse.exe needs the .NET 10 Desktop Runtime x64, which was not found ^(host error %CODE%^).
>&2 echo codex-session-pulse: install it from https://dotnet.microsoft.com/download/dotnet/10.0 ^(".NET Desktop Runtime", x64^).

:failed
rem A hook must never fail Codex; the rollout files stay the source of truth without it.
if /i "%~1"=="--hook" exit /b 0
exit /b 1

:version
rem Only the top-level key, two spaces in, as the repository's manifests are written: a nested "version" or a minified
rem manifest yields nothing, which is refused rather than guessed at. Directory.Build.props reads it the same way.
set "LINE=%LINE:"=%"
if not "%LINE:~0,10%"=="  version:" exit /b 0
set "VERSION=%LINE:~10%"
set "VERSION=%VERSION: =%"
set "VERSION=%VERSION:,=%"
exit /b 0
