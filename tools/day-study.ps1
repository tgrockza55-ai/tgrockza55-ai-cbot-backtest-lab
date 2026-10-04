# Day-of-week statistics from M1 bars: "this weekday + this shape -> rest of the day closes up X% / down Y% of the time".
#
# Input : CSV written by the cBot utility strategy DATA_EXPORT (Documents\BacktestLab\data\<symbol>_Minute.csv)
# Output: docs\data\daystudy-<symbol>.json   (read by docs\daystudy.html, which holds the Thai labels)
#
# Usage : powershell -ExecutionPolicy Bypass -File tools\day-study.ps1 [-Symbol XAUUSD] [-AsiaEnd 7] [-Csv path]
#
# Definitions (all times UTC; Thailand = UTC+7)
#   trading day   UTC calendar day; Sunday-evening bars count towards Monday
#   morning       day open -> AsiaEnd:00 (Asian session)
#   rest          AsiaEnd:00 -> day close (London + New York)
#   morning shape |close-open| < 30% of the morning range = S (sideways), otherwise U (up) / D (down)
#   prev candle   |body| >= 50% of the previous day's range = G (long green) / R (long red), otherwise N (indecisive)
#   outcome       day close vs the price at AsiaEnd:00 -> up / down

param(
    [string]$Symbol = "XAUUSD",
    [string]$Csv,
    [int]$AsiaEnd = 7,
    [string]$Out
)

$ErrorActionPreference = "Stop"
$inv = [Globalization.CultureInfo]::InvariantCulture
$minBars = 120      # both sessions need at least this many M1 bars (drops holidays / half days)
$reliableN = 30

if (-not $Csv) { $Csv = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "BacktestLab\data\${Symbol}_Minute.csv" }
if (-not $Out) { $Out = Join-Path $PSScriptRoot "..\docs\data\daystudy-$Symbol.json" }
if (-not (Test-Path $Csv)) {
    Write-Host "Not found: $Csv" -ForegroundColor Red
    Write-Host "Run: tools\backtest.ps1 -Strategy DATA_EXPORT -Symbol $Symbol -Period m1 -Start ... -End ..."
    exit 1
}

# The 1M-row pass is done in compiled code; everything after works on ~800 day rows.
Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public class DayBar
{
    public int DayNumber;
    public double Open, High, Low, Close;
    public double AOpen, AHigh, ALow, AClose; public int AN;
    public double ROpen, RHigh, RLow, RClose; public int RN;
    public double[] Hours = new double[24];
}

public static class DayLoader
{
    public static List<DayBar> Load(string path, int asiaEnd)
    {
        var inv = CultureInfo.InvariantCulture;
        var map = new SortedDictionary<int, DayBar>();
        using (var reader = new StreamReader(path))
        {
            reader.ReadLine();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 5) continue;
                long unix = long.Parse(p[0], inv);
                int dayNumber = (int)(unix / 86400);
                int weekday = (dayNumber + 4) % 7;            // 0 = Sunday
                if (weekday == 6) continue;
                bool sunday = weekday == 0;
                if (sunday) dayNumber++;
                int hour = (int)((unix % 86400) / 3600);
                double o = double.Parse(p[1], inv), h = double.Parse(p[2], inv), l = double.Parse(p[3], inv), c = double.Parse(p[4], inv);

                DayBar d;
                if (!map.TryGetValue(dayNumber, out d))
                {
                    d = new DayBar { DayNumber = dayNumber, Open = o, High = h, Low = l, Close = c };
                    for (int i = 0; i < 24; i++) d.Hours[i] = double.NaN;
                    map[dayNumber] = d;
                }
                if (h > d.High) d.High = h;
                if (l < d.Low) d.Low = l;
                d.Close = c;

                if (sunday || hour < asiaEnd)
                {
                    if (d.AN == 0) { d.AOpen = o; d.AHigh = h; d.ALow = l; }
                    if (h > d.AHigh) d.AHigh = h;
                    if (l < d.ALow) d.ALow = l;
                    d.AClose = c; d.AN++;
                }
                else
                {
                    if (d.RN == 0) { d.ROpen = o; d.RHigh = h; d.RLow = l; }
                    if (h > d.RHigh) d.RHigh = h;
                    if (l < d.RLow) d.RLow = l;
                    d.RClose = c; d.RN++;
                }
                if (!sunday) d.Hours[hour] = c;               // close of the last bar in that hour
            }
        }
        return new List<DayBar>(map.Values);
    }
}
"@

