<#
.SYNOPSIS
The measurement of concurrency-hunter phase 5d run L1: the library members that answered library-does-not-compile before the run.

.DESCRIPTION
Reads the member list (a header and one row per member, fields separated by a tab: assembly, package, version, framework, member;
an empty package or framework is not passed), deletes the output folder whole and creates it anew, builds the CLI in Release once,
and then starts one process per row, six at a time:

    dotnet run --project src/ConcurrencyHunter.Cli -c Release --no-build -- generate --assembly ... [--package ...] --version ...
        [--framework ...] --member ... --out <output folder>/<row number>.json

A row is a success only when its process exited 0, its file appeared in this run (the folder was emptied first, and a usage error
writes no file), the file parses as JSON, and its member, assembly.name, assembly.version and assembly.package are the row's
fields (SPEC TD-034b: generate writes them as asked). The script exits 1 when a row is not a success or answered
library-does-not-compile.

The script writes only in the output folder, but for the build output of the CLI project. The output folder holds:
  <row number>.json   the answer of a row (and <row number>.log, when its process printed something and the row failed)
  build.log           the CLI build
  outcomes.tsv        row, member, assembly (name and version), outcome, detail, generation.seconds; the outcome is the answer's
                      reason, else "model" when it holds a model, else its modelReason; a row that is not a success has the outcome
                      no-answer and the problem as its detail
  summary.md          the outcomes counted, and the assemblies with their outcomes counted

.PARAMETER Members
The member list. By default build/check-phase5d-runL1-members.tsv.

.PARAMETER Out
The output folder, which the script deletes whole. By default scratch/l1/members.
#>
param([string]$Members, [string]$Out)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path $PSScriptRoot -Parent
$cliProject = Join-Path $root 'src/ConcurrencyHunter.Cli'
$header = 'assembly', 'package', 'version', 'framework', 'member'
$throttle = 6

function Fail([string]$message)
{
    throw "GATE: $message"
}

# An argument as a full path; a relative one is relative to the caller's location.
function Resolve-Argument([string]$value)
{
    return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($value))
}

# A path with one trailing separator, so that a prefix test compares whole path segments.
function With-Separator([string]$path)
{
    return $path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
}

# A text as one cell: no tab and no line break.
function Cell([string]$text)
{
    return ($text -replace '[\t\r\n]+', ' ').Trim()
}

