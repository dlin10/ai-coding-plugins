@echo off
rem Bootstraps the Roslyn MCP shim, roslyn-mcp.exe mcp. cmd rather than PowerShell because this process stays as the
rem shim's parent until the session ends, in every session whether Roslyn is used or not: powershell.exe costs around
rem 50 MB of working set to do that, cmd.exe under 10. A short-lived inbox PowerShell helper fetches the executable and
rem retires old versions, then exits before the shim starts. stdout belongs to the MCP protocol, so every message here
rem goes to stderr, and the manifests pass /d so that a user's AutoRun cannot write to it either. See docs/adr/0008.
setlocal
for %%i in ("%~dp0..") do set "PLUGIN_ROOT=%%~fi"

rem validate-plugins.mjs asserts that the host manifests agree, so this one names the release. A manifest this cannot
rem read yields no version, which is refused rather than guessed at.
set "VERSION="
for /f "usebackq tokens=1,2 delims=:, " %%a in ("%PLUGIN_ROOT%\.claude-plugin\plugin.json") do if /i "%%~a"=="version" if not defined VERSION set "VERSION=%%~b"
if not defined VERSION (
    >&2 echo roslyn-mcp: no version in "%PLUGIN_ROOT%\.claude-plugin\plugin.json", so there is no release to fetch
    exit /b 1
)

rem Keyed by version, so that the plugin folders of both hosts share one copy of each release.
set "CACHE=%LOCALAPPDATA%\roslyn-mcp\bin\%VERSION%"
set "EXE=%CACHE%\roslyn-mcp.exe"
rem stdin is reserved for MCP and stdout for the protocol, so the helper inherits neither. A failed cleanup still
rem lets a cached executable start.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0roslyn-mcp-cache.ps1" <nul 1>&2
if exist "%EXE%" goto :run
>&2 echo roslyn-mcp: could not prepare "%EXE%"; check the messages above and access to github.com
exit /b 1

:run
rem The executable unpacks itself on first start, here rather than under %TEMP%: a deep TEMP pushes the build hosts'
rem files past MAX_PATH, and the unpacked copy goes when the helper retires its version. The daemon the shim starts
rem inherits this and unpacks to the same place.
set "DOTNET_BUNDLE_EXTRACT_BASE_DIR=%CACHE%"
rem Serving MCP is what every manifest launches this for, so no arguments means mcp. Anything else is forwarded.
if not "%~1"=="" goto :forward
"%EXE%" mcp
exit /b %ERRORLEVEL%

:forward
"%EXE%" %*
exit /b %ERRORLEVEL%
