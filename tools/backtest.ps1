# Run one backtest of the BacktestLab cBot through cTrader CLI (ctrader-cli).
# Results are sent to the lab by the cBot itself; this script only launches it and shows the outcome.
#
# Usage (from anywhere):
#   powershell -ExecutionPolicy Bypass -File tools\backtest.ps1 -Strategy EMA_CROSS -Symbol EURUSD -Period h1 `
#       -Start "01/01/2025 00:00" -End "30/06/2025 00:00" -P1 9 -P2 21
#   Add -Build to sync the source from this repo and rebuild the .algo first.
#
# Per-machine settings (never in the repo): Documents\BacktestLab\cli.json
#   { "ctidFile": "<file holding the cTID>", "pwdFile": "<password file>", "account": "<demo account number>" }
# The password file is only passed to ctrader-cli as --pwd-file; this script never reads it.

param(
    [string]$Strategy = "EMA_CROSS",
    [string]$Symbol = "EURUSD",
    [string]$Period = "h1",
    [Parameter(Mandatory = $true)][string]$Start,   # dd/MM/yyyy HH:mm (UTC)
    [Parameter(Mandatory = $true)][string]$End,
    [Nullable[double]]$P1, [Nullable[double]]$P2, [Nullable[double]]$P3, [Nullable[double]]$P4,
    [Nullable[double]]$Lots, [Nullable[double]]$StopLossPips, [Nullable[double]]$TakeProfitPips,
    [double]$Balance = 1000,
    [string]$DataMode = "m1",                        # ticks | m1 | open
    [Nullable[double]]$Spread,                       # pips; with m1/open data the CLI default is 0 (too optimistic)
    [Nullable[double]]$Commission,                   # per million; CLI default 0
    [Nullable[double]]$MinStopPct, [Nullable[double]]$MaxStopPct,
    [string]$Note = "",
    [switch]$Build,
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$inv  = [Globalization.CultureInfo]::InvariantCulture
$docs = [Environment]::GetFolderPath("MyDocuments")
$lab  = Join-Path $docs "BacktestLab"

$cliCmd = Get-Command ctrader-cli -ErrorAction SilentlyContinue
if (-not $cliCmd) { Write-Host "ctrader-cli not found in PATH." -ForegroundColor Red; exit 1 }
$cli = $cliCmd.Source

# ---- per-machine settings ----
$cliFile = Join-Path $lab "cli.json"
if (-not (Test-Path $cliFile)) {
    New-Item -ItemType Directory -Force -Path $lab | Out-Null
    $c = Read-Host "Path of the file holding your cTID (user name / email)"
    $p = Read-Host "Path of the cTID password file"
    $a = Read-Host "Demo account number"
    @{ ctidFile = $c.Trim('" '); pwdFile = $p.Trim('" '); account = $a.Trim() } | ConvertTo-Json | Set-Content -Encoding UTF8 $cliFile
    Write-Host "Created $cliFile" -ForegroundColor Green
}
$cfg = Get-Content $cliFile -Raw | ConvertFrom-Json
foreach ($f in @($cfg.ctidFile, $cfg.pwdFile)) {
    if (-not (Test-Path $f)) { Write-Host "Not found: $f (check $cliFile)" -ForegroundColor Red; exit 1 }
}
$ctid = (Get-Content $cfg.ctidFile -Raw).Trim()
$auth = '--ctid={0} --pwd-file="{1}" --account={2}' -f $ctid, $cfg.pwdFile, $cfg.account

$logDir = Join-Path $lab "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Run ctrader-cli with a time limit. It does not always exit by itself after a backtest,
# so stop it once $doneMarker has appeared in the output.
function Invoke-Cli([string]$arguments, [string]$logFile, [int]$timeoutSec, [string]$doneMarker) {
    $errFile = "$logFile.err"
    $proc = Start-Process -FilePath $cli -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $logFile -RedirectStandardError $errFile
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $doneAt = $null
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        if ($doneMarker -and -not $doneAt) {
            $hit = Select-String -Path $logFile -Pattern $doneMarker -SimpleMatch -Quiet -ErrorAction SilentlyContinue
            if ($hit) { $doneAt = $sw.Elapsed.TotalSeconds }
        }
        if ($doneAt -and ($sw.Elapsed.TotalSeconds - $doneAt) -gt 15) { $proc.Kill(); break }
        if ($sw.Elapsed.TotalSeconds -gt $timeoutSec) {
            $proc.Kill()
            Write-Host "Timed out after $timeoutSec s. Log: $logFile" -ForegroundColor Red
            return $false
        }
    }
    return $true
}

