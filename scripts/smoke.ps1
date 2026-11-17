<#
.SYNOPSIS
  Opt-in smoke test: one real review on a tiny scratch repo.

.DESCRIPTION
  Costs real money for Pi reviewers (a few cents for ds). Uses a temp config/data dir, with your
  keys.env copied in, so your real runs and config are untouched. `opus` runs Claude with
  --model haiku unless -RealOpus is given (Opus behaviour under test).

.EXAMPLE
  .\scripts\smoke.ps1                 # ds (DeepSeek via Pi)
  .\scripts\smoke.ps1 -Reviewer opus  # Claude, as haiku
  .\scripts\smoke.ps1 -Reviewer opus -RealOpus
#>
param(
    [ValidateSet("ds", "sol", "opus")][string]$Reviewer = "ds",
    [switch]$RealOpus,
    [switch]$Keep
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "Building bobar-cli..."
dotnet build "$root\src\Bobar.Cli\Bobar.Cli.csproj" -c Debug -v quiet -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$cli = "$root\src\Bobar.Cli\bin\Debug\net10.0\bobar-cli.exe"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bobar-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$repo = Join-Path $tmp "repo"
$cfgDir = Join-Path $tmp "config"
$dataDir = Join-Path $tmp "data"
New-Item -ItemType Directory -Force $repo, $cfgDir, $dataDir | Out-Null

# Scratch repo with a deliberate off-by-one.
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

# Temp config: defaults (written by the CLI on first use), keys copied, opus → haiku.
$env:BOBAR_CONFIG_DIR = $cfgDir
$env:BOBAR_DATA_DIR = $dataDir
& $cli runs | Out-Null
$realKeys = Join-Path $env:APPDATA "BobarCode\keys.env"
if (Test-Path $realKeys) { Copy-Item $realKeys (Join-Path $cfgDir "keys.env") }
elseif ($Reviewer -ne "opus") { throw "no keys.env at $realKeys; the $Reviewer reviewer needs one" }
if ($Reviewer -eq "opus" -and -not $RealOpus) {
    $cfgFile = Join-Path $cfgDir "config.json"
    $cfg = Get-Content -Raw $cfgFile | ConvertFrom-Json
    $cfg.reviewers.opus.model = "haiku"
    $cfg.reviewers.opus.effort = "low"
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $cfgFile
    Write-Host "opus reviewer runs as haiku (pass -RealOpus to use Opus)."
}

$desc = Join-Path $tmp "desc.md"
Set-Content -Encoding ascii $desc "Changed the loop in sum.py's total() to start at index 1."

Write-Host "Running one real review with '$Reviewer'..."
Push-Location $repo
try {
    $out = & $cli review start --reviewer $Reviewer --desc $desc
    $code = $LASTEXITCODE
} finally { Pop-Location }
$out | Out-Host

$first = ($out | Select-Object -First 1)
$runId = if ($first -match "run=(\S+)") { $Matches[1] } else { $null }
if ($runId) {
    $events = Get-Content (Join-Path $dataDir "runs\$runId\events.jsonl") | ForEach-Object { $_ | ConvertFrom-Json }
    $rr = $events | Where-Object { $_.type -eq "review_received" } | Select-Object -First 1
    if ($rr) {
        Write-Host ("Usage: in {0}, out {1}, cacheRead {2}; cost: {3}; models: {4}; reasoning: {5} -> {6}" -f `
            $rr.usage.input, $rr.usage.output, $rr.usage.cacheRead, $rr.costUsd, ($rr.resolvedModels -join ","), $rr.requestedReasoning, $rr.effectiveReasoning)
    }
    if ($first -match "STATUS: findings") { & $cli review cancel $runId | Out-Null }
}

if (-not $Keep) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
else { Write-Host "Kept $tmp" }
Remove-Item Env:BOBAR_CONFIG_DIR, Env:BOBAR_DATA_DIR

if ($code -ne 0 -or $first -notmatch "STATUS: (findings|done)") { Write-Host "SMOKE FAILED" -ForegroundColor Red; exit 1 }
Write-Host "SMOKE OK" -ForegroundColor Green
