# Run under pwsh 7. The launcher itself uses inbox Windows PowerShell, as on a fresh Windows host.
# Only curl is substituted. cmd, file promotion, file sharing and running images are real.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcher = [IO.File]::ReadAllText((Join-Path $pluginRoot 'bin/planforge-launcher.cmd'))
$helperPath = Join-Path $pluginRoot 'bin/planforge-cache.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('planforge-launcher-tests-' + [Guid]::NewGuid())
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message`: expected '$Expected', got '$Actual'" }
}

function New-Fixture {
    $root = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
    $plugin = Join-Path $root 'plugin with spaces'
    $cache = Join-Path $root 'local app data/plan-forge-flow/bin'
    foreach ($path in @('bin', '.claude-plugin', 'prompts')) {
        [IO.Directory]::CreateDirectory((Join-Path $plugin $path)) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $plugin '.claude-plugin/plugin.json'), "{`r`n  `"name`": `"plan-forge-flow`",`r`n  `"version`": `"1.0.0`"`r`n}")
    [IO.File]::WriteAllText((Join-Path $plugin 'bin/planforge-launcher.cmd'),
        $launcher.Replace('"%SystemRoot%\System32\curl.exe"', ('"' + $downloader + '"')))
    if (Test-Path -LiteralPath $helperPath) {
        $helper = [IO.File]::ReadAllText($helperPath).Replace("Join-Path `$env:SystemRoot 'System32\curl.exe'", "'$($downloader.Replace("'", "''"))'")
        [IO.File]::WriteAllText((Join-Path $plugin 'bin/planforge-cache.ps1'), $helper)
    }
    return @{ Root = $root; Plugin = $plugin; Cache = $cache }
}

function Add-Version($Fixture, [string]$Version, [int]$Days = 30) {
    $directory = Join-Path $Fixture.Cache $Version
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $exe = Join-Path $directory 'planforge.exe'
    [IO.File]::Copy($server, $exe)
    [IO.File]::SetLastWriteTimeUtc($exe, [DateTime]::UtcNow.AddDays(-$Days))
    return $exe
}

function Start-Launcher($Fixture, [string]$Mode = 'success', [string]$Arguments = 'scan "argument with spaces"') {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/cmd.exe'))
    $start.Arguments = '/d /s /c ""' + (Join-Path $Fixture.Plugin 'bin/planforge-launcher.cmd') + '" ' + $Arguments + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['LOCALAPPDATA'] = Join-Path $Fixture.Root 'local app data'
    $start.Environment['USERPROFILE'] = Join-Path $Fixture.Root 'user'
    $start.Environment['CODEX_HOME'] = Join-Path $Fixture.Root 'codex home'
    $start.Environment['CLAUDE_CONFIG_DIR'] = Join-Path $Fixture.Root 'claude home'
    $start.Environment['TEST_SERVER'] = $server
    $start.Environment['TEST_DOWNLOAD_MODE'] = $Mode
    $start.Environment['TEST_ROOT'] = $Fixture.Root
    $start.Environment.Remove('PLANFORGE_SELF_EXCLUDED') | Out-Null
    $p = [Diagnostics.Process]::Start($start)
    $processes.Add($p)
    return @{ Process = $p; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync() }
}

function Complete-Launcher($Launch, [int]$Exit = 23, [string]$Output = "scan|argument with spaces`r`n", [bool]$AllowMaintenanceFailure = $false) {
    if (-not $Launch.Process.WaitForExit(20000)) { throw 'Launcher timed out' }
    $errors = $Launch.Err.GetAwaiter().GetResult()
    Assert-Equal $Exit $Launch.Process.ExitCode "Exit code ($errors)"
    Assert-Equal $Output $Launch.Out.GetAwaiter().GetResult() 'MCP stdout / arguments'
    if ($Exit -eq 23 -and -not $AllowMaintenanceFailure -and $errors -match 'cache (cleanup skipped|preparation failed)|ParserError') {
        throw "Cache maintenance did not run: $errors"
    }
}

function Assert-NoDownloads($Fixture) {
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $Fixture.Cache -Filter '*.download' -Recurse -ErrorAction SilentlyContinue).Count 'Abandoned downloads'
}

function Wait-File([string]$Path) {
    $until = [DateTime]::UtcNow.AddSeconds(20)
    while (-not [IO.File]::Exists($Path)) {
        if ([DateTime]::UtcNow -ge $until) { throw "Timed out waiting for $Path" }
        Start-Sleep -Milliseconds 30
    }
}

