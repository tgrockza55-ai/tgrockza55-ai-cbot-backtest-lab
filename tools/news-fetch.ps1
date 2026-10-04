# Build the news / macro cache used by the research tools and (later) the cBot.
#
#   FRED (official API, free key)  -> release dates of the high-impact US releases, 2019-12 .. scheduled future dates
#                                     + daily macro series (real yield, yields, dollar index, breakeven, fed funds)
#   Forex Factory (weekly export)  -> this week's calendar with impact / forecast / previous (kept as dated snapshots)
#   Investing.com                  -> not used: no export, and its pages refuse automated access
#
# Output (Documents\BacktestLab\data\news\):
#   events.csv   unix,code,impact,title          time = scheduled release time in UTC
#   macro.csv    date,<series...>                daily values as published (use the previous day's row to avoid look-ahead)
#   ff-<monday>.json                             Forex Factory snapshot of the current week
#
# The FRED key lives in Documents\BacktestLab\config.json as "fredApiKey" (asked once; never in the repo).
# Get one at https://fredaccount.stlouisfed.org/apikeys
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\news-fetch.ps1

param([string]$From = "2019-12-01")

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$inv = [Globalization.CultureInfo]::InvariantCulture
$lab = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "BacktestLab"
$out = Join-Path $lab "data\news"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---- Forex Factory: this week's calendar (works without any key) ----
try {
    $ff = Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri "https://nfs.faireconomy.media/ff_calendar_thisweek.json"
    $text = if ($ff.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($ff.Content) } else { $ff.Content }
    $now = [DateTime]::UtcNow; $monday = $now.Date.AddDays(-(([int]$now.DayOfWeek + 6) % 7))
    $file = Join-Path $out ("ff-" + $monday.ToString("yyyy-MM-dd", $inv) + ".json")
    [IO.File]::WriteAllText($file, $text, (New-Object Text.UTF8Encoding($false)))
    $events = $text | ConvertFrom-Json
    Write-Host ("Forex Factory: {0} events this week ({1} high impact USD) -> {2}" -f @($events).Count,
        @($events | Where-Object { $_.impact -eq "High" -and $_.country -eq "USD" }).Count, $file) -ForegroundColor Green
} catch {
    Write-Host "Forex Factory weekly export failed: $($_.Exception.Message)" -ForegroundColor Yellow
}

