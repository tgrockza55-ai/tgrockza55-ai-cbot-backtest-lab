// HAT: trading the Heikin-Ashi call (user question 06/10/2026): enter one second before the 1-minute bar closes, in the direction the
// next Heikin-Ashi bar is expected to take, and leave as soon as the price has run a set distance.
//
// Run: Phase1.exe hatrade [live-test|live] [from yyyy-MM-dd] [to yyyy-MM-dd] [symbol]
//
// Everything is computed from the recorded ticks (bid and ask), the bars from the bid as cTrader builds them:
//   at second 59 of a minute:  haClose of the running bar is estimated with the price so far, the open of the next HA bar follows from it,
//                              gap = bid - that open. gap > 0 -> buy at the ask, gap < 0 -> sell at the bid (the quote standing at second 59).
//   exit:                      the first tick on which the profit reaches the target (closed at that tick's bid / ask), or the stop (StopUsd),
//                              otherwise the quote standing `hold` minutes after the entry.
// Result per trade in USD for 0.01 lot (1 ounce): real spread included, commission 0.08 per round trip.
// Every signal is followed on its own (positions may overlap when hold > 1). No latency is modelled: live orders took 0.6-1.1 s.
// "gap" and "target" are in usual 1-minute ranges (mean high-low of the last 60 bars), so they scale with the price of gold.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

static partial class Phase1
{
    class HatProbe
    {
        public int Dir, Bucket; public double Entry, Other, Mid, Usual, Gap; public long Time;
        public bool[] SlHit; public double[] SlPnl; public bool[,] TpHit; public double[,] TpPnl; public bool[] Done;
    }

