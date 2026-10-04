// Tier 0 of the research loop: event studies straight from the M1 bars — seconds per run, no model, no cTrader.
// A hypothesis here is "condition known at time T -> what the price did over the next window".
//
// Run: Phase1.exe study:<h1|h2|h4|h5|all> [period:<explore|validate|holdout|all>]
//
// Data discipline (default period = explore):
//   explore   2020-01 .. 2024-12   free trial and error
//   validate  2025-01 .. 2026-02   check a theory that has already been fixed
//   holdout   2026-03 ..           only for a release candidate; every look is written in research\JOURNAL.md

using System;
using System.Collections.Generic;
using System.Linq;

static partial class Phase1
{
    static long PFrom, PTo;

    static void SetPeriod(string name)
    {
        long U(int y, int m) => new DateTimeOffset(y, m, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        switch (name)
        {
            case "explore": PFrom = U(2020, 1); PTo = U(2025, 1); break;
            case "validate": PFrom = U(2025, 1); PTo = U(2026, 3); break;
            case "holdout": PFrom = U(2026, 3); PTo = long.MaxValue; break;
            case "all": PFrom = 0; PTo = long.MaxValue; break;
            default: throw new ArgumentException("period: explore | validate | holdout | all");
        }
        Console.WriteLine($"period: {name}  ({Date(Math.Max(PFrom, T[0]))} .. {Date(Math.Min(PTo - 1, T[T.Length - 1]))})   cost per round trip: {FixedCost}");
    }

    static bool InPeriod(long unix) => unix >= PFrom && unix < PTo;
    static int YearOf(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Year;

    class St
    {
        public int N, Wins; public double Sum, Sq;
        public void Add(double x) { N++; Sum += x; Sq += x * x; if (x > 0) Wins++; }
        public double Mean => N > 0 ? Sum / N : double.NaN;
        public double TStat => N > 2 ? Mean / Math.Sqrt(Math.Max(1e-18, (Sq / N - Mean * Mean) / (N - 1))) : double.NaN;
        public double Win => N > 0 ? 100.0 * Wins / N : double.NaN;
    }

    /// <summary>Close of the last bar that ended at or before the time (NaN when the market was closed around it).</summary>
    static double PriceAt(long unix)
    {
        int i = LowerBound(unix) - 1;
        return i >= 0 && unix - (T[i] + 60) <= 300 ? C[i] : double.NaN;
    }

    static string Years(SortedDictionary<int, St> by, Func<St, double> f, string fmt = "+0.00;-0.00") =>
        string.Join(" ", by.Select(kv => $"{kv.Key % 100}:{f(kv.Value).ToString(fmt, Inv)}"));

    static int RunStudy(string name, string period)
    {
        if (name == "h1" || name == "all") CostMap();
        if (name != "h1") SetPeriod(period);
        if (name == "h2" || name == "all") SessionStudy();
        if (name == "h4" || name == "all") NewsDrift();
        if (name == "h5" || name == "all") HourStudy();
        if (name == "h6") MomentumDose();
        if (name == "h7") AnchorDrive();
        return 0;
    }

    // ------------------------------------------------------------------ H6: intraday momentum as a dose-response
    // signal = move since 00:00 UTC at the entry hour, in units of its own trailing 60-day size (so it is comparable across years).
    // If momentum is real, the outcome should rise step by step from the most negative bin to the most positive one.

    static void MomentumDose()
    {
        var days = Days();
        double[] edges = { -1.5, -0.75, 0, 0.75, 1.5 };
        string[] bins = { "<-1.5", "-1.5..-0.75", "-0.75..0", "0..0.75", "0.75..1.5", ">1.5" };
        var outs = new (string name, long add)[] { ("+2h", 7200), ("+4h", 14400), ("to 20:45", long.MaxValue) };
        Console.WriteLine("\n==== H6 intraday momentum, dose-response: move since 00:00 UTC (in units of its usual size) -> what happens next ====");
        Console.WriteLine("cells: mean USD per oz of simply being LONG over the window (n). Momentum = negative on the left, positive on the right.");
        foreach (int eh in new[] { 7, 10, 13, 15 })
        {
            var hist = new Queue<double>();
            var cell = outs.Select(_ => bins.Select(__ => new St()).ToArray()).ToArray();
            var follow = outs.Select(_ => (mid: new St(), big: new St(), years: new SortedDictionary<int, St>())).ToArray();
            foreach (var d in days)
            {
                long te = d.T0 + eh * 3600L; double pe = PriceAt(te);
                if (double.IsNaN(pe)) continue;
                double raw = pe - d.P00;
                if (InPeriod(d.T0) && hist.Count >= 20)
                {
                    double sigma = Math.Sqrt(hist.Average(v => v * v));
                    if (sigma > 0 && raw != 0)
                    {
                        double s = raw / sigma; int b = 0; while (b < edges.Length && s >= edges[b]) b++;
                        for (int o = 0; o < outs.Length; o++)
                        {
                            long end = Math.Min(outs[o].add == long.MaxValue ? long.MaxValue : te + outs[o].add, d.T0 + 20 * 3600 + 45 * 60);
                            if (end <= te) continue;
                            double px = PriceAt(end); if (double.IsNaN(px)) continue;
                            cell[o][b].Add(px - pe);
                            double f = Math.Sign(s) * (px - pe);
                            if (Math.Abs(s) >= 0.75) { follow[o].mid.Add(f); if (!follow[o].years.TryGetValue(d.Year, out var y)) follow[o].years[d.Year] = y = new St(); y.Add(f); }
                            if (Math.Abs(s) >= 1.5) follow[o].big.Add(f);
                        }
                    }
                }
                hist.Enqueue(raw); if (hist.Count > 60) hist.Dequeue();
            }
            Console.WriteLine($"\nentry {eh:00}:00 UTC ({(eh + 7) % 24:00}:00 Thai)        " + string.Join("  ", bins.Select(b => $"{b,14}")));
            for (int o = 0; o < outs.Length; o++)
            {
                Console.WriteLine($"  long {outs[o].name,-9}        " + string.Join("  ", cell[o].Select(c => $"{c.Mean,7:N2} ({c.N,4})")));
                var f = follow[o];
                Console.WriteLine($"    follow |s|>=0.75: n {f.mid.N,4} win {f.mid.Win,5:N1}% mean {f.mid.Mean,6:N2} t {f.mid.TStat,5:N2} net {f.mid.Mean - FixedCost,6:N2} | {Years(f.years, y => y.Mean)}    |s|>=1.5: n {f.big.N,4} win {f.big.Win,5:N1}% mean {f.big.Mean,6:N2} t {f.big.TStat,5:N2}");
            }
        }
    }

    // ------------------------------------------------------------------ H7: does the first 5 minutes after a fixed clock time set the direction?

    /// <summary>UTC time of a US Eastern wall-clock time on the given UTC day (US daylight saving: 2nd Sunday of March to 1st Sunday of November).</summary>
    static long EasternToUtc(long dayStart, int hour, int minute)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(dayStart).UtcDateTime;
        DateTime NthSunday(int month, int nth) { var first = new DateTime(d.Year, month, 1); int offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7; return first.AddDays(offset + 7 * (nth - 1)); }
        bool dst = d.Date >= NthSunday(3, 2) && d.Date < NthSunday(11, 1);
        return dayStart + (hour + (dst ? 4 : 5)) * 3600L + minute * 60L;
    }

    static void AnchorDrive()
    {
        var days = Days(); int[] waits = { 30, 60, 120 };
        var anchors = new (string name, Func<long, long> at)[]
        {
            ("08:30 New York (US data time)", t0 => EasternToUtc(t0, 8, 30)),
            ("10:00 New York", t0 => EasternToUtc(t0, 10, 0)),
            ("09:30 New York (stock market open)", t0 => EasternToUtc(t0, 9, 30)),
            ("07:00 UTC (placebo: London morning)", t0 => t0 + 7 * 3600L),
            ("03:00 UTC (placebo: Asia)", t0 => t0 + 3 * 3600L),
        };
        Console.WriteLine("\n==== H7 opening drive: direction of the first 5 minutes after a clock time -> the following 30 / 60 / 120 minutes ====");
        Console.WriteLine("mean = USD per oz from following the first 5 minutes, before cost. 'release' = a scheduled US release (any level) at exactly that time.");
        foreach (var an in anchors)
        {
            var groups = new[] { "all days", "release", "no release" }.ToDictionary(g => g, g => (st: waits.Select(_ => new St()).ToArray(), strong: waits.Select(_ => new St()).ToArray(), years: new SortedDictionary<int, St>()));
            foreach (var d in days)
            {
                if (!InPeriod(d.T0)) continue;
                long t = an.at(d.T0); double p0 = PriceAt(t), p5 = PriceAt(t + 300);
                if (double.IsNaN(p0) || double.IsNaN(p5) || p5 == p0) continue;
                int i = LowerBound(t); if (i < 70) continue;
                double usual = 0;
                for (int w = 1; w <= 12; w++)
                {
                    double a = double.MinValue, b = double.MaxValue;
                    for (int q = i - 5 * w; q < i - 5 * w + 5; q++) { a = Math.Max(a, H[q]); b = Math.Min(b, L[q]); }
                    usual += (a - b) / 12;
                }
                bool isStrong = Math.Abs(p5 - p0) >= 2 * usual;
                bool release = HasNews && Array.BinarySearch(EvT, t) >= 0;
                foreach (var key in new[] { "all days", release ? "release" : "no release" })
                {
                    var g = groups[key];
                    for (int k = 0; k < waits.Length; k++)
                    {
                        double px = PriceAt(t + 300 + waits[k] * 60L); if (double.IsNaN(px)) continue;
                        double f = Math.Sign(p5 - p0) * (px - p5);
                        g.st[k].Add(f); if (isStrong) g.strong[k].Add(f);
                        if (k == 1) { if (!g.years.TryGetValue(d.Year, out var y)) g.years[d.Year] = y = new St(); y.Add(f); }
                    }
                }
            }
            Console.WriteLine($"\n{an.name}");
            foreach (var kv in groups)
            {
                if (kv.Value.st[0].N == 0) continue;
                Console.WriteLine($"  {kv.Key,-10} " + string.Join("   ", waits.Select((w, k) => $"+{w}m: n {kv.Value.st[k].N,4} win {kv.Value.st[k].Win,5:N1}% mean {kv.Value.st[k].Mean,5:N2} t {kv.Value.st[k].TStat,5:N2}")));
                Console.WriteLine($"  {"  strong",-10} " + string.Join("   ", waits.Select((w, k) => $"+{w}m: n {kv.Value.strong[k].N,4} win {kv.Value.strong[k].Win,5:N1}% mean {kv.Value.strong[k].Mean,5:N2} t {kv.Value.strong[k].TStat,5:N2}")) + $"   | +60m per year (all): {Years(kv.Value.years, y => y.Mean)}");
            }
        }
    }

    // ------------------------------------------------------------------ H1: how big is the move compared with the cost?

    static void CostMap()
    {
        int[] hs = { 5, 15, 30, 60, 120, 240 };
        var by = new SortedDictionary<int, (double[] sum, long[] n)>();
        for (int i = 0; i < T.Length; i += 5)
        {
            int y = YearOf(T[i]);
            if (!by.TryGetValue(y, out var a)) by[y] = a = (new double[hs.Length], new long[hs.Length]);
            for (int k = 0; k < hs.Length; k++)
            {
                int j = i + hs[k];
                if (j < T.Length && T[j] - T[i] == hs[k] * 60L) { a.sum[k] += Math.Abs(C[j] - C[i]); a.n[k]++; }
            }
        }
        Console.WriteLine($"\n==== H1 cost map: average |move| in USD, and cost {FixedCost} as a share of it (all years; this is volatility, not a signal) ====");
        Console.WriteLine("year   " + string.Join("", hs.Select(h => $"{h + "m",16}")));
        foreach (var kv in by)
            Console.WriteLine($"{kv.Key}   " + string.Join("", hs.Select((h, k) => { double m = kv.Value.sum[k] / Math.Max(1, kv.Value.n[k]); return $"{m,8:N2} ({100 * FixedCost / m,4:N0}%)"; })));
        Console.WriteLine("naive break-even accuracy = 50% + cost / (2 x |move|); real trades need more, because losing calls tend to be bigger than winning ones");
        foreach (var kv in by)
            Console.WriteLine($"{kv.Key}   " + string.Join("", hs.Select((h, k) => { double m = kv.Value.sum[k] / Math.Max(1, kv.Value.n[k]); return $"{50 + 100 * FixedCost / (2 * m),15:N1}%"; })));
    }

    // ------------------------------------------------------------------ trading days

    class Day
    {
        public long T0; public int Year, Wd;
        public double P00, P07, P13, P14, PEnd, AsiaHi, AsiaLo, PrevRet = double.NaN;
    }

    /// <summary>One row per weekday (UTC). PEnd = 20:45 UTC, the flat time of the intraday rule.</summary>
    static List<Day> Days()
    {
        var list = new List<Day>(); Day prev = null;
        for (long d = T[0] / 86400; d <= T[T.Length - 1] / 86400; d++)
        {
            int wd = (int)((d + 4) % 7);                       // 0 = Sunday
            if (wd == 0 || wd == 6) continue;
            long t0 = d * 86400; int i0 = LowerBound(t0);
            if (i0 >= T.Length || T[i0] - t0 > 1800) continue;
            var x = new Day
            {
                T0 = t0, Wd = wd, Year = YearOf(t0), P00 = O[i0],
                P07 = PriceAt(t0 + 7 * 3600), P13 = PriceAt(t0 + 13 * 3600), P14 = PriceAt(t0 + 14 * 3600), PEnd = PriceAt(t0 + 20 * 3600 + 45 * 60),
                AsiaHi = double.MinValue, AsiaLo = double.MaxValue,
            };
            if (double.IsNaN(x.P07) || double.IsNaN(x.P13) || double.IsNaN(x.P14) || double.IsNaN(x.PEnd)) continue;   // holiday / half day
            for (int i = i0; i < T.Length && T[i] < t0 + 7 * 3600; i++) { x.AsiaHi = Math.Max(x.AsiaHi, H[i]); x.AsiaLo = Math.Min(x.AsiaLo, L[i]); }
            if (prev != null && t0 - prev.T0 <= 4 * 86400) x.PrevRet = prev.PEnd - prev.P00;
            list.Add(x); prev = x;
        }
        return list;
    }

    // ------------------------------------------------------------------ H2: does one session predict the next?

    static void SessionStudy()
    {
        var days = Days();
        var defs = new (string name, Func<Day, double> sig, Func<Day, double> from, Func<Day, double> to)[]
        {
            ("Asia 00-07 -> rest 07-20:45", d => d.P07 - d.P00, d => d.P07, d => d.PEnd),
            ("Asia 00-07 -> London 07-13", d => d.P07 - d.P00, d => d.P07, d => d.P13),
            ("Asia+London 00-13 -> NY 13-20:45", d => d.P13 - d.P00, d => d.P13, d => d.PEnd),
            ("London 07-13 -> NY 13-20:45", d => d.P13 - d.P07, d => d.P13, d => d.PEnd),
            ("NY first hour 13-14 -> rest 14-20:45", d => d.P14 - d.P13, d => d.P14, d => d.PEnd),
            ("Previous day -> today 00-20:45", d => d.PrevRet, d => d.P00, d => d.PEnd),
            ("Previous day -> Asia 00-07", d => d.PrevRet, d => d.P00, d => d.P07),
        };
        Console.WriteLine("\n==== H2 session momentum: trade the next window in the direction of the signal window ====");
        Console.WriteLine("mean = result of following the signal, USD per oz before cost; net = after cost. A clearly negative mean says: fade it instead.");
        Console.WriteLine("'strong' = signal bigger than the median of the previous 60 days. baseline = simply being long over the same window.");
        foreach (var def in defs)
        {
            var recent = new Queue<double>();
            var stats = new Dictionary<string, (St usd, St pct, SortedDictionary<int, St> years)>();
            (St usd, St pct, SortedDictionary<int, St> years) Get(string k) { if (!stats.TryGetValue(k, out var v)) stats[k] = v = (new St(), new St(), new SortedDictionary<int, St>()); return v; }
            void Add(string k, Day d, double usd, double basis)
            {
                var s = Get(k); s.usd.Add(usd); s.pct.Add(100 * usd / basis);
                if (!s.years.TryGetValue(d.Year, out var y)) s.years[d.Year] = y = new St();
                y.Add(usd);
            }
            foreach (var d in days)
            {
                double sig = def.sig(d), a = def.from(d), b = def.to(d);
                if (double.IsNaN(sig) || sig == 0) continue;
                double median = recent.Count >= 20 ? recent.OrderBy(v => v).ElementAt(recent.Count / 2) : double.NaN;
                if (InPeriod(d.T0))
                {
                    string dir = sig > 0 ? "up  " : "down";
                    double follow = Math.Sign(sig) * (b - a);
                    Add("baseline long", d, b - a, a);
                    Add(dir + " all   ", d, follow, a);
                    if (!double.IsNaN(median) && Math.Abs(sig) > median) Add(dir + " strong", d, follow, a);
                }
                recent.Enqueue(Math.Abs(sig)); if (recent.Count > 60) recent.Dequeue();
            }
            Console.WriteLine($"\n{def.name}");
            foreach (var key in new[] { "baseline long", "up   all   ", "up   strong", "down all   ", "down strong" })
            {
                if (!stats.TryGetValue(key, out var s)) continue;
                Console.WriteLine($"  {key,-14} n {s.usd.N,5}  win {s.usd.Win,5:N1}%  mean {s.usd.Mean,7:N2} USD ({s.pct.Mean,6:N3}%)  t {s.usd.TStat,5:N2}  net {s.usd.Mean - FixedCost,7:N2}  | per year: {Years(s.years, y => y.Mean)}");
            }
        }
    }

    // ------------------------------------------------------------------ H4: after a release, does the first reaction continue?

    static void NewsDrift()
    {
        if (!HasNews) { Console.WriteLine("\nH4 needs the news cache (tools\\news-fetch.ps1)"); return; }
        int[] waits = { 30, 60, 120 };
        Console.WriteLine("\n==== H4 post-release drift: direction of the first 5 minutes after a release -> the following 30 / 60 / 120 minutes ====");
        Console.WriteLine("mean = USD per oz from following the first reaction, before cost. 'strong' = first reaction at least 2x the usual 5-minute range.");
        foreach (var (label, lo, hi) in new[] { ("EXTREME (NFP, CPI, FOMC)", 2, 2), ("HIGH (PCE, PPI, GDP, retail)", 1, 1), ("MEDIUM (claims, JOLTS)", 0, 0) })
        {
            var all = waits.Select(_ => new St()).ToArray(); var strong = waits.Select(_ => new St()).ToArray();
            var years = new SortedDictionary<int, St>();
            for (int j = 0; j < EvT.Length; j++)
            {
                if (EvLevel[j] < lo || EvLevel[j] > hi || !InPeriod(EvT[j])) continue;
                double p0 = PriceAt(EvT[j]), p5 = PriceAt(EvT[j] + 300);
                if (double.IsNaN(p0) || double.IsNaN(p5) || p5 == p0) continue;
                int i = LowerBound(EvT[j]); if (i < 70) continue;
                double usual = 0;
                for (int w = 1; w <= 12; w++)
                {
                    double a = double.MinValue, b = double.MaxValue;
                    for (int q = i - 5 * w; q < i - 5 * w + 5; q++) { a = Math.Max(a, H[q]); b = Math.Min(b, L[q]); }
                    usual += (a - b) / 12;
                }
                bool isStrong = Math.Abs(p5 - p0) >= 2 * usual;
                for (int k = 0; k < waits.Length; k++)
                {
                    double px = PriceAt(EvT[j] + 300 + waits[k] * 60L);
                    if (double.IsNaN(px)) continue;
                    double follow = Math.Sign(p5 - p0) * (px - p5);
                    all[k].Add(follow); if (isStrong) strong[k].Add(follow);
                    if (k == 1) { int y = YearOf(EvT[j]); if (!years.TryGetValue(y, out var s)) years[y] = s = new St(); s.Add(follow); }
                }
            }
            Console.WriteLine($"\n{label}");
            for (int k = 0; k < waits.Length; k++)
                Console.WriteLine($"  +{waits[k],3}m  all: n {all[k].N,4} win {all[k].Win,5:N1}% mean {all[k].Mean,6:N2} t {all[k].TStat,5:N2}   strong: n {strong[k].N,4} win {strong[k].Win,5:N1}% mean {strong[k].Mean,6:N2} t {strong[k].TStat,5:N2}");
            Console.WriteLine($"  +60m per year (all): {Years(years, y => y.Mean)}");
        }
    }

    // ------------------------------------------------------------------ H5: is there a drift by hour of day?

    static void HourStudy()
    {
        var days = Days();
        var hours = Enumerable.Range(0, 24).Select(_ => (bp: new St(), usd: new St(), years: new SortedDictionary<int, St>())).ToArray();
        foreach (var d in days)
        {
            if (!InPeriod(d.T0)) continue;
            for (int h = 0; h < 24; h++)
            {
                double a = h == 0 ? d.P00 : PriceAt(d.T0 + h * 3600L), b = PriceAt(d.T0 + (h + 1) * 3600L);
                if (double.IsNaN(a) || double.IsNaN(b)) continue;
                hours[h].bp.Add(1e4 * (b - a) / a); hours[h].usd.Add(b - a);
                if (!hours[h].years.TryGetValue(d.Year, out var y)) hours[h].years[d.Year] = y = new St();
                y.Add(1e4 * (b - a) / a);
            }
        }
        Console.WriteLine("\n==== H5 drift by hour of day (return of that hour, in basis points = 0.01% of price) ====");
        Console.WriteLine("UTC  Thai      n   mean bp      t   win%   mean USD   years with a positive mean | per year (bp)");
        for (int h = 0; h < 24; h++)
        {
            var s = hours[h]; if (s.bp.N == 0) continue;
            int pos = s.years.Values.Count(y => y.Mean > 0);
            Console.WriteLine($"{h:00}   {(h + 7) % 24:00}   {s.bp.N,5}   {s.bp.Mean,7:N2}  {s.bp.TStat,5:N2}  {s.bp.Win,5:N1}   {s.usd.Mean,8:N3}   {pos}/{s.years.Count}   | {Years(s.years, y => y.Mean)}");
        }
    }
}