# ---- FRED key ----
$cfgFile = Join-Path $lab "config.json"
$cfg = if (Test-Path $cfgFile) { Get-Content $cfgFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if (-not $cfg.fredApiKey) {
    Write-Host "A free FRED API key is needed: https://fredaccount.stlouisfed.org/apikeys"
    $key = (Read-Host "FRED API key").Trim()
    if ($key -notmatch '^[0-9a-f]{32}$') { Write-Host "That does not look like a FRED key (32 hex characters)." -ForegroundColor Red; exit 1 }
    $cfg | Add-Member -NotePropertyName fredApiKey -NotePropertyValue $key -Force
    [IO.File]::WriteAllText($cfgFile, ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Write-Host "Saved the key to $cfgFile" -ForegroundColor Green
}
$api = "https://api.stlouisfed.org/fred"
function Get-Fred([string]$path, [string]$query) {
    $url = "$api/$path`?$query&file_type=json&api_key=$($cfg.fredApiKey)"
    for ($try = 1; $try -le 3; $try++) {
        try { return Invoke-RestMethod -Uri $url -TimeoutSec 60 } catch { if ($try -eq 3) { throw }; Start-Sleep -Seconds (2 * $try) }
    }
}

# ---- high-impact US releases: FRED release id, scheduled local time (US Eastern), impact ----
$releases = @(
    @{ code = "NFP";    id = 50;  time = "08:30"; impact = "EXTREME" },   # Employment Situation
    @{ code = "CPI";    id = 10;  time = "08:30"; impact = "EXTREME" },   # Consumer Price Index
    @{ code = "FOMC";   id = 101; time = "14:00"; impact = "EXTREME" },   # FOMC Press Release
    @{ code = "PCE";    id = 54;  time = "08:30"; impact = "HIGH" },      # Personal Income and Outlays
    @{ code = "PPI";    id = 46;  time = "08:30"; impact = "HIGH" },      # Producer Price Index
    @{ code = "GDP";    id = 53;  time = "08:30"; impact = "HIGH" },      # Gross Domestic Product
    @{ code = "RETAIL"; id = 9;   time = "08:30"; impact = "HIGH" },      # Advance Monthly Sales for Retail and Food Services
    @{ code = "CLAIMS"; id = 180; time = "08:30"; impact = "MEDIUM" },    # Unemployment Insurance Weekly Claims Report
    @{ code = "JOLTS";  id = 192; time = "10:00"; impact = "MEDIUM" }     # Job Openings and Labor Turnover Survey
)
$eastern = [TimeZoneInfo]::FindSystemTimeZoneById("Eastern Standard Time")
$epoch = New-Object DateTime 1970, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc)

$rows = New-Object System.Collections.Generic.List[string]
foreach ($r in $releases) {
    $info = Get-Fred "release" "release_id=$($r.id)"
    $name = $info.releases[0].name
    $d = Get-Fred "release/dates" "release_id=$($r.id)&realtime_start=$From&realtime_end=9999-12-31&include_release_dates_with_no_data=true&sort_order=asc&limit=10000"
    $dates = @($d.release_dates | ForEach-Object { $_.date } | Where-Object { $_ -ge $From } | Sort-Object -Unique)
    foreach ($day in $dates) {
        $local = [DateTime]::ParseExact("$day $($r.time)", "yyyy-MM-dd HH:mm", $inv)
        $utc = [TimeZoneInfo]::ConvertTimeToUtc([DateTime]::SpecifyKind($local, [DateTimeKind]::Unspecified), $eastern)
        $rows.Add(("{0},{1},{2},{3}" -f [long]($utc - $epoch).TotalSeconds, $r.code, $r.impact, ($name -replace ',', ' ')))
    }
    Write-Host ("FRED release {0,3} {1,-7} {2,4} dates  {3} .. {4}   ({5})" -f $r.id, $r.code, $dates.Count, $dates[0], $dates[-1], $name)
}
$eventsFile = Join-Path $out "events.csv"
$sorted = $rows | Sort-Object { [long]($_.Split(',')[0]) }
[IO.File]::WriteAllLines($eventsFile, @("unix,code,impact,title") + $sorted, (New-Object Text.UTF8Encoding($false)))
Write-Host ("events: {0} -> {1}" -f $rows.Count, $eventsFile) -ForegroundColor Green

# ---- daily macro series ----
$series = "DFII10", "DGS10", "DGS2", "T10YIE", "DTWEXBGS", "DFF"   # 10y real yield, 10y, 2y, 10y breakeven, broad dollar index, fed funds
$table = @{}
foreach ($s in $series) {
    $o = Get-Fred "series/observations" "series_id=$s&observation_start=$From"
    $n = 0
    foreach ($obs in $o.observations) {
        if ($obs.value -eq ".") { continue }
        if (-not $table.ContainsKey($obs.date)) { $table[$obs.date] = @{} }
        $table[$obs.date][$s] = $obs.value; $n++
    }
    Write-Host ("FRED series {0,-9} {1,5} observations" -f $s, $n)
}
$macroFile = Join-Path $out "macro.csv"
$lines = @("date," + ($series -join ","))
foreach ($day in ($table.Keys | Sort-Object)) { $lines += ($day + "," + (($series | ForEach-Object { $table[$day][$_] }) -join ",")) }
[IO.File]::WriteAllLines($macroFile, $lines, (New-Object Text.UTF8Encoding($false)))
Write-Host ("macro: {0} days -> {1}" -f ($lines.Count - 1), $macroFile) -ForegroundColor Green
