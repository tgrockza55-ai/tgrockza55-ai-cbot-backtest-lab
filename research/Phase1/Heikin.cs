// HA: can the direction of the NEXT Heikin-Ashi bar (1 minute) be called at the moment it opens?  (user request 06/10/2026: accuracy only,
// no spread, no profit — at least 80% right over 2024-2026)
//
// Heikin-Ashi bar:  haClose = (open + high + low + close) / 4 of the real bar,  haOpen = (previous haOpen + previous haClose) / 2.
// A bar is "up" when haClose > haOpen. At the open of a bar its haOpen is already fixed, and the real price is known, so the question is
// only whether the average price of the coming minute ends above or below that fixed level.
//
// Run: Phase1.exe study:ha [period:all]
//
// The last table answers a different question on purpose: for the same calls, does the REAL price of that minute go the predicted way?
// A high score on Heikin-Ashi bars comes from their smoothing; it is not the same thing as knowing where the price goes.

using System;
using System.Collections.Generic;
using System.Linq;

static partial class Phase1
{
    static void HeikinStudy()
    {
        int n = T.Length;
        var ho = new double[n]; var hc = new double[n]; var rng = new double[n];
        hc[0] = (O[0] + H[0] + L[0] + C[0]) / 4; ho[0] = (O[0] + C[0]) / 2;
        for (int i = 1; i < n; i++) { hc[i] = (O[i] + H[i] + L[i] + C[i]) / 4; ho[i] = (ho[i - 1] + hc[i - 1]) / 2; }
        for (int i = 0; i < n; i++) rng[i] = H[i] - L[i];
        var usual = RollMean(rng, 60); var usualVol = RollMean(V, 60);

        // features known at the open of bar i (everything from bar i-1 and before, plus the opening price of bar i)
        const int K = 10;
        double[] Features(int i)
        {
            double u = Math.Max(usual[i - 1], 1e-6);
            double d = (O[i] - ho[i]) / u;                                               // real price against the fixed open of the new HA bar
            int streak = 0, side = Math.Sign(hc[i - 1] - ho[i - 1]);
            for (int j = i - 1; j > i - 11 && side != 0 && Math.Sign(hc[j] - ho[j]) == side; j--) streak++;
            return new[]
            {
                d, (hc[i - 1] - ho[i - 1]) / u, (C[i - 1] - C[i - 2]) / u, (C[i - 1] - C[i - 6]) / u,
                rng[i - 1] > 0 ? (C[i - 1] - L[i - 1]) / rng[i - 1] - 0.5 : 0, side * streak / 10.0, (O[i] - hc[i - 1]) / u,
                Math.Log(Math.Max(V[i - 1], 1) / Math.Max(usualVol[i - 1], 1)), rng[i - 1] / u, d * Math.Abs(d),
            };
        }

        var years = new SortedDictionary<int, int[]>();          // per year: [bars, same colour right, price-vs-open right, model right, alt target right, real right, real counted]
        int[] Y(int year) { if (!years.TryGetValue(year, out var a)) years[year] = a = new int[8]; return a; }
        double[] cover = { 0, 0.1, 0.2, 0.3, 0.5, 1.0 }; var covN = new long[cover.Length]; var covRight = new long[cover.Length];

        // walk-forward logistic model: fitted on the 12 months before each month, used on that month only
        var months = MonthStarts(); var w = new double[K + 1]; int monthIdx = -1; bool haveModel = false;
        var mean = new double[K]; var sd = new double[K];
        long total = 0;
        for (int i = 300; i < n; i++)
        {
            if (hc[i] == ho[i]) continue;
            while (monthIdx + 1 < months.Count - 1 && T[i] >= months[monthIdx + 1])
            {
                monthIdx++;
                haveModel = false;
                if (monthIdx >= 12)
                {
                    int a = LowerBound(months[monthIdx - 12]), b = LowerBound(months[monthIdx]);
                    var xs = new List<double[]>(); var ys = new List<int>();
                    for (int j = Math.Max(a, 300); j < b; j += 3) { if (hc[j] == ho[j]) continue; xs.Add(Features(j)); ys.Add(hc[j] > ho[j] ? 1 : 0); }
                    if (xs.Count > 20000) { FitLogistic(xs, ys, K, mean, sd, w); haveModel = true; }
                }
            }
            if (!InPeriod(T[i])) continue;
            bool up = hc[i] > ho[i];
            var f = Features(i);
            bool same = hc[i - 1] > ho[i - 1];
            bool rule = O[i] != ho[i] ? O[i] > ho[i] : same;
            double z = w[K]; if (haveModel) for (int k = 0; k < K; k++) z += w[k] * Math.Max(-8, Math.Min(8, (f[k] - mean[k]) / sd[k]));
            bool model = haveModel ? z > 0 : rule;
            var y = Y(YearOf(T[i])); total++;
            y[0]++; if (same == up) y[1]++; if (rule == up) y[2]++; if (model == up) y[3]++;
            if (hc[i] != hc[i - 1]) { y[6]++; if ((O[i] > hc[i - 1]) == (hc[i] > hc[i - 1])) y[4]++; }           // other reading: HA close against the previous HA close
            if (C[i] != O[i]) { y[7]++; if (model == (C[i] > O[i])) y[5]++; }                                    // the real price of the same minute
            double ad = Math.Abs(f[0]);
            for (int c = 0; c < cover.Length; c++) if (ad >= cover[c]) { covN[c]++; if (rule == up) covRight[c]++; }
        }

        Console.WriteLine($"\n==== HA: direction of the next 1-minute Heikin-Ashi bar, called at its open ({total:N0} bars) ====");
        Console.WriteLine("year      bars    same colour as the last bar   real price above/below the new HA open   model (10 readings)   |  HA close vs previous HA close   real price of that minute, same calls");
        int[] sum = new int[8];
        foreach (var kv in years)
        {
            var y = kv.Value; for (int k = 0; k < 8; k++) sum[k] += y[k];
            Console.WriteLine($"{kv.Key}  {y[0],9:N0}   {100.0 * y[1] / y[0],24:N2}%   {100.0 * y[2] / y[0],36:N2}%   {100.0 * y[3] / y[0],18:N2}%   |  {100.0 * y[4] / Math.Max(1, y[6]),28:N2}%   {100.0 * y[5] / Math.Max(1, y[7]),34:N2}%");
        }
        Console.WriteLine($"all   {sum[0],9:N0}   {100.0 * sum[1] / sum[0],24:N2}%   {100.0 * sum[2] / sum[0],36:N2}%   {100.0 * sum[3] / sum[0],18:N2}%   |  {100.0 * sum[4] / Math.Max(1, sum[6]),28:N2}%   {100.0 * sum[5] / Math.Max(1, sum[7]),34:N2}%");

        Console.WriteLine("\ncalling only when the price is clearly away from the new HA open (distance in usual 1-minute ranges):");
        for (int c = 0; c < cover.Length; c++)
            Console.WriteLine($"  distance >= {cover[c]:0.0}: calls {100.0 * covN[c] / total,5:N1}% of the bars, right {100.0 * covRight[c] / Math.Max(1, covN[c]):N2}%");
        if (haveModel) Console.WriteLine("\nweights of the last model (standardised): " + string.Join("  ", new[] { "priceVsHaOpen", "lastHaBody", "ret1", "ret5", "closeInBar", "streak", "priceVsLastHaClose", "volume", "lastRange", "priceVsHaOpen^2" }.Select((name, k) => $"{name} {w[k]:+0.00;-0.00}")));

        WriteHeikinExamples(ho, hc, usual);
    }

