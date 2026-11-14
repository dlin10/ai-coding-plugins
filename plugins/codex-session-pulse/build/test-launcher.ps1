# Runs bin/codex-session-pulse-launcher.cmd through cmd.exe against the published exe, in scratch plugin folders and a
# scratch home: the fast path, the download from a release (a local folder through curl's file:// URL), a failed and a
# refused download, SessionEnd without a download, parallel cold hooks, a missing .NET Desktop Runtime and no .NET at
# all (no dialog), and how the version is read from the manifest. Only the helper's release URL is substituted.
# Run after build/package.ps1, with pwsh 7: ./build/test-launcher.ps1 [-Executable <path>]
[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '../artifacts/publish/win-x64/CodexSessionPulse.exe')
)

$ErrorActionPreference = 'Stop'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$launcher = [IO.File]::ReadAllText((Join-Path $pluginRoot 'bin/codex-session-pulse-launcher.cmd'))
$helper = [IO.File]::ReadAllText((Join-Path $pluginRoot 'bin/codex-session-pulse-cache.ps1'))
$manifest = [IO.File]::ReadAllText((Join-Path $pluginRoot '.codex-plugin/plugin.json'))
$version = ($manifest | ConvertFrom-Json).version
$releaseLine = "`$releases = 'https://github.com/dlin10/ai-coding-plugins/releases/download'"
if (-not $helper.Contains($releaseLine)) { throw "The helper no longer holds the line this test substitutes: $releaseLine" }

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('csp-launcher-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$wpfAssemblies = @('PresentationFramework', 'PresentationCore', 'WindowsBase')
$junctions = @()

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function New-Release([string]$Name, [byte[]]$Bytes) {
    $folder = Join-Path $fixture "releases-$Name"
    if ($null -ne $Bytes) {
        New-Item -ItemType Directory -Path (Join-Path $folder "codex-session-pulse-v$version") | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $folder "codex-session-pulse-v$version/CodexSessionPulse.exe"), $Bytes)
    }
    return ([Uri]::new($folder).AbsoluteUri)
}

function New-Case([string]$Name, [string]$ReleaseUrl, [string]$Manifest = $manifest, [switch]$Cached) {
    $root = Join-Path $fixture $Name
    # A profile name may hold any of these; the launcher must survive them in every case.
    $plugin = Join-Path $root 'plugin with spaces & a bang!'
    New-Item -ItemType Directory -Path (Join-Path $plugin 'bin'), (Join-Path $plugin '.codex-plugin'), (Join-Path $root 'home/codex/sessions') | Out-Null
    [IO.File]::WriteAllText((Join-Path $plugin 'bin/codex-session-pulse-launcher.cmd'), $launcher)
    [IO.File]::WriteAllText((Join-Path $plugin 'bin/codex-session-pulse-cache.ps1'), $helper.Replace($releaseLine, "`$releases = '$ReleaseUrl'"))
    [IO.File]::WriteAllText((Join-Path $plugin '.codex-plugin/plugin.json'), $Manifest)
    $caseHome = Join-Path $root 'home'
    # Settings that match no window, in case anything starts an overlay.
    [IO.File]::WriteAllText((Join-Path $caseHome 'settings.json'), '{"schemaVersion":1,"codexProcessNames":["csp-no-such-process"],"codexExecutablePathFilters":[]}')
    $cache = Join-Path $caseHome "bin/$version"
    if ($Cached) {
        New-Item -ItemType Directory -Path $cache | Out-Null
        Copy-Item -LiteralPath $Executable -Destination $cache
    }
    return [pscustomobject]@{ Launcher = Join-Path $plugin 'bin/codex-session-pulse-launcher.cmd'; Home = $caseHome; Exe = Join-Path $cache 'CodexSessionPulse.exe'; Cache = $cache }
}

function Start-Launcher($Case, [string]$Arguments, [string]$Stdin = '{}', [hashtable]$Environment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/cmd.exe'), "/d /c `"`"$($Case.Launcher)`" $Arguments`"")
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Environment['CODEX_SESSION_PULSE_HOME'] = $Case.Home
    $start.Environment['CODEX_HOME'] = Join-Path $Case.Home 'codex'
    $start.Environment['CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES'] = '1'
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Write($Stdin)
    $process.StandardInput.Close()
    return [pscustomobject]@{ Process = $process; Stdout = $process.StandardOutput.ReadToEndAsync(); Stderr = $process.StandardError.ReadToEndAsync() }
}

function Wait-Launcher($Started) {
    # A dialog would block here: 60 s is the SessionStart timeout, far above any path that does not wait for a click.
    if (-not $Started.Process.WaitForExit(60000)) {
        $Started.Process.Kill($true)
        throw 'The launcher did not exit within 60 s (a dialog?)'
    }
    $result = [pscustomobject]@{ Exit = $Started.Process.ExitCode; Stdout = $Started.Stdout.GetAwaiter().GetResult(); Stderr = $Started.Stderr.GetAwaiter().GetResult() }
    $Started.Process.Dispose()
    return $result
}

