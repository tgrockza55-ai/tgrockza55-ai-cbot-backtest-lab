// Tick-level study of price behaviour: volume profile, liquidity sweeps, anchored VWAP and order-flow proxies (tick delta, CVD).
//
// Input: the tick files written by the cBot's recorder — live on the Demo account (folder "live") or history replayed with
// tools\backtest.ps1 -Record (folder "live-test"). Both have the same format, so a rule found on history is checked on live data unchanged.
//
// Run: Phase1.exe flow <live|live-test> [from yyyy-MM-dd] [to yyyy-MM-dd] [symbol]
//
// What "volume" and "order flow" mean here (spot gold has no exchange, so there are no trade prints):
//   volume      = number of quote changes (ticks). The feed caps it near 550 per minute, so it is a coarse measure of activity.
//   delta / CVD = upticks minus downticks of the mid price (tick rule), cumulated over the trading day. A proxy for buying / selling pressure.
//   profile     = ticks per 0.5 USD price bucket over a trading day -> point of control (POC) and the 70% value area (VAL..VAH).
// A trading day starts at 22:00 UTC (the daily reopening). Sessions in UTC: Asia 22-07, London 07-13, New York 13-21.
//
// Every event is known when its minute closes. The trade is filled on the first quote of the next minute — buy at the ask, sell at the bid —
// and closed on the first quote `hold` minutes later, or at the stop if the bid / ask touched it first. Commission 0.07 per round trip.
// So the result already contains the real spread at that moment. Events overlap: the t-values are too large; look at the months.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

static partial class Phase1
{
    const double FlowBucket = 0.5, FlowCommission = 0.07;

    static (double poc, double vah, double val) ValueArea(Dictionary<int, int> profile)
    {
        if (profile.Count == 0) return (double.NaN, double.NaN, double.NaN);
        int lo = profile.Keys.Min(), hi = profile.Keys.Max(); var a = new int[hi - lo + 1]; long total = 0;
        foreach (var kv in profile) { a[kv.Key - lo] = kv.Value; total += kv.Value; }
        int p = 0; for (int i = 1; i < a.Length; i++) if (a[i] > a[p]) p = i;
        int l = p, h = p; long acc = a[p];
        while (acc < 0.7 * total && (l > 0 || h < a.Length - 1))
        {
            long up = h < a.Length - 1 ? a[h + 1] : -1, down = l > 0 ? a[l - 1] : -1;
            if (up >= down) { h++; acc += up; } else { l--; acc += down; }
        }
        return ((lo + p + 0.5) * FlowBucket, (lo + h + 1) * FlowBucket, (lo + l) * FlowBucket);
    }

