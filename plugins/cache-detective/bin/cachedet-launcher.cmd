@echo off
rem Bootstraps the Cache Detective MCP server. stdout belongs exclusively to JSON-RPC, so every
rem launcher message is redirected to stderr and the manifests use cmd /d to suppress AutoRun.
setlocal
for %%i in ("%~dp0..") do set "PLUGIN_ROOT=%%~fi"
set "EXE=%~dp0win-x64\cachedet.exe"
if exist "%EXE%" goto :run

rem package.ps1 asserts that all host manifests carry the same version.
set "VERSION="
for /f "usebackq tokens=1,2 delims=:, " %%a in ("%PLUGIN_ROOT%\.claude-plugin\plugin.json") do if /i "%%~a"=="version" if not defined VERSION set "VERSION=%%~b"
if not defined VERSION (
  >&2 echo cache-detective: no version in "%PLUGIN_ROOT%\.claude-plugin\plugin.json", so there is no release to fetch
  exit /b 1
)

set "CACHE=%LOCALAPPDATA%\cache-detective\bin\%VERSION%"
set "EXE=%CACHE%\cachedet.exe"
set "URL=https://github.com/dlin10/ai-coding-plugins/releases/download/cache-detective-v%VERSION%/cachedet.exe"
set "DOWNLOAD=%CACHE%\cachedet.%RANDOM%.download"
>&2 echo cache-detective: downloading cachedet.exe %VERSION% from %URL%
if not exist "%CACHE%" mkdir "%CACHE%" 2>nul
"%SystemRoot%\System32\curl.exe" -fLsS -o "%DOWNLOAD%" "%URL%" 1>&2
if errorlevel 1 goto :fetchfailed
move /y "%DOWNLOAD%" "%EXE%" >nul 2>&1
if exist "%EXE%" goto :run

:fetchfailed
if exist "%DOWNLOAD%" del /f /q "%DOWNLOAD%" >nul 2>&1
rem A concurrent launcher may have completed the same download while this one was running.
if exist "%EXE%" goto :run
rd "%CACHE%" >nul 2>&1
>&2 echo cache-detective: could not fetch %URL%
>&2 echo cache-detective: download it by hand to "%EXE%", or check that %SystemRoot%\System32\curl.exe exists
exit /b 1

:run
"%EXE%" %*
exit /b %ERRORLEVEL%