function Invoke-Launcher($Case, [string]$Arguments, [hashtable]$Environment = @{}) {
    return Wait-Launcher (Start-Launcher $Case $Arguments '{}' $Environment)
}

function Assert-ExeRan($Result) {
    $names = @($Result.Stdout -split "`r?`n" | Where-Object { $_ })
    Assert-True ($names -contains 'CodexSessionPulse') "The exe did not run (stdout: '$($Result.Stdout)', stderr: '$($Result.Stderr)')"
    $wpf = @($names | Where-Object { $wpfAssemblies -contains $_ })
    Assert-True ($wpf.Count -eq 0) "--hook loaded WPF: $($wpf -join ', ')"
}

function Assert-NoDownloadLeft($Case) {
    $left = @(Get-ChildItem -LiteralPath $Case.Cache -Filter '*.download' -ErrorAction SilentlyContinue)
    Assert-True ($left.Count -eq 0) "Temporary downloads left behind: $($left.Name -join ', ')"
}

function Assert-Installed($Case) {
    Assert-True (Test-Path -LiteralPath $Case.Exe) 'The exe was not installed'
    Assert-True ((Get-FileHash -LiteralPath $Case.Exe).Hash -eq (Get-FileHash -LiteralPath $Executable).Hash) 'The installed exe differs from the release'
}

function New-DotnetRoot([string]$Name, [switch]$WithoutDesktop) {
    # A .NET location with the host and the base runtime but no Microsoft.WindowsDesktop.App, joined by junctions.
    $root = Join-Path $fixture $Name
    New-Item -ItemType Directory -Path $root | Out-Null
    if ($WithoutDesktop) {
        $real = Split-Path -Parent (Get-Command dotnet).Source
        $runtime = Get-ChildItem (Join-Path $real 'shared/Microsoft.NETCore.App') -Directory | Where-Object Name -like '10.*' | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
        New-Item -ItemType Directory -Path (Join-Path $root 'shared/Microsoft.NETCore.App') | Out-Null
        $script:junctions += (New-Item -ItemType Junction -Path (Join-Path $root 'host') -Target (Join-Path $real 'host')).FullName
        $script:junctions += (New-Item -ItemType Junction -Path (Join-Path $root "shared/Microsoft.NETCore.App/$($runtime.Name)") -Target $runtime.FullName).FullName
    }
    return $root
}