$epoch = New-Object DateTime 1970, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc)
$days = [DayLoader]::Load($Csv, $AsiaEnd)

# ---- classify each day ----
$rows = New-Object System.Collections.Generic.List[object]
$prev = $null
foreach ($d in $days) {
    $date = $epoch.AddDays($d.DayNumber)
    $wd = [int]$date.DayOfWeek - 1                      # Monday = 0 .. Friday = 4
    if ($wd -lt 0 -or $wd -gt 4) { continue }

    if ($d.AN -ge $minBars -and $d.RN -ge $minBars) {
        $rng = $d.AHigh - $d.ALow
        $net = $d.AClose - $d.AOpen
        $asia = if ($rng -le 0 -or [math]::Abs($net) -lt 0.3 * $rng) { "S" } elseif ($net -gt 0) { "U" } else { "D" }

        $pshape = $null
        if ($prev) {
            $prng = $prev.High - $prev.Low
            $body = $prev.Close - $prev.Open
            $pshape = if ($prng -le 0 -or [math]::Abs($body) -lt 0.5 * $prng) { "N" } elseif ($body -gt 0) { "G" } else { "R" }
        }

        $path = foreach ($h in 0..23) {
            if ([double]::IsNaN($d.Hours[$h])) { $null } else { [math]::Round(100 * ($d.Hours[$h] - $d.Open) / $d.Open, 2) }
        }
        $rows.Add([pscustomobject]@{
            date     = $date.ToString("yyyy-MM-dd", $inv)
            wd       = $wd
            asia     = $asia
            prev     = $pshape
            asiaPct  = [math]::Round(100 * $net / $d.AOpen, 3)
            restPct  = [math]::Round(100 * ($d.Close - $d.AClose) / $d.AClose, 3)
            dayPct   = [math]::Round(100 * ($d.Close - $d.Open) / $d.Open, 3)
            rangePct = [math]::Round(100 * ($d.High - $d.Low) / $d.Open, 3)
            path     = @($path)
        })
    }
    if ($d.AN + $d.RN -gt 0) { $prev = $d }
}
if ($rows.Count -eq 0) { Write-Host "No usable days in $Csv" -ForegroundColor Red; exit 1 }
$rows = $rows.ToArray()

# ---- three equal sub-periods, to see whether a statistic is stable over time ----
$first = [DateTime]::ParseExact($rows[0].date, "yyyy-MM-dd", $inv)
$last = [DateTime]::ParseExact($rows[$rows.Count - 1].date, "yyyy-MM-dd", $inv).AddDays(1)
$step = ($last - $first).TotalDays / 3
$periods = foreach ($i in 0..2) {
    $s = $first.AddDays($step * $i)
    $e = if ($i -eq 2) { $last } else { $first.AddDays($step * ($i + 1)) }
    [pscustomobject]@{
        label = $s.ToString("MM/yyyy", $inv) + "-" + $e.AddDays(-1).ToString("MM/yyyy", $inv)
        from  = $s.ToString("yyyy-MM-dd", $inv)
        to    = $e.ToString("yyyy-MM-dd", $inv)
    }
}

