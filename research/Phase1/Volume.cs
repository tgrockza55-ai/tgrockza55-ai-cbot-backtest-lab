// H11 (tier 0): tick volume and speed of a one-minute bar -> what the price does over the next minutes.
//
// "Volume" on spot gold is tick volume: how many times the price changed in the minute. It measures activity, not traded size.
// An event is known when bar i closes; the trade is entered at the open of bar i+1 and closed at the open h minutes later.
//   continues = share of events where the price then went the way the event points
//   follow    = trade with the event, fade = against it; net = after the fixed cost per round trip
// Events overlap, so the t-values are too large. A pattern counts only if the sign is the same in every year.
//
// Run: Phase1.exe study:h11 period:dev

using System;
using System.Collections.Generic;
using System.Linq;

static partial class Phase1
{
    // ------------------------------------------------------------------ LX: London exhaustion fade
    // Rule locked 04/10/2026 from period:dev (study h11, section I; the middle of the grid, not its best cell):
    //   London 07:00-13:00 UTC, away from scheduled news. A closed M1 bar whose body is >= 1.5x the average range of the 60 bars before it
    //   and whose tick volume is in the busiest 5% of the last 24 hours -> trade AGAINST that bar at the next open,
    //   hold 5 minutes, protective stop 5 USD, no target, one trade at a time.
    // No model, so nothing depends on a training seed. Run: Phase1.exe study:lx period:dev   (then period:test, once)

    static void ScalpRuleLX()
    {
        int n = T.Length;
        var rng = new double[n]; for (int i = 0; i < n; i++) rng[i] = H[i] - L[i];
        var usualRng = RollMean(rng, 60);
        Console.WriteLine("\n==== LX London exhaustion fade (USD per oz = percent of a 100 USD account at 0.01 lot; replay enters at the next open, stop " + StopUsd.ToString("0.#", Inv) + " filled at its price) ====");
        // LXg = the same with a volatility gate: only when the average range of the last 60 bars is >= 1 USD (about 4-5x the cost), because the
        // reversal grows with volatility while the cost does not. The gate value was fixed before looking at any result.
        foreach (var (gate, cost) in new[] { (0.0, FixedCost), (0.0, 0.30), (1.0, FixedCost), (1.0, 0.30) })
        {
            var st = new St(); double gw = 0, gl = 0, cum = 0, peak = 0, maxDd = 0, worst = 0; long first = 0, last = 0; int free = 0, stops = 0;
            var months = new SortedDictionary<string, St>(); var years = new SortedDictionary<int, St>(); var days = new Dictionary<long, double>();
            for (int i = 1741; i < n - 1; i++)
            {
                if (i < free || !InPeriod(T[i]) || Session[i] != 1 || Window[i] != 0 || T[i] - T[i - 60] != 3600 || T[i] - T[i - 1440] > 4 * 86400) continue;
                double body = C[i] - O[i];
                if (body == 0 || usualRng[i - 1] <= 0 || usualRng[i - 1] < gate || Math.Abs(body) < 1.5 * usualRng[i - 1]) continue;
                int below = 0; for (int j = i - 1440; j < i; j++) if (V[j] < V[i]) below++;
                if (below < 0.95 * 1440) continue;

                double raw = ReplayExit(i, -Math.Sign(body), StopUsd, 0, 5, out int next);
                if (double.IsNaN(raw)) continue;
                free = next; long t = T[i]; if (first == 0) first = t; last = t;
                if (raw <= -StopUsd) stops++;
                double pnl = raw - cost;
                st.Add(pnl); if (pnl > 0) gw += pnl; else gl -= pnl;
                cum += pnl; peak = Math.Max(peak, cum); maxDd = Math.Max(maxDd, peak - cum); worst = Math.Min(worst, pnl);
                var d = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime; string key = d.ToString("yy-MM", Inv);
                if (!months.TryGetValue(key, out var m)) months[key] = m = new St(); m.Add(pnl);
                if (!years.TryGetValue(d.Year, out var y)) years[d.Year] = y = new St(); y.Add(pnl);
                days[t / 86400] = (days.TryGetValue(t / 86400, out var dp) ? dp : 0) + pnl;
            }
            string name = gate > 0 ? "LXg (usual range >= " + gate.ToString("0.0", Inv) + ")" : "LX";
            if (st.N == 0) { Console.WriteLine($"{name}, cost {cost}: no trades"); continue; }
            Console.WriteLine($"{name}, cost {cost:0.00}: n {st.N} ({st.N / Math.Max(1, (last - first) / 86400.0 * 5 / 7):N1}/day)  win {st.Win:N1}%  avg {st.Mean:N3}  PF {(gl > 0 ? gw / gl : 0):N2}  net {st.Sum:N0}  t {st.TStat:N2}  stops {stops}");
            Console.WriteLine($"   max drawdown {maxDd:N0}   worst trade {worst:N1}   worst day {days.Values.Min():N1}   best day {days.Values.Max():N1}   losing days {100.0 * days.Values.Count(v => v < 0) / days.Count:N0}%");
            Console.WriteLine("   by year (net, trades, PF-like avg): " + string.Join("  ", years.Select(kv => $"{kv.Key}: {kv.Value.Sum.ToString("+0;-0", Inv)} ({kv.Value.N}, avg {kv.Value.Mean.ToString("+0.00;-0.00", Inv)})")));
            if (cost == FixedCost) Console.WriteLine("   by month (net, trades): " + string.Join("  ", months.Select(kv => $"{kv.Key}: {kv.Value.Sum.ToString("+0;-0", Inv)} ({kv.Value.N})")));
        }
    }

