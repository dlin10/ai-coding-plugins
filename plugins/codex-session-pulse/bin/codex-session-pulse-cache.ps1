# The launcher's slow path: downloads CodexSessionPulse.exe of the plugin's version from its GitHub release into
# <root>\bin\<version>\, where the launcher's "if exist" finds it from then on. cmd sets VERSION and ROOT and gives this
# script neither stdin (the hook payload is the exe's) nor stdout (Codex reads it); every message goes to stderr.
# Inbox Windows PowerShell 5.1 compatible, no profile. The download goes to a file of its own and is renamed into place,
# never over an existing exe, so parallel cold hooks and an interrupted download never leave a broken exe behind
# (the pattern of roslyn-mcp's docs/adr/0008). This is the only network request the plugin makes.
$ErrorActionPreference = 'Stop'
$releases = 'https://github.com/dlin10/ai-coding-plugins/releases/download'
$version = $env:VERSION
$versionPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'

try {
    if ($version -notmatch $versionPattern) { throw "invalid plugin version '$version'" }
    if (-not [IO.Path]::IsPathRooted($env:ROOT)) { throw "the cache root '$($env:ROOT)' is not an absolute path" }

    $cache = Join-Path $env:ROOT "bin\$version"
    $exe = Join-Path $cache 'CodexSessionPulse.exe'
    [IO.Directory]::CreateDirectory($cache) | Out-Null

    # Codex ends a hook at its timeout, job and all, so a killed download can leave its file; a day later it is no
    # one's. A file another download still writes is open and refuses the delete.
    foreach ($stale in @(Get-ChildItem -LiteralPath $cache -File -Filter 'CodexSessionPulse.*.download')) {
        if ($stale.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-1)) {
            try { $stale.Delete() } catch { }
        }
    }

    # A parallel hook may have finished first.
    if ([IO.File]::Exists($exe)) { exit 0 }

    $url = "$releases/codex-session-pulse-v$version/CodexSessionPulse.exe"
    $download = Join-Path $cache ('CodexSessionPulse.' + [Guid]::NewGuid().ToString('N') + '.download')
    [Console]::Error.WriteLine("codex-session-pulse: downloading CodexSessionPulse.exe $version from $url")

    try {
        # SessionStart, the hook that normally gets here first, has 60 s; leave it time to start the exe.
        & (Join-Path $env:SystemRoot 'System32\curl.exe') -fLsS --retry 2 --connect-timeout 10 --max-time 45 -o $download $url
        if ($LASTEXITCODE -ne 0) { throw "curl exited with $LASTEXITCODE for $url" }

        # A proxy's or portal's HTML page is not an executable; refuse it rather than install it.
        $head = New-Object byte[] 2
        $file = [IO.File]::OpenRead($download)
        try { $read = $file.Read($head, 0, 2) } finally { $file.Dispose() }
        if ($read -ne 2 -or $head[0] -ne 0x4D -or $head[1] -ne 0x5A) { throw "$url did not return an executable" }

        # Never over an exe another hook put there first: it may already be running.
        try { [IO.File]::Move($download, $exe) }
        catch { if (-not [IO.File]::Exists($exe)) { throw } }
    }
    finally {
        if ([IO.File]::Exists($download)) { [IO.File]::Delete($download) }
    }

    exit 0
}
catch {
    [Console]::Error.WriteLine("codex-session-pulse: download failed: $($_.Exception.Message)")
    exit 1
}
