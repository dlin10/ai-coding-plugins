#Requires -Version 7
# Regression scenarios for bin/roslyn-mcp-launcher.cmd and its cache helper. Only curl and the executable are stand-ins;
# cmd, inbox Windows PowerShell, file promotion, file sharing and running images are real. See docs/adr/0008.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcher = [IO.File]::ReadAllText((Join-Path $pluginRoot 'bin\roslyn-mcp-launcher.cmd'))
$helper = [IO.File]::ReadAllText((Join-Path $pluginRoot 'bin\roslyn-mcp-cache.ps1'))
$curlLine = "Join-Path `$env:SystemRoot 'System32\curl.exe'"
if (-not $helper.Contains($curlLine)) { throw 'The helper no longer starts curl the way this test replaces it' }
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('roslyn-mcp-launcher-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$held = [Collections.Generic.List[Diagnostics.Process]]::new()
$passed = 0

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message`: expected '$Expected', got '$Actual'" }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Fixture([string]$Version = '3.0.0-preview.1') {
    $root = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $plugin = Join-Path $root 'plugin with spaces'
    foreach ($path in @('bin', '.claude-plugin')) { [IO.Directory]::CreateDirectory((Join-Path $plugin $path)) | Out-Null }
    $manifest = if ($Version) { "{`r`n  `"name`": `"roslyn-mcp`",`r`n  `"version`": `"$Version`"`r`n}" } else { "{`r`n  `"name`": `"roslyn-mcp`"`r`n}" }
    [IO.File]::WriteAllText((Join-Path $plugin '.claude-plugin\plugin.json'), $manifest)
    [IO.File]::WriteAllText((Join-Path $plugin 'bin\roslyn-mcp-launcher.cmd'), $launcher)
    [IO.File]::WriteAllText((Join-Path $plugin 'bin\roslyn-mcp-cache.ps1'), $helper.Replace($curlLine, "'$($curl.Replace("'", "''"))'"))
    return @{ Root = $root; Plugin = $plugin; Cache = Join-Path $root 'local app data\roslyn-mcp\bin'; Version = $Version }
}

function Add-Version($Fixture, [string]$Version, [int]$Days, [switch]$Unpacked, [switch]$NoExe) {
    $directory = Join-Path $Fixture.Cache $Version
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    if (-not $NoExe) {
        $exe = Join-Path $directory 'roslyn-mcp.exe'
        [IO.File]::Copy($server, $exe)
        [IO.File]::SetLastWriteTimeUtc($exe, [DateTime]::UtcNow.AddDays(-$Days))
    }
    if ($Unpacked) {
        $buildHost = Join-Path $directory 'roslyn-mcp\AbCdEf123456\BuildHost-netcore'
        [IO.Directory]::CreateDirectory($buildHost) | Out-Null
        [IO.File]::WriteAllText((Join-Path $buildHost 'BuildHost.dll'), 'unpacked')
    }
    return $directory
}

function Invoke-Launcher($Fixture, [string]$Mode = 'success', [string[]]$Arguments = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\cmd.exe'))
    $path = Join-Path $Fixture.Plugin 'bin\roslyn-mcp-launcher.cmd'
    # With no arguments, exactly as the manifests launch it. cmd /c drops the outer quotes of a line holding more than
    # two, so arguments need /s and a quoted whole.
    if ($Arguments.Count -eq 0) { foreach ($argument in @('/d', '/c', $path)) { $start.ArgumentList.Add($argument) } }
    else { $start.Arguments = '/d /s /c ""' + $path + '" ' + (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ') + '"' }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['LOCALAPPDATA'] = Join-Path $Fixture.Root 'local app data'
    $start.Environment['TEST_SERVER'] = $server
    $start.Environment['TEST_ROOT'] = $Fixture.Root
    $start.Environment['TEST_DOWNLOAD_MODE'] = $Mode
    $start.Environment.Remove('DOTNET_BUNDLE_EXTRACT_BASE_DIR') | Out-Null
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Close()
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Launcher timed out' }
    return @{ Exit = $process.ExitCode; Out = $out.GetAwaiter().GetResult(); Err = $err.GetAwaiter().GetResult() }
}

function Get-Fetches($Fixture) { @(Get-ChildItem -LiteralPath $Fixture.Root -Filter '*.fetch' -File) }

function Assert-NoDownloads($Fixture) {
    $left = @(Get-ChildItem -LiteralPath $Fixture.Cache -Filter '*.download' -Recurse -File -ErrorAction SilentlyContinue)
    Assert-Equal 0 $left.Count "Abandoned downloads ($($left.Name -join ', '))"
}

function Test-Scenario([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Host "ok - $Name"
}

try {
    # Framework csc builds small native-launchable stand-ins, even from pwsh 7.
    $server = Join-Path $fixtureRoot 'server.exe'
    $curl = Join-Path $fixtureRoot 'curl.exe'
    $source = Join-Path $fixtureRoot 'fixture.cs'
    [IO.File]::WriteAllText($source, @'
using System;
using System.IO;
using System.Threading;
public static class Fixture
{
    public static int Main(string[] args)
    {
        string root = Environment.GetEnvironmentVariable("TEST_ROOT");
        if (Path.GetFileName(Environment.GetCommandLineArgs()[0]).Equals("curl.exe", StringComparison.OrdinalIgnoreCase))
        {
            // curl -fLsS -o <destination> <url>; each call leaves its own record, so concurrent calls cannot race.
            File.WriteAllText(Path.Combine(root, Guid.NewGuid() + ".fetch"), args[3]);
            string destination = args[2];
            string mode = Environment.GetEnvironmentVariable("TEST_DOWNLOAD_MODE");
            if (mode == "fail")
            {
                // Shared as curl shares it: the launcher holds the file open while curl writes.
                using (var partial = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) partial.WriteByte(1);
                return 22;
            }
            if (mode == "concurrent")
                File.Copy(Environment.GetEnvironmentVariable("TEST_SERVER"), Path.Combine(Path.GetDirectoryName(destination), "roslyn-mcp.exe"));
            File.Copy(Environment.GetEnvironmentVariable("TEST_SERVER"), destination, true);
            return 0;
        }
        string release = Environment.GetEnvironmentVariable("TEST_HOLD");
        if (release != null) { while (!File.Exists(release)) Thread.Sleep(30); return 0; }
        Console.Out.Write(string.Join("|", args) + ";" + Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        return 23;
    }
}
'@)
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /target:exe "/out:$server" $source | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'csc failed' }
    [IO.File]::Copy($server, $curl)

    Test-Scenario 'a first launch downloads the version, then serves MCP from the cache with the unpack base beside it' {
        $f = New-Fixture
        $r = Invoke-Launcher $f
        $cache = Join-Path $f.Cache $f.Version
        Assert-Equal 23 $r.Exit "Exit code ($($r.Err))"
        Assert-Equal "mcp;$cache" $r.Out 'MCP stdout: the arguments and the unpack base'
        Assert-True ($r.Err -match 'downloading roslyn-mcp.exe 3\.0\.0-preview\.1') "No download message: $($r.Err)"
        $fetches = @(Get-Fetches $f)
        Assert-Equal 1 $fetches.Count 'Fetches'
        Assert-Equal 'https://github.com/dlin10/ai-coding-plugins/releases/download/roslyn-mcp-v3.0.0-preview.1/roslyn-mcp.exe' (Get-Content -Raw $fetches[0].FullName) 'Release URL'
        Assert-True ([IO.File]::Exists((Join-Path $cache 'roslyn-mcp.exe'))) 'The executable was not promoted'
        Assert-NoDownloads $f
    }

    Test-Scenario 'a cached version starts without a download, and arguments are forwarded' {
        $f = New-Fixture
        Add-Version $f $f.Version 30 | Out-Null
        $r = Invoke-Launcher $f -Arguments @('--version', 'with spaces')
        Assert-Equal 23 $r.Exit "Exit code ($($r.Err))"
        Assert-Equal "--version|with spaces;$(Join-Path $f.Cache $f.Version)" $r.Out 'Forwarded arguments'
        Assert-Equal 0 @(Get-Fetches $f).Count 'Fetches'
        Assert-True ((Get-Item -LiteralPath (Join-Path $f.Cache "$($f.Version)\.last-used")).LastWriteTimeUtc -gt [DateTime]::UtcNow.AddMinutes(-1)) 'The launch did not record its use'
    }

    Test-Scenario 'a failed download leaves no file behind and starts nothing' {
        $f = New-Fixture
        $r = Invoke-Launcher $f 'fail'
        Assert-Equal 1 $r.Exit "Exit code ($($r.Err))"
        Assert-Equal '' $r.Out 'stdout'
        Assert-True ($r.Err -match 'could not prepare') "No failure message: $($r.Err)"
        Assert-True (-not [IO.File]::Exists((Join-Path $f.Cache "$($f.Version)\roslyn-mcp.exe"))) 'A failed download was promoted'
        Assert-NoDownloads $f
    }

    Test-Scenario 'a launcher that loses the race runs the winner and removes only its own download' {
        $f = New-Fixture
        $r = Invoke-Launcher $f 'concurrent'
        Assert-Equal 23 $r.Exit "Exit code ($($r.Err))"
        Assert-Equal "mcp;$(Join-Path $f.Cache $f.Version)" $r.Out 'MCP stdout'
        Assert-NoDownloads $f
    }

    Test-Scenario 'a manifest without a version is refused' {
        $f = New-Fixture -Version ''
        $r = Invoke-Launcher $f
        Assert-Equal 1 $r.Exit "Exit code ($($r.Err))"
        Assert-True ($r.Err -match 'no version in') "No refusal: $($r.Err)"
        Assert-Equal 0 @(Get-Fetches $f).Count 'Fetches'
    }

    Test-Scenario 'a version that is not semver fetches nothing' {
        $f = New-Fixture -Version '..\escape'
        $r = Invoke-Launcher $f
        Assert-Equal 1 $r.Exit "Exit code ($($r.Err))"
        Assert-True ($r.Err -match 'Invalid release version') "No refusal: $($r.Err)"
        Assert-Equal 0 @(Get-Fetches $f).Count 'Fetches'
    }

    Test-Scenario 'old versions retire after a week, two rollbacks and running ones stay' {
        $f = New-Fixture
        Add-Version $f $f.Version 1 | Out-Null
        $recent = Add-Version $f '2.9.0' 2 -Unpacked
        $rollback1 = Add-Version $f '2.8.0' 10 -Unpacked
        $rollback2 = Add-Version $f '2.7.0' 20
        $retired = Add-Version $f '2.6.0' 30 -Unpacked
        [IO.File]::WriteAllText((Join-Path $retired 'notes.txt'), 'not ours')
        $running = Add-Version $f '2.5.0' 40
        $leftover = Add-Version $f '2.4.0' 0 -Unpacked -NoExe
        $release = Join-Path $f.Root 'release'
        $hold = [Diagnostics.ProcessStartInfo]::new((Join-Path $running 'roslyn-mcp.exe'))
        $hold.UseShellExecute = $false
        $hold.Environment['TEST_HOLD'] = $release
        $held.Add([Diagnostics.Process]::Start($hold))
        $r = Invoke-Launcher $f
        Assert-Equal 23 $r.Exit "Exit code ($($r.Err))"
        Assert-True ($r.Err -notmatch 'cleanup skipped') "Cleanup did not run: $($r.Err)"
        foreach ($kept in @($recent, $rollback1, $rollback2, $running)) {
            Assert-True ([IO.File]::Exists((Join-Path $kept 'roslyn-mcp.exe'))) "$kept lost its executable"
        }
        Assert-True ([IO.Directory]::Exists((Join-Path $recent 'roslyn-mcp'))) 'A recent version lost its unpacked copy'
        Assert-True (-not [IO.File]::Exists((Join-Path $retired 'roslyn-mcp.exe'))) 'The oldest idle version kept its executable'
        Assert-True (-not [IO.Directory]::Exists((Join-Path $retired 'roslyn-mcp'))) 'The oldest idle version kept its unpacked copy'
        Assert-True ([IO.File]::Exists((Join-Path $retired 'notes.txt'))) 'An unknown file was removed'
        Assert-True ([IO.File]::Exists((Join-Path $retired '.last-used'))) 'The lease file was removed'
        Assert-True (-not [IO.Directory]::Exists((Join-Path $leftover 'roslyn-mcp'))) 'An unpacked copy without its executable stayed'
        [IO.File]::WriteAllText($release, '')
    }

    Test-Scenario 'week-old downloads of ours go, fresh ones and foreign names stay' {
        $f = New-Fixture
        $current = Add-Version $f $f.Version 1
        $other = Add-Version $f '2.9.0' 2
        $stale = Join-Path $other ('roslyn-mcp.' + [Guid]::NewGuid().ToString('N') + '.download')
        $staleCurrent = Join-Path $current ('roslyn-mcp.' + [Guid]::NewGuid().ToString('N') + '.download')
        $fresh = Join-Path $other ('roslyn-mcp.' + [Guid]::NewGuid().ToString('N') + '.download')
        $foreign = Join-Path $other 'roslyn-mcp.12345.download'
        foreach ($path in @($stale, $staleCurrent, $fresh, $foreign)) { [IO.File]::WriteAllText($path, 'partial') }
        foreach ($path in @($stale, $staleCurrent, $foreign)) { [IO.File]::SetLastWriteTimeUtc($path, [DateTime]::UtcNow.AddDays(-8)) }
        $r = Invoke-Launcher $f
        Assert-Equal 23 $r.Exit "Exit code ($($r.Err))"
        Assert-True (-not [IO.File]::Exists($stale)) 'A week-old download stayed'
        Assert-True (-not [IO.File]::Exists($staleCurrent)) "A week-old download of the current version stayed"
        Assert-True ([IO.File]::Exists($fresh)) 'A fresh download, possibly in progress, was removed'
        Assert-True ([IO.File]::Exists($foreign)) 'A file of uncertain ownership was removed'
    }

    Write-Host "$passed scenarios passed"
}
finally {
    foreach ($process in $held) {
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
