<#
.SYNOPSIS
The source compilation and online/offline measurements of concurrency-hunter phase 5d run L2.

.DESCRIPTION
Builds the CLI in Release. By default reads the tab-separated L2 member list and generates its answers online,
first one member alone to fill the source cache, then the remaining members six at a time. A row succeeds only
when its process exits 0, its new JSON answer identifies the requested member and assembly, generation.code.kind
is sources, and generation completed with a model or refusal other than library-does-not-compile, timeout, budget,
cancelled or failed. Checks this criterion against built-in answers before running. Writes answers, logs,
outcomes.tsv and summary.md.

With -Measure, measures demo and eShop three times each: online with an empty source cache, offline with an empty
cache, and offline with the cache filled by the online measurement. Every measurement starts without generated
models; demo uses --recording. Existing generated models and models.lock.json are set aside and restored even on
failure. Keeps each measurement's generated files and compares the online and warmed offline files by path and SHA256.

Both modes isolate LOCALAPPDATA in a temporary directory under scratch/l2 and restore the caller's value afterwards.
All dotnet output goes to logs. Output folders are emptied before use; source caches are removed on exit.

.PARAMETER Members
The member list, with the same columns as L1. Defaults to build/check-phase5d-runL2-members.tsv.

.PARAMETER Out
The output folder to replace. Defaults to scratch/l2/members, or scratch/l2/measure with -Measure.

.PARAMETER Measure
Run the six metrics measurements instead of the member check. Uses CH_ESHOP_ROOT, defaulting to C:\Dev\eShopOnContainers.
#>
param([string]$Members, [string]$Out, [switch]$Measure)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$root = Split-Path $PSScriptRoot -Parent
$cliProject = Join-Path $root 'src/ConcurrencyHunter.Cli'
$scratch = Join-Path $root 'scratch/l2'
$header = 'assembly', 'package', 'version', 'framework', 'member'
$utf8 = [Text.UTF8Encoding]::new($false)
$savedLocalAppData = $env:LOCALAPPDATA
$cacheRoot = $null

function Fail([string]$message)
{
    throw "GATE: $message"
}

function Resolve-Argument([string]$value)
{
    return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($value))
}

function With-Separator([string]$path)
{
    return $path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
}

function Cell([string]$text)
{
    return ($text -replace '[\t\r\n|]+', ' ').Trim()
}

function Test-SourceAnswer([object]$answer)
{
    if ($answer.generation.code.kind -cne 'sources') { return $false }
    $outcome = if ($answer.reason) { [string]$answer.reason }
               elseif ($null -ne $answer.model) { 'model' }
               else { [string]$answer.modelReason }
    return $outcome -and $outcome -cnotin @('library-does-not-compile', 'timeout', 'budget', 'cancelled', 'failed')
}

# Refuse a removal or move outside the directory that owns the data.
function Assert-Within([string]$path, [string]$parent)
{
    $fullPath = [IO.Path]::GetFullPath($path)
    $fullParent = [IO.Path]::GetFullPath($parent)
    if (-not $fullPath.StartsWith((With-Separator $fullParent), [StringComparison]::OrdinalIgnoreCase))
    {
        Fail "$fullPath is not below $fullParent"
    }
}

function Invoke-Dotnet([string]$log, [string[]]$arguments)
{
    & dotnet @arguments *> $log
    $code = $LASTEXITCODE
    if ($code -ne 0)
    {
        Get-Content -LiteralPath $log -Tail 60 | ForEach-Object { Write-Host $_ }
        Fail "dotnet $($arguments[0]) exited $code; log: $log"
    }
}