try {
    # Framework csc produces small native-launchable .NET Framework fixtures, even from pwsh 7.
    $server = Join-Path $fixtureRoot 'server.exe'
    $downloader = Join-Path $fixtureRoot 'curl.exe'
    $source = Join-Path $fixtureRoot 'fixture.cs'
    [IO.File]::WriteAllText($source, @'
using System;
using System.IO;
using System.Threading;
public static class Fixture
{
    public static int Main(string[] args)
    {
        if (Path.GetFileName(Environment.GetCommandLineArgs()[0]) == "curl.exe")
        {
            string root = Environment.GetEnvironmentVariable("TEST_ROOT");
            // Each concurrent downloader owns a log file, so the test logger cannot race.
            File.WriteAllText(Path.Combine(root, Guid.NewGuid() + ".fetch"), "fetch");
            string destination = args[2];
            string source = Environment.GetEnvironmentVariable("TEST_SERVER");
            string mode = Environment.GetEnvironmentVariable("TEST_DOWNLOAD_MODE");
            if (mode.StartsWith("concurrent"))
            {
                string installed = Path.Combine(Path.GetDirectoryName(destination), "planforge.exe");
                File.Copy(source, installed);
                if (mode != "concurrent-lock") File.SetAttributes(installed, FileAttributes.ReadOnly);
                else
                {
                    File.WriteAllText(Path.Combine(root, "ready"), installed);
                    while (!File.Exists(Path.Combine(root, "release"))) Thread.Sleep(30);
                }
            }
            if (mode == "hold")
            {
                using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) stream.WriteByte(1);
                File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddDays(-30));
                File.WriteAllText(Path.Combine(root, "ready"), destination);
                while (!File.Exists(Path.Combine(root, "release"))) Thread.Sleep(30);
                if (!File.Exists(destination)) return 99;
            }
            if (mode.EndsWith("failure"))
            {
                using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) stream.WriteByte(1);
                return 22;
            }
            File.Copy(source, destination, true);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--resume-card")
            Console.WriteLine(Console.ReadLine());
        else if (args.Length > 1 && args[0] == "--hold")
        {
            File.WriteAllText(args[1], "ready");
            while (!File.Exists(args[1] + ".release")) Thread.Sleep(30);
        }
        else Console.WriteLine(string.Join("|", args));
        return 23;
    }
}
'@)
    & (Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /out:$server $source
    if ($LASTEXITCODE) { throw 'Fixture compilation failed' }
    [IO.File]::Copy($server, $downloader)

    $tests = [ordered]@{
        'cold cache and argument forwarding' = {
            $f = New-Fixture
            Complete-Launcher (Start-Launcher $f)
            Assert-NoDownloads $f
        }
        'cached and locked executable is reused without downloading' = {
            $f = New-Fixture
            $exe = Add-Version $f '1.0.0'
            $handle = [IO.File]::Open($exe, 'Open', 'Read', 'Read')
            try { 1..2 | ForEach-Object { Complete-Launcher (Start-Launcher $f) } }
            finally { $handle.Dispose() }
            Assert-Equal 0 @(Get-ChildItem $f.Root -Filter '*.fetch').Count 'Unnecessary fetches'
        }
        'bundled binary takes precedence' = {
            $f = New-Fixture
            $exe = Add-Version $f '1.0.0'
            [IO.File]::WriteAllText($exe, 'broken cached exe')
            [IO.Directory]::CreateDirectory((Join-Path $f.Plugin 'bin/win-x64')) | Out-Null
            [IO.File]::Copy($server, (Join-Path $f.Plugin 'bin/win-x64/planforge.exe'))
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal 0 @(Get-ChildItem $f.Root -Filter '*.fetch').Count 'Bundled fetches'
        }
        'failed promotion reuses winner and removes own download' = {
            $f = New-Fixture
            Complete-Launcher (Start-Launcher $f 'concurrent')
            Assert-NoDownloads $f
        }
        'failed promotion reuses an executable locked by another launcher' = {
            $f = New-Fixture
            $launch = Start-Launcher $f 'concurrent-lock'
            Wait-File (Join-Path $f.Root 'ready')
            $lease = [IO.File]::Open((Join-Path $f.Cache '1.0.0/planforge.exe'), 'Open', 'Read', 'Read')
            try {
                [IO.File]::WriteAllText((Join-Path $f.Root 'release'), '')
                Complete-Launcher $launch
            }
            finally { $lease.Dispose() }
            Assert-NoDownloads $f
        }
        'failed download removes partial file' = {
            $f = New-Fixture
            Complete-Launcher (Start-Launcher $f 'failure') 1 ''
            Assert-NoDownloads $f
        }
        'failed download reuses concurrent winner' = {
            $f = New-Fixture
            Complete-Launcher (Start-Launcher $f 'concurrent-failure')
            Assert-NoDownloads $f
        }
        'hook stdin survives cache preparation' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            $launch = Start-Launcher $f 'success' '--resume-card'
            $launch.Process.StandardInput.WriteLine('session input')
            $launch.Process.StandardInput.Close()
            Complete-Launcher $launch 23 "session input`r`n"
        }
        'retention removes only old surplus binaries' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            $old = Add-Version $f '0.1.0' 60
            $backup1 = Add-Version $f '0.2.0' 40
            $backup2 = Add-Version $f '0.3.0' 30
            $recent = Add-Version $f '0.4.0' 1
            $lockFile = Join-Path (Split-Path $old) 'workspace.lock'
            [IO.File]::WriteAllText($lockFile, 'keep')
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal $false ([IO.File]::Exists($old)) 'Old surplus executable'
            foreach ($p in @($backup1, $backup2, $recent, $lockFile)) {
                Assert-Equal $true ([IO.File]::Exists($p)) "Retained file $p"
            }
        }
        'running older image is retained' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            $old = Add-Version $f '0.1.0' 60
            Add-Version $f '0.2.0' 40 | Out-Null
            Add-Version $f '0.3.0' 30 | Out-Null
            $ready = Join-Path $f.Root 'server-ready'
            $start = [Diagnostics.ProcessStartInfo]::new($old)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.Arguments = '--hold "' + $ready + '"'
            $p = [Diagnostics.Process]::Start($start)
            $processes.Add($p)
            Wait-File $ready
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal $true ([IO.File]::Exists($old)) 'Live image'
            Assert-Equal $false $p.HasExited 'Live process'
            [IO.File]::WriteAllText(($ready + '.release'), '')
            if (-not $p.WaitForExit(5000)) { throw 'Fixture server did not exit' }
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal $false ([IO.File]::Exists($old)) 'Unused older image after exit'
        }
        'concurrent downloads keep active files even when aged' = {
            $f = New-Fixture
            $first = Start-Launcher $f 'hold'
            Wait-File (Join-Path $f.Root 'ready')
            $active = [IO.File]::ReadAllText((Join-Path $f.Root 'ready'))
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal $true ([IO.File]::Exists($active)) 'Active download'
            [IO.File]::WriteAllText((Join-Path $f.Root 'release'), '')
            Complete-Launcher $first
            Assert-NoDownloads $f
        }
        'installed versions in both default and custom host homes are retained' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            Add-Version $f '0.8.0' 20 | Out-Null
            Add-Version $f '0.9.0' 10 | Out-Null
            $homes = @('user/.codex', 'user/.claude', 'codex home', 'claude home')
            for ($i = 0; $i -lt $homes.Count; $i++) {
                $v = "0.$($i + 1).0"
                Add-Version $f $v 60 | Out-Null
                $hostFolder = if ($i % 2 -eq 0) { '.codex-plugin' } else { '.claude-plugin' }
                $manifest = Join-Path $f.Root "$($homes[$i])/plugins/cache/market/plan-forge-flow/install-$i/$hostFolder/plugin.json"
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifest)) | Out-Null
                [IO.File]::WriteAllText($manifest, ('{"name":"plan-forge-flow","version":"' + $v + '"}'))
            }
            $unused = Add-Version $f '0.5.0' 60
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal $false ([IO.File]::Exists($unused)) 'Unused version beside installed versions'
            1..4 | ForEach-Object {
                Assert-Equal $true ([IO.File]::Exists((Join-Path $f.Cache "0.$_.0/planforge.exe"))) 'Installed version'
            }
        }
        'a previously launched custom installation remains protected' = {
            $first = New-Fixture
            $old = Add-Version $first '1.0.0'
            Complete-Launcher (Start-Launcher $first)
            [IO.File]::SetLastWriteTimeUtc((Join-Path $first.Cache '1.0.0/.last-used'), [DateTime]::UtcNow.AddDays(-60))
            $second = New-Fixture
            $second.Root = $first.Root
            $second.Cache = $first.Cache
            [IO.File]::WriteAllText((Join-Path $second.Plugin '.claude-plugin/plugin.json'), "{`r`n  `"name`": `"plan-forge-flow`",`r`n  `"version`": `"2.0.0`"`r`n}")
            Add-Version $second '2.0.0' | Out-Null
            Add-Version $second '0.8.0' 20 | Out-Null
            Add-Version $second '0.9.0' 10 | Out-Null
            Complete-Launcher (Start-Launcher $second)
            Assert-Equal $true ([IO.File]::Exists($old)) 'Registered custom installation'
        }
        'an incomplete installation inventory skips cleanup without breaking startup' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            $old = Add-Version $f '0.1.0' 60
            Add-Version $f '0.2.0' 40 | Out-Null
            Add-Version $f '0.3.0' 30 | Out-Null
            $manifest = Join-Path $f.Root 'codex home/plugins/cache/market/plan-forge-flow/damaged/.codex-plugin/plugin.json'
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifest)) | Out-Null
            [IO.File]::WriteAllText($manifest, '{broken')
            Complete-Launcher (Start-Launcher $f) 23 "scan|argument with spaces`r`n" $true
            Assert-Equal $true ([IO.File]::Exists($old)) 'Uncertain inventory'
        }
        'only old abandoned owned downloads are cleaned' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            $paths = @()
            1..3 | ForEach-Object {
                $path = Join-Path $f.Cache ('1.0.0/planforge.' + [Guid]::NewGuid().ToString('N') + '.download')
                [IO.File]::WriteAllText($path, 'partial')
                [IO.File]::SetLastWriteTimeUtc($path, [DateTime]::UtcNow.AddDays(-30))
                $paths += $path
            }
            [IO.File]::SetLastWriteTimeUtc($paths[1], [DateTime]::UtcNow)
            $legacy = Join-Path $f.Cache '1.0.0/planforge.123.download'
            [IO.File]::WriteAllText($legacy, 'uncertain legacy owner')
            [IO.File]::SetLastWriteTimeUtc($legacy, [DateTime]::UtcNow.AddDays(-30))
            $lease = [IO.File]::Open($paths[2], 'Open', 'Read', 'Read')
            try { Complete-Launcher (Start-Launcher $f) } finally { $lease.Dispose() }
            Assert-Equal $false ([IO.File]::Exists($paths[0])) 'Abandoned download'
            foreach ($path in @($paths[1], $paths[2], $legacy)) { Assert-Equal $true ([IO.File]::Exists($path)) 'Protected download' }
        }
        'busy version lease is skipped' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            $old = Add-Version $f '0.1.0' 60
            Add-Version $f '0.2.0' 40 | Out-Null
            Add-Version $f '0.3.0' 30 | Out-Null
            $lease = [IO.File]::Open((Join-Path $f.Cache '0.1.0/.last-used'), 'OpenOrCreate', 'ReadWrite', 'None')
            try { Complete-Launcher (Start-Launcher $f) } finally { $lease.Dispose() }
            Assert-Equal $true ([IO.File]::Exists($old)) 'Version being prepared by another launcher'
        }
        'junctions do not let cleanup escape the binary cache' = {
            $f = New-Fixture
            Add-Version $f '1.0.0' | Out-Null
            Add-Version $f '0.8.0' 20 | Out-Null
            Add-Version $f '0.9.0' 10 | Out-Null
            $outside = Join-Path $f.Root 'outside cache'
            [IO.Directory]::CreateDirectory($outside) | Out-Null
            $sentinel = Join-Path $outside 'planforge.exe'
            [IO.File]::Copy($server, $sentinel)
            [IO.File]::SetLastWriteTimeUtc($sentinel, [DateTime]::UtcNow.AddDays(-60))
            New-Item -ItemType Junction -Path (Join-Path $f.Cache '0.1.0') -Target $outside | Out-Null
            Complete-Launcher (Start-Launcher $f)
            Assert-Equal $true ([IO.File]::Exists($sentinel)) 'File beyond junction'
            Assert-Equal 1 @(Get-ChildItem -LiteralPath $outside -Force).Count 'No marker beyond junction'
        }
        'a junction at the cache root prevents any maintenance writes' = {
            $f = New-Fixture
            $outside = Join-Path $f.Root 'outside cache'
            $outsideFixture = @{ Cache = $outside }
            $exe = Add-Version $outsideFixture '1.0.0'
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($f.Cache)) | Out-Null
            New-Item -ItemType Junction -Path $f.Cache -Target $outside | Out-Null
            Complete-Launcher (Start-Launcher $f) 23 "scan|argument with spaces`r`n" $true
            Assert-Equal $true ([IO.File]::Exists($exe)) 'Existing executable still usable'
            Assert-Equal 1 @(Get-ChildItem -LiteralPath (Split-Path $exe) -Force).Count 'No maintenance writes outside cache'
        }
        'inbox curl can write while the download lease is held' = {
            $f = New-Fixture
            $helper = [IO.File]::ReadAllText($helperPath)
            $helper = $helper.Replace('https://github.com/dlin10/ai-coding-plugins/releases/download/plan-forge-flow-v$version/planforge.exe', ([Uri]$server).AbsoluteUri)
            [IO.File]::WriteAllText((Join-Path $f.Plugin 'bin/planforge-cache.ps1'), $helper)
            Complete-Launcher (Start-Launcher $f)
            Assert-NoDownloads $f
        }
    }
    $failures = 0
    foreach ($test in $tests.GetEnumerator()) {
        try { & $test.Value; Write-Output "PASS: $($test.Key)" }
        catch { $failures++; Write-Output "FAIL: $($test.Key): $_" }
    }
    if ($failures) { throw "$failures launcher regression test(s) failed" }
}
finally {
    foreach ($p in $processes) {
        if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }
        $p.Dispose()
    }
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('planforge-launcher-tests-')) {
        throw "Unsafe fixture cleanup path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
