# Run with Windows PowerShell: powershell.exe -NoProfile -File build/test-launcher.ps1
# Only the downloader is substituted; the launcher runs through cmd.exe, including the real move.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$launcher = [IO.File]::ReadAllText((Join-Path $pluginRoot 'bin/cachedet-launcher.cmd'))
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('cachedet-launcher-tests-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message`: expected '$Expected', got '$Actual'" }
}

try {
    $server = Join-Path $fixtureRoot 'server.exe'
    Add-Type -OutputAssembly $server -OutputType ConsoleApplication -TypeDefinition @'
public static class Server
{
    public static int Main(string[] args)
    {
        System.Console.WriteLine(string.Join("|", args));
        return 23;
    }
}
'@
    $downloader = Join-Path $fixtureRoot 'curl.exe'
    Add-Type -OutputAssembly $downloader -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.IO;
public static class Downloader
{
    public static int Main(string[] args)
    {
        File.AppendAllText(Environment.GetEnvironmentVariable("TEST_DOWNLOAD_LOG"), "fetch\n");
        string destination = args[2];
        string source = Environment.GetEnvironmentVariable("TEST_SERVER");
        string mode = Environment.GetEnvironmentVariable("TEST_DOWNLOAD_MODE");
        if (mode.StartsWith("concurrent"))
        {
            string installed = Path.Combine(Path.GetDirectoryName(destination), "cachedet.exe");
            File.Copy(source, installed);
            File.SetAttributes(installed, FileAttributes.ReadOnly);
        }
        if (mode.EndsWith("failure"))
        {
            File.WriteAllText(destination, "partial download");
            return 22;
        }
        File.Copy(source, destination);
        return 0;
    }
}
'@

    $cases = @(
        @{ Name = 'cold cache defaults to mcp'; Mode = 'success'; Args = ''; Fetches = 1 },
        @{ Name = 'cached executable is reused on repeated launches'; Cached = $true; Repeats = 2; Args = 'scan "argument with spaces"'; Fetches = 0 },
        @{ Name = 'locked cached executable is reused'; Cached = $true; Locked = $true; Fetches = 0 },
        @{ Name = 'bundled executable takes precedence'; Bundled = $true; Cached = $true; Fetches = 0 },
        @{ Name = 'concurrent installation survives failed replacement'; Mode = 'concurrent'; Fetches = 1 },
        @{ Name = 'failed fetch removes partial download'; Mode = 'failure'; Fetches = 1; Exit = 1 },
        @{ Name = 'failed fetch reuses concurrent installation'; Mode = 'concurrent-failure'; Fetches = 1 }
    )

    $failures = 0
    foreach ($case in $cases) {
        $lock = $null
        $process = $null
        $caseRoot = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString())
        $bin = Join-Path $caseRoot 'plugin with spaces/bin'
        $cache = Join-Path $caseRoot 'local app data/cache-detective/bin/test-version'
        New-Item -ItemType Directory -Path $bin, (Join-Path $bin '../.claude-plugin') | Out-Null
        [IO.File]::WriteAllText((Join-Path $bin '../.claude-plugin/plugin.json'), "{`r`n  `"version`": `"test-version`"`r`n}")
        $testLauncher = Join-Path $bin 'cachedet-launcher.cmd'
        [IO.File]::WriteAllText($testLauncher, $launcher.Replace('"%SystemRoot%\System32\curl.exe"', ('"' + $downloader + '"')))
        $cachedExe = Join-Path $cache 'cachedet.exe'
        if ($case.Cached) {
            New-Item -ItemType Directory -Path $cache | Out-Null
            Copy-Item -LiteralPath $server -Destination $cachedExe
        }
        if ($case.Bundled) {
            New-Item -ItemType Directory -Path (Join-Path $bin 'win-x64') | Out-Null
            Copy-Item -LiteralPath $server -Destination (Join-Path $bin 'win-x64/cachedet.exe')
            # A broken cached executable makes selecting the bundled one observable.
            [IO.File]::WriteAllText($cachedExe, 'must not run')
        }
        try {
            if ($case.Locked) {
                $lock = [IO.File]::Open($cachedExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            }
            $repeats = if ($case.Repeats) { $case.Repeats } else { 1 }
            for ($attempt = 0; $attempt -lt $repeats; $attempt++) {
                $start = New-Object Diagnostics.ProcessStartInfo
                $start.FileName = Join-Path $env:SystemRoot 'System32/cmd.exe'
                $start.Arguments = '/d /s /c ""' + $testLauncher + '" ' + $case.Args + '"'
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $start.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $caseRoot 'local app data'
                $start.EnvironmentVariables['TEST_SERVER'] = $server
                $start.EnvironmentVariables['TEST_DOWNLOAD_LOG'] = Join-Path $caseRoot 'downloads.log'
                $start.EnvironmentVariables['TEST_DOWNLOAD_MODE'] = if ($case.Mode) { $case.Mode } else { 'success' }
                $process = [Diagnostics.Process]::Start($start)
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(15000)) { throw 'Launcher timed out' }
                $output = $stdout.GetAwaiter().GetResult()
                $errors = $stderr.GetAwaiter().GetResult()
                $expectedExit = if ($case.Exit) { $case.Exit } else { 23 }
                Assert-Equal $expectedExit $process.ExitCode "Exit code ($errors)"
                $expectedOutput = if ($case.Exit) { '' } elseif ($case.Args) { "scan|argument with spaces`r`n" } else { "mcp`r`n" }
                Assert-Equal $expectedOutput $output 'stdout and forwarded arguments'
                if ($case.Fetches -eq 0) { Assert-Equal '' $errors 'Cache reuse must be silent' }
                $process.Dispose()
                $process = $null
            }
            $downloadLog = Join-Path $caseRoot 'downloads.log'
            $fetches = if (Test-Path -LiteralPath $downloadLog) { [IO.File]::ReadAllLines($downloadLog).Length } else { 0 }
            Assert-Equal $case.Fetches $fetches 'Download count'
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $cache -Filter '*.download' -ErrorAction SilentlyContinue).Count 'Temporary downloads left behind'
            if (-not $case.Exit -and -not $case.Bundled) {
                Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes($server))) ([Convert]::ToBase64String([IO.File]::ReadAllBytes($cachedExe))) 'Installed executable was preserved'
            }
            Write-Output "PASS: $($case.Name)"
        }
        catch {
            $failures++
            Write-Output "FAIL: $($case.Name): $_"
        }
        finally {
            if ($lock) { $lock.Dispose() }
            if ($process) {
                if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
                $process.Dispose()
            }
        }
    }
    if ($failures) { throw "$failures launcher regression test(s) failed" }
}
finally {
    # Only this run's uniquely named temporary fixture can be removed.
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('cachedet-launcher-tests-')) {
        throw "Unsafe fixture cleanup path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