$cases = [ordered]@{
    'fast path runs the cached exe silently, with no download' = {
        $case = New-Case 'fast' (New-Release 'none' $null) -Cached
        $result = Invoke-Launcher $case '--hook'
        Assert-True ($result.Exit -eq 0) "Exit $($result.Exit)"
        Assert-ExeRan $result
        Assert-True ($result.Stderr -eq '') "The fast path wrote to stderr: $($result.Stderr)"
    }
    'a cold cache downloads the release, then runs it' = {
        $case = New-Case 'cold' (New-Release 'good' ([IO.File]::ReadAllBytes($Executable)))
        $result = Invoke-Launcher $case '--hook'
        Assert-True ($result.Exit -eq 0) "Exit $($result.Exit)"
        Assert-True ($result.Stderr -like "*downloading CodexSessionPulse.exe $version*") "No download message: $($result.Stderr)"
        Assert-Installed $case
        Assert-NoDownloadLeft $case
        Assert-ExeRan $result
        $again = Invoke-Launcher $case '--hook'
        Assert-True ($again.Stderr -eq '') "The second start downloaded again: $($again.Stderr)"
    }
    'a failed download explains, never fails a hook, and fails a manual start' = {
        $case = New-Case 'failed' (New-Release 'missing' $null)
        $hook = Invoke-Launcher $case '--hook'
        Assert-True ($hook.Exit -eq 0) "Hook exit $($hook.Exit)"
        Assert-True ($hook.Stderr -like '*download failed*' -and $hook.Stderr -like '*could not prepare*') "Unclear message: $($hook.Stderr)"
        $overlay = Invoke-Launcher $case '--overlay'
        Assert-True ($overlay.Exit -eq 1) "--overlay exit $($overlay.Exit)"
        Assert-True (-not (Test-Path -LiteralPath $case.Exe)) 'An exe appeared'
        Assert-NoDownloadLeft $case
    }
    'a download that is not an executable is refused' = {
        $case = New-Case 'html' (New-Release 'html' ([Text.Encoding]::ASCII.GetBytes('<html>portal</html>')))
        $result = Invoke-Launcher $case '--hook'
        Assert-True ($result.Exit -eq 0) "Exit $($result.Exit)"
        Assert-True ($result.Stderr -like '*did not return an executable*') "Unclear message: $($result.Stderr)"
        Assert-True (-not (Test-Path -LiteralPath $case.Exe)) 'The page was installed as the exe'
        Assert-NoDownloadLeft $case
    }
    'SessionEnd (--no-start) never downloads' = {
        $case = New-Case 'session-end' (New-Release 'end' ([IO.File]::ReadAllBytes($Executable)))
        $result = Invoke-Launcher $case '--hook --no-start'
        Assert-True ($result.Exit -eq 0) "Exit $($result.Exit)"
        Assert-True ($result.Stderr -like '*is missing*' -and $result.Stderr -notlike '*downloading*') "Unexpected stderr: $($result.Stderr)"
        Assert-True (-not (Test-Path -LiteralPath $case.Exe)) 'SessionEnd downloaded the exe'
    }
    'parallel cold hooks install one exe and all succeed' = {
        $case = New-Case 'parallel' (New-Release 'parallel' ([IO.File]::ReadAllBytes($Executable)))
        $started = @(1..4 | ForEach-Object { Start-Launcher $case '--hook' })
        $results = @($started | ForEach-Object { Wait-Launcher $_ })
        foreach ($result in $results) {
            Assert-True ($result.Exit -eq 0) "Exit $($result.Exit): $($result.Stderr)"
            Assert-ExeRan $result
        }
        Assert-Installed $case
        Assert-NoDownloadLeft $case
    }
    'no .NET Desktop Runtime: a message, exit 0 for a hook, no dialog' = {
        $case = New-Case 'no-desktop' (New-Release 'none' $null) -Cached
        $environment = @{ DOTNET_ROOT = (New-DotnetRoot 'dotnet-no-desktop' -WithoutDesktop); DOTNET_ROOT_X64 = $null }
        $hook = Invoke-Launcher $case '--hook' $environment
        Assert-True ($hook.Exit -eq 0) "Hook exit $($hook.Exit)"
        Assert-True ($hook.Stderr -like '*needs the .NET 10 Desktop Runtime x64*' -and $hook.Stderr -like '*dotnet.microsoft.com/download/dotnet/10.0*') "Unclear message: $($hook.Stderr)"
        $overlay = Invoke-Launcher $case '--overlay' $environment
        Assert-True ($overlay.Exit -eq 1) "--overlay exit $($overlay.Exit)"
        Assert-True ($overlay.Stderr -like '*needs the .NET 10 Desktop Runtime x64*') "Unclear message: $($overlay.Stderr)"
    }
    'no .NET at all: a message, exit 0 for a hook, no dialog' = {
        $case = New-Case 'no-dotnet' (New-Release 'none' $null) -Cached
        $hook = Invoke-Launcher $case '--hook' @{ DOTNET_ROOT = (New-DotnetRoot 'dotnet-empty'); DOTNET_ROOT_X64 = $null }
        Assert-True ($hook.Exit -eq 0) "Hook exit $($hook.Exit)"
        Assert-True ($hook.Stderr -like '*needs the .NET 10 Desktop Runtime x64*') "Unclear message: $($hook.Stderr)"
    }
    'a nested "version" key does not win over the top-level one' = {
        $nested = "{`r`n  `"interface`": {`r`n    `"version`": `"9.9.9`"`r`n  },`r`n  `"name`": `"codex-session-pulse`",`r`n  `"version`": `"$version`"`r`n}`r`n"
        $case = New-Case 'nested' (New-Release 'none' $null) $nested -Cached
        $result = Invoke-Launcher $case '--hook'
        Assert-True ($result.Exit -eq 0) "Exit $($result.Exit)"
        Assert-ExeRan $result
    }
    'a minified manifest is refused rather than guessed at' = {
        $case = New-Case 'minified' (New-Release 'none' $null) "{`"name`":`"codex-session-pulse`",`"version`":`"$version`"}" -Cached
        $result = Invoke-Launcher $case '--hook'
        Assert-True ($result.Exit -eq 0) "Exit $($result.Exit)"
        Assert-True ($result.Stderr -like '*no top-level "version"*') "Unclear message: $($result.Stderr)"
    }
}

New-Item -ItemType Directory -Path $fixture | Out-Null
$failures = 0
try {
    foreach ($name in $cases.Keys) {
        try {
            & $cases[$name]
            Write-Output "PASS: $name"
        }
        catch {
            $failures++
            Write-Output "FAIL: ${name}: $_"
        }
    }
}
finally {
    # Junctions first, so the recursive delete can never walk into the real .NET folder.
    foreach ($junction in $junctions) { [IO.Directory]::Delete($junction) }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not ([IO.Path]::GetFileName($resolved)).StartsWith('csp-launcher-tests-')) { throw "Unsafe fixture cleanup path: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failures) { throw "$failures launcher test(s) failed" }
