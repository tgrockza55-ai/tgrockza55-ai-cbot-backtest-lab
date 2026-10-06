// HATP: the Heikin-Ashi call with a short fixed take-profit and a wide stop (user question 06/10/2026: "lock the TP at a distance most
// trades reach, e.g. 0.2 USD, and keep the stop wide").
//
// Run: Phase1.exe hatp [live-test|live] [from yyyy-MM-dd] [to yyyy-MM-dd] [symbol]
//
// Same entry as HAT (HeikinTrade.cs): second 59 of the minute, buy at the ask / sell at the bid in the direction called for the next HA bar.
// Exit: take-profit as a limit order (filled at exactly the target, i.e. the profit is the target), the stop at the quote that crossed it,
// otherwise the quote standing `hold` minutes after the entry. USD per trade for 0.01 lot, commission 0.08 per round trip.
// Every third minute is used as a signal (the trades of neighbouring minutes overlap almost completely). "opposite" = the same entries taken
// the other way: if both directions give the same result, the call adds nothing and the result is the shape of the exit alone.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

static partial class Phase1
{
    class TpProbe
    {
        public int Dir, Bucket; public double Ask, Bid, Gap; public long Time; public int Settled;
        public int[] TpNext = new int[2], SlNext = new int[2];              // per direction (0 = as called, 1 = opposite): first target / stop not reached yet
        public long[,] TpSeq, SlSeq; public double[,] SlPnl;                // tick number of the first touch (0 = never)
    }