    static int HeikinTrade(string[] args)
    {
        var a = args.Where(x => !x.Contains(":")).ToArray();
        string kind = a.Length > 1 ? a[1] : "live-test", from = a.Length > 2 ? a[2] : "0000-00-00", to = a.Length > 3 ? a[3] : "9999-99-99";
        string symbol = a.Length > 4 ? a[4] : "XAUUSD";
        var stopArg = args.FirstOrDefault(x => x.StartsWith("stop:")); if (stopArg != null) StopUsd = double.Parse(stopArg.Substring(5), Inv);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", kind, symbol);
        if (!Directory.Exists(dir)) { Console.WriteLine("no recording: " + dir); return 1; }
        var files = Directory.GetFiles(dir, "tick-*.csv*").Select(f => (file: f, day: Path.GetFileName(f).Substring(5, 10)))
            .Where(x => string.CompareOrdinal(x.day, from) >= 0 && string.CompareOrdinal(x.day, to) <= 0).OrderBy(x => x.day).ToList();
        if (files.Count == 0) { Console.WriteLine("no tick files in " + dir); return 1; }

        const double Commission = 0.08;
        // ses:<asia|asia-core|london|ny> keeps only the signals of that session (UTC): asia 22-07, asia-core 00-07, london 07-13, ny 13-21
        string ses = args.FirstOrDefault(x => x.StartsWith("ses:"))?.Substring(4) ?? "all";
        bool InSession(long ms) { int h = (int)(ms / 3600000 % 24); return ses == "asia" ? h >= 22 || h < 7 : ses == "asia-core" ? h < 7 : ses == "london" ? h >= 7 && h < 13 : ses == "ny" ? h >= 13 && h < 21 : true; }
        double[] gaps = { 0, 0.2, 0.5, 1.0, 2.0 }, tps = { 0, 0.25, 0.5, 1.0, 2.0 }; int[] holds = { 1, 3 }; double[] sls = { 0.5, 1, 2, 4, 0 };     // stop in usual ranges; the last one (0) = StopUsd flat.           // target 0 = none: leave on time only
        string[] buckets = { "2024", "2025", "2026-01..03", "2026-04..09" };
        long[] bucketEnd = { Unix(2025, 1), Unix(2026, 1), Unix(2026, 4), long.MaxValue };
        int G = gaps.Length, K = tps.Length, Hn = holds.Length, S = sls.Length;
        var st = new double[buckets.Length, G, K, Hn, 6]; var ss = new double[2, G, K, Hn, S, 3];      // ss: dev / test by stop: trades, wins, net.  st[..,5] = net of the opposite trade (no target)                                                                 // trades, wins, net, mid move (no target), mid right
        double spreadSum = 0; long signals = 0;

        long curMin = -1, lastT = 0; double bO = 0, bH = 0, bL = 0, lastBid = 0, lastAsk = 0, hoCur = 0; bool haveHa = false, decided = true;
        var ranges = new Queue<double>(); double rangeSum = 0;
        var probes = new List<HatProbe>();

        void Settle(HatProbe p, int h, bool valid)
        {
            p.Done[h] = true; if (!valid) return;
            double now = p.Dir > 0 ? lastBid - p.Entry : p.Entry - lastAsk, mid = p.Dir * ((lastBid + lastAsk) / 2 - p.Mid);
            for (int g = 0; g < G; g++)
            {
                if (p.Gap < gaps[g]) break;
                for (int k = 0; k < K; k++)
                {
                    for (int s = 0; s < S; s++)
                    {
                        double pnl = (k > 0 && p.TpHit[s, k] ? p.TpPnl[s, k] : p.SlHit[s] ? p.SlPnl[s] : now) - Commission; int part = p.Bucket < 3 ? 0 : 1;
                        ss[part, g, k, h, s, 0]++; if (pnl > 0) ss[part, g, k, h, s, 1]++; ss[part, g, k, h, s, 2] += pnl;
                        if (s < S - 1) continue;
                        st[p.Bucket, g, k, h, 0]++; if (pnl > 0) st[p.Bucket, g, k, h, 1]++; st[p.Bucket, g, k, h, 2] += pnl;
                    }
                    if (k == 0) { st[p.Bucket, g, k, h, 3] += mid; if (mid > 0) st[p.Bucket, g, k, h, 4]++; st[p.Bucket, g, k, h, 5] += (p.Dir > 0 ? p.Other - lastAsk : lastBid - p.Other) - Commission; }
                }
            }
        }

        foreach (var (file, day) in files)
        {
            long dayStart = new DateTimeOffset(DateTime.ParseExact(day, "yyyy-MM-dd", Inv), TimeSpan.Zero).ToUnixTimeSeconds() * 1000;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream s = file.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
            using var r = new StreamReader(s);
            r.ReadLine(); string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 16 || line[12] != ',') continue;
                int c2 = line.IndexOf(',', 13); if (c2 < 0) continue;
                if (!double.TryParse(line.AsSpan(13, c2 - 13), NumberStyles.Float, Inv, out double bid) || !double.TryParse(line.AsSpan(c2 + 1), NumberStyles.Float, Inv, out double ask)) continue;
                long t = dayStart + ((line[0] - '0') * 10 + (line[1] - '0')) * 3600000L + ((line[3] - '0') * 10 + (line[4] - '0')) * 60000L
                       + ((line[6] - '0') * 10 + (line[7] - '0')) * 1000L + (line[9] - '0') * 100 + (line[10] - '0') * 10 + (line[11] - '0');

                // 1) second 59 of the running minute has passed: decide with the quote that stood at that moment
                long decideAt = curMin * 60000 + 59000;
                if (!decided && t >= decideAt)
                {
                    decided = true;
                    if (haveHa && InSession(decideAt) && ranges.Count >= 60 && lastT >= decideAt - 10000 && t - decideAt < 30000)
                    {
                        double usual = rangeSum / ranges.Count, hcEst = (bO + bH + bL + lastBid) / 4, gap = lastBid - (hoCur + hcEst) / 2;
                        if (gap != 0 && usual > 0)
                        {
                            int d = Math.Sign(gap), b = 0; while (decideAt / 1000 >= bucketEnd[b]) b++;
                            probes.Add(new HatProbe { Dir = d, Bucket = b, Entry = d > 0 ? lastAsk : lastBid, Other = d > 0 ? lastBid : lastAsk, Mid = (lastBid + lastAsk) / 2, Usual = usual, Gap = Math.Abs(gap) / usual,
                                Time = decideAt, SlHit = new bool[S], SlPnl = new double[S], TpHit = new bool[S, K], TpPnl = new double[S, K], Done = new bool[Hn] });
                            spreadSum += lastAsk - lastBid; signals++;
                        }
                    }
                }

                // 2) holding time over: leave at the quote standing at that moment (dropped when the market was shut across it)
                for (int i = probes.Count - 1; i >= 0; i--)
                {
                    var p = probes[i];
                    for (int h = 0; h < Hn; h++)
                    {
                        long due = p.Time + holds[h] * 60000L;
                        if (!p.Done[h] && t >= due) Settle(p, h, t - due < 120000);
                    }
                    if (p.Done[Hn - 1]) probes.RemoveAt(i);
                }

                // 3) this tick against the stop and the targets
                foreach (var p in probes)
                {
                    double pnl = p.Dir > 0 ? bid - p.Entry : p.Entry - ask;
                    for (int j = 0; j < S; j++)
                    {
                        if (!p.SlHit[j] && pnl <= -(sls[j] > 0 ? sls[j] * p.Usual : StopUsd)) { p.SlHit[j] = true; p.SlPnl[j] = pnl; }
                        if (!p.SlHit[j]) for (int k = 1; k < K; k++) if (!p.TpHit[j, k] && pnl >= tps[k] * p.Usual) { p.TpHit[j, k] = true; p.TpPnl[j, k] = pnl; }
                    }
                }

                // 4) the bid bar
                long minute = t / 60000;
                if (minute != curMin)
                {
                    if (curMin >= 0)
                    {
                        double hc = (bO + bH + bL + lastBid) / 4;
                        hoCur = haveHa ? (hoCur + hc) / 2 : (bO + lastBid) / 2 * 0.5 + hc * 0.5; haveHa = true;
                        ranges.Enqueue(bH - bL); rangeSum += bH - bL; if (ranges.Count > 60) rangeSum -= ranges.Dequeue();
                    }
                    curMin = minute; bO = bH = bL = bid; decided = false;
                }
                else { if (bid > bH) bH = bid; if (bid < bL) bL = bid; }
                lastBid = bid; lastAsk = ask; lastT = t;
            }
        }

        Console.WriteLine($"\n==== HAT: enter at second 59 in the direction called for the next Heikin-Ashi bar ({files[0].day} .. {files[^1].day}, {signals:N0} signals, session: {ses}) ====");
        Console.WriteLine($"USD per trade for 0.01 lot, after the real spread (mean {spreadSum / Math.Max(1, signals):N3} at entry) and {Commission:N2} commission. stop {StopUsd:N0}. dev = 2024-01..2026-03, test = 2026-04..09");
        Console.WriteLine("gap and target in usual 1-minute ranges; target 0 = leave on time only\n");
        Console.WriteLine("hold  gap>=  target |  2024     2025   26-Q1  |  dev trades   win%    avg      total |  test trades   win%    avg     total |  price went the called way (mid, no cost): dev   avg move   opposite trade, net");
        (int g, int k, int h) best = (0, 0, 0); double bestAvg = double.MinValue;
        for (int h = 0; h < Hn; h++)
            for (int g = 0; g < G; g++)
                for (int k = 0; k < K; k++)
                {
                    double Avg(int b) => st[b, g, k, h, 0] > 0 ? st[b, g, k, h, 2] / st[b, g, k, h, 0] : 0;
                    double dn = 0, dw = 0, ds = 0, dm = 0, dr = 0; for (int b = 0; b < 3; b++) { dn += st[b, g, k, h, 0]; dw += st[b, g, k, h, 1]; ds += st[b, g, k, h, 2]; dm += st[b, g, k, h, 3]; dr += st[b, g, k, h, 4]; }
                    double tn = st[3, g, k, h, 0], tw = st[3, g, k, h, 1], ts = st[3, g, k, h, 2];
                    if (dn >= 1000 && ds / dn > bestAvg) { bestAvg = ds / dn; best = (g, k, h); }
                    string real = k == 0 ? $"{100 * dr / Math.Max(1, dn),38:N2}%   {dm / Math.Max(1, dn),8:+0.000;-0.000}   {(st[0, g, k, h, 5] + st[1, g, k, h, 5] + st[2, g, k, h, 5]) / Math.Max(1, dn),12:+0.000;-0.000}" : "";
                    Console.WriteLine($"{holds[h],3}m  {gaps[g],4:0.0}  {tps[k],6:0.00} | {Avg(0),6:+0.000;-0.000}  {Avg(1),6:+0.000;-0.000}  {Avg(2),6:+0.000;-0.000} | {dn,10:N0}  {100 * dw / Math.Max(1, dn),5:N1}  {ds / Math.Max(1, dn),6:+0.000;-0.000}  {ds,10:N0} | {tn,11:N0}  {100 * tw / Math.Max(1, tn),5:N1}  {ts / Math.Max(1, tn),6:+0.000;-0.000}  {ts,9:N0} |{real}");
                }
        Console.WriteLine($"\nbest on dev: hold {holds[best.h]}m, gap >= {gaps[best.g]:0.0}, target {tps[best.k]:0.00} -> {bestAvg:+0.000;-0.000} USD per trade on dev");

        Console.WriteLine("\n---- the stop: USD per trade (win%) by stop distance, in usual 1-minute ranges; the last column is the flat stop in USD. dev | test");
        Console.WriteLine("hold  gap>=  target |" + string.Concat(sls.Select(x => x > 0 ? $"      stop {x:0.0}      " : $"    stop {StopUsd:0} USD    ")) + "| test:" + string.Concat(sls.Select(x => x > 0 ? $"  {x:0.0}   " : "  flat  ")));
        for (int h = 0; h < Hn; h++)
            foreach (int g in new[] { 0, 2, 3 })
                foreach (int k in new[] { 0, 1, 3 })
                    Console.WriteLine($"{holds[h],3}m  {gaps[g],4:0.0}  {tps[k],6:0.00} |" +
                        string.Concat(Enumerable.Range(0, S).Select(s => $"  {ss[0, g, k, h, s, 2] / Math.Max(1, ss[0, g, k, h, s, 0]):+0.000;-0.000} ({100 * ss[0, g, k, h, s, 1] / Math.Max(1, ss[0, g, k, h, s, 0]),4:N1}%)  ")) + "|      " +
                        string.Concat(Enumerable.Range(0, S).Select(s => $"{ss[1, g, k, h, s, 2] / Math.Max(1, ss[1, g, k, h, s, 0]):+0.000;-0.000}  ")));
        return 0;
    }

    static long Unix(int year, int month) => new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
}