    /// <summary>
    /// Three real cases for the picture on the summary page (docs\data\ha-examples.json): a clear call up that was right, a clear call down
    /// that was right, and a close call that was wrong. Taken from the most recent data, each from a different day, 07:00-17:00 UTC.
    /// Each case: 15 Heikin-Ashi bars before the call, then the bar that was called.
    /// </summary>
    static void WriteHeikinExamples(double[] ho, double[] hc, double[] usual)
    {
        var repo = FindRepo(); if (repo == null) return;
        int n = T.Length; const int Before = 15;
        var picked = new List<(string kind, int i)>(); var days = new HashSet<long>();
        foreach (var kind in new[] { "up", "down", "wrong" })
            for (int i = n - 2; i > 400; i--)
            {
                long hour = T[i] % 86400 / 3600; if (hour < 7 || hour >= 17 || days.Contains(T[i] / 86400) || T[i] - T[i - Before] != Before * 60) continue;
                double u = usual[i - 1]; if (u <= 0) continue;
                double d = (O[i] - ho[i]) / u, body = (hc[i] - ho[i]) / u;
                bool ok = kind == "up" ? d >= 0.6 && d <= 1.2 && body >= 0.5
                        : kind == "down" ? d <= -0.6 && d >= -1.2 && body <= -0.5
                        : Math.Abs(d) >= 0.08 && Math.Abs(d) <= 0.25 && Math.Sign(body) == -Math.Sign(d) && Math.Abs(body) >= 0.35;
                if (!ok) continue;
                picked.Add((kind, i)); days.Add(T[i] / 86400); break;
            }
        string N(double v) => Math.Round(v, 3).ToString(Inv);
        var items = picked.Select(p =>
        {
            int i = p.i;
            var bars = Enumerable.Range(i - Before, Before + 1).Select(j => $"[{T[j]},{N(ho[j])},{N(Math.Max(H[j], Math.Max(ho[j], hc[j])))},{N(Math.Min(L[j], Math.Min(ho[j], hc[j])))},{N(hc[j])}]");
            return $"{{\"kind\":\"{p.kind}\",\"time\":{T[i]},\"open\":{N(O[i])},\"haOpen\":{N(ho[i])},\"call\":{(O[i] > ho[i] ? 1 : -1)},\"result\":{(hc[i] > ho[i] ? 1 : -1)},\"bars\":[{string.Join(",", bars)}]}}";
        });
        var file = System.IO.Path.Combine(repo, "docs", "data", "ha-examples.json");
        System.IO.File.WriteAllText(file, "{\"symbol\":\"" + SymbolName + "\",\"examples\":[" + string.Join(",", items) + "]}");
        Console.WriteLine($"\nwrote {picked.Count} example(s) to {file}");
    }

