// BURST: momentum burst / velocity scalping (user prompt 06/10/2026: "not moving fast = no entry; does speed carry an edge?").
//
// Run: Phase1.exe burst [live-test|live] [from yyyy-MM-dd] [to yyyy-MM-dd] [symbol]
//
// Data: the recorded ticks (time to the millisecond, bid, ask). The feed gives at most ~9 quotes a second, so windows below one second
// hold one or two quotes and are not measured; windows: 1, 2, 5, 10 seconds. One sample per second (the first quote of the second).
//
// Speed, causal:  move = mid(now) - mid(now - window);  speed ratio = |move| / its own running mean (exponential, ~10 minutes, updated
// only AFTER the sample is used). Nothing is normalised with data from the future; thresholds are fixed numbers, not percentiles of the file.
//   accelerating   = the later half of the window moved further than the earlier half, the same way
//   tick frequency = quotes in the window against their running mean (>= 1.5)
//   tick imbalance = (upticks - downticks) / (upticks + downticks) of the mid in the window, in the direction of the move (>= 0.6)
//
// Part 1 (every sample): speed ratio -> what the mid price does next, in the direction of the move, 1..60 seconds on (no cost), and the
//   same traded at once with the real bid / ask and 0.08 commission.
// Part 2 (burst onsets = the first second the ratio is above the threshold, spread <= 0.35, 10 s apart): enter 0 / 0.5 / 1 s after the
//   signal (live orders took 0.6-1.1 s) at the ask / bid, leave on time, on momentum decay (the move over the window turns against the
//   trade), on a target / stop, or decay with a safety stop. USD per trade for 0.01 lot. dev = ..2026-03, test = 2026-04..

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