    static int HeikinTp(string[] args)
    {
        var a = args.Where(x => !x.Contains(":")).ToArray();
        string kind = a.Length > 1 ? a[1] : "live-test", from = a.Length > 2 ? a[2] : "0000-00-00", to = a.Length > 3 ? a[3] : "9999-99-99";
        string symbol = a.Length > 4 ? a[4] : "XAUUSD";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", kind, symbol);
        if (!Directory.Exists(dir)) { Console.WriteLine("no recording: " + dir); return 1; }
        var files = Directory.GetFiles(dir, "tick-*.csv*").Select(f => (file: f, day: Path.GetFileName(f).Substring(5, 10)))
            .Where(x => string.CompareOrdinal(x.day, from) >= 0 && string.CompareOrdinal(x.day, to) <= 0).OrderBy(x => x.day).ToList();
        if (files.Count == 0) { Console.WriteLine("no tick files in " + dir); return 1; }

        const double Commission = 0.08;
        double[] tps = { 0.1, 0.2, 0.3, 0.5, 1.0 }, sls = { 5, 12, 30 }, gaps = { 0, 0.5 }; int[] holds = { 3, 10, 60 };
        long[] bucketEnd = { Unix(2025, 1), Unix(2026, 1), Unix(2026, 4), long.MaxValue };
        int K = tps.Length, S = sls.Length, Hn = holds.Length, G = gaps.Length;
        var st = new double[4, G, 2, K, S, Hn, 4];                                         // trades, target reached, net, stop hit
        long signals = 0, seq = 0;

        long curMin = -1, lastT = 0; double bO = 0, bH = 0, bL = 0, lastBid = 0, lastAsk = 0, hoCur = 0; bool haveHa = false, decided = true;
        var ranges = new Queue<double>(); double rangeSum = 0;
        var probes = new List<TpProbe>();

        void Settle(TpProbe p, int h, bool valid)
        {
            p.Settled = h + 1; if (!valid) return;
            for (int d = 0; d < 2; d++)
            {
                bool isLong = (p.Dir > 0) == (d == 0);
                double now = isLong ? lastBid - p.Ask : p.Bid - lastAsk;
                for (int g = 0; g < G && p.Gap >= gaps[g]; g++)
                    for (int k = 0; k < K; k++)
                        for (int s = 0; s < S; s++)
                        {
                            long tp = p.TpSeq[d, k], sl = p.SlSeq[d, s];
                            bool won = tp > 0 && (sl == 0 || tp < sl), stopped = !won && sl > 0;
                            double pnl = (won ? tps[k] : stopped ? p.SlPnl[d, s] : now) - Commission;
                            st[p.Bucket, g, d, k, s, h, 0]++; if (won) st[p.Bucket, g, d, k, s, h, 1]++; st[p.Bucket, g, d, k, s, h, 2] += pnl; if (stopped) st[p.Bucket, g, d, k, s, h, 3]++;
                        }
            }
        }

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
                seq++;

                long decideAt = curMin * 60000 + 59000;
                if (!decided && t >= decideAt)
                {
                    decided = true;
                    if (curMin % 3 == 0 && haveHa && ranges.Count >= 60 && lastT >= decideAt - 10000 && t - decideAt < 30000)
                    {
                        double usual = rangeSum / ranges.Count, hcEst = (bO + bH + bL + lastBid) / 4, gap = lastBid - (hoCur + hcEst) / 2;
                        if (gap != 0 && usual > 0)
                        {
                            int b = 0; while (decideAt / 1000 >= bucketEnd[b]) b++;
                            probes.Add(new TpProbe { Dir = Math.Sign(gap), Bucket = b, Ask = lastAsk, Bid = lastBid, Gap = Math.Abs(gap) / usual, Time = decideAt,
                                TpSeq = new long[2, K], SlSeq = new long[2, S], SlPnl = new double[2, S] });
                            signals++;
                        }
                    }
                }

                for (int i = probes.Count - 1; i >= 0; i--)
                {
                    var p = probes[i];
                    while (p.Settled < Hn && t >= p.Time + holds[p.Settled] * 60000L) Settle(p, p.Settled, t - (p.Time + holds[p.Settled] * 60000L) < 120000);
                    if (p.Settled >= Hn) { probes[i] = probes[^1]; probes.RemoveAt(probes.Count - 1); }
                }

                foreach (var p in probes)
                    for (int d = 0; d < 2; d++)
                    {
                        double pnl = (p.Dir > 0) == (d == 0) ? bid - p.Ask : p.Bid - ask;
                        while (p.TpNext[d] < K && pnl >= tps[p.TpNext[d]]) p.TpSeq[d, p.TpNext[d]++] = seq;
                        while (p.SlNext[d] < S && pnl <= -sls[p.SlNext[d]]) { p.SlPnl[d, p.SlNext[d]] = pnl; p.SlSeq[d, p.SlNext[d]++] = seq; }
                    }

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

        Console.WriteLine($"\n==== HATP: Heikin-Ashi call, short fixed take-profit, wide stop ({files[0].day} .. {files[^1].day}, {signals:N0} signals, every third minute) ====");
        Console.WriteLine($"USD per trade for 0.01 lot after the real spread and {Commission:N2} commission; a win pays target - {Commission:N2}. dev = 2024-01..2026-03, test = 2026-04..09");
        foreach (int g in Enumerable.Range(0, G))
        {
            Console.WriteLine($"\n-- signals with gap >= {gaps[g]:0.0} usual ranges");
            Console.WriteLine("hold  stop  target |  dev trades  reached TP  hit stop   avg     2024    2025   26-Q1 |  test: reached TP   avg  |  opposite direction: dev reached TP   avg   test avg");
            for (int h = 0; h < Hn; h++)
                for (int s = 0; s < S; s++)
                    for (int k = 0; k < K; k++)
                    {
                        double V(int b, int d, int m) => st[b, g, d, k, s, h, m];
                        double Dev(int d, int m) => V(0, d, m) + V(1, d, m) + V(2, d, m);
                        double Avg(int b) => V(b, 0, 2) / Math.Max(1, V(b, 0, 0));
                        Console.WriteLine($"{holds[h],3}m  {sls[s],4:0}  {tps[k],6:0.0} | {Dev(0, 0),11:N0}  {100 * Dev(0, 1) / Math.Max(1, Dev(0, 0)),9:N1}%  {100 * Dev(0, 3) / Math.Max(1, Dev(0, 0)),7:N2}%  {Dev(0, 2) / Math.Max(1, Dev(0, 0)),6:+0.000;-0.000}  {Avg(0),6:+0.000;-0.000}  {Avg(1),6:+0.000;-0.000}  {Avg(2),6:+0.000;-0.000} |  {100 * V(3, 0, 1) / Math.Max(1, V(3, 0, 0)),14:N1}%  {Avg(3),6:+0.000;-0.000} |  {100 * Dev(1, 1) / Math.Max(1, Dev(1, 0)),32:N1}%  {Dev(1, 2) / Math.Max(1, Dev(1, 0)),6:+0.000;-0.000}   {V(3, 1, 2) / Math.Max(1, V(3, 1, 0)),6:+0.000;-0.000}");
                    }
        }
        return 0;
    }
}