    /// <summary>Logistic regression by Newton steps (IRLS) on standardised inputs; w[K] is the intercept.</summary>
    static void FitLogistic(List<double[]> xs, List<int> ys, int K, double[] mean, double[] sd, double[] w)
    {
        int m = xs.Count;
        for (int k = 0; k < K; k++)
        {
            double s = 0, s2 = 0; foreach (var x in xs) { s += x[k]; s2 += x[k] * x[k]; }
            mean[k] = s / m; sd[k] = Math.Sqrt(Math.Max(1e-12, s2 / m - mean[k] * mean[k]));
        }
        var z = xs.Select(x => { var r = new double[K + 1]; for (int k = 0; k < K; k++) r[k] = Math.Max(-8, Math.Min(8, (x[k] - mean[k]) / sd[k])); r[K] = 1; return r; }).ToArray();
        Array.Clear(w, 0, K + 1);
        for (int iter = 0; iter < 12; iter++)
        {
            var g = new double[K + 1]; var hess = new double[K + 1, K + 1];
            for (int r = 0; r < m; r++)
            {
                double s = 0; for (int k = 0; k <= K; k++) s += w[k] * z[r][k];
                double p = 1 / (1 + Math.Exp(-s)), q = Math.Max(1e-6, p * (1 - p)), e = ys[r] - p;
                for (int a = 0; a <= K; a++) { g[a] += e * z[r][a]; for (int b = a; b <= K; b++) hess[a, b] += q * z[r][a] * z[r][b]; }
            }
            for (int a = 0; a <= K; a++) { hess[a, a] += 1e-3 * m; for (int b = 0; b < a; b++) hess[a, b] = hess[b, a]; }      // ridge, and fill the lower half
            for (int a = 0; a <= K; a++) g[a] -= 1e-3 * m * w[a];
            // solve hess * step = g (Gauss elimination with partial pivoting)
            int N = K + 1; var A = new double[N, N + 1];
            for (int a = 0; a < N; a++) { for (int b = 0; b < N; b++) A[a, b] = hess[a, b]; A[a, N] = g[a]; }
            for (int c = 0; c < N; c++)
            {
                int piv = c; for (int r = c + 1; r < N; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
                if (piv != c) for (int b = 0; b <= N; b++) (A[c, b], A[piv, b]) = (A[piv, b], A[c, b]);
                for (int r = c + 1; r < N; r++) { double fct = A[r, c] / A[c, c]; for (int b = c; b <= N; b++) A[r, b] -= fct * A[c, b]; }
            }
            var step = new double[N]; double change = 0;
            for (int r = N - 1; r >= 0; r--) { double s = A[r, N]; for (int b = r + 1; b < N; b++) s -= A[r, b] * step[b]; step[r] = s / A[r, r]; }
            for (int a = 0; a < N; a++) { w[a] += step[a]; change = Math.Max(change, Math.Abs(step[a])); }
            if (change < 1e-6) break;
        }
    }
}
