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
    [string]$Step = "",                              # shown with the progress on the website, e.g. "3/10"
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
function Invoke-Cli([string]$arguments, [string]$logFile, [int]$timeoutSec, [string]$doneMarker, [scriptblock]$onTick) {
    $errFile = "$logFile.err"
    $proc = Start-Process -FilePath $cli -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $logFile -RedirectStandardError $errFile
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $doneAt = $null
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        if ($onTick) { & $onTick $logFile }
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

# ---- progress for the website: POST /progress on the lab (needs the backtest_jobs table + current lab function) ----
$labCfgFile = Join-Path $lab "config.json"
$labCfg = if (Test-Path $labCfgFile) { Get-Content $labCfgFile -Raw | ConvertFrom-Json } else { $null }
$jobId = "{0}-{1}-{2}-{3}" -f $stamp, $Strategy, $Symbol, $Period
$script:progressOn = [bool]$labCfg
$script:lastSent = ""

function Send-Progress([string]$phase, $percent, [string]$status, $runId) {
    if (-not $script:progressOn) { return }
    $key = "$phase|$percent|$status"
    if ($key -eq $script:lastSent) { return }
    $script:lastSent = $key
    $body = @{ id = $jobId; strategyCode = $Strategy; symbol = $Symbol; timeframe = $Period; dateFrom = $Start; dateTo = $End
        phase = $phase; percent = $percent; status = $status; runId = $runId; machine = $env:COMPUTERNAME
        note = (@($Step, $Note) | Where-Object { $_ }) -join " - " } | ConvertTo-Json -Compress
    try {
        Invoke-RestMethod -Method Post -Uri ($labCfg.apiUrl.TrimEnd('/') + "/progress") -TimeoutSec 5 `
            -Headers @{ "x-ingest-key" = $labCfg.ingestKey } -ContentType "application/json; charset=utf-8" `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) | Out-Null
    } catch {
        $script:progressOn = $false     # lab not updated yet, or offline: never let this disturb the backtest
    }
}

$tick = {
    param($logFile)
    $last = Get-Content $logFile -Tail 20 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^Progress \| (.+?) \| ([\d.]+) %' } | Select-Object -Last 1
    if ($last -and $last -match '^Progress \| (.+?) \| ([\d.]+) %') {
        Send-Progress $Matches[1] ([math]::Floor([double]::Parse($Matches[2], $inv))) "running" $null
    }
}

Write-Host ("Backtest {0} {1} {2}  {3} -> {4}" -f $Strategy, $Symbol, $Period, $Start, $End) -ForegroundColor Cyan
Send-Progress "Starting" 0 "running" $null
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok = Invoke-Cli ($a -join " ") $log ($TimeoutMinutes * 60) "] stopped." $tick
Write-Host ("Finished in {0} s. Log: {1}" -f [int]$sw.Elapsed.TotalSeconds, $log)

$lines = @(Get-Content $log -ErrorAction SilentlyContinue) + @(Get-Content "$log.err" -ErrorAction SilentlyContinue)
$sent = $lines | Where-Object { $_ -match 'Sent to lab: .*"runId":(\d+)' } | Select-Object -Last 1
$runId = if ($sent -and $sent -match '"runId":(\d+)') { [int]$Matches[1] } else { $null }
$isUtility = [bool]($lines -match 'Exported \d+ bars')
Send-Progress "Finished" 100 $(if ($ok -and ($runId -or $isUtility)) { "done" } else { "failed" }) $runId
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
