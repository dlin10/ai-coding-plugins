# A behaviour snapshot that moved without a written reason is indistinguishable from a regression that
# was re-approved. This is the gate that makes re-approving one a deliberate act: the snapshot may move,
# but the changelog has to say why, in a line that was added since the run started.
[CmdletBinding()]
param(
    [string]$Base = '1b7c73742b16fa24a42054bc6e3e533d2d6c8055'
)

$ErrorActionPreference = 'Continue'
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Full paths, never leaf names: two of the three are called behaviour-snapshot.json, and a changelog
# line naming the leaf would excuse whichever of them moved.
$snapshots = @(
    'demo/behaviour-snapshot.json',
    'skills/scan/evals/eshop/behaviour-snapshot.json',
    'skills/scan/evals/eshop/expected.json'
)

Push-Location $pluginRoot
try {
    # --relative because the plugin is a subdirectory of the repository and the paths above are written
    # from the plugin root; comparing against the working tree because a snapshot already committed in
    # this run has to be explained just as much as one still unstaged.
    $changed = @(git diff --name-only --relative $Base -- $snapshots)
    if ($LASTEXITCODE -ne 0) {
        Write-Output "git diff failed for the snapshot paths; cannot tell whether a snapshot moved."
        exit 1
    }

    if ($changed.Count -eq 0) {
        Write-Output 'No behaviour snapshot moved since the base revision.'
        exit 0
    }

    $changelog = @(git diff --unified=0 --relative $Base -- 'CHANGELOG.md')
    if ($LASTEXITCODE -ne 0) {
        Write-Output 'git diff failed for CHANGELOG.md; cannot tell whether a snapshot was explained.'
        exit 1
    }

    # Added lines only. A path that was already mentioned before the run explains nothing new about the
    # snapshot that moved during it.
    $added = @($changelog | Where-Object { $_.StartsWith('+') -and -not $_.StartsWith('+++') })

    $unexplained = @($changed | Where-Object { $path = $_; -not ($added | Where-Object { $_.Contains($path) }) })
    foreach ($path in $unexplained) {
        Write-Output "$path moved without a reason: no line added to CHANGELOG.md names it."
    }

    if ($unexplained.Count -ne 0) { exit 1 }

    foreach ($path in $changed) {
        Write-Output "$path moved, and CHANGELOG.md says why."
    }
    exit 0
}
finally {
    Pop-Location
}
