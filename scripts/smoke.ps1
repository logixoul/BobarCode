<#
.SYNOPSIS
  Opt-in smoke test: one real review on a tiny scratch repo.

.DESCRIPTION
  Costs real money for Pi reviewers (a few cents for ds). Uses a temp config/data dir, with your
  keys.env copied in, so your real runs and config are untouched. `opus` runs Claude with
  --model haiku unless -RealOpus is given (Opus behaviour under test).

.PARAMETER RepoDir
  Review this repo's uncommitted changes (read-only: nothing in it is touched) instead of the scratch
  repo, with -Desc as the description. Useful to see when a streaming reviewer reports its findings.

.PARAMETER Explore
  Run one exploration (`explore start`) instead of a review, with a short cleanup task.

.PARAMETER ServiceTier
  Run the reviewer on this service tier (e.g. flex). Prints Pi's own vendor cost next to ours: Pi prices
  by the tier the provider says it served, so a halved vendor cost shows the tier took effect.

.PARAMETER Web
  Ask the reviewer to search the web and fetch a page during the review, then count its successful web tool
  calls (Pi: web_search/web_fetch, Claude: WebSearch/WebFetch). Fails unless both kinds succeeded.

.PARAMETER Discuss
  Run a short real discussion instead (2 rounds, no folder): -Reviewer and opus (as haiku unless -RealOpus),
  on a small question. Passes when it ends `discussed` with a solution from both.

.PARAMETER ClaudeCommand
  Path to the claude executable for the opus reviewer, when `claude` isn't on PATH.

.EXAMPLE
  .\scripts\smoke.ps1                 # ds (DeepSeek via Pi)
  .\scripts\smoke.ps1 -Web            # ds, using web_search and web_fetch
  .\scripts\smoke.ps1 -Reviewer opus -Web
  .\scripts\smoke.ps1 -Reviewer terra -ServiceTier flex
  .\scripts\smoke.ps1 -Explore
  .\scripts\smoke.ps1 -RepoDir D:\BobarCode -Desc "Streaming findings prototype"
  .\scripts\smoke.ps1 -Reviewer opus  # Claude, as haiku
  .\scripts\smoke.ps1 -Reviewer opus -RealOpus
