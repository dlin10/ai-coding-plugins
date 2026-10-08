<#
.SYNOPSIS
Guards docs/SPEC.md against the development process: SPEC describes the finished first version, and how it is being built
lives in docs/PLAN.md, docs/QUESTIONS.md and docs/runs/.

.DESCRIPTION
Fails on any of these in SPEC, with the line, the section and the text around it:

- a phase or subphase (`фаза 5`, `в фазе 8`, `5d`, `подфазы`);
- a run (`run B2b`, `ран A4`, `третий ран`);
- a question number (`вопрос 85`);
- a date (`2026-09-27`);
- a link to PLAN.md, QUESTIONS.md or docs/runs/.

Text in backticks is code and is not read, so a fingerprint or an identifier never matches. Section 12.2 may name phases:
`phase` and `until` of demo/expected-findings.json and the order of phases are a file format, not the process. The phase of a
run (Run State, the deadline) is a word of the product and is not a phase number, so it does not match either.

Where a match is a requirement that is not built yet, the requirement stays in SPEC and the gap goes to the temporary limits
of PLAN; where it records history, it goes (plugins/concurrency-hunter/AGENTS.md, "Requirements and process").
#>

$ErrorActionPreference = 'Stop'

# The patterns below are Cyrillic, so this file is decoded correctly or it is not run at all: Windows PowerShell 5.1 reads a
# file with no byte-order mark in the ANSI code page and would match nothing. The mark makes both shells agree.
$PHASE_WORD = 'фаза'
if ([int][char]$PHASE_WORD[0] -ne 0x444) {
    Write-Output "build/check-spec.ps1 was decoded as $([Text.Encoding]::Default.WebName), not UTF-8, so its patterns are mojibake."
    Write-Output "  Restore the UTF-8 byte-order mark on this file, or run it under pwsh 7."
    exit 1
}

# The one section that may name phases: the format of demo/expected-findings.json.
$PHASE_FORMAT_SECTION = '12.2'

$RULES = [ordered]@{
    'phase'    = '(?i)\b(?:под)?фаз[а-яё]*\s+(?:[0-8][a-g]?\b|[0-8]\s*[–-]\s*[0-8])|\bподфаз[а-яё]*|(?<![\w.\-])[1-8][a-g](?![\w\-])'
    'run'      = '\b(?:[Rr]un|[Рр]ан[а-яё]*)\s+[A-C]\d*b?\b|(?i:\b(?:перв|втор|трет|четвёрт|последн)[а-яё]*\s+ран[а-яё]*)'
    'question' = '(?i)\bвопрос[а-яё]*\s+\d+'
    'date'     = '\b20\d\d-\d\d-\d\d\b'
    'link'     = 'QUESTIONS\.md|PLAN\.md|docs/runs/|\]\(runs/'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$specPath = Join-Path $root 'docs/SPEC.md'
$problems = [Collections.Generic.List[string]]::new()

$section = ''
$number = 0
foreach ($line in [IO.File]::ReadAllLines($specPath, [Text.UTF8Encoding]::new($false))) {
    $number++
    if ($line -match '^#{2,3}\s+(\d+(?:\.\d+)?)\.') { $section = $Matches[1] }
    # A list item labelled like `8a.` is an item of SPEC, not a subphase.
    $text = [regex]::Replace($line, '`[^`]*`', '`…`') -replace '^(\s*)\d+[a-z]\.\s', '$1'
    $text = $text -replace 'п\.\s*\d+[a-z]\b', 'п.'
    foreach ($rule in $RULES.Keys) {
        if ($rule -eq 'phase' -and $section -eq $PHASE_FORMAT_SECTION) { continue }
        foreach ($match in [regex]::Matches($text, $RULES[$rule])) {
            $start = [Math]::Max(0, $match.Index - 60)
            $length = [Math]::Min($text.Length - $start, $match.Length + 120)
            $problems.Add("SPEC.md:$number (§$section) ${rule}: …$($text.Substring($start, $length))…")
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Output "docs/SPEC.md names the development process:"
    foreach ($problem in $problems) { Write-Output "  $problem" }
    exit 1
}

Write-Output "docs/SPEC.md names no phase, run, question or date of the development process."
exit 0
