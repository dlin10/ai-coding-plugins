# Short-lived cache preparation. cmd remains the MCP server's parent; stdout is never used here.
# Compatible with inbox Windows PowerShell 5.1. No user profile or host module is loaded.
$ErrorActionPreference = 'Stop'
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'plan-forge-flow\bin'))
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = $env:VERSION
$versionPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$'

function Assert-OrdinaryPath([string]$Path) {
    # Check ancestors too: a normal child of a junction is still outside the cache we own.
    for ($item = [IO.FileInfo]::new($Path); $null -ne $item; $item = [IO.Directory]::GetParent($item.FullName)) {
        try { $attributes = [IO.File]::GetAttributes($item.FullName) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse point in cache or installation path: $($item.FullName)"
        }
    }
}

function Remove-CacheFile([string]$Path) {
    try {
        $full = [IO.Path]::GetFullPath($Path)
        if (-not $full.StartsWith($cacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
        Assert-OrdinaryPath $full
        # Acquire exclusivity and deletion in the SAME open. Never close a probe handle and then
        # delete by name: a downloader could open the file between those operations. Windows also
        # refuses this open for an executing image (server, collector, hook, or any other mode).
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

function Register-Installation {
    # Custom/local installations are discoverable on subsequent launches from another host too.
    # Immutable records avoid lost updates between launchers; publish only a complete record.
    $registrations = Join-Path $cacheRoot '.installations'
    Assert-OrdinaryPath $registrations
    [IO.Directory]::CreateDirectory($registrations) | Out-Null
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($pluginRoot.ToUpperInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    $destination = Join-Path $registrations ($key + '.path')
    if ([IO.File]::Exists($destination)) { return }
    $temporary = Join-Path $registrations ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $pluginRoot)
        try { [IO.File]::Move($temporary, $destination) }
        catch { if (-not [IO.File]::Exists($destination)) { throw } }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Add-InstalledVersions([string]$Root, $Versions) {
    Assert-OrdinaryPath $Root
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $found = $false
    foreach ($hostManifest in @('.codex-plugin\plugin.json', '.claude-plugin\plugin.json')) {
        $manifest = Join-Path $Root $hostManifest
        Assert-OrdinaryPath $manifest
        if (-not (Test-Path -LiteralPath $manifest)) { continue }
        $metadata = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
        if ($metadata.name -ne 'plan-forge-flow' -or $metadata.version -notmatch $versionPattern) {
            throw "Unrecognised Plan Forge installation: $Root"
        }
        $Versions.Add([string]$metadata.version) | Out-Null
        $found = $true
    }
    if (-not $found) { throw "Installation has no readable manifest: $Root" }
}

function Remove-UnusedCache {
    $installed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $installed.Add($version) | Out-Null
    Add-InstalledVersions $pluginRoot $installed
    foreach ($record in @(Get-ChildItem -LiteralPath (Join-Path $cacheRoot '.installations') -File -Filter '*.path')) {
        Assert-OrdinaryPath $record.FullName
        $root = [IO.File]::ReadAllText($record.FullName)
        if (-not [IO.Path]::IsPathRooted($root)) { throw 'Invalid installation record' }
        Add-InstalledVersions $root $installed
    }
    # Read only the host cache layout, not host databases/configuration. Conservatively protect
    # every retained plugin folder, even if the host no longer selects it. Include default homes
    # as well as overrides so switching between Codex and Claude cannot discard the other's exe.
    $homes = @((Join-Path $env:USERPROFILE '.codex'), (Join-Path $env:USERPROFILE '.claude'),
        $env:CODEX_HOME, $env:CLAUDE_CONFIG_DIR) | Where-Object { $_ } | Select-Object -Unique
    foreach ($homePath in $homes) {
        $plugins = Join-Path $homePath 'plugins\cache'
        Assert-OrdinaryPath $plugins
        if (-not (Test-Path -LiteralPath $plugins)) { continue }
        foreach ($marketplace in @(Get-ChildItem -LiteralPath $plugins -Directory)) {
            $package = Join-Path $marketplace.FullName 'plan-forge-flow'
            Assert-OrdinaryPath $package
            if (-not (Test-Path -LiteralPath $package)) { continue }
            foreach ($installation in @(Get-ChildItem -LiteralPath $package -Directory)) {
                Add-InstalledVersions $installation.FullName $installed
            }
        }
    }

    $cutoff = [DateTime]::UtcNow.AddDays(-7)
    $candidates = @()
    foreach ($directory in @(Get-ChildItem -LiteralPath $cacheRoot -Directory)) {
        if ($directory.Name -notmatch $versionPattern) { continue }
        try { Assert-OrdinaryPath $directory.FullName } catch { continue }
        # Legacy RANDOM names have no ownership guarantee: leave them alone. Our GUID files are
        # created exclusively and kept open through curl; even a seven-day download stays busy.
        foreach ($download in @(Get-ChildItem -LiteralPath $directory.FullName -File -Filter '*.download')) {
            if ($download.Name -cmatch '^planforge\.[0-9a-f]{32}\.download$' -and $download.LastWriteTimeUtc -lt $cutoff) {
                Remove-CacheFile $download.FullName | Out-Null
            }
        }
        $exe = Join-Path $directory.FullName 'planforge.exe'
        if ($installed.Contains($directory.Name) -or -not [IO.File]::Exists($exe)) { continue }
        $used = Join-Path $directory.FullName '.last-used'
        try { Assert-OrdinaryPath $used; Assert-OrdinaryPath $exe } catch { continue }
        $lastUse = if ([IO.File]::Exists($used) -and (Get-Item -LiteralPath $used).Length -gt 0) {
            [IO.File]::GetLastWriteTimeUtc($used)
        } else { [IO.File]::GetLastWriteTimeUtc($exe) }
        if ($lastUse -lt $cutoff) { $candidates += [pscustomobject]@{ Exe = $exe; LastUse = $lastUse } }
    }
    # Seven days of grace, plus two most recently used older rollback copies. Busy images may
    # survive beyond this bound. Never recurse or remove unknown files, installation records/locks.
    foreach ($candidate in @($candidates | Sort-Object LastUse -Descending | Select-Object -Skip 2)) {
        $directory = [IO.Path]::GetDirectoryName($candidate.Exe)
        # A launcher may have reused this version after inventory was read. Serialize that touch
        # with the final age check and deletion; retain the lease file so its identity never races.
        try { $lease = Open-VersionLease $directory } catch { continue }
        try {
            $lastUse = if ($lease.Length -gt 0) { [IO.File]::GetLastWriteTimeUtc($lease.Name) } else { [IO.File]::GetLastWriteTimeUtc($candidate.Exe) }
            if ($lastUse -lt $cutoff) { Remove-CacheFile $candidate.Exe | Out-Null }
        }
        finally { $lease.Dispose() }
    }
}

try {
    if ($version -notmatch $versionPattern) { throw 'Invalid release version' }
    $cache = Join-Path $cacheRoot $version
    $exe = Join-Path $cache 'planforge.exe'
    Assert-OrdinaryPath $cache
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    # Touch before checking the exe, under the same short lease as a cleaner. Downloads have
    # their own handles and run outside this lease, so concurrent cold launches need not wait.
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
        $url = "https://github.com/dlin10/ai-coding-plugins/releases/download/plan-forge-flow-v$version/planforge.exe"
        $download = Join-Path $cache ('planforge.' + [Guid]::NewGuid().ToString('N') + '.download')
        [Console]::Error.WriteLine("plan-forge-flow: downloading planforge.exe $version from $url")
        try {
            # curl can write, but maintenance cannot acquire an exclusive/delete handle while this
            # launcher owns the file, including the gap before curl opens it and after it closes it.
            $lease = [IO.File]::Open($download, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
            try {
                $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
                & $curl -fLsS -o $download $url
                $fetched = $LASTEXITCODE -eq 0
            }
            finally { $lease.Dispose() }
            if ($fetched) {
                # Never overwrite the winning exe. A failed promotion is an explicit branch; the
                # finally below removes only OUR download before cmd runs the winner.
                try { [IO.File]::Move($download, $exe) }
                catch { if (-not [IO.File]::Exists($exe)) { throw } }
            }
        }
        finally { Remove-CacheFile $download | Out-Null }
        if (-not [IO.File]::Exists($exe)) { throw "Could not fetch $url" }
    }
    # Maintenance is best effort and must not break a cached launch. Failed registration or an
    # incomplete installation inventory skips cleanup altogether.
    try {
        Register-Installation
        Remove-UnusedCache
    }
    catch { [Console]::Error.WriteLine("plan-forge-flow: cache cleanup skipped: $($_.Exception.Message)") }
    exit 0
}
catch {
    [Console]::Error.WriteLine("plan-forge-flow: cache preparation failed: $($_.Exception.Message)")
    exit 1
}
