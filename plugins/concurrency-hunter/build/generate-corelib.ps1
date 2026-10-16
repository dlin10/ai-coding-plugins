<#
.SYNOPSIS
Generate every public member of the installed System.Private.CoreLib and compare the snapshot with the committed one.

.DESCRIPTION
Builds tools/ConcurrencyHunter.CoreLibGeneration in Release, runs its `generate` into -Out (by default
scratch/corelib-generation/<time>) and compares <out>/snapshot.tsv with skills/hunt/evals/models/corelib-generation.tsv. A run
over every member takes about two hours. -Members names a file of declaration ids, one per line, to generate and compare only those.
Passing the same -Out again resumes a run that was cut short. <out>/answers.jsonl holds every answer document, with the words of each
cause, and <out>/failures.txt the members whose generation threw.

-Record copies the snapshot of a run over every member over the committed one, and refuses a run over -Members or one with a
`timeout` or `threw` row, whose rows depend on the machine. Account for every moved line before re-recording.

The snapshot's header names the commit the generator was built from (`# commit <sha>`). Whether to generate again is the owner's
decision, and the commit is what it rests on. -Record therefore refuses, before building, a working tree with uncommitted changes to
anything the generator is built from, and a resumed -Out that a run on another commit started.

-Since generates nothing: it reads the recorded commit and lists the commits and files changed since, in three groups: the
generator, the engine whose accesses the generator reads, and the build inputs (package versions, SDK). A change of the engine can
move models as surely as a change of the generator; -Members over the members it touches shows whether it did.

Exit code: 0 when the snapshots are the same, after -Record and after -Since, 1 when they differ or the run failed.
#>
[CmdletBinding()]
param(
    [string]$Out = '',
    [int]$Parallelism = 0,
    [string]$Members = '',
    [switch]$Record,
    [switch]$Since
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path "$PSScriptRoot/..").Path
$committed = Join-Path $root 'skills/hunt/evals/models/corelib-generation.tsv'
$project = Join-Path $root 'tools/ConcurrencyHunter.CoreLibGeneration/ConcurrencyHunter.CoreLibGeneration.csproj'

# What the generator is built from, by group, as git pathspecs relative to the plugin. The generator's own code sits inside Core.
$generation = 'src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation'
$groups = [ordered]@{
    'generator' = @($generation, 'tools/ConcurrencyHunter.CoreLibGeneration')
    'engine'    = @('src/ConcurrencyHunter.Analysis', 'src/ConcurrencyHunter.Core', ":(exclude)$generation")
    'build'     = @('../Directory.Build.props', '../Directory.Packages.props', 'global.json')
}
$inputs = @($groups.Values | ForEach-Object { $_ } | Where-Object { -not $_.StartsWith(':(exclude)') })

if ($Since) {
    if (-not (Test-Path -LiteralPath $committed)) {
        Write-Output "No committed snapshot at $committed; record a run over every member with -Record."
        exit 1
    }
    $line = Get-Content -LiteralPath $committed -TotalCount 5 | Where-Object { $_.StartsWith('# commit ') } | Select-Object -First 1
    if (-not $line) {
        Write-Output "$committed names no commit; it was recorded before the snapshot carried one."
        exit 1
    }
    $base = $line.Substring('# commit '.Length)
    git -C $root cat-file -e "$base^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Output "The recorded commit $base is not in this clone; fetch it first."
        exit 1
    }
    Write-Output "Recorded on $base; HEAD is $(git -C $root rev-parse HEAD)."
    foreach ($group in $groups.Keys) {
        $log = @(git -C $root log --oneline "$base..HEAD" -- @($groups[$group]))
        Write-Output ''
        if ($log.Count -eq 0) {
            Write-Output "${group}: unchanged"
            continue
        }
        Write-Output "${group}: $($log.Count) commit(s)"
        $log | ForEach-Object { Write-Output "  $_" }
        git -C $root diff --stat "$base..HEAD" -- @($groups[$group]) | ForEach-Object { Write-Output "  $_" }
    }
    exit 0
}

$commit = (git -C $root rev-parse HEAD).Trim()
if ($Record) {
    $dirty = @(git -C $root status --porcelain -- @($inputs))
    if ($dirty.Count -ne 0) {
        Write-Output "Uncommitted changes to what the generator is built from; commit them, so the snapshot's commit names the code that ran:"
        $dirty | ForEach-Object { Write-Output "  $_" }
        exit 1
    }
}

if ($Record -and $Members) {
    Write-Output 'A run over -Members is no snapshot to record.'
    exit 1
}
if (-not $Out) {
    $Out = Join-Path $root "scratch/corelib-generation/$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

# A resumed run keeps the rows of the commit that started it; one record names one commit.
$started = Join-Path $Out 'commit.txt'
if (Test-Path -LiteralPath $started) {
    $first = (Get-Content -LiteralPath $started -Raw).Trim()
    if ($first -ne $commit) {
        if ($Record) {
            Write-Output "$Out was started on $first, and HEAD is $commit; record a run started on one commit."
            exit 1
        }
        Write-Output "Warning: $Out was started on $first, and HEAD is $commit; its rows mix both."
    }
} else {
    [IO.File]::WriteAllText($started, $commit)
}

$buildLog = Join-Path $Out 'build.log'
dotnet build $project -c Release --disable-build-servers -nodeReuse:false *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $buildLog -Tail 30
    Write-Output "The tool did not build; see $buildLog."
    exit 1
}

$generate = @('generate', '--out', $Out, '--commit', $commit)
$compare = @()
if ($Parallelism -gt 0) {
    $generate += @('--parallelism', $Parallelism)
}
if ($Members) {
    $list = (Resolve-Path $Members).Path
    $generate += @('--members', $list)
    $compare += @('--members', $list)
}

$log = Join-Path $Out 'generate.log'
Write-Output "Generating into $Out; log: $log"
dotnet run --project $project -c Release --no-build -- @generate *> $log
$code = $LASTEXITCODE
Get-Content -LiteralPath $log -Tail 25
if ($code -ne 0) {
    Write-Output "The generation failed with exit code $code."
    exit 1
}

$fresh = Join-Path $Out 'snapshot.tsv'
if ($Record) {
    $unstable = @(Select-String -LiteralPath $fresh -Pattern "`t(timeout|threw)`t")
    if ($unstable.Count -ne 0) {
        Write-Output "$($unstable.Count) member(s) timed out or threw, so the run is no snapshot to record; see $Out."
        exit 1
    }
    Copy-Item -LiteralPath $fresh -Destination $committed -Force
    Write-Output "Recorded $committed."
    exit 0
}
if (-not (Test-Path -LiteralPath $committed)) {
    Write-Output "No committed snapshot at $committed; record a run over every member with -Record."
    exit 1
}

$compareLog = Join-Path $Out 'compare.log'
dotnet run --project $project -c Release --no-build -- compare $committed $fresh @compare *> $compareLog
$code = $LASTEXITCODE
Get-Content -LiteralPath $compareLog
exit $code
