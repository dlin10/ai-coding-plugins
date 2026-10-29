# Short-lived cache preparation for the launcher: fetches roslyn-mcp.exe for the plugin's version, then retires the
# versions nobody has started for a week. cmd remains the shim's parent; stdout is never used here. Compatible with
# inbox Windows PowerShell 5.1; no user profile or host module is loaded. The file handling follows plan-forge-flow's
# planforge-cache.ps1 (#203). See docs/adr/0008.
$ErrorActionPreference = 'Stop'
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'roslyn-mcp\bin'))
$version = $env:VERSION
$versionPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$'
$cutoff = [DateTime]::UtcNow.AddDays(-7)

function Assert-OrdinaryPath([string]$Path) {
    # Check ancestors too: a normal child of a junction is still outside the cache we own.
    for ($item = [IO.FileInfo]::new($Path); $null -ne $item; $item = [IO.Directory]::GetParent($item.FullName)) {
        try { $attributes = [IO.File]::GetAttributes($item.FullName) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse point in cache path: $($item.FullName)"
        }
    }
}

function Remove-CacheFile([string]$Path) {
    try {
        $full = [IO.Path]::GetFullPath($Path)
        if (-not $full.StartsWith($cacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
        Assert-OrdinaryPath $full
        # Acquire exclusivity and deletion in the SAME open. Never close a probe handle and then delete by name: a
        # downloader could open the file between those operations. Windows also refuses this open for a running
        # image, so a version that still serves a shim or a daemon keeps its executable.
        $file = [IO.FileStream]::new($full, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
        $file.Dispose()
        return $true
    }
    catch { return $false } # Busy, read-only, inaccessible or uncertain: leave it alone.
}

function Open-VersionLease([string]$Directory) {
    $path = Join-Path $Directory '.last-used'
    Assert-OrdinaryPath $path
    return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}

function Get-LastUse([string]$Directory) {
    # Every launch writes the lease, so its time is the version's last start; a version fetched before it had one
    # counts from its download.
    $used = Join-Path $Directory '.last-used'
    if ([IO.File]::Exists($used) -and ([IO.FileInfo]::new($used)).Length -gt 0) { return [IO.File]::GetLastWriteTimeUtc($used) }
    $exe = Join-Path $Directory 'roslyn-mcp.exe'
    if ([IO.File]::Exists($exe)) { return [IO.File]::GetLastWriteTimeUtc($exe) }
    return [DateTime]::MinValue
}

function Remove-Version([string]$Directory) {
    # A launcher may have reused this version after the inventory was read. Serialize its touch with the final age
    # check and the deletion; keep the lease file so that its identity never races.
    try { $lease = Open-VersionLease $Directory } catch { return }
    try {
        if ((Get-LastUse $Directory) -ge $cutoff) { return }
        $exe = Join-Path $Directory 'roslyn-mcp.exe'
        if ([IO.File]::Exists($exe) -and -not (Remove-CacheFile $exe)) { return }
        # The launcher points the single-file host here, which unpacks the executable into a folder named after it.
        # A build host still running from it holds some files; a later launch tries again.
        $unpacked = Join-Path $Directory 'roslyn-mcp'
        try {
            Assert-OrdinaryPath $unpacked
            if ([IO.Directory]::Exists($unpacked)) { [IO.Directory]::Delete($unpacked, $true) }
        }
        catch { }
    }
    finally { $lease.Dispose() }
}

function Remove-UnusedVersions {
    $candidates = @()
    $leftovers = @()
    foreach ($directory in @(Get-ChildItem -LiteralPath $cacheRoot -Directory)) {
        if ($directory.Name -notmatch $versionPattern) { continue }
        try { Assert-OrdinaryPath $directory.FullName } catch { continue }
        # Our download files are created exclusively and kept open through curl, so even a week-long download stays
        # busy. Any other name has no ownership guarantee: leave it alone.
        foreach ($download in @(Get-ChildItem -LiteralPath $directory.FullName -File -Filter '*.download')) {
            if ($download.Name -cmatch '^roslyn-mcp\.[0-9a-f]{32}\.download$' -and $download.LastWriteTimeUtc -lt $cutoff) {
                Remove-CacheFile $download.FullName | Out-Null
            }
        }
        if ($directory.Name -eq $version) { continue }
        $lastUse = Get-LastUse $directory.FullName
        if ($lastUse -ge $cutoff) { continue }
        if ([IO.File]::Exists((Join-Path $directory.FullName 'roslyn-mcp.exe'))) {
            $candidates += [pscustomobject]@{ Directory = $directory.FullName; LastUse = $lastUse }
        }
        elseif ([IO.Directory]::Exists((Join-Path $directory.FullName 'roslyn-mcp'))) {
            $leftovers += $directory.FullName
        }
    }
    # A week of grace, plus the two most recently used older versions kept whole for a rollback. A running version
    # survives beyond this bound. Never remove unknown files or the lease files.
    foreach ($candidate in @($candidates | Sort-Object LastUse -Descending | Select-Object -Skip 2)) {
        Remove-Version $candidate.Directory
    }
    foreach ($leftover in $leftovers) { Remove-Version $leftover }
}

try {
    if ($version -notmatch $versionPattern) { throw 'Invalid release version' }
    $cache = Join-Path $cacheRoot $version
    $exe = Join-Path $cache 'roslyn-mcp.exe'
    Assert-OrdinaryPath $cache
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    # Touch before checking the exe, under the same short lease as a cleaner. Downloads have their own handles and
    # run outside this lease, so concurrent cold launches need not wait.
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while ($true) {
        try { $useLease = Open-VersionLease $cache; break }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $until) { throw }
            Start-Sleep -Milliseconds 25
        }
    }
    try { $useLease.WriteByte(1) } finally { $useLease.Dispose() }
    if (-not [IO.File]::Exists($exe)) {
        $url = "https://github.com/dlin10/ai-coding-plugins/releases/download/roslyn-mcp-v$version/roslyn-mcp.exe"
        $download = Join-Path $cache ('roslyn-mcp.' + [Guid]::NewGuid().ToString('N') + '.download')
        [Console]::Error.WriteLine("roslyn-mcp: downloading roslyn-mcp.exe $version from $url")
        try {
            # curl can write, but a cleaner cannot acquire an exclusive or delete handle while this launcher owns the
            # file, including the gap before curl opens it and after it closes it.
            $lease = [IO.File]::Open($download, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
            try {
                $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
                & $curl -fLsS -o $download $url
                $fetched = $LASTEXITCODE -eq 0
            }
            finally { $lease.Dispose() }
            if ($fetched) {
                # Never overwrite an executable another launcher put there first: it may already be running. The
                # finally below removes only OUR download before cmd runs the winner.
                try { [IO.File]::Move($download, $exe) }
                catch { if (-not [IO.File]::Exists($exe)) { throw } }
            }
        }
        finally { Remove-CacheFile $download | Out-Null }
        if (-not [IO.File]::Exists($exe)) { throw "Could not fetch $url" }
    }
    # Maintenance is best effort and must not break a cached launch.
    try { Remove-UnusedVersions }
    catch { [Console]::Error.WriteLine("roslyn-mcp: cache cleanup skipped: $($_.Exception.Message)") }
    exit 0
}
catch {
    [Console]::Error.WriteLine("roslyn-mcp: cache preparation failed: $($_.Exception.Message)")
    exit 1
}
