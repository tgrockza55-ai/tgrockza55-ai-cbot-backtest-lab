// HASL: the Heikin-Ashi call with stops that follow the volatility (user question 06/10/2026: "make the stop not fixed: ATR or others").
//
// Run: Phase1.exe hasl [live-test|live] [from yyyy-MM-dd] [to yyyy-MM-dd] [symbol]
//
// Same entry as HAT (HeikinTrade.cs), every fifth minute. ATR = mean high-low of the last 14 one-minute bid bars at the entry.
// Exit plans, all distances in ATR:  fixed stop + target  ·  trailing stop (follows the best price reached)  ·  stop moved to the entry price
// once the trade is ahead by a set distance. A stop is filled at the quote that crossed it, a target at the target; whatever is still
// open after 30 minutes is closed at the quote. USD per trade for 0.01 lot, real spread, commission 0.08.
// "opposite" = the same entries taken the other way.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

static partial class Phase1
{
    class SlProbe { public int Dir, Bucket; public double Ask, Bid, Atr; public long Time; public bool[,] Done; public double[,] Best, Res; public int Open; }

    static int HeikinSl(string[] args)
    {
        var a = args.Where(x => !x.Contains(":")).ToArray();
        string kind = a.Length > 1 ? a[1] : "live-test", from = a.Length > 2 ? a[2] : "0000-00-00", to = a.Length > 3 ? a[3] : "9999-99-99";
        string symbol = a.Length > 4 ? a[4] : "XAUUSD";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", kind, symbol);
        if (!Directory.Exists(dir)) { Console.WriteLine("no recording: " + dir); return 1; }
        var files = Directory.GetFiles(dir, "tick-*.csv*").Select(f => (file: f, day: Path.GetFileName(f).Substring(5, 10)))
            .Where(x => string.CompareOrdinal(x.day, from) >= 0 && string.CompareOrdinal(x.day, to) <= 0).OrderBy(x => x.day).ToList();
        if (files.Count == 0) { Console.WriteLine("no tick files in " + dir); return 1; }

        const double Commission = 0.08; const int HoldMinutes = 30, AtrBars = 14;
        // plans: stop, target (0 = none), trailing, move the stop to the entry after this gain (0 = never)
        var plans = new List<(string name, double sl, double tp, bool trail, double be)>();
        foreach (double sl in new[] { 1.0, 2, 3, 5 }) foreach (double tp in new[] { 0.5, 1, 2, 3 }) plans.Add(($"stop {sl:0.0}  target {tp:0.0}", sl, tp, false, 0));
        foreach (double sl in new[] { 1.0, 2, 3, 5 }) plans.Add(($"trailing stop {sl:0.0}", sl, 0, true, 0));
        foreach (double sl in new[] { 2.0, 3 }) foreach (double be in new[] { 0.5, 1 }) plans.Add(($"stop {sl:0.0}  to entry after +{be:0.0}  target 3.0", sl, 3, false, be));
        int J = plans.Count;
        long[] bucketEnd = { Unix(2025, 1), Unix(2026, 1), Unix(2026, 4), long.MaxValue };
        var st = new double[4, 2, J, 3];                                                     // trades, wins, net
        long signals = 0; double atrSum = 0;

        long curMin = -1, lastT = 0; double bO = 0, bH = 0, bL = 0, lastBid = 0, lastAsk = 0, hoCur = 0; bool haveHa = false, decided = true;
        var ranges = new Queue<double>(); double rangeSum = 0;
        var probes = new List<SlProbe>();

        foreach (var (file, day) in files)
        {
            long dayStart = new DateTimeOffset(DateTime.ParseExact(day, "yyyy-MM-dd", Inv), TimeSpan.Zero).ToUnixTimeSeconds() * 1000;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream stream = file.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
            using var r = new StreamReader(stream);
            r.ReadLine(); string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 16 || line[12] != ',') continue;
                int c2 = line.IndexOf(',', 13); if (c2 < 0) continue;
                if (!double.TryParse(line.AsSpan(13, c2 - 13), NumberStyles.Float, Inv, out double bid) || !double.TryParse(line.AsSpan(c2 + 1), NumberStyles.Float, Inv, out double ask)) continue;
                long t = dayStart + ((line[0] - '0') * 10 + (line[1] - '0')) * 3600000L + ((line[3] - '0') * 10 + (line[4] - '0')) * 60000L
                       + ((line[6] - '0') * 10 + (line[7] - '0')) * 1000L + (line[9] - '0') * 100 + (line[10] - '0') * 10 + (line[11] - '0');

                long decideAt = curMin * 60000 + 59000;
                if (!decided && t >= decideAt)
                {
                    decided = true;
                    if (curMin % 5 == 0 && haveHa && ranges.Count >= AtrBars && lastT >= decideAt - 10000 && t - decideAt < 30000)
                    {
                        double atr = rangeSum / ranges.Count, hcEst = (bO + bH + bL + lastBid) / 4, gap = lastBid - (hoCur + hcEst) / 2;
                        if (gap != 0 && atr > 0)
                        {
                            int b = 0; while (decideAt / 1000 >= bucketEnd[b]) b++;
                            probes.Add(new SlProbe { Dir = Math.Sign(gap), Bucket = b, Ask = lastAsk, Bid = lastBid, Atr = atr, Time = decideAt,
                                Done = new bool[2, J], Best = new double[2, J], Res = new double[2, J], Open = 2 * J });
                            signals++; atrSum += atr;
                        }
                    }
                }

                // time is up: close what is still open at the standing quote, then count the trade (dropped when the market was shut across it)
                for (int i = probes.Count - 1; i >= 0; i--)
                {
                    var p = probes[i]; long due = p.Time + HoldMinutes * 60000L;
                    if (t < due) continue;
                    if (t - due < 120000)
                        for (int d = 0; d < 2; d++)
                        {
                            double now = (p.Dir > 0) == (d == 0) ? lastBid - p.Ask : p.Bid - lastAsk;
                            for (int j = 0; j < J; j++)
                            {
                                double pnl = (p.Done[d, j] ? p.Res[d, j] : now) - Commission;
                                st[p.Bucket, d, j, 0]++; if (pnl > 0) st[p.Bucket, d, j, 1]++; st[p.Bucket, d, j, 2] += pnl;
                            }
                        }
                    probes[i] = probes[^1]; probes.RemoveAt(probes.Count - 1);
                }

                foreach (var p in probes)
                {
                    if (p.Open == 0) continue;
                    for (int d = 0; d < 2; d++)
                    {
                        double pnl = (p.Dir > 0) == (d == 0) ? bid - p.Ask : p.Bid - ask;
                        for (int j = 0; j < J; j++)
                        {
                            if (p.Done[d, j]) continue;
                            var pl = plans[j]; double best = p.Best[d, j];
                            double stop = (pl.trail ? best : 0) - pl.sl * p.Atr;
                            if (pl.be > 0 && best >= pl.be * p.Atr) stop = 0;
                            if (pnl <= stop) { p.Done[d, j] = true; p.Res[d, j] = pnl; p.Open--; }
                            else if (pl.tp > 0 && pnl >= pl.tp * p.Atr) { p.Done[d, j] = true; p.Res[d, j] = pl.tp * p.Atr; p.Open--; }
                            else if (pnl > best) p.Best[d, j] = pnl;
                        }
                    }
                }

                long minute = t / 60000;
                if (minute != curMin)
                {
                    if (curMin >= 0)
                    {
                        double hc = (bO + bH + bL + lastBid) / 4;
                        hoCur = haveHa ? (hoCur + hc) / 2 : (bO + lastBid) / 2 * 0.5 + hc * 0.5; haveHa = true;
                        ranges.Enqueue(bH - bL); rangeSum += bH - bL; if (ranges.Count > AtrBars) rangeSum -= ranges.Dequeue();
                    }
                    curMin = minute; bO = bH = bL = bid; decided = false;
                }
                else { if (bid > bH) bH = bid; if (bid < bL) bL = bid; }
                lastBid = bid; lastAsk = ask; lastT = t;
            }
        }

        Console.WriteLine($"\n==== HASL: Heikin-Ashi call, exits measured in ATR({AtrBars}) of 1-minute bars ({files[0].day} .. {files[^1].day}, {signals:N0} signals, every fifth minute) ====");
        Console.WriteLine($"USD per trade for 0.01 lot after the real spread and {Commission:N2} commission; mean ATR at entry {atrSum / Math.Max(1, signals):N2} USD; open trades closed after {HoldMinutes} minutes");
        Console.WriteLine("dev = 2024-01..2026-03, test = 2026-04..09\n");
        Console.WriteLine("exit plan (distances in ATR)                 |  dev trades   win%    avg     2024    2025   26-Q1 |  test win%    avg  |  opposite direction: dev win%    avg   test avg");
        for (int j = 0; j < J; j++)
        {
            double V(int b, int d, int m) => st[b, d, j, m];
            double Dev(int d, int m) => V(0, d, m) + V(1, d, m) + V(2, d, m);
            double Avg(int b) => V(b, 0, 2) / Math.Max(1, V(b, 0, 0));
            Console.WriteLine($"{plans[j].name,-44} | {Dev(0, 0),11:N0}  {100 * Dev(0, 1) / Math.Max(1, Dev(0, 0)),5:N1}  {Dev(0, 2) / Math.Max(1, Dev(0, 0)),6:+0.000;-0.000}  {Avg(0),6:+0.000;-0.000}  {Avg(1),6:+0.000;-0.000}  {Avg(2),6:+0.000;-0.000} |  {100 * V(3, 0, 1) / Math.Max(1, V(3, 0, 0)),8:N1}  {Avg(3),6:+0.000;-0.000} |  {100 * Dev(1, 1) / Math.Max(1, Dev(1, 0)),27:N1}  {Dev(1, 2) / Math.Max(1, Dev(1, 0)),6:+0.000;-0.000}   {V(3, 1, 2) / Math.Max(1, V(3, 1, 0)),6:+0.000;-0.000}");
        }
        return 0;
    }
}