# ---- optional: sync + build ----
if ($Build) {
    & (Join-Path $PSScriptRoot "sync-cbot.ps1")
    $proj = Join-Path $docs "cAlgo\Sources\Robots\BacktestLab\BacktestLab"
    $buildLog = Join-Path $logDir "build.log"
    $ok = Invoke-Cli ('build --project-path="{0}" {1} -q' -f $proj, $auth) $buildLog 300 $null
    $text = Get-Content $buildLog -Raw
    if (-not $ok -or $text -notmatch '"success":\s*true') {
        Write-Host "Build failed:" -ForegroundColor Red
        (Get-Content $buildLog) | Where-Object { $_ -match 'error|warning|success|"message"|"file"|"line"' } | ForEach-Object { $_.Replace($ctid, "<ctid>") }
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}

# ---- locate the .algo ----
$algo = @(
    (Join-Path $docs "cAlgo\Sources\Robots\BacktestLab.algo"),
    (Join-Path $docs "cAlgo\Robots\BacktestLab.algo")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $algo) { Write-Host "BacktestLab.algo not found. Build the cBot first (-Build)." -ForegroundColor Red; exit 1 }

# ---- backtest ----
$stamp = (Get-Date).ToString("yyyyMMdd-HHmmss", $inv)
$base  = Join-Path $logDir ("{0}-{1}-{2}-{3}" -f $stamp, $Strategy, $Symbol, $Period)
$log   = "$base.log"

$a = @(
    'backtest', ('"{0}"' -f $algo), $auth,
    "--symbol=$Symbol", "--period=$Period",
    ('"--start={0}"' -f $Start), ('"--end={0}"' -f $End),
    "--data-mode=$DataMode", ("--balance=" + $Balance.ToString($inv)),
    "--full-access", ('--report-json="{0}.report.json"' -f $base),
    "--StrategyCode=$Strategy"
)
if ($null -ne $Spread) { $a += ("--spread=" + ([double]$Spread).ToString($inv)) }
if ($null -ne $Commission) { $a += ("--commission=" + ([double]$Commission).ToString($inv)) }
$named = [ordered]@{ P1 = $P1; P2 = $P2; P3 = $P3; P4 = $P4; Lots = $Lots; StopLossPips = $StopLossPips; TakeProfitPips = $TakeProfitPips
    MinStopPct = $MinStopPct; MaxStopPct = $MaxStopPct }
foreach ($k in $named.Keys) {
    if ($null -ne $named[$k]) { $a += ("--{0}={1}" -f $k, ([double]$named[$k]).ToString($inv)) }
}
if ($Note) { $a += ('"--RunNote={0}"' -f $Note) }

Write-Host ("Backtest {0} {1} {2}  {3} -> {4}" -f $Strategy, $Symbol, $Period, $Start, $End) -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok = Invoke-Cli ($a -join " ") $log ($TimeoutMinutes * 60) "] stopped."
Write-Host ("Finished in {0} s. Log: {1}" -f [int]$sw.Elapsed.TotalSeconds, $log)

$lines = @(Get-Content $log -ErrorAction SilentlyContinue) + @(Get-Content "$log.err" -ErrorAction SilentlyContinue)
$lines | Where-Object { $_ -match 'Sent to lab|Lab responded|Send failed|Saved for retry|Missing config|config\.json error|Unknown strategy|Crashed|Exception|^Error|Exported ' } |
    ForEach-Object { $_.Replace($ctid, "<ctid>") }

# ctrader-cli prints its own summary as the last JSON object
$start = -1
for ($i = $lines.Count - 1; $i -ge 0; $i--) { if ($lines[$i].Trim() -eq "{") { $start = $i; break } }
if ($start -ge 0) { $lines[$start..($lines.Count - 1)] | Where-Object { $_ -match '"\w+":' } | ForEach-Object { $_.Trim() } }

if (-not $ok) { exit 1 }
if (-not ($lines -match 'Sent to lab')) {
    Write-Host "The result was NOT confirmed as sent to the lab. Check the log." -ForegroundColor Yellow
    exit 2
}
