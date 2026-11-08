# Publishes the one release asset, a framework-dependent win-x64 single-file CodexSessionPulse.exe, into
# artifacts/publish/win-x64/, and smoke-tests that exact file: its version is the manifest's, --hook exits 0 without
# loading WPF, and --overlay starts, stays up without a Codex window and exits 0 on WM_CLOSE. The release workflow
# uploads what this produces; build/test-launcher.ps1 then runs the launcher against it.
# Run from anywhere with pwsh 7: ./build/package.ps1
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $pluginRoot 'src/CodexSessionPulse.App/CodexSessionPulse.App.csproj'
$output = Join-Path $pluginRoot 'artifacts/publish/win-x64'
$executable = Join-Path $output 'CodexSessionPulse.exe'
$manifestPath = Join-Path $pluginRoot '.codex-plugin/plugin.json'
$wpfAssemblies = @('PresentationFramework', 'PresentationCore', 'WindowsBase')

$manifestText = [IO.File]::ReadAllText($manifestPath)
$version = ($manifestText | ConvertFrom-Json).version
# The launcher and Directory.Build.props read the top-level line two spaces in; a reformatted manifest must fail here.
if ($manifestText -notmatch "(?m)^  `"version`": `"$([regex]::Escape($version))`"") {
    throw "The manifest's top-level version $version is not on a line of its own two spaces in, where the launcher reads it"
}

git -C $pluginRoot check-ignore -q -- 'artifacts/publish/win-x64/CodexSessionPulse.exe'
if ($LASTEXITCODE -ne 0) { throw 'artifacts/publish/ is not git-ignored; the exe must never be committed' }

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }

# ReadyToRun: about 8 ms off a warm hook for 0.4 MB (docs/compatibility.md 12.1 and 13.1). Symbols are embedded, so
# the asset is one file. Native libraries come from the shared framework, so nothing is extracted at start.
Write-Host "Publishing CodexSessionPulse.exe $version for win-x64"
dotnet publish $project -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true -p:DebugType=embedded -p:PublishDocumentationFile=false `
    -p:PublishReferencesDocumentationFiles=false -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$published = @(Get-ChildItem -LiteralPath $output -Recurse -File)
if ($published.Count -ne 1 -or $published[0].FullName -ne $executable) {
    throw "Expected exactly one published file, CodexSessionPulse.exe; found: $($published.FullName -join ', ')"
}

$reported = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
if ($reported -ne $version) { throw "The published exe reports $reported but the manifest declares $version" }

$smokeHome = Join-Path ([IO.Path]::GetTempPath()) ('csp-package-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path (Join-Path $smokeHome 'codex/sessions') | Out-Null
# Settings that match no window, so the smoke overlay never anchors to a real Codex Desktop.
[IO.File]::WriteAllText((Join-Path $smokeHome 'settings.json'),
    '{"schemaVersion":1,"codexProcessNames":["csp-no-such-process"],"codexExecutablePathFilters":[]}')

function Start-Exe([string]$Arguments, [string]$Stdin, [hashtable]$Environment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable, $Arguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.Environment['CODEX_SESSION_PULSE_HOME'] = $smokeHome
    $start.Environment['CODEX_HOME'] = Join-Path $smokeHome 'codex'
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Write($Stdin)
    $process.StandardInput.Close()
    return $process
}

Add-Type -Namespace CodexSessionPulsePackage -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
'@

$overlay = $null
try {
    $hook = Start-Exe '--hook' '{}' @{ CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES = '1' }
    $loaded = $hook.StandardOutput.ReadToEnd()
    if (-not $hook.WaitForExit(10000)) { throw '--hook did not exit within 10 s' }
    if ($hook.ExitCode -ne 0) { throw "--hook exited with $($hook.ExitCode)" }
    $names = @($loaded -split "`r?`n" | Where-Object { $_ })
    if ($names -notcontains 'CodexSessionPulse') { throw "--hook printed no assembly list: $loaded" }
    $wpf = @($names | Where-Object { $wpfAssemblies -contains $_ })
    if ($wpf.Count -ne 0) { throw "--hook loaded WPF: $($wpf -join ', ')" }
    Write-Host "--hook: exit 0, $($names.Count) assemblies, no WPF"

    $overlay = Start-Exe '--overlay' ''
    $log = Join-Path $smokeHome 'logs/overlay.log'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $tray = [IntPtr]::Zero
    while ($tray -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline -and -not $overlay.HasExited) {
        Start-Sleep -Milliseconds 200
        for ($window = [CodexSessionPulsePackage.Native]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'CodexSessionPulse.TrayWindow', [NullString]::Value);
             $window -ne [IntPtr]::Zero;
             $window = [CodexSessionPulsePackage.Native]::FindWindowEx([IntPtr]::Zero, $window, 'CodexSessionPulse.TrayWindow', [NullString]::Value)) {
            [uint32]$owner = 0
            [void][CodexSessionPulsePackage.Native]::GetWindowThreadProcessId($window, [ref]$owner)
            if ($owner -eq $overlay.Id) { $tray = $window; break }
        }
    }
    if ($tray -eq [IntPtr]::Zero) { throw "--overlay did not create its tray window (exited: $($overlay.HasExited))" }

    # Without a Codex window it must stay up: a manual start waits for Codex rather than exiting.
    Start-Sleep -Seconds 3
    if ($overlay.HasExited) { throw "--overlay exited by itself with $($overlay.ExitCode)" }
    $started = Select-String -LiteralPath $log -Pattern "overlay start: version $([regex]::Escape($version)),"
    if (-not $started) { throw "overlay.log does not record version $version" }

    [void][CodexSessionPulsePackage.Native]::PostMessage($tray, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $overlay.WaitForExit(10000)) { throw '--overlay did not exit within 10 s of WM_CLOSE' }
    if ($overlay.ExitCode -ne 0) { throw "--overlay exited with $($overlay.ExitCode)" }
    Write-Host '--overlay: started, stayed up without Codex, exit 0 on WM_CLOSE'
}
finally {
    if ($overlay -and -not $overlay.HasExited) { $overlay.Kill(); $overlay.WaitForExit() }
    Remove-Item -LiteralPath $smokeHome -Recurse -Force -ErrorAction SilentlyContinue
}

$file = Get-Item -LiteralPath $executable
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
Write-Host "Published $executable"
Write-Host "  version $version, $($file.Length) bytes, SHA256 $hash"
