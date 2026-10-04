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
            // scalp research uses only the high-volatility regime (user decision 04/10/2026)
            case "hv-explore": PFrom = U(2025, 1); PTo = U(2026, 3); break;
            case "hv-validate": PFrom = U(2026, 3); PTo = U(2026, 7); break;
            case "hv-holdout": PFrom = U(2026, 7); PTo = long.MaxValue; break;
            default: throw new ArgumentException("period: explore | validate | holdout | all | hv-explore | hv-validate | hv-holdout");
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
        if (name == "h8") SessionBehaviour();
        if (name == "h9") ScalpGate();
        if (name == "h10") ScalpAnatomy();
        if (name == "s3") ScalpRuleS3();
        return 0;
    }

    // ------------------------------------------------------------------ prediction cache (so scalp rules can be tried in seconds)
    // Phase1.exe predict  -> %LOCALAPPDATA%\BacktestLab\cache\pred-<symbol>.bin   (outside OneDrive; safe to delete, takes ~2 minutes to rebuild)

    static readonly int[] ScalpHorizons = { 1, 3, 5, 15 };
    static string CachePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", "cache", "pred-" + SymbolName + ".bin");

    static void SavePredictions()
    {
        var months = MonthStarts();
        Active = X.Take(BaseCount).ToArray();                    // price/volume features only: the news layer did not improve short horizons
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath));
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create(CachePath)))
        {
            w.Write(T.Length); w.Write(T[T.Length - 1]); w.Write(ScalpHorizons.Length);
            foreach (var h in ScalpHorizons)
            {
                var res = WalkForward(h, months);
                w.Write(h); w.Write(res.Count);
                foreach (var p in res) { w.Write(p.Bar); w.Write(p.P); }
                Console.WriteLine($"horizon {h} min: {res.Count:N0} predictions");
            }
        }
        Console.WriteLine("wrote " + CachePath + $" ({new System.IO.FileInfo(CachePath).Length / 1048576} MB)");
    }

    static Dictionary<int, List<Pred>> LoadPredictions()
    {
        if (!System.IO.File.Exists(CachePath)) { Console.WriteLine("no prediction cache: run  Phase1.exe predict"); return null; }
        var all = new Dictionary<int, List<Pred>>();
        using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(CachePath)))
        {
            if (r.ReadInt32() != T.Length || r.ReadInt64() != T[T.Length - 1]) { Console.WriteLine("prediction cache does not match the bar file: run  Phase1.exe predict"); return null; }
            int nh = r.ReadInt32();
            for (int q = 0; q < nh; q++)
            {
                int h = r.ReadInt32(), n = r.ReadInt32(); var list = new List<Pred>(n);
                for (int i = 0; i < n; i++) { int bar = r.ReadInt32(); float p = r.ReadSingle(); list.Add(new Pred { Bar = bar, P = p, Move = (float)(C[bar + h] - C[bar]) }); }
                all[h] = list;
            }
        }
        return all;
    }

    // ------------------------------------------------------------------ S3: pullback scalp in the direction of the day (rule locked 04/10/2026 from hv-explore)
    // minute model confident >= 56% at 5 minutes, trade direction = direction of the day so far (price vs the day's open, UTC),
    // away from scheduled news, hold 5 minutes, one trade at a time. S3b = the same, London session only (07:00-13:00 UTC).

    static void ScalpRuleS3()
    {
        var preds = LoadPredictions(); if (preds == null) return;
        int n = T.Length; const int h = 5; const double th = 0.56;
        var dayOpen = new double[n]; long curDay = -1; double open = 0;
        for (int i = 0; i < n; i++) { long d = T[i] / 86400; if (d != curDay) { curDay = d; open = O[i]; } dayOpen[i] = open; }
        Console.WriteLine("\n==== S3 pullback scalp with the day (USD per oz after cost = percent of a 100 USD account at 0.01 lot) ====");
        foreach (var (label, londonOnly) in new[] { ("S3  all sessions", false), ("S3b London only ", true) })
        {
            var st = new St(); double gw = 0, gl = 0, cum = 0, peak = 0, maxDd = 0, worst = 0; long freeAt = 0, first = 0, last = 0;
            var months = new SortedDictionary<string, St>(); var daysPnl = new SortedDictionary<long, double>();
            foreach (var p in preds[h])
            {
                long t = T[p.Bar]; int i = p.Bar;
                double conf = p.P >= 0.5 ? p.P : 1 - p.P;
                if (!InPeriod(t) || t < freeAt || conf < th || Window[i] != 0) continue;
                int dir = p.P >= 0.5 ? 1 : -1;
                if (Math.Sign(C[i] - dayOpen[i]) != dir) continue;
                if (londonOnly && Session[i] != 1) continue;
                freeAt = t + h * 60L; if (first == 0) first = t; last = t;
                double pnl = dir * p.Move - FixedCost;
                st.Add(pnl); if (pnl > 0) gw += pnl; else gl -= pnl;
                cum += pnl; peak = Math.Max(peak, cum); maxDd = Math.Max(maxDd, peak - cum); worst = Math.Min(worst, pnl);
                var d = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime; string key = d.ToString("yy-MM", Inv);
                if (!months.TryGetValue(key, out var m)) months[key] = m = new St();
                m.Add(pnl);
                daysPnl[t / 86400] = (daysPnl.TryGetValue(t / 86400, out var dp) ? dp : 0) + pnl;
            }
            if (st.N == 0) { Console.WriteLine($"{label}: no trades"); continue; }
            Console.WriteLine($"{label}: n {st.N} ({st.N / Math.Max(1, (last - first) / 86400.0 * 5 / 7):N1}/day)  win {st.Win:N1}%  avg {st.Mean:N3}  PF {(gl > 0 ? gw / gl : 0):N2}  net {st.Sum:N0}  t {st.TStat:N2}");
            Console.WriteLine($"   max drawdown {maxDd:N0}   worst trade {worst:N1}   worst day {daysPnl.Values.Min():N1}   best day {daysPnl.Values.Max():N1}   losing days {100.0 * daysPnl.Values.Count(v => v < 0) / daysPnl.Count:N0}%");
            Console.WriteLine("   by month (net, trades): " + string.Join("  ", months.Select(kv => $"{kv.Key}: {kv.Value.Sum.ToString("+0;-0", Inv)} ({kv.Value.N})")));
        }
    }

    // ------------------------------------------------------------------ H10: where do the scalp trades of the minute model win and lose?
    // Trades: confidence >= threshold, away from scheduled news, one at a time, hold h minutes, cost charged. Then split by context.

    static void ScalpAnatomy()
    {
        var preds = LoadPredictions(); if (preds == null) return;
        int n = T.Length;
        var dayOpen = new double[n]; long curDay = -1; double open = 0;
        for (int i = 0; i < n; i++) { long d = T[i] / 86400; if (d != curDay) { curDay = d; open = O[i]; } dayOpen[i] = open; }
        var a5 = new double[n]; for (int i = 5; i < n; i++) a5[i] = Math.Abs(C[i] - C[i - 5]);
        var usual5 = RollMean(a5, 1440);
        Console.WriteLine("\n==== H10 anatomy of the model scalps (USD per oz after cost = percent of a 100 USD account) ====");
        foreach (var h in new[] { 3, 5, 15 })
            foreach (var th in new[] { 0.56, 0.58 })
            {
                var groups = new SortedDictionary<string, (St st, double[] g)>();
                void Add(string key, double pnl) { if (!groups.TryGetValue(key, out var v)) groups[key] = v = (new St(), new double[2]); v.st.Add(pnl); if (pnl > 0) v.g[0] += pnl; else v.g[1] -= pnl; }
                long freeAt = 0;
                foreach (var p in preds[h])
                {
                    long t = T[p.Bar]; int i = p.Bar;
                    double conf = p.P >= 0.5 ? p.P : 1 - p.P;
                    if (!InPeriod(t) || t < freeAt || conf < th || Window[i] != 0) continue;
                    freeAt = t + h * 60L;
                    int dir = p.P >= 0.5 ? 1 : -1; double pnl = dir * p.Move - FixedCost;
                    Add("0 all", pnl);
                    int dayDir = Math.Sign(C[i] - dayOpen[i]);
                    Add(dayDir == dir ? "1 WITH the day so far" : "1 against the day so far", pnl);
                    Add(Math.Sign(C[i] - C[i - 15]) == dir ? "2 follows the last 15 min" : "2 fades the last 15 min", pnl);
                    Add(Math.Sign(C[i] - C[i - 1]) == dir ? "3 follows the last candle" : "3 fades the last candle", pnl);
                    Add("4 session " + SessionNames[Session[i]], pnl);
                    Add("5 regime " + RegimeNames[Regime[i]], pnl);
                    Add("6 usual 5-min move " + (usual5[i] < 1.5 ? "a < 1.5" : usual5[i] < 3 ? "b 1.5-3" : usual5[i] < 6 ? "c 3-6" : "d >= 6"), pnl);
                }
                Console.WriteLine($"\n---- hold {h} min, confidence >= {th * 100:0}% ----");
                foreach (var kv in groups)
                    Console.WriteLine($"  {kv.Key.Substring(2),-28} n {kv.Value.st.N,5} win {kv.Value.st.Win,5:N1}% avg {kv.Value.st.Mean,7:N3} PF {(kv.Value.g[1] > 0 ? kv.Value.g[0] / kv.Value.g[1] : 0),4:N2} net {kv.Value.st.Sum,7:N0} t {kv.Value.st.TStat,5:N2}");
            }
    }

    // ------------------------------------------------------------------ H9 / S1: scalp only when the expected edge beats the cost
    // alpha(bucket) = how far the price goes in the predicted direction, as a share of the usual h-minute move — measured on 2021-2024 only.
    // Rule: enter when alpha x (usual move right now) - cost >= margin x cost; hold h minutes; one trade at a time.

    static void ScalpGate()
    {
        var preds = LoadPredictions(); if (preds == null) return;
        long exFrom = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), exTo = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        bool byMonth = PTo - Math.Max(PFrom, T[0]) < 800L * 86400;
        int nb = Edges.Length + 1, n = T.Length;
        Console.WriteLine("\n==== H9 / S1 scalp gate: trade the minute model only when its expected edge beats the cost ====");
        Console.WriteLine("USD per oz = percent of a 100 USD account at 0.01 lot. alpha is measured on 2021-2024 and then frozen.");
        foreach (var h in ScalpHorizons)
        {
            var a = new double[n]; for (int i = h; i < n; i++) a[i] = Math.Abs(C[i] - C[i - h]);
            var usual = RollMean(a, 1440);                                       // typical |h-minute move| over the last trading day
            var num = new double[nb]; var den = new double[nb]; var cnt = new long[nb];
            foreach (var p in preds[h])
            {
                long t = T[p.Bar]; if (t < exFrom || t >= exTo) continue;
                int b = Bucket(p.P); num[b] += (p.P >= 0.5 ? p.Move : -p.Move); den[b] += usual[p.Bar]; cnt[b]++;
            }
            var alpha = Enumerable.Range(0, nb).Select(b => den[b] > 0 ? num[b] / den[b] : 0).ToArray();
            Console.WriteLine($"\n---- hold {h} minutes ----");
            Console.WriteLine("  confidence   n (2021-24)    alpha   usual move needed to cover the cost");
            for (int b = 0; b < nb; b++) if (cnt[b] > 0) Console.WriteLine($"  {BucketName(b),-9} {cnt[b],12:N0}  {alpha[b],7:N3}   {(alpha[b] > 0 ? (FixedCost / alpha[b]).ToString("N2", Inv) : "never")}");

            foreach (var margin in new[] { 0.0, 0.5, 1.0 })
                foreach (var skipNews in new[] { false, true })
                {
                    var st = new St(); double gw = 0, gl = 0; long freeAt = 0, first = 0, last = 0;
                    var by = new SortedDictionary<string, St>();
                    foreach (var p in preds[h])
                    {
                        long t = T[p.Bar]; if (!InPeriod(t) || t < freeAt) continue;
                        int b = Bucket(p.P);
                        if (alpha[b] <= 0 || alpha[b] * usual[p.Bar] - FixedCost < margin * FixedCost) continue;
                        if (skipNews && Window[p.Bar] != 0) continue;
                        double pnl = (p.P >= 0.5 ? p.Move : -p.Move) - FixedCost;
                        st.Add(pnl); if (pnl > 0) gw += pnl; else gl -= pnl;
                        var d = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime; string key = byMonth ? d.ToString("yy-MM", Inv) : d.ToString("yy", Inv);
                        if (!by.TryGetValue(key, out var y)) by[key] = y = new St();
                        y.Add(pnl);
                        freeAt = t + h * 60L; if (first == 0) first = t; last = t;
                    }
                    double tradingDays = Math.Max(1, (last - first) / 86400.0 * 5 / 7);
                    Console.WriteLine($"  edge >= {1 + margin:0.0}x cost{(skipNews ? ", away from news" : "               ")}: n {st.N,5} ({st.N / tradingDays,4:N1}/day) win {st.Win,5:N1}% PF {(gl > 0 ? gw / gl : 0),4:N2} net {st.Sum,7:N0} avg {st.Mean,6:N3} t {st.TStat,5:N2} | "
                        + string.Join(" ", by.Select(kv => $"{kv.Key}:{kv.Value.Sum.ToString("+0;-0", Inv)}")));
                }
        }
    }

    // ------------------------------------------------------------------ H8: how does each session behave, and which kind of rule fits it?

    static readonly (string name, int start, int end)[] Sessions =
    {
        ("Asia      00:00-07:00", 0, 420),
        ("London    07:00-13:00", 420, 780),
        ("NY early  13:00-17:00", 780, 1020),
        ("NY late   17:00-20:45", 1020, 1245),
    };

    static void SessionBehaviour()
    {
        var days = Days(); int ns = Sessions.Length;
        // behaviour
        var range = new St[ns]; var absNet = new St[ns]; var eff = new St[ns]; var netOverRange = new St[ns]; var vol = new St[ns];
        var sum1 = new double[ns]; var n1 = new long[ns];
        int[] ks = { 5, 15, 30 }; var sumK = new double[ns, 3]; var nK = new long[ns, 3]; var acNum = new double[ns]; var acDen = new double[ns];
        var highIn = new int[ns]; var lowIn = new int[ns]; int dayCount = 0;
        // rules: 0 opening-range breakout, 1 mid-session extension (>=0.75), 2 same (>=1.5), 3 carry-over from earlier in the day, 4 break of the previous session's range
        string[] rules = { "A opening-range breakout (first 60 min) -> session end",
                           "B at mid-session, already moved >= 0.75x usual: keep going?",
                           "B at mid-session, already moved >= 1.5x usual: keep going?",
                           "C day already moved >= 0.75x usual before this session: keep going?",
                           "D first close beyond the previous session's high/low -> session end",
                           "A+ opening-range breakout in the SAME direction as the day so far",
                           "A- opening-range breakout AGAINST the direction of the day so far",
                           "D+ break of the previous session's range in the SAME direction as the day so far",
                           "D- break of the previous session's range AGAINST the direction of the day so far" };
        var res = new (St st, SortedDictionary<int, St> years)[ns, rules.Length];
        for (int s = 0; s < ns; s++) { range[s] = new St(); absNet[s] = new St(); eff[s] = new St(); netOverRange[s] = new St(); vol[s] = new St(); for (int r = 0; r < rules.Length; r++) res[s, r] = (new St(), new SortedDictionary<int, St>()); }
        void Rec(int s, int r, Day d, double usd) { res[s, r].st.Add(usd); if (!res[s, r].years.TryGetValue(d.Year, out var y)) res[s, r].years[d.Year] = y = new St(); y.Add(usd); }
        var midHist = Enumerable.Range(0, ns).Select(_ => new Queue<double>()).ToArray();
        var carryHist = Enumerable.Range(0, ns).Select(_ => new Queue<double>()).ToArray();
        double prevDayHi = double.NaN, prevDayLo = double.NaN;

        foreach (var d in days)
        {
            bool use = InPeriod(d.T0);
            var hi = new double[ns]; var lo = new double[ns]; var ok = new bool[ns]; var first = new int[ns]; var last = new int[ns];
            double dayHi = double.MinValue, dayLo = double.MaxValue; int hiS = -1, loS = -1;
            for (int s = 0; s < ns; s++)
            {
                int i0 = LowerBound(d.T0 + Sessions[s].start * 60L), i1 = LowerBound(d.T0 + Sessions[s].end * 60L);
                first[s] = i0; last[s] = i1;
                ok[s] = i1 - i0 >= 0.9 * (Sessions[s].end - Sessions[s].start) && i0 < T.Length && T[i0] - (d.T0 + Sessions[s].start * 60L) < 600;
                if (!ok[s]) continue;
                hi[s] = double.MinValue; lo[s] = double.MaxValue;
                for (int i = i0; i < i1; i++) { if (H[i] > hi[s]) hi[s] = H[i]; if (L[i] < lo[s]) lo[s] = L[i]; }
                if (hi[s] > dayHi) { dayHi = hi[s]; hiS = s; }
                if (lo[s] < dayLo) { dayLo = lo[s]; loS = s; }
            }
            if (use && ok.All(v => v)) { dayCount++; highIn[hiS]++; lowIn[loS]++; }

            for (int s = 0; s < ns; s++)
            {
                if (!ok[s]) continue;
                int i0 = first[s], i1 = last[s];
                double open = O[i0], close = C[i1 - 1], net = close - open;

                // ---- rule B needs the trailing size of the mid-session move; rule C the trailing size of the move before the session
                int im = i0 + (i1 - i0) / 2; double dev = C[im] - open;
                double sigmaB = midHist[s].Count >= 20 ? Math.Sqrt(midHist[s].Average(v => v * v)) : 0;
                double prior = s == 0 ? d.PrevRet : open - d.P00;
                double sigmaC = carryHist[s].Count >= 20 ? Math.Sqrt(carryHist[s].Average(v => v * v)) : 0;

                if (use)
                {
                    double path = 0; for (int i = i0 + 1; i < i1; i++) path += Math.Abs(C[i] - C[i - 1]);
                    range[s].Add(hi[s] - lo[s]); absNet[s].Add(Math.Abs(net)); vol[s].Add(Enumerable.Range(i0, i1 - i0).Sum(i => V[i]));
                    if (path > 0) eff[s].Add(Math.Abs(net) / path);
                    if (hi[s] > lo[s]) netOverRange[s].Add(Math.Abs(net) / (hi[s] - lo[s]));
                    for (int i = i0 + 1; i < i1; i++) { double r = C[i] - C[i - 1]; sum1[s] += r * r; n1[s]++; }
                    for (int q = 0; q < ks.Length; q++)
                    {
                        double prevR = double.NaN;
                        for (int i = i0 + ks[q]; i < i1; i += ks[q])
                        {
                            double r = C[i] - C[i - ks[q]]; sumK[s, q] += r * r; nK[s, q]++;
                            if (ks[q] == 15) { if (!double.IsNaN(prevR)) { acNum[s] += r * prevR; acDen[s] += prevR * prevR; } prevR = r; }
                        }
                    }

                    // A: opening-range breakout
                    double orHi = double.MinValue, orLo = double.MaxValue;
                    for (int i = i0; i < i0 + 60; i++) { orHi = Math.Max(orHi, H[i]); orLo = Math.Min(orLo, L[i]); }
                    for (int i = i0 + 60; i < i1 - 1; i++)
                    {
                        int dirA = C[i] > orHi ? 1 : C[i] < orLo ? -1 : 0;
                        if (dirA == 0) continue;
                        Rec(s, 0, d, dirA * (close - C[i]));
                        if (!double.IsNaN(prior) && prior != 0) Rec(s, dirA == Math.Sign(prior) ? 5 : 6, d, dirA * (close - C[i]));
                        break;
                    }
                    // B: extension at mid-session, result of continuing in the same direction
                    if (sigmaB > 0 && dev != 0)
                    {
                        double follow = Math.Sign(dev) * (close - C[im]);
                        if (Math.Abs(dev) >= 0.75 * sigmaB) Rec(s, 1, d, follow);
                        if (Math.Abs(dev) >= 1.5 * sigmaB) Rec(s, 2, d, follow);
                    }
                    // C: carry-over
                    if (sigmaC > 0 && !double.IsNaN(prior) && Math.Abs(prior) >= 0.75 * sigmaC) Rec(s, 3, d, Math.Sign(prior) * net);
                    // D: break of the previous session's range (for Asia: the previous day's range)
                    double pHi = s == 0 ? prevDayHi : (ok[s - 1] ? hi[s - 1] : double.NaN), pLo = s == 0 ? prevDayLo : (ok[s - 1] ? lo[s - 1] : double.NaN);
                    if (!double.IsNaN(pHi))
                        for (int i = i0; i < i1 - 1; i++)
                        {
                            int dirD = C[i] > pHi ? 1 : C[i] < pLo ? -1 : 0;
                            if (dirD == 0) continue;
                            Rec(s, 4, d, dirD * (close - C[i]));
                            if (!double.IsNaN(prior) && prior != 0) Rec(s, dirD == Math.Sign(prior) ? 7 : 8, d, dirD * (close - C[i]));
                            break;
                        }
                }
                midHist[s].Enqueue(dev); if (midHist[s].Count > 60) midHist[s].Dequeue();
                if (!double.IsNaN(prior)) { carryHist[s].Enqueue(prior); if (carryHist[s].Count > 60) carryHist[s].Dequeue(); }
            }
            if (ok.All(v => v)) { prevDayHi = dayHi; prevDayLo = dayLo; } else { prevDayHi = prevDayLo = double.NaN; }
        }

        Console.WriteLine("\n==== H8 session behaviour (UTC; Thai time = +7) ====");
        Console.WriteLine("session                 range USD  |net| USD  cost/|net|  |net|/range  efficiency   VR5   VR15  VR30  autocorr15  day high here  day low here  tick volume");
        for (int s = 0; s < ns; s++)
        {
            double v1 = sum1[s] / Math.Max(1, n1[s]);
            string VR(int q) => (sumK[s, q] / Math.Max(1, nK[s, q]) / (ks[q] * v1)).ToString("0.00", Inv);
            Console.WriteLine($"{Sessions[s].name}  {range[s].Mean,9:N2}  {absNet[s].Mean,9:N2}  {100 * FixedCost / absNet[s].Mean,9:N0}%  {netOverRange[s].Mean,11:N2}  {eff[s].Mean,10:N3}  {VR(0),5} {VR(1),5} {VR(2),5}  {acNum[s] / Math.Max(1e-12, acDen[s]),10:N3}  {100.0 * highIn[s] / Math.Max(1, dayCount),12:N1}%  {100.0 * lowIn[s] / Math.Max(1, dayCount),11:N1}%  {vol[s].Mean,11:N0}");
        }
        Console.WriteLine("VR (variance ratio): below 1 = moves tend to be undone (mean reverting), above 1 = moves tend to extend (trending). autocorr15 = correlation of consecutive 15-minute moves.");
        Console.WriteLine("\nrule results: mean = USD per oz from going WITH the move, before cost. Negative = the opposite trade (fade) is the one that pays.");
        for (int r = 0; r < rules.Length; r++)
        {
            Console.WriteLine($"\n{rules[r]}");
            for (int s = 0; s < ns; s++)
            {
                var x = res[s, r]; if (x.st.N == 0) continue;
                Console.WriteLine($"  {Sessions[s].name}  n {x.st.N,5}  win {x.st.Win,5:N1}%  mean {x.st.Mean,6:N2}  t {x.st.TStat,5:N2}  net with {x.st.Mean - FixedCost,6:N2} / fade {-x.st.Mean - FixedCost,6:N2}  | {Years(x.years, y => y.Mean)}");
            }
        }
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