function Get-Wilson([int]$up, [int]$n) {
    if ($n -eq 0) { return @($null, $null) }
    $z = 1.96; $p = $up / $n
    $den = 1 + $z * $z / $n
    $c = ($p + $z * $z / (2 * $n)) / $den
    $h = $z * [math]::Sqrt($p * (1 - $p) / $n + $z * $z / (4 * $n * $n)) / $den
    return @([math]::Round(100 * ($c - $h), 1), [math]::Round(100 * ($c + $h), 1))
}

function Get-Avg($values) {
    $values = @($values)
    if ($values.Count -eq 0) { return $null }
    return [math]::Round(($values | Measure-Object -Average).Average, 3)
}

function New-Cell($sub) {
    $sub = @($sub)
    $ups = @($sub | Where-Object { $_.restPct -gt 0 } | ForEach-Object { $_.restPct })
    $downs = @($sub | Where-Object { $_.restPct -lt 0 } | ForEach-Object { $_.restPct })
    $decided = $ups.Count + $downs.Count
    $byPeriod = foreach ($p in $periods) {
        $ps = @($sub | Where-Object { $_.date -ge $p.from -and $_.date -lt $p.to })
        $u = @($ps | Where-Object { $_.restPct -gt 0 }).Count
        [ordered]@{ label = $p.label; n = $ps.Count; upPct = $(if ($ps.Count) { [math]::Round(100 * $u / $ps.Count, 1) } else { $null }) }
    }
    return [ordered]@{
        n          = $sub.Count
        up         = $ups.Count
        down       = $downs.Count
        upPct      = $(if ($decided) { [math]::Round(100 * $ups.Count / $decided, 1) } else { $null })
        downPct    = $(if ($decided) { [math]::Round(100 * $downs.Count / $decided, 1) } else { $null })
        ci95       = @(Get-Wilson $ups.Count $decided)
        avgRestPct = Get-Avg ($sub | ForEach-Object { $_.restPct })
        avgUpPct   = Get-Avg $ups
        avgDownPct = Get-Avg $downs
        reliable   = ($sub.Count -ge $reliableN)
        periods    = @($byPeriod)
    }
}

function New-Group($sub) {
    $sub = @($sub)
    $g = [ordered]@{ all = New-Cell $sub; asia = [ordered]@{}; prev = [ordered]@{}; combo = [ordered]@{} }
    foreach ($a in "U", "D", "S") { $g.asia[$a] = New-Cell ($sub | Where-Object { $_.asia -eq $a }) }
    foreach ($p in "G", "R", "N") { $g.prev[$p] = New-Cell ($sub | Where-Object { $_.prev -eq $p }) }
    foreach ($p in "G", "R", "N") {
        foreach ($a in "U", "D", "S") { $g.combo[$p + $a] = New-Cell ($sub | Where-Object { $_.prev -eq $p -and $_.asia -eq $a }) }
    }
    return $g
}

$result = [ordered]@{
    symbol         = $Symbol
    generatedAt    = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", $inv)
    from           = $rows[0].date
    to             = $rows[$rows.Count - 1].date
    days           = $rows.Count
    asiaEndHourUtc = $AsiaEnd
    reliableN      = $reliableN
    periods        = @($periods | ForEach-Object { $_.label })
    overall        = New-Group $rows
    weekdays       = @(foreach ($i in 0..4) { New-Group ($rows | Where-Object { $_.wd -eq $i }) })
    rows           = $rows
}

$outDir = Split-Path -Parent $Out
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$json = $result | ConvertTo-Json -Depth 10 -Compress
[IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))

Write-Host ("{0} days {1} .. {2} -> {3} ({4} KB)" -f $rows.Count, $result.from, $result.to, (Resolve-Path $Out), [int]((Get-Item $Out).Length / 1KB))
$names = "Mon", "Tue", "Wed", "Thu", "Fri"
foreach ($i in 0..4) {
    $c = $result.weekdays[$i].all
    Write-Host ("  {0}  n={1,3}  up={2}%  down={3}%  avg rest move={4}%" -f $names[$i], $c.n, $c.upPct, $c.downPct, $c.avgRestPct)
}