# Repository root: nearest .concurrency-hunter, otherwise the containing git work tree, otherwise the solution folder.
function Get-RepositoryRoot([string]$solution)
{
    $folder = Split-Path $solution -Parent
    $gitRoot = $null
    for ($current = $folder; $current; $current = Split-Path $current -Parent)
    {
        if (Test-Path -LiteralPath (Join-Path $current '.concurrency-hunter') -PathType Container) { return $current }
        if (-not $gitRoot -and (Test-Path -LiteralPath (Join-Path $current '.git'))) { $gitRoot = $current }
    }
    if ($gitRoot) { return $gitRoot }
    return $folder
}

function Get-FileManifest([string]$folder)
{
    if (Test-Path -LiteralPath $folder)
    {
        Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\', '/')
            "$relative`t$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
        } | Sort-Object
    }
}

function Invoke-Measurements([string]$eshopRoot, [string]$eshopSolution)
{
    $projects = @(Get-ChildItem -LiteralPath $eshopRoot -Recurse -Filter '*.csproj' -File)
    if ($projects.Count -eq 0) { Fail "no eShop projects below $eshopRoot" }
    $missing = @($projects | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'obj/project.assets.json') -PathType Leaf) })
    if ($missing.Count -gt 0)
    {
        Invoke-Dotnet (Join-Path $outPath 'eshop-restore.log') @('restore', $eshopSolution, '--disable-build-servers', '-nodeReuse:false')
        $missing = @($projects | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'obj/project.assets.json') -PathType Leaf) })
        if ($missing.Count -gt 0) { Fail "eShop restore left assets missing: $($missing.FullName -join ', ')" }
    }

    $summary = [Collections.Generic.List[string]]::new()
    $summary.Add('| цель | режим | время, с | sources | coverage.generationRefusals (cause: число) | файлы совпадают с онлайн |')
    $summary.Add('| --- | --- | --- | --- | --- | --- |')
    $targets = @([pscustomobject]@{ Name = 'demo'; Solution = Join-Path $root 'demo/Demo.slnx'; Recording = $true },
                 [pscustomobject]@{ Name = 'eshop'; Solution = $eshopSolution; Recording = $false })
    foreach ($target in $targets)
    {
        $repository = Get-RepositoryRoot $target.Solution
        $models = Join-Path $repository '.concurrency-hunter/models'
        $generated = Join-Path $models 'generated'
        $lock = Join-Path $models 'models.lock.json'
        $backup = Join-Path $cacheRoot "$($target.Name)-saved"
        New-Item -ItemType Directory -Path $backup | Out-Null
        Assert-Within $generated $repository
        Assert-Within $lock $repository
        Assert-Within $backup $cacheRoot
        $savedGenerated = $false
        $savedLock = $false
        $ready = $false
        try
        {
            if (Test-Path -LiteralPath $generated)
            {
                Move-Item -LiteralPath $generated -Destination (Join-Path $backup 'generated')
                $savedGenerated = $true
            }
            if (Test-Path -LiteralPath $lock)
            {
                Move-Item -LiteralPath $lock -Destination (Join-Path $backup 'models.lock.json')
                $savedLock = $true
            }
            $ready = $true
            $onlineManifest = @()
            foreach ($mode in @('online-empty', 'offline-empty', 'offline-cached'))
            {
                $name = "$($target.Name)-$mode"
                $env:LOCALAPPDATA = Join-Path $cacheRoot "$($target.Name)-$(if ($mode -eq 'offline-empty') { 'empty' } else { 'online' })"
                $file = Join-Path $outPath "$name.json"
                $arguments = @('run', '--project', $cliProject, '-c', 'Release', '--no-build', '--', 'metrics',
                               '--target', $target.Solution, '--generation', 'on', '--out', $file)
                if ($target.Recording) { $arguments += '--recording' }
                if ($mode -ne 'online-empty') { $arguments += '--offline' }
                Write-Host "measure: $name"
                $watch = [Diagnostics.Stopwatch]::StartNew()
                Invoke-Dotnet (Join-Path $outPath "$name.log") $arguments
                $watch.Stop()
                if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Fail "metrics wrote no $file" }
                $json = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json
                if ($null -eq $json.sources) { Fail "$file has no sources object" }
                $destination = Join-Path $outPath "$name-generated"
                Assert-Within $destination $outPath
                if (Test-Path -LiteralPath $generated) { Move-Item -LiteralPath $generated -Destination $destination }
                $manifest = @(Get-FileManifest $destination)
                [IO.File]::WriteAllLines((Join-Path $outPath "$name-files.tsv"), [string[]]$manifest, $utf8)
                $same = '—'
                if ($mode -eq 'online-empty') { $onlineManifest = $manifest }
                if ($mode -eq 'offline-cached')
                {
                    $same = if (($onlineManifest -join "`n") -ceq ($manifest -join "`n")) { 'да' } else { 'нет' }
                }
                $refusals = @($json.coverage | ForEach-Object { $_.generationRefusals } | Where-Object { $null -ne $_ } |
                              Group-Object cause | Sort-Object Name | ForEach-Object { "$($_.Name): $($_.Count)" }) -join ', '
                $sources = $json.sources | ConvertTo-Json -Depth 20 -Compress
                $seconds = $watch.Elapsed.TotalSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)
                $summary.Add("| $($target.Name) | $mode | $seconds | $(Cell $sources) | $(Cell $refusals) | $same |")
                [IO.File]::WriteAllLines((Join-Path $outPath 'summary.md'), $summary, $utf8)
                if (Test-Path -LiteralPath $lock) { Remove-Item -LiteralPath $lock -Force }
            }
        }
        finally
        {
            # Only discard run output after the original files were safely set aside (or absent).
            if ($ready)
            {
                if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated -Recurse -Force }
                if (Test-Path -LiteralPath $lock) { Remove-Item -LiteralPath $lock -Force }
            }
            if ($savedGenerated) { Move-Item -LiteralPath (Join-Path $backup 'generated') -Destination $generated }
            if ($savedLock) { Move-Item -LiteralPath (Join-Path $backup 'models.lock.json') -Destination $lock }
        }
    }
    Write-Host "PASS: six measurements; see $(Join-Path $outPath 'summary.md')"
}

