<#
.SYNOPSIS
Regenerate the census of the shared frameworks Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0 into the built-in models.

.DESCRIPTION
The census is the members of the two frameworks, other than System.Private.CoreLib's, that runs of the demo and of eShop reach, with the
members their generation reads (ADR 0012 and ADR 0022, amendments of 2026-11-07). The script:

1. refuses a working tree with uncommitted changes to the engine, so that the recorded commit names the code that ran, and reads the
   engine fingerprint (`ComputeEngineFingerprint` of Core);
2. builds the CLI and tools/ConcurrencyHunter.FrameworkGeneration in Release;
3. runs `metrics --generation on --census <folder>` on demo/Demo.slnx and on $env:CH_ESHOP_ROOT's solution. The census run reads the built-in
   models without the census's own records, so every census member is generated, one the owner wrote included; it reads no project model
   and writes its generated files under -Out, never in the corpus;
4. writes the census into src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn and its record into
   skills/hunt/evals/models/framework-census.tsv: a model replaces the member's census entry wherever it stood, a refused member keeps the
   model it had, one without goes to the failed list;
5. settles it: rebuilds, runs both corpora again with `--settle`, which reads the built-in models as written, and writes again what they
   still generate, until a run of each generates no member of the two frameworks;
6. prints the report against the record it started from: models gained and changed, members newly failed, failed members that got a model,
   members that joined or left the census, and the size of the census files.

-Since generates nothing: it reads the recorded commit and lists the commits and files changed since, in three groups: the generator, the
engine whose accesses the generator reads, and the build inputs. Whether to regenerate is the owner's decision (ADR 0022).

Exit code: 0 when the census is written and settled, and after -Since; 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$Out = '',
    [int]$Settles = 3,
    [switch]$Since
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$root = (Resolve-Path "$PSScriptRoot/..").Path
$record = Join-Path $root 'skills/hunt/evals/models/framework-census.tsv'
$builtIn = Join-Path $root 'src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn'
$core = Join-Path $root 'src/ConcurrencyHunter.Core/ConcurrencyHunter.Core.csproj'
$cli = Join-Path $root 'src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj'
$tool = Join-Path $root 'tools/ConcurrencyHunter.FrameworkGeneration/ConcurrencyHunter.FrameworkGeneration.csproj'
$frameworks = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')

# What the generator is built from, by group, as git pathspecs relative to the plugin; the built-in models are what the census writes.
$generation = 'src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation'
$builtInSpec = 'src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn'
$groups = [ordered]@{
    'generator' = @($generation, 'tools/ConcurrencyHunter.FrameworkGeneration')
    'engine'    = @('src/ConcurrencyHunter.Analysis', 'src/ConcurrencyHunter.Core', ":(exclude)$generation", ":(exclude)$builtInSpec")
    'build'     = @('../Directory.Build.props', '../Directory.Packages.props', 'global.json')
}

if ($Since) {
    if (-not (Test-Path -LiteralPath $record)) {
        Write-Output "No census record at $record; regenerate the census first."
        exit 1
    }
    $line = Get-Content -LiteralPath $record -TotalCount 5 | Where-Object { $_.StartsWith('# commit ') } | Select-Object -First 1
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

if (-not $env:CH_ESHOP_ROOT) {
    Write-Output 'CH_ESHOP_ROOT is not set; the census needs eShop.'
    exit 1
}
$eshop = Join-Path $env:CH_ESHOP_ROOT 'src/eShopOnContainers-ServicesAndWebApps.sln'
$unrestored = @(Get-ChildItem -LiteralPath (Join-Path $env:CH_ESHOP_ROOT 'src') -Recurse -Filter '*.csproj' |
                Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'obj/project.assets.json')) })
if ($unrestored.Count -ne 0) {
    Write-Output "$($unrestored.Count) eShop project(s) are not restored; restore the solution first."
    exit 1
}
$inputs = @($groups.Values | ForEach-Object { $_ } | Where-Object { -not $_.StartsWith(':(exclude)') })
$dirty = @(git -C $root status --porcelain -- @($inputs) ":(exclude)$builtInSpec")
if ($dirty.Count -ne 0) {
    Write-Output "Uncommitted changes to what the generator is built from; commit them, so the census's commit names the code that ran:"
    $dirty | ForEach-Object { Write-Output "  $_" }
    exit 1
}
$commit = (git -C $root rev-parse HEAD).Trim()