static partial class Phase1
{
    static int BurstStudy(string[] args)
    {
        var a = args.Where(x => !x.Contains(":")).ToArray();
        string kind = a.Length > 1 ? a[1] : "live-test", from = a.Length > 2 ? a[2] : "0000-00-00", to = a.Length > 3 ? a[3] : "9999-99-99";
        string symbol = a.Length > 4 ? a[4] : "XAUUSD";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", kind, symbol);
        if (!Directory.Exists(dir)) { Console.WriteLine("no recording: " + dir); return 1; }
        var files = Directory.GetFiles(dir, "tick-*.csv*").Select(f => (file: f, day: Path.GetFileName(f).Substring(5, 10)))
            .Where(x => string.CompareOrdinal(x.day, from) >= 0 && string.CompareOrdinal(x.day, to) <= 0).OrderBy(x => x.day).ToList();
        if (files.Count == 0) { Console.WriteLine("no tick files in " + dir); return 1; }
        var news = LoadFlowNews();

        const double Commission = 0.08, MaxSpread = 0.35, Alpha = 1.0 / 600;
        int[] win = { 1000, 2000, 5000, 10000 };                                  // ms
        int[] back = { 1000, 2000, 5000, 10000, 500, 2500, 60000 }; int[] halfOf = { 4, 0, 5, 2 };
        int[] hor = { 1000, 2000, 5000, 10000, 30000, 60000 };
        double[] edges = { 1, 2, 3, 5, 8 }; string[] bucketName = { "< 1", "1-2", "2-3", "3-5", "5-8", ">= 8" };
        int[] cfgWin = { 1, 2, 3 }; double[] cfgTh = { 3, 5, 8 };                  // part 2: windows 2 / 5 / 10 s x thresholds
        string[] models = { "speed only", "speed + accelerating", "speed + tick frequency", "speed + tick imbalance", "all four" };
        int[] delays = { 0, 500, 1000 };
        int[] fixedExit = { 2000, 5000, 10000, 30000, 60000, 300000 };
        string[] exitName = { "2s", "5s", "10s", "30s", "60s", "300s", "decay", "TP1/SL.5", "decay+SL1" };
        int W = win.Length, B = bucketName.Length, Hn = hor.Length, CF = cfgWin.Length * cfgTh.Length, M = models.Length, D = delays.Length, E = exitName.Length;

        var p1 = new double[W, B, Hn, 4];                                          // n, gross sum, continued, net sum
        var p1n = new long[W, B];
        var p2 = new double[2, CF, M, D, E, 2, 4];                                 // [follow / fade][...][dev/test]: n, net, wins, gross (mid from the signal)
        var ses = new double[2, CF, 5, 2, 2];                                         // asia, london, new york, (unused), near news x exit (10s, decay): n, net
        var yr = new double[2, CF, 4, E, 2];                                            // model 0, delay 0.5 s, decay: n, net per period
        var exc = new double[CF, 3];                                               // n, MFE, MAE over 60 s (delay 0.5 s)
        long[] yrEnd = { Unix(2025, 1), Unix(2026, 1), Unix(2026, 4), long.MaxValue };
        var ema = new double[W]; var emaFreq = new double[W]; double vol60 = 0, spreadSum = 0; long samples = 0, warm = 0, totalTicks = 0;

        var tm = new List<long>(200000); var bid = new List<double>(200000); var ask = new List<double>(200000); var cu = new List<int>(200000); var cd = new List<int>(200000);
        foreach (var (file, day) in files)
        {
            tm.Clear(); bid.Clear(); ask.Clear(); cu.Clear(); cd.Clear();
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using Stream stream = file.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
                using var r = new StreamReader(stream);
                r.ReadLine(); string line; int up = 0, dn = 0; double lastMid = double.NaN;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length < 16 || line[12] != ',') continue;
                    int c2 = line.IndexOf(',', 13); if (c2 < 0) continue;
                    if (!double.TryParse(line.AsSpan(13, c2 - 13), NumberStyles.Float, Inv, out double b) || !double.TryParse(line.AsSpan(c2 + 1), NumberStyles.Float, Inv, out double k)) continue;
                    long t = ((line[0] - '0') * 10 + (line[1] - '0')) * 3600000L + ((line[3] - '0') * 10 + (line[4] - '0')) * 60000L
                           + ((line[6] - '0') * 10 + (line[7] - '0')) * 1000L + (line[9] - '0') * 100 + (line[10] - '0') * 10 + (line[11] - '0');
                    if (tm.Count > 0 && t < tm[^1]) continue;
                    double m = (b + k) / 2; if (m > lastMid) up++; else if (m < lastMid) dn++; lastMid = m;
                    tm.Add(t); bid.Add(b); ask.Add(k); cu.Add(up); cd.Add(dn);
                }
            }
            int n = tm.Count; if (n < 1000) continue; totalTicks += n;
            long dayUnix = new DateTimeOffset(DateTime.ParseExact(day, "yyyy-MM-dd", Inv), TimeSpan.Zero).ToUnixTimeSeconds();
            int part = string.CompareOrdinal(day, "2026-04-01") >= 0 ? 1 : 0, period = 0; while (dayUnix >= yrEnd[period]) period++;
            double Mid(int i) => (bid[i] + ask[i]) / 2;

            var bp = new int[back.Length]; for (int o = 0; o < bp.Length; o++) bp[o] = -1;
            var fp = new int[Hn];
            var prevIn = new bool[CF]; var coolUntil = new long[CF];
            long nextSample = 0;
            for (int i = 0; i < n; i++)
            {
                long t = tm[i]; if (t < nextSample) continue;
                nextSample = t - t % 1000 + 1000;
                for (int o = 0; o < back.Length; o++) while (bp[o] + 1 < n && tm[bp[o] + 1] <= t - back[o]) bp[o]++;
                bool Has(int o) => bp[o] >= 0 && t - tm[bp[o]] <= back[o] + 3000;
                double mid = Mid(i), spread = ask[i] - bid[i];
                if (Has(6)) vol60 += Alpha * (Math.Abs(mid - Mid(bp[6])) - vol60);
                samples++;

                var move = new double[W]; var ratio = new double[W]; var ok = new bool[W];
                for (int w = 0; w < W; w++)
                {
                    if (!Has(w)) continue;
                    move[w] = mid - Mid(bp[w]); double am = Math.Abs(move[w]);
                    ok[w] = warm > 1800 && ema[w] > 0; ratio[w] = ok[w] ? am / ema[w] : 0;
                    ema[w] += Alpha * (am - ema[w]); emaFreq[w] += Alpha * (i - bp[w] - emaFreq[w]);               // after the ratio: causal
                }
                warm++;

                // ---- part 1: speed -> what comes next
                bool future = tm[n - 1] >= t + 60000;
                if (future)
                {
                    for (int h = 0; h < Hn; h++) { if (fp[h] < i) fp[h] = i; while (fp[h] + 1 < n && tm[fp[h] + 1] <= t + hor[h]) fp[h]++; }
                    for (int w = 0; w < W; w++)
                    {
                        if (!ok[w] || move[w] == 0) continue;
                        int b = 0; while (b < edges.Length && ratio[w] >= edges[b]) b++;
                        int d = Math.Sign(move[w]); p1n[w, b]++; if (w == 2) spreadSum += spread;
                        for (int h = 0; h < Hn; h++)
                        {
                            int f = fp[h]; double g = d * (Mid(f) - mid);
                            p1[w, b, h, 0]++; p1[w, b, h, 1] += g; if (g > 0) p1[w, b, h, 2]++;
                            p1[w, b, h, 3] += (d > 0 ? bid[f] - ask[i] : bid[i] - ask[f]) - Commission;
                        }
                    }
                }

                // ---- part 2: burst onsets
                if (tm[n - 1] < t + 302000) continue;
                for (int c = 0; c < CF; c++)
                {
                    int w = cfgWin[c / cfgTh.Length]; double th = cfgTh[c % cfgTh.Length];
                    bool inCond = ok[w] && move[w] != 0 && ratio[w] >= th && spread <= MaxSpread;
                    bool onset = inCond && !prevIn[c] && t >= coolUntil[c]; prevIn[c] = inCond;
                    if (!onset) continue;
                    coolUntil[c] = t + 10000;
                    int d = Math.Sign(move[w]), h2 = halfOf[w];
                    bool acc = false, frq = emaFreq[w] > 0 && (i - bp[w]) >= 1.5 * emaFreq[w], imb = false;
                    if (Has(h2)) { double late = d * (mid - Mid(bp[h2])), early = d * (Mid(bp[h2]) - Mid(bp[w])); acc = late > 0 && late > early; }
                    { int u = cu[i] - cu[bp[w]], dn = cd[i] - cd[bp[w]]; imb = u + dn > 0 && d * (u - dn) >= 0.6 * (u + dn); }
                    bool[] inModel = { true, acc, frq, imb, acc && frq && imb };
                    long hourUtc = t / 3600000; int sc = hourUtc >= 22 || hourUtc < 7 ? 0 : hourUtc < 13 ? 1 : 2;
                    long unix = dayUnix + t / 1000; int ni = Array.BinarySearch(news, unix); if (ni < 0) ni = ~ni;
                    bool nearNews = (ni < news.Length && news[ni] - unix <= 300) || (ni > 0 && unix - news[ni - 1] <= 300);
                    double v = Math.Max(vol60, 0.05);

                    int burstDir = d;
                    for (int side = 0; side < 2; side++)
                    for (int di = 0; di < D; di++)
                    {
                        d = side == 0 ? burstDir : -burstDir;                                   // side 1 = fade: the same moments traded against the burst
                        int e = i; while (e < n && tm[e] < t + delays[di]) e++;
                        if (e >= n) continue;
                        long t0 = tm[e]; double entry = d > 0 ? ask[e] : bid[e];
                        var res = new double[E]; var done = new bool[E]; int open = E, q = bp[w]; double mfe = 0, mae = 0; bool excDone = false;
                        for (int j = e + 1; j < n && open > 0; j++)
                        {
                            long dt = tm[j] - t0;
                            double prev = d > 0 ? bid[j - 1] - entry : entry - ask[j - 1], now = d > 0 ? bid[j] - entry : entry - ask[j];
                            for (int x = 0; x < fixedExit.Length; x++) if (!done[x] && dt > fixedExit[x]) { done[x] = true; res[x] = prev; open--; }
                            if (dt > 300000) { for (int x = 6; x < E; x++) if (!done[x]) { done[x] = true; res[x] = prev; open--; } break; }
                            if (!excDone) { if (dt > 60000) excDone = true; else { if (now > mfe) mfe = now; if (now < mae) mae = now; } }
                            while (q + 1 < n && tm[q + 1] <= tm[j] - win[w]) q++;
                            bool decayed = burstDir * (Mid(j) - Mid(q)) <= 0;                      // the burst is over: the move across the window has turned
                            if (!done[6] && decayed) { done[6] = true; res[6] = now; open--; }
                            if (!done[7]) { if (now <= -0.5 * v) { done[7] = true; res[7] = now; open--; } else if (now >= v) { done[7] = true; res[7] = v; open--; } }
                            if (!done[8] && (decayed || now <= -v)) { done[8] = true; res[8] = now; open--; }
                        }
                        if (open > 0) continue;
                        for (int m = 0; m < M; m++)
                        {
                            if (!inModel[m]) continue;
                            for (int x = 0; x < E; x++)
                            {
                                double net = res[x] - Commission;
                                p2[side, c, m, di, x, part, 0]++; p2[side, c, m, di, x, part, 1] += net; if (net > 0) p2[side, c, m, di, x, part, 2]++;
                                p2[side, c, m, di, x, part, 3] += res[x] + (d > 0 ? ask[e] - mid : mid - bid[e]);          // what the mid did from the signal: cost and latency taken out
                            }
                        }
                        if (di == 1)
                        {
                            if (side == 0) { exc[c, 0]++; exc[c, 1] += mfe; exc[c, 2] += mae; }
                            for (int x = 0; x < E; x++) { yr[side, c, period, x, 0]++; yr[side, c, period, x, 1] += res[x] - Commission; }
                            for (int x = 0; x < 2; x++)
                            {
                                double net = res[x == 0 ? 2 : 4] - Commission;
                                ses[side, c, sc, x, 0]++; ses[side, c, sc, x, 1] += net; if (nearNews) { ses[side, c, 4, x, 0]++; ses[side, c, 4, x, 1] += net; }
                            }
                        }
                    }
                }
            }
        }

        string F(double v) => v.ToString("+0.000;-0.000", Inv);
        Console.WriteLine($"\n==== BURST: speed of the price and what follows ({files[0].day} .. {files[^1].day}, {totalTicks:N0} ticks, {samples:N0} one-second samples) ====");
        Console.WriteLine($"mean spread at a sample {spreadSum / Math.Max(1, Enumerable.Range(0, B).Sum(b => p1n[2, b])):N3}; commission {Commission:N2} per round trip; a trade must make spread + commission (about 0.25) to break even");

        Console.WriteLine("\n-- 1) speed ratio (move over the window / its usual size) -> mid price over the next seconds, in the direction of the move. USD, no cost");
        Console.WriteLine("window  ratio   share of time |" + string.Concat(hor.Select(h => $"  next {h / 1000,2}s ")) + "| went on (10s) | traded at once, net of spread + commission:" + string.Concat(hor.Select(h => $" {h / 1000,3}s  ")));
        for (int w = 0; w < W; w++)
        {
            long all = Enumerable.Range(0, B).Sum(b => p1n[w, b]);
            for (int b = 0; b < B; b++)
            {
                double N(int h) => Math.Max(1, p1[w, b, h, 0]);
                Console.WriteLine($"{win[w] / 1000,4}s   {bucketName[b],-5}  {100.0 * p1n[w, b] / Math.Max(1, all),10:N2}%   |" + string.Concat(Enumerable.Range(0, Hn).Select(h => $"   {F(p1[w, b, h, 1] / N(h))} ")) +
                    $"| {100 * p1[w, b, 3, 2] / N(3),10:N1}%   |                                            " + string.Concat(Enumerable.Range(0, Hn).Select(h => $" {F(p1[w, b, h, 3] / N(h))}")));
            }
        }

        string Cfg(int c) => $"{win[cfgWin[c / cfgTh.Length]] / 1000,2}s >= {cfgTh[c % cfgTh.Length]:0}";
        string[] sideName = { "FOLLOW the burst", "FADE the burst (trade against it)" };
        string[] sesName = { "Asia 22-07", "London 07-13", "New York 13-22", "", "near news" };
        for (int s = 0; s < 2; s++)
        {
            double Avg(int c, int m, int di, int x, int part, int what) => p2[s, c, m, di, x, part, what] / Math.Max(1, p2[s, c, m, di, x, part, 0]);
            Console.WriteLine($"\n================ {sideName[s]} ================");
            Console.WriteLine("-- 2) burst onsets traded (speed only, entry 0.5 s after the signal): net USD per trade by exit. dev | test");
            Console.WriteLine("burst      dev n  test n |" + string.Concat(exitName.Select(x => $" {x,9}")) + " | test:" + string.Concat(exitName.Select(x => $" {x,9}")));
            for (int c = 0; c < CF; c++)
                Console.WriteLine($"{Cfg(c)}  {p2[s, c, 0, 1, 0, 0, 0],8:N0} {p2[s, c, 0, 1, 0, 1, 0],7:N0} |" + string.Concat(Enumerable.Range(0, E).Select(x => $" {F(Avg(c, 0, 1, x, 0, 1)),9}")) + " |      " + string.Concat(Enumerable.Range(0, E).Select(x => $" {F(Avg(c, 0, 1, x, 1, 1)),9}")));
            Console.WriteLine("   the same with the entry half-spread and the latency taken out (the exit half-spread is still in; part 1 has the mid-to-mid numbers), dev; and the share of winning trades after cost:");
            for (int c = 0; c < CF; c++)
                Console.WriteLine($"{Cfg(c)}                   |" + string.Concat(Enumerable.Range(0, E).Select(x => $" {F(Avg(c, 0, 1, x, 0, 3)),9}")) + $" | win% at 10s {100 * Avg(c, 0, 1, 2, 0, 2):N1}, 60s {100 * Avg(c, 0, 1, 4, 0, 2):N1}, decay {100 * Avg(c, 0, 1, 6, 0, 2):N1}");

            Console.WriteLine("\n-- 3) what each ingredient adds (entry 0.5 s). dev: n, before cost / net at 10 s, net at 60 s, net on decay | test: n, net 10 s, 60 s, decay");
            for (int c = 0; c < CF; c++)
                for (int m = 0; m < M; m++)
                    Console.WriteLine($"{Cfg(c)}  {models[m],-24} {p2[s, c, m, 1, 0, 0, 0],8:N0}   {F(Avg(c, m, 1, 2, 0, 3))} / {F(Avg(c, m, 1, 2, 0, 1))}   {F(Avg(c, m, 1, 4, 0, 1))}   {F(Avg(c, m, 1, 6, 0, 1))}  |  {p2[s, c, m, 1, 0, 1, 0],7:N0}  {F(Avg(c, m, 1, 2, 1, 1))}  {F(Avg(c, m, 1, 4, 1, 1))}  {F(Avg(c, m, 1, 6, 1, 1))}");

            Console.WriteLine("\n-- 4) entry delay (speed only): net at 10 s, 60 s and on decay, dev");
            for (int c = 0; c < CF; c++)
                Console.WriteLine($"{Cfg(c)}" + string.Concat(Enumerable.Range(0, D).Select(di => $" | {delays[di],4} ms: {F(Avg(c, 0, di, 2, 0, 1))}  {F(Avg(c, 0, di, 4, 0, 1))}  {F(Avg(c, 0, di, 6, 0, 1))}")));

            Console.WriteLine("\n-- 5) by session and near HIGH news (+-5 min), entry 0.5 s, all data: n, net at 10 s, net at 60 s");
            for (int c = 0; c < CF; c++)
                Console.WriteLine($"{Cfg(c)} | " + string.Join(" | ", new[] { 0, 1, 2, 4 }.Select(k => $"{sesName[k]} {ses[s, c, k, 0, 0],6:N0} {F(ses[s, c, k, 0, 1] / Math.Max(1, ses[s, c, k, 0, 0]))} {F(ses[s, c, k, 1, 1] / Math.Max(1, ses[s, c, k, 1, 0]))}")));

            Console.WriteLine("\n-- 6) by period (speed only, entry 0.5 s): net per trade at 10 s / 60 s / on decay");
            for (int c = 0; c < CF; c++)
                Console.WriteLine($"{Cfg(c)} | " + string.Join(" | ", Enumerable.Range(0, 4).Select(p => new[] { "2024", "2025", "2026-01..03", "2026-04..09" }[p] + $" ({yr[s, c, p, 0, 0]:N0}) " + string.Join(" ", new[] { 2, 4, 6 }.Select(x => F(yr[s, c, p, x, 1] / Math.Max(1, yr[s, c, p, x, 0])))))) + (s == 0 ? $" | within 60 s: best {F(exc[c, 1] / Math.Max(1, exc[c, 0]))} worst {F(exc[c, 2] / Math.Max(1, exc[c, 0]))}" : ""));

            // every combination: how many are positive on dev, and what the best of dev does on test
            int combos = 0, devPos = 0, bothPos = 0; var ranked = new List<(double dev, int c, int m, int di, int x)>();
            for (int c = 0; c < CF; c++) for (int m = 0; m < M; m++) for (int di = 1; di < D; di++) for (int x = 0; x < E; x++)
            {
                if (p2[s, c, m, di, x, 0, 0] < 300) continue;
                combos++; double dv = Avg(c, m, di, x, 0, 1); ranked.Add((dv, c, m, di, x));
                if (dv > 0) { devPos++; if (p2[s, c, m, di, x, 1, 0] > 0 && Avg(c, m, di, x, 1, 1) > 0) bothPos++; }
            }
            Console.WriteLine($"\n-- 7) all {combos} combinations with a realistic delay (0.5 / 1 s) and >= 300 dev trades: positive on dev {devPos}, of those positive on test {bothPos}");
            foreach (var r in ranked.OrderByDescending(r => r.dev).Take(8))
                Console.WriteLine($"   burst {Cfg(r.c)}, {models[r.m],-24} delay {delays[r.di],4} ms, exit {exitName[r.x],-9}: dev {F(r.dev)} ({p2[s, r.c, r.m, r.di, r.x, 0, 0]:N0} trades, win {100 * Avg(r.c, r.m, r.di, r.x, 0, 2):N1}%)  ->  test {F(Avg(r.c, r.m, r.di, r.x, 1, 1))} ({p2[s, r.c, r.m, r.di, r.x, 1, 0]:N0} trades, win {100 * Avg(r.c, r.m, r.di, r.x, 1, 2):N1}%)");
        }
        return 0;
    }
}