try
{
    $timeoutSample = '{"generation":{"code":{"kind":"sources"}},"reason":"timeout"}' | ConvertFrom-Json
    $modelSample = '{"generation":{"code":{"kind":"sources"}},"model":{}}' | ConvertFrom-Json
    if ((Test-SourceAnswer $timeoutSample) -or -not (Test-SourceAnswer $modelSample))
    {
        Fail 'the source compilation criterion must reject sources + timeout and accept sources + model'
    }
    $membersPath = if ($PSBoundParameters.ContainsKey('Members')) { Resolve-Argument $Members } else { Join-Path $root 'build/check-phase5d-runL2-members.tsv' }
    $outPath = if ($PSBoundParameters.ContainsKey('Out')) { Resolve-Argument $Out } else { Join-Path $scratch $(if ($Measure) { 'measure' } else { 'members' }) }
    $keptPaths = @($root, $membersPath)
    if ($Measure)
    {
        $eshopRoot = if ($env:CH_ESHOP_ROOT) { Resolve-Argument $env:CH_ESHOP_ROOT } else { 'C:\Dev\eShopOnContainers' }
        $eshopSolution = Join-Path $eshopRoot 'src/eShopOnContainers-ServicesAndWebApps.sln'
        if (-not (Test-Path -LiteralPath $eshopSolution -PathType Leaf)) { Fail "no eShop solution at $eshopSolution" }
        $keptPaths += $eshopRoot
    }
    else
    {
        if (-not (Test-Path -LiteralPath $membersPath -PathType Leaf)) { Fail "no member list at $membersPath" }
        $lines = [IO.File]::ReadAllLines($membersPath, $utf8)
        if ($lines.Count -eq 0 -or $lines[0].TrimStart([char]0xFEFF) -cne ($header -join "`t")) { Fail "invalid member list header in $membersPath" }
        $rows = [Collections.Generic.List[object]]::new()
        foreach ($line in ($lines | Select-Object -Skip 1))
        {
            if (-not $line.Trim()) { continue }
            $fields = $line.Split("`t")
            if ($fields.Count -ne $header.Count) { Fail "invalid field count in row $($rows.Count + 1)" }
            $rows.Add([pscustomobject]@{ Number = $rows.Count + 1; Assembly = $fields[0]; Package = $fields[1]; Version = $fields[2]
                                         Framework = $fields[3]; Member = $fields[4] })
        }
        if ($rows.Count -eq 0) { Fail "$membersPath holds no row" }
    }
    foreach ($kept in $keptPaths)
    {
        if ((With-Separator $kept).StartsWith((With-Separator $outPath), [StringComparison]::OrdinalIgnoreCase)) { Fail "$outPath would delete $kept" }
    }
    # Default and custom outputs are removable only as children of an existing parent, never a volume root.
    $outParent = Split-Path $outPath -Parent
    if (-not $outParent) { Fail "invalid output folder $outPath" }
    Assert-Within $outPath $outParent
    if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Recurse -Force }
    New-Item -ItemType Directory -Force $outPath | Out-Null
    $cacheRoot = Join-Path $scratch "localappdata-$([Guid]::NewGuid().ToString('N'))"
    Assert-Within $cacheRoot $scratch
    New-Item -ItemType Directory -Force $cacheRoot | Out-Null
    $env:LOCALAPPDATA = $cacheRoot
    Invoke-Dotnet (Join-Path $outPath 'build.log') @('build', (Join-Path $cliProject 'ConcurrencyHunter.Cli.csproj'), '-c', 'Release',
                                                  '--nologo', '--disable-build-servers', '-nodeReuse:false')
    if ($Measure)
    {
        Invoke-Measurements $eshopRoot $eshopSolution
    }
    else
    {
        $sourceCriterion = ${function:Test-SourceAnswer}.ToString()
        $generate = {
            $ErrorActionPreference = 'Stop'
            $PSNativeCommandUseErrorActionPreference = $false
            $row = $_
            $file = Join-Path $using:outPath "$($row.Number).json"
            $log = Join-Path $using:outPath "$($row.Number).log"
            $arguments = @('run', '--project', $using:cliProject, '-c', 'Release', '--no-build', '--', 'generate', '--assembly', $row.Assembly)
            if ($row.Package) { $arguments += @('--package', $row.Package) }
            $arguments += @('--version', $row.Version)
            if ($row.Framework) { $arguments += @('--framework', $row.Framework) }
            $arguments += @('--member', $row.Member, '--out', $file)
            $outcome = 'no-answer'
            $detail = ''
            $seconds = ''
            $kind = ''
            $reason = ''
            $fromSources = $false
            $problem = $null
            try
            {
                & dotnet @arguments *> $log
                if ($LASTEXITCODE -ne 0) { $problem = "the process exited $LASTEXITCODE; log: $log" }
                elseif (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $problem = 'the process wrote no file' }
                else
                {
                    $json = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json
                    if ($json.member -cne $row.Member -or $json.assembly.name -cne $row.Assembly -or
                        $json.assembly.version -cne $row.Version -or [string]$json.assembly.package -cne $row.Package)
                    {
                        $problem = 'the answer does not identify the requested member and assembly'
                    }
                    else
                    {
                        if ($json.reason) { $outcome = [string]$json.reason }
                        elseif ($null -ne $json.model) { $outcome = 'model' }
                        elseif ($json.modelReason) { $outcome = [string]$json.modelReason }
                        else { $outcome = 'unclassified' }
                        $detail = [string]$json.detail
                        if (-not $detail -and @($json.causes).Count -gt 0) { $detail = 'causes: ' + ($json.causes -join ', ') }
                        if ($json.generation.code.detail) { $detail = (@([string]$json.generation.code.detail, $detail) | Where-Object { $_ }) -join '; ' }
                        $kind = [string]$json.generation.code.kind
                        $reason = [string]$json.generation.code.reason
                        $fromSources = & ([scriptblock]::Create($using:sourceCriterion)) $json
                        if ($null -ne $json.generation.seconds)
                        {
                            $seconds = ([double]$json.generation.seconds).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)
                        }
                    }
                }
            }
            catch { $problem = $_.Exception.Message }
            if ($problem) { $detail = $problem }
            [pscustomobject]@{ Number = $row.Number; Member = $row.Member; Assembly = "$($row.Assembly) $($row.Version)"; Success = -not $problem
                               Outcome = $outcome; Detail = $detail; Seconds = $seconds; Code = $kind; CodeReason = $reason; FromSources = $fromSources }
        }
        Write-Host "build: the CLI passes; running the first member alone, then $($rows.Count - 1) members six at a time"
        $started = Get-Date
        $answers = @($rows[0] | ForEach-Object -ThrottleLimit 1 -Parallel $generate)
        $answers += @($rows | Select-Object -Skip 1 | ForEach-Object -ThrottleLimit 6 -Parallel $generate)
        $answers = @($answers | Sort-Object Number)
        Write-Host ("run: {0} rows in {1:0} s" -f $answers.Count, ((Get-Date) - $started).TotalSeconds)
        $table = @("row`tmember`tassembly`tcode`tcodeReason`toutcome`tdetail`tseconds")
        foreach ($answer in $answers)
        {
            $table += (@($answer.Number, (Cell $answer.Member), (Cell $answer.Assembly), (Cell $answer.Code), (Cell $answer.CodeReason),
                         (Cell $answer.Outcome), (Cell $answer.Detail), $answer.Seconds) -join "`t")
        }
        [IO.File]::WriteAllLines((Join-Path $outPath 'outcomes.tsv'), [string[]]$table, $utf8)
        $sources = @($answers | Where-Object { $_.Success -and $_.FromSources }).Count
        $summary = @('| исход | число |', '| --- | --- |', "| из исходников | $sources |")
        foreach ($group in @($answers | Group-Object Outcome | Sort-Object @{ Expression = 'Count'; Descending = $true }, Name))
        {
            $summary += "| $($group.Name) | $($group.Count) |"
        }
        $summary += "| всего | $($answers.Count) |"
        [IO.File]::WriteAllLines((Join-Path $outPath 'summary.md'), [string[]]$summary, $utf8)
        $failed = @($answers | Where-Object { -not $_.Success -or -not $_.FromSources })
        foreach ($answer in ($failed | Select-Object -First 20))
        {
            $shown = Cell $answer.Detail
            Write-Host ("row {0}: {1}, code {2} ({3}): {4}" -f $answer.Number, $answer.Outcome, $answer.Code, $answer.CodeReason,
                        $shown.Substring(0, [Math]::Min(200, $shown.Length)))
        }
        if ($failed.Count -gt 0) { Fail "$($failed.Count) of $($answers.Count) rows failed; see $(Join-Path $outPath 'outcomes.tsv')" }
        Write-Host "PASS: $($answers.Count) rows completed from sources; see $(Join-Path $outPath 'summary.md')"
    }
    exit 0
}
catch
{
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
}
finally
{
    $env:LOCALAPPDATA = $savedLocalAppData
    if ($cacheRoot -and (Test-Path -LiteralPath $cacheRoot))
    {
        Assert-Within $cacheRoot $scratch
        $unrestored = @(Get-ChildItem -LiteralPath $cacheRoot -Directory -Filter '*-saved' | ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Force
        })
        if ($unrestored.Count -gt 0) { Write-Host "Saved originals remain at $cacheRoot; retaining it for recovery." }
        else { Remove-Item -LiteralPath $cacheRoot -Recurse -Force }
    }
}