#>
param(
    [ValidateSet("ds", "sol", "hydrogen", "qwen4", "glm", "terra", "luna", "neon", "opus")][string]$Reviewer = "ds",
    [switch]$RealOpus,
    [switch]$Keep,
    [string]$RepoDir,
    [string]$Desc = "See the diff.",
    [switch]$Explore,
    [string]$ServiceTier,
    [switch]$Web,
    [switch]$Discuss,
    [string]$ClaudeCommand
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "Building bobar-cli..."
dotnet build "$root/src/Bobar.Cli/Bobar.Cli.csproj" -c Debug -v quiet -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$cli = "$root/src/Bobar.Cli/bin/Debug/net11.0/bobar-cli" + $(if ($IsMacOS -or $IsLinux) { "" } else { ".exe" })

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bobar-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$repo = if ($RepoDir) { (Resolve-Path $RepoDir).Path } else { Join-Path $tmp "repo" }
$cfgDir = Join-Path $tmp "config"
$dataDir = Join-Path $tmp "data"
New-Item -ItemType Directory -Force $cfgDir, $dataDir | Out-Null

# Scratch repo with a deliberate off-by-one.
if (-not $RepoDir) {
    New-Item -ItemType Directory -Force $repo | Out-Null
    Push-Location $repo
    try {
        git init -q -b main
        git config user.name smoke
        git config user.email smoke@example.invalid
        git config core.autocrlf false
        Set-Content -Encoding ascii sum.py "def total(xs):`n    s = 0`n    for i in range(len(xs)):`n        s += xs[i]`n    return s`n"
        git add -A; git commit -qm initial
        Set-Content -Encoding ascii sum.py "def total(xs):`n    s = 0`n    for i in range(1, len(xs)):`n        s += xs[i]`n    return s`n"
    } finally { Pop-Location }
}

# Temp config: defaults (written by the CLI on first use), keys copied, opus → haiku.
$env:BOBAR_CONFIG_DIR = $cfgDir
$env:BOBAR_DATA_DIR = $dataDir
& $cli runs | Out-Null
$realKeys = if ($IsMacOS) { Join-Path $HOME "Library/Application Support/BobarCode/keys.env" } else { Join-Path $env:APPDATA "BobarCode/keys.env" }
if (Test-Path $realKeys) { Copy-Item $realKeys (Join-Path $cfgDir "keys.env") }
elseif ($Reviewer -ne "opus") { throw "no keys.env at $realKeys; the $Reviewer reviewer needs one" }
if (($Reviewer -eq "opus" -or $Discuss) -and -not $RealOpus) {
    $cfgFile = Join-Path $cfgDir "config.json"
    $cfg = Get-Content -Raw $cfgFile | ConvertFrom-Json
    $cfg.reviewers.opus.model = "haiku"
    $cfg.reviewers.opus.effort = "low"
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $cfgFile
    Write-Host "opus reviewer runs as haiku (pass -RealOpus to use Opus)."
}

if ($ClaudeCommand) {
    $cfgFile = Join-Path $cfgDir "config.json"
    $cfg = Get-Content -Raw $cfgFile | ConvertFrom-Json
    $cfg.reviewers.opus | Add-Member -Force command $ClaudeCommand
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $cfgFile
}

if ($ServiceTier) {
    $cfgFile = Join-Path $cfgDir "config.json"
    $cfg = Get-Content -Raw $cfgFile | ConvertFrom-Json
    $cfg.reviewers.$Reviewer | Add-Member -Force serviceTier $ServiceTier
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $cfgFile
    Write-Host "'$Reviewer' runs on the '$ServiceTier' service tier (no price for it in the table, so our cost is unknown)."
}

$descFile = Join-Path $tmp "desc.md"
if ($Discuss) {
    $models = if ($Reviewer -eq "opus") { "ds,opus" } else { "$Reviewer,opus" }
    Write-Host "Running a real 2-round discussion between $models..."
    $out = "In one short paragraph: is a 4-day work week a good idea for a 10-person software startup? Keep your answer under 120 words." |
        & $cli discuss start --models $models --slug smoke --rounds 2 --prompt -
    $code = $LASTEXITCODE
    $out | Out-Host
    $first = ($out | Select-Object -First 1)
    $solutions = @($out | Where-Object { $_ -match "^- \S+: round \d+: ``" }).Count
    if (-not $Keep) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue } else { Write-Host "Kept $tmp" }
    Remove-Item Env:BOBAR_CONFIG_DIR, Env:BOBAR_DATA_DIR
    if ($code -ne 0 -or $first -notmatch "STATUS: discussed" -or $solutions -lt 2) { Write-Host "SMOKE FAILED" -ForegroundColor Red; exit 1 }
    Write-Host "SMOKE OK" -ForegroundColor Green
    exit 0
}

$webTask ="Before you review, you must use your web tools, as a test of them: (1) search the web for ""python builtin sum function documentation"", and (2) fetch https://docs.python.org/3/library/functions.html and quote what it says about sum() in one finding's explanation."
Set-Content -Encoding utf8 $descFile $(if ($Explore) { "Explore the codebase for opportunities for code cleanup. List high-impact ones only." } elseif ($RepoDir) { $Desc } else { "Changed the loop in sum.py's total() to start at index 1." + $(if ($Web) { "`n`n$webTask" } else { "" }) })

Write-Host "Running one real $(if ($Explore) { 'exploration' } else { 'review' }) with '$Reviewer'..."
Push-Location $repo
try {
    $out = if ($Explore) { & $cli explore start --reviewer $Reviewer --prompt $descFile } else { & $cli review start --reviewer $Reviewer --desc $descFile }
    $code = $LASTEXITCODE
} finally { Pop-Location }
$out | Out-Host

