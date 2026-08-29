# Bootstraps the Plan Forge Flow MCP server. A locally built bin/win-x64/planforge.exe wins;
# otherwise the executable for the manifest version is fetched once from the matching GitHub
# release and cached per user, so per-commit plugin caches never re-download it.
# stdout belongs to the MCP protocol, so every message here goes to stderr.
$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $PSScriptRoot 'win-x64/planforge.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    # package.ps1 asserts the three manifest versions agree, so any one of them names the release.
    $version = (Get-Content -LiteralPath (Join-Path $pluginRoot '.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).version
    $cacheDir = Join-Path $env:LOCALAPPDATA "plan-forge-flow\bin\$version"
    $exe = Join-Path $cacheDir 'planforge.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        $url = "https://github.com/dlin10/ai-coding-plugins/releases/download/plan-forge-flow-v$version/planforge.exe"
        [Console]::Error.WriteLine("plan-forge-flow: downloading planforge.exe $version from $url")
        New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
        $temp = Join-Path $cacheDir "planforge.$PID.download"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing
            Move-Item -LiteralPath $temp -Destination $exe -ErrorAction Stop
        }
        catch {
            # A concurrent host may have won the race to the cache path; its download is as good.
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                [Console]::Error.WriteLine("plan-forge-flow: could not fetch $url : $($_.Exception.Message)")
                exit 1
            }
        }
        finally {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
        }
    }
}
# The prompts ship in the plugin package, never in the release asset, so an executable running from
# the download cache has none above it to walk up to. This is the only place that knows both, and
# PromptLibrary takes the value as given -- hence the existence check here rather than there.
$prompts = Join-Path $pluginRoot 'prompts'
if (Test-Path -LiteralPath $prompts -PathType Container) { $env:PLANFORGE_PROMPTS = $prompts }
& $exe @args
exit $LASTEXITCODE