    static void VolumeBehaviour()
    {
        int n = T.Length;
        var rng = new double[n]; for (int i = 0; i < n; i++) rng[i] = H[i] - L[i];
        var usualRng = RollMean(rng, 60); var usualVol = RollMean(V, 60);
        var vol15 = RollMean(V, 15); var vol240 = RollMean(V, 240);

        // volume and body of bar i against the 60 bars before it
        double Rv(int i) => usualVol[i - 1] > 0 ? V[i] / usualVol[i - 1] : 1;
        double Size(int i) => usualRng[i - 1] > 0 ? Math.Abs(C[i] - O[i]) / usualRng[i - 1] : 0;
        double Fwd(int i, int h) { int e = i + 1, x = e + h; return x < n && T[e] - T[i] == 60 && T[x] - T[e] == h * 60L ? O[x] - O[e] : double.NaN; }
        bool Ok(int i) => i > 300 && InPeriod(T[i]) && Window[i] == 0 && T[i] - T[i - 60] == 3600;     // an unbroken hour behind it, away from news

        void Row(string label, List<(int i, int dir)> events, int h)
        {
            var g = new St(); var years = new SortedDictionary<int, St>();
            foreach (var (i, dir) in events)
            {
                double f = Fwd(i, h); if (double.IsNaN(f)) continue;
                double x = dir * f; g.Add(x);
                int y = YearOf(T[i]); if (!years.TryGetValue(y, out var s)) years[y] = s = new St(); s.Add(x);
            }
            if (g.N < 30) { Console.WriteLine($"{label,-50} {h,3}m  n {g.N,6}  (too few)"); return; }
            Console.WriteLine($"{label,-50} {h,3}m  n {g.N,6}  continues {g.Win,5:N1}%  gross {g.Mean.ToString("+0.000;-0.000", Inv),7}  follow {(g.Mean - FixedCost).ToString("+0.000;-0.000", Inv),7}  fade {(-g.Mean - FixedCost).ToString("+0.000;-0.000", Inv),7}  t {g.TStat,6:N2}  | {Years(years, s => s.Mean)}");
        }

        Console.WriteLine($"\n==== H11 volume and speed (USD per oz; cost {FixedCost} per round trip; news windows excluded) ====");

        // ---- A) how fast does the price move after a busy / quiet minute?
        Console.WriteLine("\n-- A) volume of the minute (x the average of the hour before) -> size of what follows --");
        Console.WriteLine("volume            n      range of the bar   |move| next 1m   next 5m   next 15m   next 5m vs usual");
        var all5 = new St();
        for (int i = 301; i < n; i++) if (Ok(i)) { double f = Fwd(i, 5); if (!double.IsNaN(f)) all5.Add(Math.Abs(f)); }
        var cuts = new[] { 0, 0.6, 1.0, 1.5, 2.5, 4, double.MaxValue };
        for (int b = 0; b + 1 < cuts.Length; b++)
        {
            var range = new St(); var m1 = new St(); var m5 = new St(); var m15 = new St();
            for (int i = 301; i < n; i++)
            {
                if (!Ok(i)) continue;
                double rv = Rv(i); if (rv < cuts[b] || rv >= cuts[b + 1]) continue;
                range.Add(rng[i]);
                double a = Fwd(i, 1), c5 = Fwd(i, 5), c15 = Fwd(i, 15);
                if (!double.IsNaN(a)) m1.Add(Math.Abs(a)); if (!double.IsNaN(c5)) m5.Add(Math.Abs(c5)); if (!double.IsNaN(c15)) m15.Add(Math.Abs(c15));
            }
            string label = b == 0 ? "< 0.6" : b + 2 == cuts.Length ? ">= 4" : cuts[b].ToString("0.#", Inv) + " - " + cuts[b + 1].ToString("0.#", Inv);
            Console.WriteLine($"{label,-12} {range.N,8}   {range.Mean,16:N2}   {m1.Mean,14:N2}   {m5.Mean,7:N2}   {m15.Mean,8:N2}   {m5.Mean / all5.Mean,8:N2}x");
        }

        // ---- B) a strong bar: does the volume behind it tell whether it continues?
        Console.WriteLine("\n-- B) strong bar (body >= 1.5x the usual range): event = direction of the bar --");
        foreach (int h in new[] { 1, 5 })
            for (int b = 1; b + 1 < cuts.Length; b++)
            {
                var ev = new List<(int, int)>();
                for (int i = 301; i < n; i++)
                    if (Ok(i) && Size(i) >= 1.5 && C[i] != O[i]) { double rv = Rv(i); if (rv >= cuts[b] && rv < cuts[b + 1]) ev.Add((i, Math.Sign(C[i] - O[i]))); }
                Row($"strong bar, volume {(b + 2 == cuts.Length ? ">= 4" : cuts[b].ToString("0.#", Inv) + "-" + cuts[b + 1].ToString("0.#", Inv))}x", ev, h);
            }
        Console.WriteLine("by session, strong bar with volume >= 2.5x:");
        for (int s = 0; s < 4; s++)
        {
            var ev = new List<(int, int)>();
            for (int i = 301; i < n; i++) if (Ok(i) && Session[i] == s && Size(i) >= 1.5 && C[i] != O[i] && Rv(i) >= 2.5) ev.Add((i, Math.Sign(C[i] - O[i])));
            Row("  " + SessionNames[s], ev, 5);
        }

        // ---- C) climax: a spike to a new 30-minute extreme on heavy volume that closes back in the other third of the bar
        Console.WriteLine("\n-- C) rejected spike (new 30-min extreme, volume >= 2.5x, range >= 2x usual, close in the far third): event = direction of the rejection --");
        {
            var ev = new List<(int, int)>(); var quiet = new List<(int, int)>();
            for (int i = 301; i < n; i++)
            {
                if (!Ok(i) || rng[i] <= 0 || rng[i] < 2 * usualRng[i - 1]) continue;
                double hi = double.MinValue, lo = double.MaxValue;
                for (int j = i - 30; j < i; j++) { hi = Math.Max(hi, H[j]); lo = Math.Min(lo, L[j]); }
                double pos = (C[i] - L[i]) / rng[i];
                int dir = H[i] > hi && pos <= 1 / 3.0 ? -1 : L[i] < lo && pos >= 2 / 3.0 ? 1 : 0;
                if (dir == 0) continue;
                (Rv(i) >= 2.5 ? ev : quiet).Add((i, dir));
            }
            foreach (int h in new[] { 3, 5, 15 }) Row("rejected spike, heavy volume", ev, h);
            Row("rejected spike, volume < 2.5x (for contrast)", quiet, 5);
        }

        // ---- D) quiet box, then a break on volume
        Console.WriteLine("\n-- D) 15 quiet minutes (volume <= 0.7x of the 4 hours before), then a close outside their range: event = direction of the break --");
        {
            var loud = new List<(int, int)>(); var soft = new List<(int, int)>();
            for (int i = 301; i < n; i++)
            {
                if (!Ok(i) || vol15[i - 1] > 0.7 * vol240[i - 16]) continue;
                double hi = double.MinValue, lo = double.MaxValue;
                for (int j = i - 15; j < i; j++) { hi = Math.Max(hi, H[j]); lo = Math.Min(lo, L[j]); }
                int dir = C[i] > hi ? 1 : C[i] < lo ? -1 : 0;
                if (dir == 0) continue;
                (Rv(i) >= 2 ? loud : soft).Add((i, dir));
            }
            foreach (int h in new[] { 5, 15, 30 }) Row("break of a quiet box, volume >= 2x", loud, h);
            Row("break of a quiet box, volume < 2x (contrast)", soft, 15);
        }

        // ---- E) effort without result: heavy volume, tiny body
        Console.WriteLine("\n-- E) heavy volume (>= 2.5x) but a tiny body (<= 0.3x usual range): event = direction of the 5 minutes before --");
        {
            var ev = new List<(int, int)>();
            for (int i = 301; i < n; i++) if (Ok(i) && Rv(i) >= 2.5 && Size(i) <= 0.3 && C[i] != C[i - 5]) ev.Add((i, Math.Sign(C[i] - C[i - 5])));
            foreach (int h in new[] { 3, 5, 15 }) Row("absorption after a 5-minute push", ev, h);
        }

        // ---- F) three bars the same way: volume rising with the push, or falling
        Console.WriteLine("\n-- F) three bars in a row the same way: event = direction of the run --");
        {
            var rising = new List<(int, int)>(); var falling = new List<(int, int)>();
            for (int i = 301; i < n; i++)
            {
                if (!Ok(i)) continue;
                int d = Math.Sign(C[i] - O[i]);
                if (d == 0 || Math.Sign(C[i - 1] - O[i - 1]) != d || Math.Sign(C[i - 2] - O[i - 2]) != d) continue;
                if (C[i] - O[i - 2] == 0 || Math.Abs(C[i] - O[i - 2]) < 2 * usualRng[i - 3]) continue;        // a push that matters
                if (V[i] > V[i - 1] && V[i - 1] > V[i - 2]) rising.Add((i, d));
                else if (V[i] < V[i - 1] && V[i - 1] < V[i - 2]) falling.Add((i, d));
            }
            foreach (int h in new[] { 1, 5 }) { Row("run with rising volume", rising, h); Row("run with falling volume", falling, h); }
        }

        // The broker's feed caps tick volume at about 550 per minute since 2025 (2024 reached 1,700), so "x times the usual volume" cannot
        // occur in the current market. From here volume is ranked against the last 24 hours instead: rank 0.98 = busier than 98% of them.
        double Rank(int i) { int below = 0; for (int j = i - 1440; j < i; j++) if (V[j] < V[i]) below++; return below / 1440.0; }
        var dayOpen = new double[n]; { long cur = -1; double open = 0; for (int i = 0; i < n; i++) { long d = T[i] / 86400; if (d != cur) { cur = d; open = O[i]; } dayOpen[i] = open; } }

        // ---- G) the busiest minutes of the day
        Console.WriteLine("\n-- G) bar with a clear direction (body >= 1x usual range) by volume rank of the last 24 hours: event = direction of the bar --");
        {
            var bands = new (string name, double lo, double hi)[] { ("rank < 0.50", 0, 0.5), ("rank 0.50-0.90", 0.5, 0.9), ("rank 0.90-0.98", 0.9, 0.98), ("rank >= 0.98", 0.98, 1.01) };
            var lists = bands.Select(_ => new List<(int, int)>()).ToArray();
            var top = new List<(int, int)>[4]; for (int s = 0; s < 4; s++) top[s] = new List<(int, int)>();
            var withDay = new List<(int, int)>(); var againstDay = new List<(int, int)>();
            for (int i = 1741; i < n; i++)
            {
                if (!Ok(i) || Size(i) < 1 || C[i] == O[i] || T[i] - T[i - 1440] > 4 * 86400) continue;
                double r = Rank(i); int d = Math.Sign(C[i] - O[i]);
                for (int b = 0; b < bands.Length; b++) if (r >= bands[b].lo && r < bands[b].hi) lists[b].Add((i, d));
                if (r >= 0.98) { top[Session[i]].Add((i, d)); (Math.Sign(C[i] - dayOpen[i]) == d ? withDay : againstDay).Add((i, d)); }
            }
            foreach (int h in new[] { 1, 5, 15 }) for (int b = 0; b < bands.Length; b++) Row(bands[b].name, lists[b], h);
            Console.WriteLine("busiest 2% only, 5 minutes:");
            for (int s = 0; s < 4; s++) Row("  " + SessionNames[s], top[s], 5);
            Row("  bar goes with the day so far", withDay, 5); Row("  bar goes against the day so far", againstDay, 5);
        }

        // ---- H) shock bars: the price runs several times its usual range in one minute
        Console.WriteLine("\n-- H) shock bar (range of the minute vs the usual range; close in the outer third): event = direction of the bar --");
        {
            var bands = new (string name, double lo, double hi)[] { ("range 2-3x", 2, 3), ("range 3-5x", 3, 5), ("range 5-8x", 5, 8), ("range >= 8x", 8, 1e9) };
            var lists = bands.Select(_ => new List<(int, int)>()).ToArray();
            var big = new List<(int, int)>[4]; for (int s = 0; s < 4; s++) big[s] = new List<(int, int)>();
            var withDay = new List<(int, int)>(); var againstDay = new List<(int, int)>();
            for (int i = 301; i < n; i++)
            {
                if (!Ok(i) || rng[i] <= 0 || usualRng[i - 1] <= 0) continue;
                double x = rng[i] / usualRng[i - 1]; if (x < 2) continue;
                double pos = (C[i] - L[i]) / rng[i];
                int d = pos >= 2 / 3.0 ? 1 : pos <= 1 / 3.0 ? -1 : 0;
                if (d == 0) continue;
                for (int b = 0; b < bands.Length; b++) if (x >= bands[b].lo && x < bands[b].hi) lists[b].Add((i, d));
                if (x >= 3) { big[Session[i]].Add((i, d)); (Math.Sign(C[i] - dayOpen[i]) == d ? withDay : againstDay).Add((i, d)); }
            }
            foreach (int h in new[] { 1, 3, 5, 15 }) for (int b = 0; b < bands.Length; b++) Row(bands[b].name, lists[b], h);
            Console.WriteLine("range >= 3x only, 5 minutes:");
            for (int s = 0; s < 4; s++) Row("  " + SessionNames[s], big[s], 5);
            Row("  shock goes with the day so far", withDay, 5); Row("  shock goes against the day so far", againstDay, 5);
        }

        // ---- I) London only: is the reversal after a busy directional minute a smooth effect, or one lucky cell?
        Console.WriteLine("\n-- I) London 07:00-13:00 UTC, directional bar by volume rank and body: event = direction of the bar (a negative gross = it reverses) --");
        {
            var grid = new Dictionary<(double rank, double body), List<(int, int)>>();
            foreach (var r in new[] { 0.90, 0.95, 0.98 }) foreach (var b in new[] { 1.0, 1.5 }) grid[(r, b)] = new List<(int, int)>();
            for (int i = 1741; i < n; i++)
            {
                if (!Ok(i) || Session[i] != 1 || C[i] == O[i] || T[i] - T[i - 1440] > 4 * 86400) continue;
                double size = Size(i); if (size < 1) continue;
                double rank = Rank(i); int d = Math.Sign(C[i] - O[i]);
                foreach (var key in grid.Keys) if (rank >= key.rank && size >= key.body) grid[key].Add((i, d));
            }
            foreach (var kv in grid.OrderBy(k => k.Key.body).ThenBy(k => k.Key.rank))
                foreach (int h in new[] { 1, 3, 5, 10, 15 })
                    Row($"rank >= {kv.Key.rank:0.00}, body >= {kv.Key.body:0.0}x", kv.Value, h);
        }
    }
}
