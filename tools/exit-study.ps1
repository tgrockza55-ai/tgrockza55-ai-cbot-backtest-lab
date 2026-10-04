# Exit research: replay every trade of a local report on the M1 bars and ask
#   1) "N minutes after entry, with the trade at this level, how did trades like it end?"  (state table)
#   2) "what would a different exit plan have earned on the same entries?"                    (rule grid)
# All distances are in R = the trade's initial stop distance, so trades of any size are comparable.
#
# Input : Documents\BacktestLab\local\<report>.json   (tools\backtest.ps1 -NoSend -SaveLocal)
#         Documents\BacktestLab\data\<symbol>_Minute.csv (strategy DATA_EXPORT)
# Usage : powershell -ExecutionPolicy Bypass -File tools\exit-study.ps1 -Report CH_ASIA_BREAK-XAUUSD-Minute [-Tag ASIA]
#           [-From 2020-01-01] [-To 2026-03-01] [-FlatTime 2045] [-Spread 0.2] [-Top 15]
# The replay ignores the one-position-at-a-time interaction between trades, so confirm any rule with a real backtest.

param(
    [Parameter(Mandatory = $true)][string]$Report,
    [string]$Symbol = "XAUUSD",
    [string]$Tag,                       # only trades with this tag (combo legs)
    [string]$Side,                      # Buy | Sell
    [int[]]$Weekdays,                   # 1 = Monday .. 5 = Friday (Sunday-evening entries count as Monday)
    [int]$HourFrom = 0, [int]$HourTo = 24,   # entry hour (UTC), from inclusive, to exclusive
    [string]$From = "0000", [string]$To = "9999",
    [int]$FlatTime = 2045,              # UTC HHmm: forced exit time of the replay
    [int]$FlatHour = -1,                # optional earlier forced exit hour (e.g. 20 for the Asian-range leg)
    [double]$Spread = 0.2,              # price units; bars are bid, so it is added to the exit side of Sell trades
    [int]$Top = 15
)

$ErrorActionPreference = "Stop"
$lab = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "BacktestLab"
$csv = Join-Path $lab "data\${Symbol}_Minute.csv"
$json = Join-Path $lab "local\$Report.json"
foreach ($f in $csv, $json) { if (-not (Test-Path $f)) { Write-Host "Not found: $f" -ForegroundColor Red; exit 1 } }

Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public class ExitTrade { public long Entry; public int Dir; public double Price, R; }

public class ExitPlan
{
    public double Tp;          // take profit in R
    public double BeAt;        // move stop to entry once profit reached this many R (0 = never)
    public int CutMin;         // time stop: minutes after entry (0 = none)
    public double CutBelow;    // ... exit then if the trade is below this many R
    public int N, Wins; public double SumR, GrossWin, GrossLoss;
}

public static class ExitLab
{
    static long[] T; static double[] H, L, C;

    public static int LoadBars(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        var t = new List<long>(); var h = new List<double>(); var l = new List<double>(); var c = new List<double>();
        using (var r = new StreamReader(path))
        {
            r.ReadLine(); string line;
            while ((line = r.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 5) continue;
                t.Add(long.Parse(p[0], inv)); h.Add(double.Parse(p[2], inv)); l.Add(double.Parse(p[3], inv)); c.Add(double.Parse(p[4], inv));
            }
        }
        T = t.ToArray(); H = h.ToArray(); L = l.ToArray(); C = c.ToArray();
        return T.Length;
    }

    static int IndexAt(long unix)
    {
        int i = Array.BinarySearch(T, unix);
        return i >= 0 ? i : ~i;
    }

    // true while the position may stay open: before the flat time of the entry's trading block
    static bool MustFlat(long unix, int flatMin, int flatHour)
    {
        int m = (int)((unix % 86400) / 60);
        if (flatHour >= 0 && m >= flatHour * 60 && m < 23 * 60) return true;
        return m >= flatMin && m < 23 * 60;
    }

