// Phase 1B: study of the market recorded live by the cBot (cbot\BacktestLab\LiveRecorder.cs) on the Demo account.
// Reads %LOCALAPPDATA%\BacktestLab\live\<symbol>\min-*.csv and trades.csv (the per-minute summary already carries tick activity,
// real spread, depth of market, nearby news, the model's prediction and the signal).
//
// Run: Phase1.exe live [symbol]        (live-test = the folder written by tools\backtest.ps1 -Record)
//
// Questions it answers, minute by minute:
//   - when the market is busy (many ticks) or thin, how far and which way does the price go next?
//   - does the order book (bid volume vs ask volume) lean the way the price then goes?
//   - what does the spread cost at each hour?
//   - what happens after a scheduled release?
//   - does the live prediction match what the backtest promised, and what do real fills cost (slippage, latency)?
// With a few days of data every table here is an observation, not evidence: the journal rule (>= 30 cases per cell) still applies.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static partial class Phase1
{
    class LiveMin
    {
        public long T; public double O, H, L, C, Spread, SpreadMax, BidVol, AskVol, Imb, PUp = double.NaN, MinSinceNews = double.NaN;
        public int Ticks, Books, DepthUpdates; public string LastNews = "", LastImpact = "", Signal = "";
    }

    static int LiveStudy(string symbol, bool test)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", test ? "live-test" : "live", symbol);
        if (!Directory.Exists(dir)) { Console.WriteLine("no recording yet: " + dir + "\n(start the BacktestLab cBot on the Demo account with 'Record market' on)"); return 1; }

        var rows = new List<LiveMin>();
        foreach (var file in Directory.GetFiles(dir, "min-*.csv").OrderBy(f => f))
            foreach (var line in ReadShared(file).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length < 24) continue;
                double D(int k) => p[k].Length == 0 ? double.NaN : double.Parse(p[k], Inv);
                rows.Add(new LiveMin
                {
                    T = new DateTimeOffset(DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm", Inv), TimeSpan.Zero).ToUnixTimeSeconds(),
                    O = D(1), H = D(2), L = D(3), C = D(4), Ticks = (int)D(6), Spread = D(7), SpreadMax = D(8),
                    Books = (int)D(9), BidVol = D(10), AskVol = D(11), Imb = D(12), DepthUpdates = (int)D(13),
                    LastNews = p[17], LastImpact = p[18], MinSinceNews = D(19), PUp = D(20), Signal = p[21],
                });
            }
        rows = rows.GroupBy(r => r.T).Select(g => g.Last()).OrderBy(r => r.T).ToList();        // a restart can write a minute twice
        if (rows.Count < 30) { Console.WriteLine($"only {rows.Count} recorded minutes in {dir}: come back after the market has been open for a while"); return 1; }

        var at = new Dictionary<long, int>(); for (int i = 0; i < rows.Count; i++) at[rows[i].T] = i;
        double Move(int i, int h) => at.TryGetValue(rows[i].T + h * 60L, out int j) ? rows[j].C - rows[i].C : double.NaN;
        int withBook = rows.Count(r => r.Books > 0);
        string When(long u) => DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime.ToString("yyyy-MM-dd HH:mm", Inv);

        Console.WriteLine($"\n==== live study {symbol}: {rows.Count:N0} minutes, {When(rows[0].T)} .. {When(rows[rows.Count - 1].T)} UTC ====");
        Console.WriteLine($"minutes with depth of market: {withBook:N0} ({100.0 * withBook / rows.Count:N0}%)   average spread {rows.Where(r => !double.IsNaN(r.Spread)).Select(r => r.Spread).DefaultIfEmpty(double.NaN).Average():N3}   ticks per minute (median) {Median(rows.Select(r => (double)r.Ticks)):N0}");

        // 1) activity -> what the price does next
        Console.WriteLine("\n-- 1) tick activity of a minute -> the next minutes (USD per oz) --");
        Console.WriteLine("ticks/min           n   range of the minute   |move| next 1m   |move| next 5m   next 5m continues this minute");
        var cuts = new[] { 0.25, 0.5, 0.75 }.Select(q => Quantile(rows.Select(r => (double)r.Ticks), q)).ToArray();
        for (int b = 0; b < 4; b++)
        {
            double lo = b == 0 ? double.MinValue : cuts[b - 1], hi = b == 3 ? double.MaxValue : cuts[b];
            var idx = Enumerable.Range(0, rows.Count).Where(i => rows[i].Ticks > lo && rows[i].Ticks <= hi).ToList();
            var cont = idx.Where(i => !double.IsNaN(Move(i, 5)) && rows[i].C != rows[i].O && Move(i, 5) != 0).ToList();
            Console.WriteLine($"{(b == 0 ? "<= " + cuts[0].ToString("0", Inv) : b == 3 ? "> " + cuts[2].ToString("0", Inv) : cuts[b - 1].ToString("0", Inv) + "-" + cuts[b].ToString("0", Inv)),-14} {idx.Count,6}   {Avg(idx.Select(i => rows[i].H - rows[i].L)),19:N2}   {Avg(idx.Select(i => Math.Abs(Move(i, 1)))),14:N2}   {Avg(idx.Select(i => Math.Abs(Move(i, 5)))),14:N2}   {(cont.Count > 0 ? 100.0 * cont.Count(i => Math.Sign(Move(i, 5)) == Math.Sign(rows[i].C - rows[i].O)) / cont.Count : double.NaN),8:N1}%  (n {cont.Count})");
        }

        // 2) order book imbalance -> direction
        Console.WriteLine("\n-- 2) order book: (bid volume - ask volume) / total, average of the minute -> where the price goes --");
        if (withBook < 30) Console.WriteLine("not enough minutes with depth of market yet");
        else
        {
            Console.WriteLine("imbalance                 n   avg move next 1m   avg move next 5m   up after 5m");
            var book = Enumerable.Range(0, rows.Count).Where(i => rows[i].Books > 0 && !double.IsNaN(rows[i].Imb)).ToList();
            double q1 = Quantile(book.Select(i => rows[i].Imb), 1 / 3.0), q2 = Quantile(book.Select(i => rows[i].Imb), 2 / 3.0);
            foreach (var (label, pick) in new (string, Func<double, bool>)[] { ($"more asks (<= {q1:N2})", v => v <= q1), ("balanced", v => v > q1 && v <= q2), ($"more bids (> {q2:N2})", v => v > q2) })
            {
                var idx = book.Where(i => pick(rows[i].Imb)).ToList();
                var m5 = idx.Select(i => Move(i, 5)).Where(v => !double.IsNaN(v) && v != 0).ToList();
                Console.WriteLine($"{label,-22} {idx.Count,5}   {Avg(idx.Select(i => Move(i, 1))),16:+0.000;-0.000}   {Avg(idx.Select(i => Move(i, 5))),16:+0.000;-0.000}   {(m5.Count > 0 ? 100.0 * m5.Count(v => v > 0) / m5.Count : double.NaN),8:N1}%");
            }
            Console.WriteLine($"book volume per side (average): bids {Avg(book.Select(i => rows[i].BidVol)):N0}, asks {Avg(book.Select(i => rows[i].AskVol)):N0} units   depth updates per minute {Avg(book.Select(i => (double)rows[i].DepthUpdates)):N0}");
        }

        // 3) spread and speed by hour
        Console.WriteLine("\n-- 3) by hour (UTC): spread, activity, size of a 5-minute move --");
        Console.WriteLine("hour      n   spread avg   spread max   ticks/min   |move| 5m");
        foreach (var g in Enumerable.Range(0, rows.Count).GroupBy(i => (int)(rows[i].T % 86400 / 3600)).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key,4}  {g.Count(),5}   {Avg(g.Select(i => rows[i].Spread)),10:N3}   {g.Select(i => rows[i].SpreadMax).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max(),10:N2}   {Avg(g.Select(i => (double)rows[i].Ticks)),9:N0}   {Avg(g.Select(i => Math.Abs(Move(i, 5)))),9:N2}");

        // 4) scheduled releases seen while recording
        Console.WriteLine("\n-- 4) scheduled releases while recording: the move after the release --");
        // the bar that opens at the release time is the first one whose "minutes since the last release" is 1; the bar before it closed at the release
        var releases = Enumerable.Range(1, rows.Count - 1).Where(i => rows[i].LastNews.Length > 0 && rows[i].MinSinceNews > 0 && rows[i].MinSinceNews <= 1
                                                                 && rows[i].T - rows[i - 1].T == 60).ToList();
        if (releases.Count == 0) Console.WriteLine("none yet");
        foreach (var i in releases)
            Console.WriteLine($"{When(rows[i].T)}  {rows[i].LastImpact,-8} {rows[i].LastNews,-32}  move 1m {Move(i - 1, 1),7:+0.00;-0.00}  5m {Move(i - 1, 5),7:+0.00;-0.00}  15m {Move(i - 1, 15),7:+0.00;-0.00}  30m {Move(i - 1, 30),7:+0.00;-0.00}   spread max in the first minute {rows[i].SpreadMax:N2}   ticks {rows[i].Ticks}");

        // 5) the model, live
        Console.WriteLine("\n-- 5) live prediction (5 minutes ahead) against what happened --");
        var pred = Enumerable.Range(0, rows.Count).Where(i => !double.IsNaN(rows[i].PUp) && !double.IsNaN(Move(i, 5)) && Move(i, 5) != 0).ToList();
        if (pred.Count < 30) Console.WriteLine("not enough predictions yet (the model needs about a day of bars after the cBot starts)");
        else
            foreach (var th in new[] { 0.5, 0.54, 0.56, 0.58 })
            {
                var idx = pred.Where(i => Math.Max(rows[i].PUp, 1 - rows[i].PUp) >= th).ToList();
                if (idx.Count == 0) continue;
                var dirMove = idx.Select(i => (rows[i].PUp >= 0.5 ? 1 : -1) * Move(i, 5)).ToList();
                Console.WriteLine($"confidence >= {th * 100:0}%: n {idx.Count,6}   right {100.0 * dirMove.Count(v => v > 0) / idx.Count,5:N1}%   avg move in the predicted direction {dirMove.Average(),7:+0.000;-0.000}   (cost per trade is about 0.20)");
            }
        Console.WriteLine($"signals of the rule: {rows.Count(r => r.Signal.Length > 0)}");

        // 6) real orders
        Console.WriteLine("\n-- 6) orders on the Demo account --");
        var tradeFile = Path.Combine(dir, "trades.csv");
        if (!File.Exists(tradeFile)) { Console.WriteLine("no orders yet"); return 0; }
        var trades = ReadShared(tradeFile).Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= 18).ToList();
        double Val(string s) => s.Length == 0 ? double.NaN : double.Parse(s, Inv);
        var opens = trades.Where(p => p[1] == "OPEN").ToList(); var closes = trades.Where(p => p[1] == "CLOSE").ToList();
        Console.WriteLine($"opened {opens.Count}, closed {closes.Count}, failed {trades.Count(p => p[1] == "FAILED")}");
        if (opens.Count > 0)
            Console.WriteLine($"entry: slippage avg {Avg(opens.Select(p => Val(p[7]))):+0.000;-0.000} (worst {opens.Select(p => Val(p[7])).Max():+0.00;-0.00}) USD per oz, + = worse than the price seen   order latency avg {Avg(opens.Select(p => Val(p[8]))):N0} ms   spread at entry avg {Avg(opens.Select(p => Val(p[9]))):N3}");
        if (closes.Count > 0)
        {
            var net = closes.Select(p => Val(p[16])).ToList();
            double gw = net.Where(v => v > 0).Sum(), gl = -net.Where(v => v < 0).Sum();
            Console.WriteLine($"exit: slippage avg {Avg(closes.Select(p => Val(p[7]))):+0.000;-0.000}   by reason: {string.Join(", ", closes.GroupBy(p => p[12]).Select(g => g.Key + " " + g.Count()))}");
            Console.WriteLine($"result: net {net.Sum():+0.00;-0.00} USD   win {100.0 * net.Count(v => v > 0) / net.Count:N1}%   PF {(gl > 0 ? gw / gl : double.NaN):N2}   commissions {closes.Sum(p => Val(p[14])):N2}");
        }
        return 0;
    }

    /// <summary>The cBot keeps these files open for writing: read them without locking it out.</summary>
    static List<string> ReadShared(string file)
    {
        var lines = new List<string>();
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(fs)) { string l; while ((l = r.ReadLine()) != null) lines.Add(l); }
        return lines;
    }

    static double Avg(IEnumerable<double> v) { var a = v.Where(x => !double.IsNaN(x)).ToList(); return a.Count > 0 ? a.Average() : double.NaN; }
    static double Median(IEnumerable<double> v) => Quantile(v, 0.5);
    static double Quantile(IEnumerable<double> v, double q)
    {
        var a = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[Math.Min(a.Length - 1, (int)(q * a.Length))];
    }
}