if (-not $Out) {
    $Out = Join-Path $root "scratch/framework-census/$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$previous = Join-Path $Out 'previous.tsv'
if (Test-Path -LiteralPath $record) { Copy-Item -LiteralPath $record -Destination $previous -Force }

function Invoke-Logged([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $Out "$Name.log"
    & dotnet @Arguments *> $log
    if ($LASTEXITCODE -ne 0) {
        # The host, not the output stream: a caller that keeps or drops the function's output would swallow the tail.
        Get-Content -LiteralPath $log -Tail 30 | ForEach-Object { Write-Host $_ }
        throw "dotnet $($Arguments[0]) ($Name) exited $LASTEXITCODE; log: $log"
    }
    return $log
}

# The scope targets a census run left at an outcome no generated file keeps: the run limit cut them, or their generation threw. The
# harvest cannot see them, so the census would miss them while every user run generated them again.
function Assert-Kept([string]$Measurement) {
    $lost = @((Get-Content -LiteralPath $Measurement -Raw -Encoding utf8 | ConvertFrom-Json).coverage | ForEach-Object {
            $scope = $_.scopeId
            @($_.generationRefusals) | Where-Object { $null -ne $_ -and $_.cause -in @('budget', 'failed') } |
                ForEach-Object { "$scope $($_.member) ($($_.cause))" }
        })
    if ($lost.Count -ne 0) {
        $lost | ForEach-Object { Write-Host "  $_" }
        throw "$Measurement left $($lost.Count) scope target(s) at an outcome no generated file keeps."
    }
}

function Build([string]$Name) {
    foreach ($project in @($cli, $tool)) {
        Invoke-Logged "$Name-$([IO.Path]::GetFileNameWithoutExtension($project))" @('build', $project, '-c', 'Release', '--disable-build-servers', '-nodeReuse:false') | Out-Null
    }
}

# The census's engine fingerprint: the generator version without the built-in models, which this script is about to write.
$fingerprintLog = Invoke-Logged 'engine' @('msbuild', $core, '-restore', '-t:ComputeEngineFingerprint', '-getProperty:EngineFingerprint', '-nologo',
                                           '--disable-build-servers', '-nodeReuse:false')
$engine = (Get-Content -LiteralPath $fingerprintLog | Where-Object { $_ -match '^[0-9a-f]{64}$' } | Select-Object -Last 1)
if (-not $engine) { throw "No engine fingerprint in $fingerprintLog." }
Write-Output "Census of $commit, engine $engine, into $Out"

$corpora = [ordered]@{ 'demo' = (Join-Path $root 'demo/Demo.slnx'); 'eshop' = $eshop }
$runs = @()

function Invoke-Pass([int]$Pass, [bool]$Settle) {
    $generated = 0
    foreach ($name in $corpora.Keys) {
        $folder = Join-Path $Out "pass$Pass/$name"
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Ignore
        $measurement = Join-Path $Out "pass$Pass/$name.json"
        $arguments = @('run', '--project', $cli, '-c', 'Release', '--no-build', '--', 'metrics', '--target', $corpora[$name], '--generation', 'on',
                       '--census', $folder, '--out', $measurement)
        if ($Settle) { $arguments += '--settle' }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Logged "pass$Pass-$name" $arguments | Out-Null
        Assert-Kept $measurement
        $files = @($frameworks | ForEach-Object { Join-Path $folder ".concurrency-hunter/models/generated/$_/10.0" } |
                   Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.json' -File })
        $members = ($files | ForEach-Object { @((Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).members).Count } | Measure-Object -Sum).Sum
        Write-Host ("pass {0} {1}: {2:N0} s, {3} framework member(s) generated" -f $Pass, $name, $clock.Elapsed.TotalSeconds, [int]$members)
        $generated += [int]$members
        $script:runs += $folder
    }
    return $generated
}

function Write-Census([string]$Name) {
    $arguments = @('run', '--project', $tool, '-c', 'Release', '--no-build', '--', 'write', '--built-in', $builtIn, '--record', $record,
                   '--commit', $commit, '--engine', $engine, '--runs', ($script:runs -join ';'), '--previous', $previous)
    $log = Invoke-Logged $Name $arguments
    return $log
}

Build 'build1'
Invoke-Pass 1 $false | Out-Null
$report = Write-Census 'write1'
for ($pass = 2; $pass -le $Settles + 1; $pass++) {
    Build "build$pass"
    $generated = Invoke-Pass $pass $true
    if ($generated -eq 0) {
        Get-Content -LiteralPath $report
        Write-Output "Settled after $($pass - 1) write(s); the report compares with the record the regeneration started from."
        exit 0
    }
    $report = Write-Census "write$pass"
}

Get-Content -LiteralPath $report
Write-Output "The census did not settle in $Settles settling pass(es); see $Out."
exit 1