    /// Replay one trade under a plan; returns the result in R (spread already charged).
    public static double Replay(ExitTrade tr, ExitPlan p, int flatMin, int flatHour, double spread, int maxBars)
    {
        int i0 = IndexAt(tr.Entry);
        double stop = -1.0, cost = 0, ask = tr.Dir < 0 ? spread : 0;   // stop level in R; bars are bid, a Sell exits on ask
        for (int i = i0; i < T.Length && i < i0 + maxBars; i++)
        {
            double hi = tr.Dir * ((tr.Dir > 0 ? H[i] : L[i] + ask) - tr.Price) / tr.R;   // best excursion of the bar
            double lo = tr.Dir * ((tr.Dir > 0 ? L[i] : H[i] + ask) - tr.Price) / tr.R;   // worst excursion of the bar
            double cl = tr.Dir * (C[i] + ask - tr.Price) / tr.R;

            if (lo <= stop) return stop - cost;                 // stop first when a bar touches both (conservative)
            if (hi >= p.Tp) return p.Tp - cost;
            if (p.BeAt > 0 && hi >= p.BeAt && stop < 0) stop = 0;

            long minutes = (T[i] + 60 - tr.Entry) / 60;
            if (p.CutMin > 0 && minutes >= p.CutMin && minutes < p.CutMin + 1 && cl < p.CutBelow) return cl - cost;
            if (MustFlat(T[i] + 60, flatMin, flatHour)) return cl - cost;
        }
        return -cost;
    }

    public static void Run(List<ExitTrade> trades, ExitPlan p, int flatMin, int flatHour, double spread)
    {
        foreach (var tr in trades)
        {
            double r = Replay(tr, p, flatMin, flatHour, spread, 1500);
            p.N++; p.SumR += r;
            if (r > 0) { p.Wins++; p.GrossWin += r; } else p.GrossLoss -= r;
        }
    }

    /// State table: at each checkpoint, bucket the trades still open by their current R and report how they ended
    /// under the baseline plan. Rows: checkpoint, bucket low, bucket high, n, share ending > 0, mean current R, mean final R.
    public static List<double[]> States(List<ExitTrade> trades, ExitPlan baseline, int[] checkpoints, double[] edges,
                                        int flatMin, int flatHour, double spread)
    {
        var rows = new List<double[]>();
        foreach (int cp in checkpoints)
        {
            int nb = edges.Length + 1;
            var n = new int[nb]; var pos = new int[nb]; var cur = new double[nb]; var fin = new double[nb];
            foreach (var tr in trades)
            {
                int i0 = IndexAt(tr.Entry);
                double final = Replay(tr, baseline, flatMin, flatHour, spread, 1500);
                // is the trade still open at the checkpoint, and where is it?
                bool open = true; double cl = 0;
                for (int i = i0; i < T.Length && i < i0 + cp; i++)
                {
                    double hi = tr.Dir * ((tr.Dir > 0 ? H[i] : L[i]) - tr.Price) / tr.R;
                    double lo = tr.Dir * ((tr.Dir > 0 ? L[i] : H[i]) - tr.Price) / tr.R;
                    cl = tr.Dir * (C[i] - tr.Price) / tr.R;
                    if (lo <= -1.0 || hi >= baseline.Tp || MustFlat(T[i] + 60, flatMin, flatHour)) { open = false; break; }
                    if ((T[i] + 60 - tr.Entry) / 60 >= cp) break;
                }
                if (!open) continue;
                int b = 0; while (b < edges.Length && cl >= edges[b]) b++;
                n[b]++; cur[b] += cl; fin[b] += final; if (final > 0) pos[b]++;
            }
            for (int b = 0; b < nb; b++)
            {
                if (n[b] == 0) continue;
                rows.Add(new[] { cp, b == 0 ? double.NegativeInfinity : edges[b - 1], b == nb - 1 ? double.PositiveInfinity : edges[b],
                                 n[b], 100.0 * pos[b] / n[b], cur[b] / n[b], fin[b] / n[b] });
            }
        }
        return rows;
    }
}
"@

$inv = [Globalization.CultureInfo]::InvariantCulture
$epoch = New-Object DateTime 1970, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc)
"bars: " + [ExitLab]::LoadBars($csv)