    static int FlowStudy(string[] args)
    {
        string kind = args.Length > 1 ? args[1] : "live", from = args.Length > 2 ? args[2] : "0000-00-00", to = args.Length > 3 ? args[3] : "9999-99-99";
        string symbol = args.Length > 4 && !args[4].Contains(":") && args[4] != "check" ? args[4] : "XAUUSD";
        var stopArg = args.FirstOrDefault(a => a.StartsWith("stop:")); if (stopArg != null) StopUsd = double.Parse(stopArg.Substring(5), Inv);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", kind, symbol);
        if (!Directory.Exists(dir)) { Console.WriteLine("no recording: " + dir); return 1; }
        var files = Directory.GetFiles(dir, "tick-*.csv*").Select(f => (file: f, day: Path.GetFileName(f).Substring(5, 10)))
            .Where(x => string.CompareOrdinal(x.day, from) >= 0 && string.CompareOrdinal(x.day, to) <= 0).OrderBy(x => x.day).ToList();
        if (files.Count == 0) { Console.WriteLine("no tick files in " + dir + " for " + from + " .. " + to); return 1; }

        // ------------------------------------------------------------------ minute table built from the ticks
        var T = new List<long>(); var bidO = new List<double>(); var askO = new List<double>(); var bidL = new List<double>(); var askH = new List<double>();
        var O = new List<double>(); var H = new List<double>(); var L = new List<double>(); var C = new List<double>();
        var ticks = new List<int>(); var delta = new List<int>(); var spread = new List<double>(); var cvd = new List<double>();
        var vwapDay = new List<double>(); var sdDay = new List<double>(); var vwapSes = new List<double>();
        var prevPoc = new List<double>(); var prevVah = new List<double>(); var prevVal = new List<double>(); var prevHigh = new List<double>(); var prevLow = new List<double>();
        var asiaHigh = new List<double>(); var asiaLow = new List<double>();

        long curMin = -1, curDay = -1, totalTicks = 0; double lastMid = double.NaN;
        double mBidO = 0, mAskO = 0, mBidL = 0, mAskH = 0, mO = 0, mH = 0, mL = 0, mC = 0, mSpread = 0; int mTicks = 0, mDelta = 0;
        var profile = new Dictionary<int, int>();
        double dayHigh = double.MinValue, dayLow = double.MaxValue, dSum = 0, dSum2 = 0, sSum = 0, cum = 0; long dCnt = 0, sCnt = 0; int sesId = -1;
        double pPoc = double.NaN, pVah = double.NaN, pVal = double.NaN, pHigh = double.NaN, pLow = double.NaN, aHigh = double.NaN, aLow = double.NaN;

        void CloseMinute()
        {
            if (curMin < 0 || mTicks == 0) return;
            T.Add(curMin); bidO.Add(mBidO); askO.Add(mAskO); bidL.Add(mBidL); askH.Add(mAskH); O.Add(mO); H.Add(mH); L.Add(mL); C.Add(mC);
            ticks.Add(mTicks); delta.Add(mDelta); spread.Add(mSpread / mTicks); cvd.Add(cum);
            double vw = dCnt > 0 ? dSum / dCnt : mC; vwapDay.Add(vw); sdDay.Add(dCnt > 0 ? Math.Sqrt(Math.Max(0, dSum2 / dCnt - vw * vw)) : 0);
            vwapSes.Add(sCnt > 0 ? sSum / sCnt : double.NaN);
            prevPoc.Add(pPoc); prevVah.Add(pVah); prevVal.Add(pVal); prevHigh.Add(pHigh); prevLow.Add(pLow);
            bool asiaOver = (curMin + 7200) % 86400 >= 9 * 3600;
            asiaHigh.Add(asiaOver ? aHigh : double.NaN); asiaLow.Add(asiaOver ? aLow : double.NaN);
        }

        foreach (var (file, day) in files)
        {
            long dayStart = new DateTimeOffset(DateTime.ParseExact(day, "yyyy-MM-dd", Inv), TimeSpan.Zero).ToUnixTimeSeconds();
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream s = file.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
            using var r = new StreamReader(s);
            r.ReadLine(); string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 16 || line[12] != ',') continue;
                int c2 = line.IndexOf(',', 13); if (c2 < 0) continue;
                if (!double.TryParse(line.AsSpan(13, c2 - 13), NumberStyles.Float, Inv, out double bid) || !double.TryParse(line.AsSpan(c2 + 1), NumberStyles.Float, Inv, out double ask)) continue;
                long unix = dayStart + ((line[0] - '0') * 10 + (line[1] - '0')) * 3600 + ((line[3] - '0') * 10 + (line[4] - '0')) * 60 + (line[6] - '0') * 10 + (line[7] - '0');
                double mid = (bid + ask) / 2; totalTicks++;

                long minute = unix / 60 * 60;
                if (minute != curMin)
                {
                    CloseMinute();
                    long td = (unix + 7200) / 86400;                                  // trading day starts 22:00 UTC
                    if (td != curDay)
                    {
                        if (curDay >= 0 && profile.Count > 0) { (pPoc, pVah, pVal) = ValueArea(profile); pHigh = dayHigh; pLow = dayLow; }
                        curDay = td; profile.Clear(); dayHigh = double.MinValue; dayLow = double.MaxValue; dSum = dSum2 = 0; dCnt = 0; cum = 0;
                        aHigh = double.NaN; aLow = double.NaN; sesId = -1;
                    }
                    long shifted = (unix + 7200) % 86400;                             // 0 = 22:00 UTC
                    int ses = shifted < 9 * 3600 ? 0 : shifted < 15 * 3600 ? 1 : 2;   // Asia, London (07:00), New York (13:00)
                    if (ses != sesId) { sesId = ses; sSum = 0; sCnt = 0; }            // session VWAP is anchored at the session open
                    curMin = minute; mBidO = bid; mAskO = ask; mBidL = bid; mAskH = ask; mO = mH = mL = mid; mSpread = 0; mTicks = 0; mDelta = 0;
                }
                if (bid < mBidL) mBidL = bid; if (ask > mAskH) mAskH = ask; if (mid > mH) mH = mid; if (mid < mL) mL = mid; mC = mid;
                mTicks++; mSpread += ask - bid;
                if (!double.IsNaN(lastMid)) { int d = mid > lastMid ? 1 : mid < lastMid ? -1 : 0; mDelta += d; cum += d; }
                lastMid = mid;
                if (mid > dayHigh) dayHigh = mid; if (mid < dayLow) dayLow = mid;
                dSum += mid; dSum2 += mid * mid; dCnt++; sSum += mid; sCnt++;
                int b = (int)Math.Floor(mid / FlowBucket); profile[b] = profile.TryGetValue(b, out int cnt) ? cnt + 1 : 1;
                if ((unix + 7200) % 86400 < 9 * 3600) { if (double.IsNaN(aHigh) || mid > aHigh) aHigh = mid; if (double.IsNaN(aLow) || mid < aLow) aLow = mid; }
            }
        }
        CloseMinute();

        int n = T.Count;
        if (n < 500) { Console.WriteLine($"only {n} minutes of ticks: not enough yet"); return 1; }
        var index = new Dictionary<long, int>(n); for (int i = 0; i < n; i++) index[T[i]] = i;
        var rng = new double[n]; for (int i = 0; i < n; i++) rng[i] = H[i] - L[i];
        var usualRng = RollMean(rng, 60);
        var news = LoadFlowNews();
        string Day(long u) => DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime.ToString("yyyy-MM-dd", Inv);
        Console.WriteLine($"\n==== flow study {symbol} ({kind}): {files.Count} days, {n:N0} minutes, {totalTicks:N0} ticks, {Day(T[0])} .. {Day(T[n - 1])} ====");
        Console.WriteLine($"average spread {spread.Average():N3}   average range of a minute {rng.Average():N2}   cost of a round trip = spread + {FlowCommission} commission");
        {   // how much does the tick delta say beyond the price change itself?
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++) { double x = delta[i], y = C[i] - O[i]; sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; }
            double corr = (n * sxy - sx * sy) / Math.Sqrt(Math.Max(1e-12, (n * sxx - sx * sx) * (n * syy - sy * sy)));
            Console.WriteLine($"correlation between the tick delta of a minute and its price change: {corr:N2}");
        }

        // ------------------------------------------------------------------ check: does the cBot's own flow file (FlowTracker.cs) agree with this tool?
        if (args.Contains("check"))
        {
            var recorded = new Dictionary<long, string[]>();
            foreach (var file in Directory.GetFiles(dir, "flow-*.csv").OrderBy(f => f))
                foreach (var line in ReadShared(file).Skip(1))
                {
                    var p = line.Split(','); if (p.Length < 24) continue;
                    recorded[new DateTimeOffset(DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm", Inv), TimeSpan.Zero).ToUnixTimeSeconds()] = p;
                }
            var columns = new (string name, int col, double tol, Func<int, double> mine)[]
            {
                ("ticks", 1, 0, i => ticks[i]), ("delta", 2, 0, i => delta[i]), ("cvd", 3, 0, i => cvd[i]),
                ("vwapDay", 4, 0.0002, i => vwapDay[i]), ("sdDay", 5, 0.0002, i => sdDay[i]), ("vwapSession", 6, 0.0002, i => vwapSes[i]),
                ("prevPoc", 10, 0.0006, i => prevPoc[i]), ("prevVah", 11, 0.0006, i => prevVah[i]), ("prevVal", 12, 0.0006, i => prevVal[i]),
                ("prevHigh", 13, 0.0006, i => prevHigh[i]), ("prevLow", 14, 0.0006, i => prevLow[i]),
                ("asiaHigh", 17, 0.0006, i => asiaHigh[i]), ("asiaLow", 18, 0.0006, i => asiaLow[i]),
            };
            var bad = new int[columns.Length]; int compared = 0, sweepMine = 0, sweepTheirs = 0, sweepBad = 0; string firstBad = null;
            for (int i = 0; i < n; i++)
            {
                if (!recorded.TryGetValue(T[i], out var p)) continue;
                compared++;
                for (int c = 0; c < columns.Length; c++)
                {
                    double theirs = p[columns[c].col].Length == 0 ? double.NaN : double.Parse(p[columns[c].col], Inv), mine = columns[c].mine(i);
                    bool same = double.IsNaN(theirs) && double.IsNaN(mine) || Math.Abs(theirs - mine) <= columns[c].tol;
                    if (!same) { bad[c]++; firstBad ??= $"{p[0]} {columns[c].name}: cBot {p[columns[c].col]} / tool {mine.ToString("0.####", Inv)}"; }
                }
                string code = "";
                if (i >= 60 && T[i] - T[i - 60] == 3600)
                {
                    double u = usualRng[i - 1], h30 = MaxH(i - 30, i - 1), l30 = MinL(i - 30, i - 1);
                    string One(double hi, double lo, string name) => double.IsNaN(hi) || double.IsNaN(lo) ? "" : h30 <= hi && H[i] > hi + 0.3 * u && C[i] < hi ? "|" + name + "H" : l30 >= lo && L[i] < lo - 0.3 * u && C[i] > lo ? "|" + name + "L" : "";
                    code = One(prevHigh[i], prevLow[i], "P") + One(asiaHigh[i], asiaLow[i], "A") + One(MaxH(i - 60, i - 1), MinL(i - 60, i - 1), "R");
                    if (code.Length > 0) code = code.Substring(1);
                }
                if (code.Length > 0) sweepMine++; if (p[21].Length > 0) sweepTheirs++;
                if (code != p[21]) { sweepBad++; firstBad ??= $"{p[0]} sweep: cBot '{p[21]}' / tool '{code}'"; }
            }
            Console.WriteLine($"\ncheck against the cBot's flow file: {compared:N0} minutes compared");
            for (int c = 0; c < columns.Length; c++) Console.WriteLine($"  {columns[c].name,-12} differences {bad[c]}");
            Console.WriteLine($"  sweep        differences {sweepBad}   (minutes with a sweep: tool {sweepMine}, cBot {sweepTheirs})");
            if (firstBad != null) Console.WriteLine("  first difference: " + firstBad);
            return 0;
        }

        // ------------------------------------------------------------------ helpers
        int Hour(int m) => (int)(T[m] % 86400 / 3600);
        bool NearNews(long t) { int k = Array.BinarySearch(news, t); if (k < 0) k = ~k; return (k < news.Length && news[k] - t <= 1800) || (k > 0 && t - news[k - 1] < 7200); }
        bool Ok(int m) => m >= 130 && m < n - 1 && T[m] - T[m - 60] == 3600 && usualRng[m - 1] > 0 && Hour(m) < 21 && !NearNews(T[m] + 60);
        double MaxH(int a, int b) { double v = double.MinValue; for (int j = a; j <= b; j++) if (H[j] > v) v = H[j]; return v; }
        double MinL(int a, int b) { double v = double.MaxValue; for (int j = a; j <= b; j++) if (L[j] < v) v = L[j]; return v; }

        double Trade(int m, int side, int hold, double stop)
        {
            int e = m + 1; if (e >= n || T[e] != T[m] + 60) return double.NaN;
            if (!index.TryGetValue(T[e] + hold * 60L, out int x) || x - e != hold) return double.NaN;        // no gap inside the trade
            double entry = side > 0 ? askO[e] : bidO[e];
            if (stop > 0) for (int j = e; j < x; j++) if (side > 0 ? bidL[j] <= entry - stop : askH[j] >= entry + stop) return -stop - FlowCommission;
            return side * ((side > 0 ? bidO[x] : askO[x]) - entry) - FlowCommission;
        }

        void Row(string label, List<(int m, int side)> events, int hold, double stop = -1)
        {
            if (stop < 0) stop = StopUsd;
            var st = new St(); var rev = new St(); double gw = 0, gl = 0; var months = new SortedDictionary<string, double>();
            foreach (var (m, side) in events)
            {
                double p = Trade(m, side, hold, stop); if (double.IsNaN(p)) continue;
                rev.Add(Trade(m, -side, hold, stop));                                              // the same event traded the other way
                st.Add(p); if (p > 0) gw += p; else gl -= p;
                string key = DateTimeOffset.FromUnixTimeSeconds(T[m]).UtcDateTime.ToString("yy-MM", Inv);
                months[key] = (months.TryGetValue(key, out double v) ? v : 0) + p;
            }
            if (st.N < 30) { Console.WriteLine($"{label,-58} {hold,3}m  n {st.N,5}  (too few)"); return; }
            Console.WriteLine($"{label,-58} {hold,3}m  n {st.N,5}  win {st.Win,5:N1}%  avg {st.Mean.ToString("+0.000;-0.000", Inv),7}  PF {(gl > 0 ? gw / gl : 0),4:N2}  net {st.Sum,7:N0}  t {st.TStat,5:N2}  other way {rev.Mean.ToString("+0.000;-0.000", Inv),7}  months+ {months.Values.Count(v => v > 0)}/{months.Count}  [{string.Join(" ", months.Values.Select(v => v.ToString("+0;-0", Inv)))}]");
        }
        void Rows(string label, List<(int, int)> events, params int[] holds) { foreach (int h in holds) Row(label, events, h); }

        // ------------------------------------------------------------------ 1) volume profile of the previous trading day
        Console.WriteLine("\n-- 1) VOLUME PROFILE of the previous day (POC, value area VAL..VAH) --");
        {
            var reenter = new List<(int, int)>(); var reject = new List<(int, int)>(); var pocTouch = new List<(int, int)>(); var accept = new List<(int, int)>();
            int outside = 0; long pocDay = -1;
            for (int m = 131; m < n - 1; m++)
            {
                if (double.IsNaN(prevPoc[m])) { outside = 0; continue; }
                int side = C[m - 1] > prevVah[m] ? 1 : C[m - 1] < prevVal[m] ? -1 : 0;        // where the previous minute closed
                outside = side != 0 && T[m] - T[m - 1] == 60 ? (outside * side > 0 ? outside + side : side) : 0;
                if (!Ok(m)) continue;
                double u = usualRng[m - 1];
                // a) back into value after >= 30 minutes outside -> towards the POC
                if (outside >= 30 && C[m] < prevVah[m]) reenter.Add((m, -1)); else if (outside <= -30 && C[m] > prevVal[m]) reenter.Add((m, 1));
                // b) from inside: poke through the edge of value and close back in -> fade
                if (side == 0 && H[m] > prevVah[m] + 0.3 * u && C[m] < prevVah[m]) reject.Add((m, -1));
                else if (side == 0 && L[m] < prevVal[m] - 0.3 * u && C[m] > prevVal[m]) reject.Add((m, 1));
                // c) from inside: two closes beyond the edge -> acceptance outside value, follow
                if (C[m] > prevVah[m] + u && C[m - 1] > prevVah[m] && C[m - 2] <= prevVah[m]) accept.Add((m, 1));
                else if (C[m] < prevVal[m] - u && C[m - 1] < prevVal[m] && C[m - 2] >= prevVal[m]) accept.Add((m, -1));
                // d) first touch of yesterday's POC today -> does the approach continue through it?
                long td = (T[m] + 7200) / 86400;
                if (td != pocDay && L[m] <= prevPoc[m] && H[m] >= prevPoc[m] && C[m] != C[m - 5]) { pocDay = td; pocTouch.Add((m, Math.Sign(C[m] - C[m - 5]))); }
            }
            Rows("a) back inside value after 30+ min outside -> to POC", reenter, 5, 15, 30);
            Rows("b) poke through VAH/VAL, close back inside -> fade", reject, 5, 15, 30);
            Rows("c) two closes beyond VAH/VAL -> follow", accept, 5, 15, 30);
            Rows("d) first touch of yesterday's POC -> continue", pocTouch, 5, 15, 30);
        }

        // ------------------------------------------------------------------ 2) liquidity sweeps
        Console.WriteLine("\n-- 2) LIQUIDITY SWEEP: a level is exceeded and the minute closes back behind it (level untouched for 30 min before) -> fade --");
        {
            var levels = new (string name, Func<int, double> high, Func<int, double> low)[]
            {
                ("yesterday's high / low", m => prevHigh[m], m => prevLow[m]),
                ("Asia high / low (after 07:00 UTC)", m => asiaHigh[m], m => asiaLow[m]),
                ("high / low of the last 60 min", m => MaxH(m - 60, m - 1), m => MinL(m - 60, m - 1)),
            };
            foreach (var lv in levels)
            {
                var sweep = new List<(int, int)>(); var breakout = new List<(int, int)>();
                for (int m = 131; m < n - 1; m++)
                {
                    if (!Ok(m)) continue;
                    double hi = lv.high(m), lo = lv.low(m), u = usualRng[m - 1];
                    if (double.IsNaN(hi) || double.IsNaN(lo)) continue;
                    bool freshHigh = MaxH(m - 30, m - 1) <= hi, freshLow = MinL(m - 30, m - 1) >= lo;
                    if (freshHigh && H[m] > hi + 0.3 * u && C[m] < hi) sweep.Add((m, -1));
                    else if (freshLow && L[m] < lo - 0.3 * u && C[m] > lo) sweep.Add((m, 1));
                    if (freshHigh && C[m] > hi + u) breakout.Add((m, 1)); else if (freshLow && C[m] < lo - u) breakout.Add((m, -1));
                }
                Rows("sweep of " + lv.name, sweep, 5, 15, 30);
                Rows("  clean break of it (close beyond by 1x range) -> follow", breakout, 5, 15);
            }
        }

        // ------------------------------------------------------------------ 3) anchored VWAP
        Console.WriteLine("\n-- 3) ANCHORED VWAP (tick-weighted): session VWAP anchored at 07:00 / 13:00 UTC, day VWAP anchored at 22:00 UTC --");
        {
            var pullback = new List<(int, int)>(); var lose = new List<(int, int)>(); var stretch = new List<(int, int)>(); var stretch3 = new List<(int, int)>();
            for (int m = 131; m < n - 1; m++)
            {
                if (!Ok(m)) continue;
                double u = usualRng[m - 1], v = vwapSes[m];
                if (!double.IsNaN(v) && Hour(m) >= 7)
                {
                    int side = 0; double far = 0; bool clean = true;
                    for (int j = m - 30; j < m && clean; j++)
                    {
                        if (double.IsNaN(vwapSes[j])) { clean = false; break; }
                        int sj = Math.Sign(C[j] - vwapSes[j]); if (sj == 0 || (side != 0 && sj != side)) clean = false; side = sj;
                        far = Math.Max(far, Math.Abs(C[j] - vwapSes[j]));
                    }
                    if (clean && side != 0 && far >= 3 * u)
                    {
                        bool touched = side > 0 ? L[m] <= v + 0.2 * u : H[m] >= v - 0.2 * u;
                        if (touched && Math.Sign(C[m] - v) == side) pullback.Add((m, side));          // held the VWAP -> with the trend
                        else if (touched && Math.Sign(C[m] - v) == -side) lose.Add((m, -side));       // closed through it -> trend broken, follow the break
                    }
                }
                if (sdDay[m] > 0 && sdDay[m - 1] > 0 && (T[m] + 7200) % 86400 >= 3 * 3600)
                {
                    double z = (C[m] - vwapDay[m]) / sdDay[m], zPrev = (C[m - 1] - vwapDay[m - 1]) / sdDay[m - 1];
                    if (Math.Abs(z) >= 2 && Math.Abs(zPrev) < 2) stretch.Add((m, -Math.Sign(z)));
                    if (Math.Abs(z) >= 3 && Math.Abs(zPrev) < 3) stretch3.Add((m, -Math.Sign(z)));
                }
            }
            Rows("pullback to the session VWAP that holds -> with the trend", pullback, 5, 15, 30);
            Rows("close through the session VWAP after a 30-min trend -> follow", lose, 5, 15, 30);
            Rows("price 2 sd away from the day VWAP -> back to it", stretch, 15, 30);
            Rows("price 3 sd away from the day VWAP -> back to it", stretch3, 15, 30);
        }

        // ------------------------------------------------------------------ 4) order-flow proxies: tick delta and CVD
        Console.WriteLine("\n-- 4) ORDER FLOW proxies: tick delta of a minute (z-score against the hour before) and cumulative delta (CVD) --");
        {
            var burst = new List<(int, int)>(); var absorb = new List<(int, int)>(); var diverge = new List<(int, int)>(); var confirm = new List<(int, int)>();
            for (int m = 131; m < n - 1; m++)
            {
                if (!Ok(m)) continue;
                double mean = 0, sq = 0; for (int j = m - 60; j < m; j++) { mean += delta[j]; sq += (double)delta[j] * delta[j]; }
                mean /= 60; double sd = Math.Sqrt(Math.Max(1e-9, sq / 60 - mean * mean)), z = (delta[m] - mean) / sd, u = usualRng[m - 1];
                if (Math.Abs(z) >= 2.5)
                {
                    if (Math.Abs(C[m] - O[m]) <= 0.3 * u) absorb.Add((m, -Math.Sign(z)));             // effort without result -> the other side holds
                    else if (Math.Sign(C[m] - O[m]) == Math.Sign(z)) burst.Add((m, Math.Sign(z)));     // flow and price agree -> does it continue?
                }
                // price makes a new 60-minute extreme: is the CVD of the trading day at a new 60-minute extreme too?
                if ((T[m] + 7200) % 86400 >= 2 * 3600)
                {
                    double cMax = double.MinValue, cMin = double.MaxValue, vMax = double.MinValue, vMin = double.MaxValue;
                    for (int j = m - 60; j < m; j++) { cMax = Math.Max(cMax, C[j]); cMin = Math.Min(cMin, C[j]); vMax = Math.Max(vMax, cvd[j]); vMin = Math.Min(vMin, cvd[j]); }
                    if (C[m] > cMax) (cvd[m] > vMax ? confirm : diverge).Add((m, cvd[m] > vMax ? 1 : -1));
                    else if (C[m] < cMin) (cvd[m] < vMin ? confirm : diverge).Add((m, cvd[m] < vMin ? -1 : 1));
                }
            }
            Rows("delta burst with the price (|z| >= 2.5) -> follow", burst, 1, 5, 15);
            Rows("delta burst but a tiny bar (absorption) -> against the delta", absorb, 5, 15);
            Rows("new 60-min price extreme, CVD confirms -> follow", confirm, 5, 15, 30);
            Rows("new 60-min price extreme, CVD does not -> fade", diverge, 5, 15, 30);
        }

        // ------------------------------------------------------------------ 5) the two patterns worth a closer look (found on 2025-10..2026-03)
        Console.WriteLine("\n-- 5) CLOSER LOOK: break of the Asia range, and the price leaving the 2-sd band around the day VWAP (both: follow) --");
        {
            string[] sesName = { "all sessions", "Asia 22-07 UTC", "London 07-13 UTC", "New York 13-21 UTC" };
            var asia = new List<(int, int)>[4]; var band = new List<(int, int)>[4]; var poke = new List<(int, int)>();
            for (int s = 0; s < 4; s++) { asia[s] = new List<(int, int)>(); band[s] = new List<(int, int)>(); }
            for (int m = 131; m < n - 1; m++)
            {
                if (!Ok(m)) continue;
                double u = usualRng[m - 1]; int hour = Hour(m), ses = hour < 7 || hour >= 22 ? 1 : hour < 13 ? 2 : 3;
                // yesterday's high / low poked and the minute closes back behind it (the "sweep" of section 2), traded WITH the poke
                if (!double.IsNaN(prevHigh[m]))
                {
                    if (MaxH(m - 30, m - 1) <= prevHigh[m] && H[m] > prevHigh[m] + 0.3 * u && C[m] < prevHigh[m]) poke.Add((m, 1));
                    else if (MinL(m - 30, m - 1) >= prevLow[m] && L[m] < prevLow[m] - 0.3 * u && C[m] > prevLow[m]) poke.Add((m, -1));
                }
                if (!double.IsNaN(asiaHigh[m]) && ses >= 2)
                {
                    int side = MaxH(m - 30, m - 1) <= asiaHigh[m] && C[m] > asiaHigh[m] + u ? 1 : MinL(m - 30, m - 1) >= asiaLow[m] && C[m] < asiaLow[m] - u ? -1 : 0;
                    if (side != 0) { asia[ses].Add((m, side)); asia[0].Add((m, side)); }
                }
                if (sdDay[m] > 0 && sdDay[m - 1] > 0 && (T[m] + 7200) % 86400 >= 3 * 3600)
                {
                    double z = (C[m] - vwapDay[m]) / sdDay[m], zPrev = (C[m - 1] - vwapDay[m - 1]) / sdDay[m - 1];
                    if (Math.Abs(z) >= 2 && Math.Abs(zPrev) < 2) { band[ses].Add((m, Math.Sign(z))); band[0].Add((m, Math.Sign(z))); }
                }
            }
            Rows("yesterday's high/low poked, closed back -> WITH the poke", poke, 5, 15, 30, 60);
            Rows("Asia range break -> follow, " + sesName[0], asia[0], 5, 15, 30);
            Rows("  " + sesName[2], asia[2], 5, 15, 30); Rows("  " + sesName[3], asia[3], 5, 15, 30);
            for (int s = 0; s < 4; s++) Rows((s == 0 ? "leaves the 2-sd VWAP band -> follow, " : "  ") + sesName[s], band[s], 15, 30, 60);

            // one trade at a time, the way a cBot would run it
            Console.WriteLine("\none trade at a time (stop " + StopUsd.ToString("0.#", Inv) + "):");
            Replay("PDX yesterday's high/low poked, closed back -> with the poke, hold 15", poke, 15, StopUsd);
            Replay("PDX the same, hold 30", poke, 30, StopUsd);
            Replay("VB  New York: leaves the 2-sd VWAP band -> follow, hold 15", band[3], 15, StopUsd);
            Replay("VB  New York: leaves the 2-sd VWAP band -> follow, hold 30", band[3], 30, StopUsd);
            Replay("VBL London: leaves the 2-sd VWAP band -> FADE, hold 15", band[2].Select(e => (e.Item1, -e.Item2)).ToList(), 15, StopUsd);
            Replay("AB  Asia range break -> follow, hold 15", asia[0], 15, StopUsd);
            Replay("AB  Asia range break -> follow, hold 30", asia[0], 30, StopUsd);
        }

        void Replay(string label, List<(int m, int side)> events, int hold, double stop)
        {
            var st = new St(); double gw = 0, gl = 0, cum = 0, peak = 0, maxDd = 0; long freeAt = 0; int stops = 0;
            var months = new SortedDictionary<string, double>(); var days = new Dictionary<long, double>();
            foreach (var (m, side) in events)
            {
                if (T[m] < freeAt) continue;
                double p = Trade(m, side, hold, stop); if (double.IsNaN(p)) continue;
                freeAt = T[m] + 60 + hold * 60L;
                if (p <= -stop) stops++;
                st.Add(p); if (p > 0) gw += p; else gl -= p; cum += p; peak = Math.Max(peak, cum); maxDd = Math.Max(maxDd, peak - cum);
                string key = DateTimeOffset.FromUnixTimeSeconds(T[m]).UtcDateTime.ToString("yy-MM", Inv);
                months[key] = (months.TryGetValue(key, out double v) ? v : 0) + p;
                days[T[m] / 86400] = (days.TryGetValue(T[m] / 86400, out double dv) ? dv : 0) + p;
            }
            if (st.N < 20) { Console.WriteLine($"{label}: n {st.N} (too few)"); return; }
            Console.WriteLine($"{label}: n {st.N}  win {st.Win:N1}%  avg {st.Mean.ToString("+0.000;-0.000", Inv)}  PF {(gl > 0 ? gw / gl : 0):N2}  net {st.Sum:N0}  t {st.TStat:N2}  max DD {maxDd:N0}  stops {stops}  worst day {days.Values.Min():N1}  months+ {months.Values.Count(v => v > 0)}/{months.Count}");
            Console.WriteLine("     by month: " + string.Join("  ", months.Select(kv => kv.Key + ": " + kv.Value.ToString("+0;-0", Inv))));
        }
        return 0;
    }

    /// <summary>Times of the HIGH / EXTREME releases (events.csv written by tools\news-fetch.ps1), sorted.</summary>
    static long[] LoadFlowNews()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BacktestLab", "data", "news", "events.csv");
        if (!File.Exists(file)) return new long[0];
        return File.ReadAllLines(file).Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= 3 && (p[2] == "HIGH" || p[2] == "EXTREME"))
            .Select(p => long.Parse(p[0], Inv)).OrderBy(t => t).ToArray();
    }
}