try
{
    $membersPath = if ($PSBoundParameters.ContainsKey('Members')) { Resolve-Argument $Members } else { Join-Path $root 'build/check-phase5d-runL1-members.tsv' }
    $outPath = if ($PSBoundParameters.ContainsKey('Out')) { Resolve-Argument $Out } else { Join-Path $root 'scratch/l1/members' }

    if (-not (Test-Path -LiteralPath $membersPath -PathType Leaf))
    {
        Fail "the member list $membersPath does not exist"
    }
    $lines = @([IO.File]::ReadAllLines($membersPath, [Text.UTF8Encoding]::new($false)))
    if ($lines.Count -eq 0 -or (($lines[0].TrimStart([char]0xFEFF).Split("`t")) -join "`t") -ne ($header -join "`t"))
    {
        Fail "the first line of $membersPath is not the header: $($header -join ' <tab> ')"
    }
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($line in ($lines | Select-Object -Skip 1))
    {
        if (-not $line.Trim())
        {
            continue
        }
        $fields = $line.Split("`t")
        if ($fields.Count -ne $header.Count)
        {
            Fail "row $($rows.Count + 1) of $membersPath has $($fields.Count) fields, not $($header.Count)"
        }
        $rows.Add([pscustomobject]@{ Number = $rows.Count + 1; Assembly = $fields[0]; Package = $fields[1]; Version = $fields[2]
                                     Framework = $fields[3]; Member = $fields[4] })
    }
    if ($rows.Count -eq 0)
    {
        Fail "$membersPath holds no row"
    }

    # The folder is deleted whole: not the repository, one of its parents, nor the folder of the member list.
    foreach ($kept in @($root, (Split-Path $membersPath -Parent), $membersPath))
    {
        if ((With-Separator $kept).StartsWith((With-Separator $outPath), [StringComparison]::OrdinalIgnoreCase))
        {
            Fail "the output folder $outPath would delete $kept"
        }
    }
    if (Test-Path -LiteralPath $outPath)
    {
        Remove-Item -LiteralPath $outPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force $outPath | Out-Null

    $buildLog = Join-Path $outPath 'build.log'
    & dotnet build (Join-Path $cliProject 'ConcurrencyHunter.Cli.csproj') -c Release --nologo --disable-build-servers -nodeReuse:false *> $buildLog
    $code = $LASTEXITCODE
    if ($code -ne 0)
    {
        Get-Content $buildLog -Tail 40 | ForEach-Object { Write-Host $_ }
        Fail "dotnet build of the CLI exited $code; log: $buildLog"
    }
    Write-Host "build: the CLI passes; running $($rows.Count) members, $throttle at a time"

    $started = Get-Date
    $answers = $rows | ForEach-Object -ThrottleLimit $throttle -Parallel {
        $row = $_
        $file = Join-Path $using:outPath "$($row.Number).json"
        $arguments = @('run', '--project', $using:cliProject, '-c', 'Release', '--no-build', '--', 'generate', '--assembly', $row.Assembly)
        if ($row.Package)
        {
            $arguments += @('--package', $row.Package)
        }
        $arguments += @('--version', $row.Version)
        if ($row.Framework)
        {
            $arguments += @('--framework', $row.Framework)
        }
        $arguments += @('--member', $row.Member, '--out', $file)

        $outcome = 'no-answer'
        $detail = ''
        $seconds = ''
        $problem = $null
        $printed = ''
        try
        {
            $printed = (& dotnet @arguments 2>&1 | Out-String)
            $code = $LASTEXITCODE
            if ($code -ne 0)
            {
                $problem = "the process exited $code"
            }
            elseif (-not (Test-Path -LiteralPath $file -PathType Leaf))
            {
                $problem = 'the process wrote no file'
            }
            else
            {
                $json = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json
                if ($json.member -cne $row.Member)
                {
                    $problem = "the answer's member is '$($json.member)'"
                }
                elseif ($json.assembly.name -cne $row.Assembly)
                {
                    $problem = "the answer's assembly.name is '$($json.assembly.name)'"
                }
                elseif ($json.assembly.version -cne $row.Version)
                {
                    $problem = "the answer's assembly.version is '$($json.assembly.version)'"
                }
                elseif ([string]$json.assembly.package -cne $row.Package)
                {
                    $problem = "the answer's assembly.package is '$($json.assembly.package)'"
                }
                else
                {
                    if ($json.reason) { $outcome = [string]$json.reason }
                    elseif ($null -ne $json.model) { $outcome = 'model' }
                    elseif ($json.modelReason) { $outcome = [string]$json.modelReason }
                    else { $outcome = 'unclassified' }
                    $detail = [string]$json.detail
                    if (-not $detail -and @($json.causes).Count -gt 0)
                    {
                        $detail = 'causes: ' + ((@($json.causes) | ForEach-Object { ([string]$_).Split(':')[0] }) -join ', ')
                    }
                    if ($null -ne $json.generation.seconds)
                    {
                        $seconds = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0}', [double]$json.generation.seconds)
                    }
                }
            }
        }
        catch
        {
            $problem = $_.Exception.Message
        }
        if ($problem)
        {
            $detail = $problem
            if ($printed.Trim())
            {
                Set-Content -LiteralPath (Join-Path $using:outPath "$($row.Number).log") -Value $printed -Encoding utf8
                $detail += ': ' + $printed.Trim()
            }
        }
        [pscustomobject]@{ Number = $row.Number; Member = $row.Member; Assembly = "$($row.Assembly) $($row.Version)"; Success = -not $problem
                           Outcome = $outcome; Detail = $detail; Seconds = $seconds }
    }
    $answers = @($answers | Sort-Object Number)
    Write-Host ("run: {0} rows in {1:0} s" -f $answers.Count, ((Get-Date) - $started).TotalSeconds)

    $table = @("row`tmember`tassembly`toutcome`tdetail`tseconds")
    foreach ($answer in $answers)
    {
        $table += (@($answer.Number, (Cell $answer.Member), (Cell $answer.Assembly), (Cell $answer.Outcome), (Cell $answer.Detail), $answer.Seconds) -join "`t")
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $outPath 'outcomes.tsv'), (($table -join "`n") + "`n"), $utf8)

    $summary = @('| исход | число |', '| --- | --- |')
    foreach ($group in @($answers | Group-Object Outcome | Sort-Object @{ Expression = 'Count'; Descending = $true }, @{ Expression = 'Name' }))
    {
        $summary += "| $($group.Name) | $($group.Count) |"
    }
    $summary += "| всего | $($answers.Count) |"
    $summary += ''
    $summary += '| сборка | исход | число |'
    $summary += '| --- | --- | --- |'
    foreach ($group in @($answers | Group-Object { "$($_.Assembly)`t$($_.Outcome)" } | Sort-Object Name))
    {
        $parts = $group.Name.Split("`t")
        $summary += "| $($parts[0]) | $($parts[1]) | $($group.Count) |"
    }
    [IO.File]::WriteAllText((Join-Path $outPath 'summary.md'), (($summary -join "`n") + "`n"), $utf8)

    $failed = @($answers | Where-Object { -not $_.Success })
    $notCompiling = @($answers | Where-Object { $_.Success -and $_.Outcome -eq 'library-does-not-compile' })
    foreach ($answer in (@($failed) + @($notCompiling) | Select-Object -First 20))
    {
        $shown = $answer.Detail
        Write-Host ("row {0} ({1}): {2}: {3}" -f $answer.Number, $answer.Assembly, $answer.Outcome, $shown.Substring(0, [Math]::Min(200, $shown.Length)))
    }
    if ($failed.Count -ne 0)
    {
        Fail "$($failed.Count) of $($answers.Count) rows are not a success; see $(Join-Path $outPath 'outcomes.tsv')"
    }
    if ($notCompiling.Count -ne 0)
    {
        Fail "$($notCompiling.Count) of $($answers.Count) rows answer library-does-not-compile; see $(Join-Path $outPath 'outcomes.tsv')"
    }
    Write-Host "PASS: $($answers.Count) rows, none answers library-does-not-compile; see $(Join-Path $outPath 'summary.md')"
    exit 0
}
catch
{
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
}