$first = ($out | Select-Object -First 1)
$runId = if ($first -match "run=(\S+)") { $Matches[1] } else { $null }
if ($runId) {
    $events = Get-Content (Join-Path $dataDir "runs/$runId/events.jsonl") | ForEach-Object { $_ | ConvertFrom-Json }
    $rr = $events | Where-Object { $_.type -eq "review_received" } | Select-Object -First 1
    if ($rr) {
        Write-Host ("Usage: in {0}, out {1}, cacheRead {2}; cost: {3}; models: {4}; reasoning: {5} -> {6}" -f `
            $rr.usage.input, $rr.usage.output, $rr.usage.cacheRead, $rr.costUsd, ($rr.resolvedModels -join ","), $rr.requestedReasoning, $rr.effectiveReasoning)
    }
    # Pi's own cost (ignored by BobarCode): it applies the tier the provider reports, so flex shows up halved.
    $log = Join-Path $dataDir "runs/$runId/round-1/reviewer.jsonl"
    if (Test-Path $log) {
        $vendor = 0.0
        Get-Content $log | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } } |
            Where-Object { $_.type -eq "message_end" -and $_.message.role -eq "assistant" -and $_.message.usage.cost } |
            ForEach-Object { $vendor += [double]$_.message.usage.cost.total }
        Write-Host ("Pi's vendor cost: {0:N5}" -f $vendor)
    }
    # Streaming reviewers: when each finding arrived, against the start and end of the review.
    $req = $events | Where-Object { $_.type -eq "review_requested" } | Select-Object -First 1
    $found = Join-Path $dataDir "runs/$runId/round-1/findings.jsonl"
    if ($req -and $rr -and (Test-Path $found)) {
        $t0 = [DateTimeOffset]$req.t
        $end = ([DateTimeOffset]$rr.t - $t0).TotalSeconds
        Get-Content $found | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | ForEach-Object {
            $at = ([DateTimeOffset]$_.t - $t0).TotalSeconds
            Write-Host ("  {0} at {1,6:N1}s of {2:N1}s ({3:P0}): [{4}] {5}" -f $_.id, $at, $end, ($at / $end), $_.severity, $_.title)
        }
    }
    # Web tools: successful calls by name (Pi: tool_execution_end; Claude: tool_use matched to a non-error tool_result).
    $webOk = @{}
    if ($Web -and (Test-Path $log)) {
        $lines = Get-Content $log | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } }
        $names = @{}
        foreach ($l in $lines) {
            if ($l.type -eq "tool_execution_end" -and $l.toolName -like "web_*" -and -not $l.isError) { $webOk[$l.toolName] = 1 + [int]$webOk[$l.toolName] }
            if ($l.type -eq "assistant") { foreach ($p in $l.message.content) { if ($p.type -eq "tool_use" -and $p.name -like "Web*") { $names[$p.id] = $p.name } } }
            if ($l.type -eq "user") { foreach ($p in $l.message.content) { if ($p.type -eq "tool_result" -and $names[$p.tool_use_id] -and -not $p.is_error) { $n = $names[$p.tool_use_id]; $webOk[$n] = 1 + [int]$webOk[$n] } } }
        }
        Write-Host ("Successful web tool calls: {0}" -f $(if ($webOk.Count) { ($webOk.GetEnumerator() | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ", " } else { "none" }))
        if ($rr) { Write-Host ("webSearches counted for the cost: {0}" -f $rr.webSearches) }
    }
    if ($first -match "STATUS: findings") { & $cli review cancel $runId | Out-Null }
}
if ($Web -and $webOk.Count -lt 2) { Write-Host "SMOKE FAILED: the reviewer didn't both search and fetch successfully" -ForegroundColor Red; exit 1 }

if (-not $Keep) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
else { Write-Host "Kept $tmp" }
Remove-Item Env:BOBAR_CONFIG_DIR, Env:BOBAR_DATA_DIR

if ($code -ne 0 -or $first -notmatch "STATUS: (findings|done)") { Write-Host "SMOKE FAILED" -ForegroundColor Red; exit 1 }
Write-Host "SMOKE OK" -ForegroundColor Green