$data = Get-Content $json -Raw | ConvertFrom-Json
$trades = New-Object 'System.Collections.Generic.List[ExitTrade]'
foreach ($t in $data.trades) {
    if ($Tag -and $t.tag -ne $Tag) { continue }
    if ($t.entryTime -lt $From -or $t.entryTime -ge $To -or -not $t.sl) { continue }
    $e = [DateTime]::Parse($t.entryTime, $inv, [Globalization.DateTimeStyles]::AdjustToUniversal)
    if ($Side -and $t.side -ne $Side) { continue }
    $wd = if ($e.DayOfWeek -eq 'Sunday') { 1 } else { [int]$e.DayOfWeek }
    if ($Weekdays -and $Weekdays -notcontains $wd) { continue }
    $hr = if ($e.DayOfWeek -eq 'Sunday') { 0 } else { $e.Hour }
    if ($hr -lt $HourFrom -or $hr -ge $HourTo) { continue }
    $r = [math]::Abs([double]$t.entryPrice - [double]$t.sl)
    if ($r -le 0) { continue }
    $x = New-Object ExitTrade
    $x.Entry = [long]($e - $epoch).TotalSeconds; $x.Dir = $(if ($t.side -eq "Buy") { 1 } else { -1 }); $x.Price = [double]$t.entryPrice; $x.R = $r
    $trades.Add($x)
}
$flatMin = [int]($FlatTime / 100) * 60 + $FlatTime % 100
"trades: $($trades.Count)  ($Report$(if ($Tag) { " tag $Tag" }), $From .. $To)  average R = {0:N2}" -f (($trades | Measure-Object R -Average).Average)

function New-Plan($tp, $be, $cutMin, $cutBelow) {
    $p = New-Object ExitPlan; $p.Tp = $tp; $p.BeAt = $be; $p.CutMin = $cutMin; $p.CutBelow = $cutBelow; return $p
}
function Show-Plan($p) {
    "TP {0,3}R  BE@{1,3}  cut {2,3}m<{3,4}R | n={4,5} win={5,5:N1}% PF={6,4:N2} total={7,7:N1}R avg={8,6:N3}R" -f $p.Tp, $p.BeAt, $p.CutMin, $p.CutBelow,
        $p.N, (100.0 * $p.Wins / [math]::Max(1, $p.N)), $(if ($p.GrossLoss -gt 0) { $p.GrossWin / $p.GrossLoss } else { 0 }), $p.SumR, ($p.SumR / [math]::Max(1, $p.N))
}

""
"--- baseline (TP 2R, no management) ---"
$base = New-Plan 2 0 0 0
[ExitLab]::Run($trades, $base, $flatMin, $FlatHour, $Spread); Show-Plan $base

""
"--- state table: trades still open N minutes after entry, by current result; how they ended under the baseline ---"
"{0,5} {1,14} {2,6} {3,9} {4,9} {5,9}  {6}" -f "min", "now (R)", "n", "end>0", "now avg", "end avg", "verdict"
$rows = [ExitLab]::States($trades, (New-Plan 2 0 0 0), [int[]](15, 30, 60, 120, 240), [double[]](-0.75, -0.5, -0.25, 0, 0.25, 0.5, 1), $flatMin, $FlatHour, $Spread)
foreach ($r in $rows) {
    $range = "{0}..{1}" -f $(if ([double]::IsInfinity($r[1])) { "" } else { $r[1] }), $(if ([double]::IsInfinity($r[2])) { "" } else { $r[2] })
    $delta = $r[6] - $r[5]
    $verdict = if ($r[3] -lt 30) { "(few)" } elseif ($delta -lt -0.1) { "holding loses {0:N2}R on average -> exit" -f (-$delta) } elseif ($delta -gt 0.1) { "holding gains {0:N2}R -> hold" -f $delta } else { "no edge either way" }
    "{0,5} {1,14} {2,6} {3,8:N1}% {4,9:N2} {5,9:N2}  {6}" -f $r[0], $range, $r[3], $r[4], $r[5], $r[6], $verdict
}

""
"--- rule grid: same entries, different exit plans (best $Top by total R) ---"
$plans = foreach ($tp in 1, 1.5, 2, 3) { foreach ($be in 0, 0.5, 1) { foreach ($cut in @(0, 0), @(30, -0.5), @(60, -0.5), @(60, 0), @(120, -0.5), @(120, 0), @(240, 0)) {
    if ($be -ge $tp) { continue }
    $p = New-Plan $tp $be $cut[0] $cut[1]; [ExitLab]::Run($trades, $p, $flatMin, $FlatHour, $Spread); $p } } }
$plans | Sort-Object SumR -Descending | Select-Object -First $Top | ForEach-Object { Show-Plan $_ }
"..."
"worst: " + (Show-Plan ($plans | Sort-Object SumR | Select-Object -First 1))
"plans tested: $(@($plans).Count), with total R > baseline: $(@($plans | Where-Object { $_.SumR -gt $base.SumR }).Count)"
